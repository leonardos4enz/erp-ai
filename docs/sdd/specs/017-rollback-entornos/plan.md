# Plan 017 — Diseño técnico

## Piezas
```
 Aplicar paquete (009, sin cambios en su compuerta)          Revertir (nuevo)
 ────────────────────────────────────────────────            ────────────────────────────────────────────────
 applock → diff → plan → roles/permisos                       «Revertir» en un paquete aplicado (último instalado)
   + instantánea ANTES (alcance del paquete)  ─┐                → fila meta.PackageImport kind='revert' (pending)
   + instantánea DESPUÉS                       ├ misma tx       diff = ModelDiff.Revert(antes, después, actual)
 meta.ModelSnapshot (huella SHA-256)          ─┘                  = Compute(antes, actual) + archivar lo creado
                                                                vista previa = PlanCompiler sobre el actual
                                                                [aprobación de otra persona: huella del diff]
                                                                Aplicar: applock → huellas de instantáneas → recalcula
                                                                  diff → misma huella → PlanService → meta.Module
                                                                  vuelve a la versión anterior → filas revert/reverted
```
Todo es genérico del motor: ninguna parte conoce un catálogo o un paquete concreto.

## Operaciones nuevas del plan (`Models.cs`, `PlanCompiler.cs`)
```jsonc
{ "op": "restoreEntity", "entity": "Marca" }                 // catálogo archivado → activo (tabla y registros intactos)
{ "op": "restoreField", "entity": "Marca", "field": "Pais" } // campo archivado → activo (columna y datos intactos)
```
- `restoreEntity`: el catálogo existe y está archivado. Sin SQL.
- `restoreField`: el catálogo está activo; el campo existe y está archivado. Sin SQL, salvo un campo único en un
  catálogo por empresa: su índice se recrea por empresa (`DROP INDEX IF EXISTS` + `CREATE UNIQUE INDEX … WHERE … IS NOT
  NULL`), porque pudo estar archivado cuando el catálogo pasó a ser por empresa (010). El paso advierte que los registros
  creados mientras estuvo archivado lo tienen vacío.
- **Pasada final** (`ValidateFinal`, para todo catálogo tocado): una referencia o un documento padre activos deben apuntar
  a un catálogo activo; una partida activa necesita su documento activo. Así `restoreEntity` y `restoreField` se pueden
  pedir en cualquier orden dentro del plan, y lo que quede a medias es error de la vista previa.
- `PlanService`: un `restoreField` de una fórmula se recalcula en los registros existentes (como `addField`).

## Diff (`Metadata/ModelDiff.cs`)
`Compute` cambia en dos puntos (aplica a paquetes y a reversiones):
1. Catálogo archivado en el destino y activo en lo deseado → `restoreEntity` y se compara como si estuviera activo.
2. Campo archivado en el destino y activo en lo deseado → `restoreField` + `updateField` si difiere; si la estructura
   difiere (tipo, longitud, catálogo referido…), sigue siendo conflicto.

Opciones nuevas de `Compute` (registro `DiffOptions`, solo las usa la reversión):
- `ArchiveScope` (catálogos y tableros): los del alcance activos en el destino que no existen (o están archivados) en lo
  deseado se archivan. Orden: `archiveField` de los campos de partidas del documento que apuntan a otra entidad que se
  archiva, luego catálogos de modo que nadie que quede activo los referencie (quien referencia antes que el referido;
  la partida antes que su documento). `archiveDashboard` para tableros.
- `Applied` (instantánea *después*): un campo del destino que lo deseado no trae es «local» (requiere confirmación) solo
  si tampoco estaba en *después*, es decir, se agregó en este entorno después de aplicar.

Orden del plan: reactivar catálogos → crear → reactivar campos → agregar campos → actualizar → procesos → reglas,
seguridad, interfaz, automatizaciones, webhooks y tareas → archivar campos → archivar catálogos → tableros.

`ModelDiff.Revert(snapshotAntes, snapshotDespués, actual, tablerosActuales, descripción)`:
- deseado = catálogos y tableros de *antes*; alcance = el de la instantánea;
- advertencias de deriva: cada catálogo o tablero del alcance cuyo estado actual difiere de *después*
  («cambió después de aplicar el paquete: revertir también deshace ese cambio»).

## Instantáneas (`Environments/Snapshots.cs`)
```csharp
public sealed record ModelSnapshot(IReadOnlyList<string> Entities, IReadOnlyList<string> Dashboards,   // alcance
    IReadOnlyList<EntityDef> EntityDefs, IReadOnlyList<DashboardDef> DashboardDefs);               // lo que existía
```
- Alcance = `ModelDiff.Scope(paquete)` (catálogos que crea el plan, partidas incluidas, y tableros).
- *Antes*: se toma del modelo leído dentro de la transacción de aplicar, antes del plan; *después*: del modelo releído
  tras el plan. Se serializa con las opciones del almacén de metadatos (`MetadataStore.Json`); huella = SHA-256 hex.
- Las propiedades calculadas de `EntityDef` (`ActiveFields`, `IsChild`, …) se escriben (la API las usa) y se ignoran al
  leer: la ida y vuelta de una instantánea da el mismo modelo (prueba unitaria).

## Esquema (migración idempotente)
```sql
meta.ModelSnapshot (Id int IDENTITY PK, ImportId int NOT NULL FK meta.PackageImport, Moment nvarchar(6) /* before | after */,
  ContentJson nvarchar(max), ContentHash char(64), CreatedAt datetime2(3) DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UX_ModelSnapshot_Import UNIQUE (ImportId, Moment))
ALTER TABLE meta.PackageImport ADD
  Kind nvarchar(10) NOT NULL DEFAULT 'package',   -- package | revert
  RevertOf int NULL FK meta.PackageImport,        -- revert: el paquete que deshace
  TargetVersion nvarchar(20) NULL,                -- revert: la versión que queda (NULL = deja de estar instalado)
  RevertedBy int NULL                             -- package: la solicitud de reversión que lo deshizo
-- Status admite además 'reverted' (package deshecho)
```
Solicitud de reversión: `PackageId`, `Name` y `Version` del paquete revertido; `ContentJson` = `{ revertOf, packageId,
version, targetVersion, beforeHash, afterHash }` y `ContentHash` = SHA-256 de ese JSON; `Signed` = 0; `UploadedBy` =
quien la pide.

## Reglas
| Regla | Error |
|---|---|
| Revertir: el paquete es `kind = package`, está `applied`, tiene instantáneas, su versión es la instalada y es el último aplicado de ese id | 409 con el motivo |
| Ya hay una reversión pendiente o aprobada de ese paquete | 409 |
| Huella de una instantánea distinta de la guardada | 409 «La instantánea no coincide con su huella» (detalle, aprobar y aplicar) |
| Aplicar: mismo diff revisado (`diffHash`) y, con aprobación, el aprobado; campos locales confirmados (014) | 409 |
| Aprobar: quien aprueba ≠ quien pidió | 403 |

## Contrato de API (solo superusuario; el resto 403)
```ts
POST /api/admin/environments/imports/{id}/revert   {}  → PackageImportDetail   // crea la solicitud (kind 'revert', pending)
// Sin cambios de ruta: GET /imports, GET /imports/{id}, POST /imports/{id}/approve | reject | apply sirven a ambos tipos.

type PackageStatus = 'pending' | 'approved' | 'rejected' | 'applied' | 'reverted';
type PackageImport = { /* … 009 … */ kind: 'package' | 'revert'; revertOf?: number; targetVersion?: string;
  revertedBy?: number };   // revertedBy: id de la solicitud que lo deshizo
type PackageImportDetail = PackageImport & { /* … 009 … */
  canRevert: boolean; revertBlockedReason?: string;              // solo en paquetes aplicados
  snapshot?: { beforeHash: string; afterHash: string } };      // paquetes con instantánea y reversiones
// diff de una reversión: mismo PackageDiffDto; roles = [] (no se otorga nada).
```
`POST /apply` de una reversión responde igual que la de un paquete (`{ changeSetId, import }`).

Auditoría: `environment.apply` agrega `snapshot: { before, after }` (huellas). Nuevas: `environment.revertRequest`
(paquete, versión que se deshace, versión que queda, huellas), `environment.revert` (versión anterior y nueva, huella del
diff y de las instantáneas, conjunto de cambios, quién aprobó). Aprobar y rechazar usan las acciones de la 009 con
`kind: 'revert'`.

## UI — Configuración › Entornos
- Tabla de *Paquetes recibidos*: la reversión se ve como «Reversión de {nombre}» con «1.1.0 → 1.0.0» (o «→ sin
  instalar»); estado *Revertido* (neutral) en el paquete deshecho.
- Diálogo de un paquete aplicado: huella de la instantánea y botón **Revertir a la versión anterior** (o *Revertir la
  instalación*), que crea la solicitud y la abre en el mismo diálogo: la solicitud **es** la confirmación con vista previa
  (constitución IV.4); nada cambia hasta aplicarla. Si no se puede revertir, el motivo.
- Diálogo de una reversión: mismo diálogo (diff, advertencias de deriva, campos locales con confirmación, aprobar,
  rechazar, aplicar con el texto «Aplicar reversión»).
- Modelador: en un catálogo archivado, botón *Reactivar* (`restoreEntity`); en el editor, el botón de un campo archivado
  pasa de deshabilitado a *Reactivar campo* (`restoreField`).

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **E** Revertir como atajo para cambiar producción sin cuatro ojos | La reversión es una fila de `PackageImport` y pasa por los mismos `approve`/`apply`: con aprobación obligatoria la aprueba otro administrador, ligada a la huella del diff y recalculada bajo el `applock`. No hay endpoint que aplique una reversión sin esa ruta |
| **E** Escalar privilegios con una reversión | La reversión nunca crea roles ni otorga permisos (`roles = []` y sin `GrantAsync`); solo archiva, reactiva o redefine metadatos que el compilador valida |
| **T** Instantánea alterada en la base para «revertir» a otra cosa | Huella SHA-256 guardada; detalle, aprobación y aplicación la recalculan y se rechaza si no coincide. La solicitud guarda las huellas en su contenido, que también tiene huella |
| **T** Aprobar un diff y aplicar otro (TOCTOU) | Igual que la 009: huella del diff aprobada; aplicar recalcula bajo el `applock` del modelo y exige igualdad |
| **D/T** Destruir datos al revertir | Ninguna operación borra: lo creado se archiva (tabla, columnas y registros se conservan) y `restoreField`/`restoreEntity` no tienen DDL destructivo |
| **R** Repudiar una reversión | Auditoría inmutable (`environment.revertRequest`, `approve`, `reject`, `revert`, `model.change`) con usuario, huellas y versiones; la fila guarda quién pidió, aprobó y aplicó, y el paquete revertido apunta a su reversión |
| **I** Fuga de datos en la instantánea | Solo definiciones (las mismas que ya están en `meta.Entity`); sin registros, usuarios, claves ni destinos de webhooks. Solo superusuario la puede revisar |
| **D** Instantáneas o diffs enormes | Alcance acotado al paquete (≤ 2 MB de origen, ≤ 100 operaciones); una reversión por paquete en revisión; solo superusuario |
