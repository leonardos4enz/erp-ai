# Plan 009 — Diseño técnico

## Piezas
```
 Desarrollo (sandbox)                         Pruebas / Producción
 ───────────────────                          ─────────────────────────────────────────────
 Entornos › Exportar                          Entornos › Subir paquete
   catálogos + tableros elegidos                verifica formato, reglas (H5), firma, versión
   ModelDiff contra «el modelo sin ellos»       guarda meta.PackageImport (pendiente, huella)
   = plan que los crea  ─────── archivo ──────▶ estado deseado = plan del paquete compilado
   + roles + source + firma                       sobre «el destino sin sus catálogos»
                                                ModelDiff(deseado, destino) = plan mínimo
                                                vista previa = PlanCompiler sobre el destino
                                                [aprobación de otra persona: huella del diff]
                                                Aplicar: applock → recalcula diff → misma huella
                                                  → PlanService (transacción, auditoría)
                                                  → roles y permisos → meta.Module (versión)
```
Todo es genérico del motor: ningún catálogo, módulo ni rol tiene código propio.

## Diff de modelos (`Metadata/ModelDiff.cs`, puro y probado sin BD)
`ModelDiff.Compute(desired, desiredBoards, target, targetBoards) → { Plan, Conflicts, Warnings, Created }`

1. **Catálogos nuevos** (no existen en el destino): `createEntity` con sus campos activos, en orden de dependencias
   (el documento antes que su partida). Un campo referencia a un catálogo que aún no existe (ciclo `A ↔ B`) se difiere a
   un `addField` posterior. El campo `Estado` lo crea `setWorkflow`.
2. **Catálogos existentes:**
   - `updateEntity` si cambian etiqueta, plural, icono, descripción, grupo o campo para mostrar.
   - Campo nuevo → `addField`; campo que cambia → `updateField` (etiqueta, obligatorio, en listado, opciones, obligatorio
     por rol, orden, valor por defecto, fórmula, editable, filtro del buscador); campo del destino que el paquete no trae
     → `archiveField` + advertencia.
   - **Conflictos** (no hay operación que los exprese; bloquean): tipo, longitud, precisión, escala, catálogo
     referido, partidas, tipo de resultado, único, prefijo o dígitos distintos; campo o catálogo archivado en el destino
     y activo en el paquete; quitar un proceso; una partida que en el destino no lo es (o al revés).
   - `setWorkflow`, `setRules`, `setSecurity`, `setLayout`, `setViews`, `setReports`, `setAutomations`, `setWebhooks`
     cuando su JSON difiere (lista vacía = nula).
3. **Tableros** del paquete: `setDashboard` si no existe, está archivado o difiere.
4. Orden: crear → agregar campos → actualizar catálogos y campos → procesos → reglas, seguridad, interfaz,
   automatizaciones y webhooks → archivar campos → tableros. El compilador valida todo en su pasada final.

**Exportar** = `Compute(elegidos, tableros elegidos, modelo − elegidos, tableros − elegidos)`: el diff contra «el
entorno sin esos catálogos» es justamente el plan que los crea, con referencias externas resueltas.
**Estado deseado al importar** = compilar el plan del paquete (base + `upgrades`) sobre «el destino sin los catálogos y
tableros que el paquete crea», con los roles del destino y los del paquete; se toman de ahí los catálogos del paquete.
Prueba de ida y vuelta: exportar Compras + Inventario y aplicar sobre el modelo original da un diff vacío.

## Formato del paquete
```jsonc
{ "id": "compras-ajustes", "name": "Ajustes de compras", "version": "1.1.0", "icon": "package", "description": "…",
  "plan": { "description": "…", "operations": [ … ] },        // igual que modules/*.json
  "roles": [{ "name": "Comprador", "description": "…", "canManageModel": false, "canManageUsers": false,
              "canViewAudit": false, "permissions": [{ "entity": "OrdenCompra", "canRead": true, … }] }],
  "seed": [],
  "source": { "environment": "Desarrollo", "exportedAt": "2026-10-23T18:00:00Z" },
  "signature": "hmac-sha256:<hex>" }                          // solo si el origen tiene clave
```
- **Contenido canónico** = el paquete deserializado con el modelo del servidor y vuelto a serializar sin `signature`
  (JSON compacto). **Huella** = SHA-256 hex del canónico. **Firma** = `HMAC-SHA256(Environments:SigningKey, canónico)`.
  El archivo se descarga con sangría; la firma no depende del formato.
- **Roles exportados:** no del sistema, con permisos sobre los catálogos elegidos o cuyo nombre aparece citado en su
  metadato (transiciones, seguridad, vistas, reportes, webhooks, tableros, `HASROLE('…')`). Solo los permisos sobre los
  catálogos del paquete; sin `canManageModel`/`canManageUsers` (advertencia).

## Reglas al subir (`Environments/PackageRules.cs`)
| Regla | Error |
|---|---|
| JSON válido ≤ 2 MB, `id` `^[a-z][a-z0-9-]{1,40}$`, no es id de un módulo del servidor | 400 |
| `version` semver `x.y.z` mayor que la aplicada de ese id (`meta.Module`) | 400 |
| Plan con al menos una operación `createEntity` o `setDashboard`; máx. 100 por plan | 400 |
| `seed` vacío | 400 |
| Roles: nombre 1–100, sin repetir, sin `canManageModel`/`canManageUsers`, no reutiliza un rol del sistema; permisos solo sobre catálogos que el paquete crea | 400 |
| Firma: con clave configurada, obligatoria y válida (`FixedTimeEquals`); intento fallido → auditoría `environment.rejected` | 400 |
| El mismo contenido ya está pendiente o aprobado | 409 |

## Esquema (migración idempotente)
```sql
meta.PackageImport (Id int PK, PackageId nvarchar(50), Name nvarchar(100), Version nvarchar(20), Description nvarchar(500) NULL,
  SourceEnvironment nvarchar(100) NULL, ExportedAt datetime2 NULL, ContentJson nvarchar(max), ContentHash char(64), Signed bit,
  Status nvarchar(12) /* pending | approved | rejected | applied */, PreviousVersion nvarchar(20) NULL,
  UploadedBy int FK User, UploadedAt, ReviewedBy int NULL FK, ReviewedAt NULL, ReviewComment nvarchar(500) NULL,
  ApprovedDiffHash char(64) NULL, AppliedBy int NULL FK, AppliedAt NULL, ChangeSetId int NULL)
meta.PackageExport (Id int PK, PackageId, Name, Version, ContentHash char(64), Signed bit, EntitiesJson nvarchar(max),
  ExportedBy int FK User, ExportedAt)
```

## Aplicar (`Environments/EnvironmentService.ApplyAsync`)
1. Superusuario; estado `approved` o (sin aprobación obligatoria) `pending`.
2. Transacción + `sp_getapplock 'erp-model'` (el mismo que `PlanService`; es reentrante en la transacción).
3. Relee la fila con `UPDLOCK`; recalcula la huella del contenido guardado (y la firma, si hay clave): un contenido
   alterado en la base no se aplica.
4. Versión mayor que la de `meta.Module` (también con `UPDLOCK`).
5. Recalcula el diff contra el modelo leído **dentro** de la transacción; su huella debe ser igual a la que el usuario
   revisó (`diffHash` del cuerpo) y, si hubo aprobación, a la aprobada. Si no, 409 «El entorno cambió…».
6. Roles nuevos (`EnsureRolesAsync`) → plan (`PlanService.ApplyInTransactionAsync`, con `extraRoles`) → permisos
   (`GrantAsync`): roles nuevos, todos sus permisos; roles existentes, solo sobre catálogos creados por el diff.
7. `meta.Module` (alta o versión), fila `applied` con `ChangeSetId`, auditoría `environment.apply`, *commit*,
   `store.Invalidate()`.

Huella del diff = SHA-256 de `{ plan.operations, roles nuevos, permisos a otorgar }` serializado.

## Configuración (por entorno, fuera del repositorio)
| Clave | Por defecto | Uso |
|---|---|---|
| `Environments:Name` | `Desarrollo` | Nombre que se muestra y viaja en `source.environment` (≤ 100) |
| `Environments:RequireApproval` | `false` | Producción: `true` (cuatro ojos) |
| `Environments:SigningKey` | vacío | ≥ 32 caracteres; compartida por la cadena de entornos (user-secrets / variable de entorno) |

## Contrato de API (solo superusuario; el resto 403)
```ts
GET  /api/admin/environments            → { name: string; requireApproval: boolean; signing: boolean;
                                             packages: { id: string; version: string; installedAt: string }[] }   // paquetes de entorno aplicados
GET  /api/admin/environments/exportable → { entities: { name; label; labelPlural; group?; children: string[] /* etiquetas */ }[];
                                             dashboards: { name; label }[];
                                             exports: { packageId; name; version; hash; exportedAt; exportedBy }[] } // últimos 20
type ExportRequest = { id: string; name: string; version: string; description?: string; icon?: string;
                       entities: string[]; dashboards: string[] };
POST /api/admin/environments/export/preview  ExportRequest → { valid; errors: PlanError[]; steps: PlanStep[]; operations: number;
                                             entities: string[]; roles: PackageRole[]; dependencies: string[]; warnings: string[];
                                             hash: string; signed: boolean }
POST /api/admin/environments/export          ExportRequest → archivo application/json «{id}-{version}.json» (400 si no es válido)
GET  /api/admin/environments/imports        → PackageImport[] (100 más recientes)
POST /api/admin/environments/imports        cuerpo: bytes del archivo → PackageImportDetail
GET  /api/admin/environments/imports/{id}   → PackageImportDetail (diff recalculado si está pendiente o aprobado)
POST /api/admin/environments/imports/{id}/approve  { diffHash: string; comment?: string } → PackageImportDetail
POST /api/admin/environments/imports/{id}/reject   { comment: string }                   → PackageImportDetail
POST /api/admin/environments/imports/{id}/apply    { diffHash: string }                   → { changeSetId: number | null; import: PackageImportDetail }

type PackageRole = { name: string; isNew: boolean; canViewAudit: boolean;
                     permissions: { entity: string; label: string; canRead: boolean; canCreate: boolean; canUpdate: boolean; canDelete: boolean }[] };
type PackageImport = { id: number; packageId: string; name: string; version: string; previousVersion?: string;
  status: 'pending' | 'approved' | 'rejected' | 'applied'; hash: string; signed: boolean; sourceEnvironment?: string;
  exportedAt?: string; uploadedBy: string; uploadedAt: string; reviewedBy?: string; reviewedAt?: string;
  reviewComment?: string; appliedBy?: string; appliedAt?: string; changeSetId?: number };
type PackageImportDetail = PackageImport & { description?: string; installedVersion?: string;
  diff?: { valid: boolean; errors: PlanError[]; steps: PlanStep[]; conflicts: string[]; warnings: string[];
           roles: PackageRole[]; diffHash: string; empty: boolean };
  canApprove: boolean; canApply: boolean; blockedReason?: string };
```
Auditoría: `environment.export` (id, versión, huella, catálogos), `environment.upload`, `environment.rejected` (firma o
formato inválidos), `environment.approve`, `environment.reject`, `environment.apply` (versión anterior y nueva, huella
del paquete y del diff, conjunto de cambios, quién aprobó). El plan aplicado queda además en `model.change` y
`meta.ChangeSet` como cualquier cambio de modelo.

## UI — Configuración › Entornos (`/admin/environments`)
- Enlace después de «Integraciones» (no empuja «Auditoría» fuera de la pantalla, regresión 003).
- Encabezado con el entorno actual: nombre, «Requiere aprobación» / «Aplicación directa», «Firma obligatoria» /
  «Sin clave de firma».
- Pestaña **Paquetes recibidos**: «Subir paquete» (`.json`), tabla con paquete, versión (anterior → nueva), estado,
  huella corta, quién y cuándo; «Revisar» abre el diálogo: datos del paquete (origen, firma, huella completa), roles
  con permisos, `PlanPreviewView` con los pasos, conflictos y advertencias, comentario, y «Rechazar» / «Aprobar» /
  «Aplicar» según estado y permisos (con el motivo cuando un botón no aplica).
- Pestaña **Exportar**: identificador, nombre, versión (sugerida), descripción; catálogos (por grupo del menú, con sus
  partidas) y tableros con casillas; «Revisar paquete» muestra la vista previa; «Descargar paquete» baja el archivo.
  Historial de exportaciones con su huella.

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **T** Paquete manipulado en el camino (agregar un rol, una regla, quitar seguridad) | Firma HMAC-SHA256 con clave compartida fuera del repositorio, verificada en tiempo constante sobre el contenido canónico; con clave configurada, sin firma = rechazo; intento auditado. Huella SHA-256 visible en origen y destino para cotejar |
| **T** Contenido alterado en la base después de subirlo o aprobarlo | Al aplicar se recalcula huella (y firma) del contenido guardado y se compara con la registrada |
| **T** Paquete hostil (DDL, nombres, expresiones, tamaño) | Mismo `PlanCompiler` que el modelador (identificadores en lista blanca, expresiones con su analizador, límites); 2 MB; `JsonUnknownDerivedType` falla; sin datos (`seed` vacío) |
| **E** Escalada por roles importados | Sin `canManageModel`/`canManageUsers`; roles del sistema no se tocan; roles existentes conservan sus capacidades y solo reciben permisos sobre catálogos que el paquete crea; permisos solo sobre catálogos del paquete; todo visible en el diff antes de aprobar |
| **E** Aplicar sin aprobación en producción | `RequireApproval` es configuración del servidor (no hay endpoint que la cambie); aplicar exige estado `approved`; quien aprueba ≠ quien sube |
| **E/T** Aprobar un diff y aplicar otro (TOCTOU) | La aprobación guarda la huella del diff; aplicar lo recalcula bajo el `applock` del modelo y exige igualdad |
| **I** Fuga de datos o secretos en la exportación | Solo metadatos: el exportador no lee tablas de datos, usuarios, claves, `meta.WebhookEndpoint` ni vistas personales. Advertencia cuando una vista compartida filtra por un registro concreto (su etiqueta viaja). Solo superusuario exporta |
| **R** Repudio de aprobaciones o aplicaciones | Auditoría inmutable (`environment.*`, trigger de solo inserción) con usuario, huella del paquete y del diff, versión y comentario; la fila guarda quién subió, aprobó y aplicó |
| **D** Paquetes enormes o diff costoso | 2 MB por petición, 100 operaciones por plan, límites del compilador; solo superusuario |
| **S** Suplantar el entorno de origen | `source.environment` es informativo; la confianza viene de la firma (clave compartida), no del nombre |
