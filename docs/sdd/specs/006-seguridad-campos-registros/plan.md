# Plan 006 — Diseño técnico

## Política de seguridad del catálogo (`setSecurity`)
Se guarda en `meta.Entity.SecurityJson` (migración idempotente) y viaja en `EntityDef.security` (solo a
modeladores). Un plan la define completa; `security: null` la elimina.

```ts
type SecurityPolicy = {
  fields?: FieldSecurity[];     // también en catálogos de partidas (el estado es el del documento)
  owner?: string;               // campo tipo usuario que identifica al dueño; omitido = CreatedBy
  records?: RecordGrant[];      // omitido = sin restricción por registro; solo en catálogos raíz
  systemManaged?: boolean;      // las personas solo consultan; solo el motor escribe
};

type FieldSecurity = {
  field: string;
  read?: string[];              // roles que lo ven; omitido = todos los que leen el catálogo
  edit?: string[];              // roles que lo capturan; omitido = todos; [] = nadie (solo lectura)
  editableIn?: string[];        // estados en que se captura; [] = solo en transiciones que lo piden
};

type RecordGrant = {
  roles?: string[];             // omitido = todos los roles
  scope?: 'all' | 'unit' | 'own';   // por defecto 'all'
  where?: string;               // condición adicional (subconjunto SQL del lenguaje de expresiones)
  access?: 'read' | 'write';    // por defecto 'write'
};
```
`Operation += { op: 'setSecurity'; entity: string; security: SecurityPolicy | null }`

### Semántica
- **Acceso a campo** para un usuario (por rol): `hidden` si `read` existe y no tiene ninguno de sus roles;
  `read` si `edit` existe y no tiene ninguno; si no, `edit`. Superusuario: `edit`. Además, en un registro con
  estado, un campo con `editableIn` que no incluye el estado actual queda en `read` (también para el superusuario).
  Los campos calculados o asignados por el sistema siguen siendo de solo lectura (spec 004).
- **Acceso a registro**: sin `records` no hay restricción. Con `records`, se toman los permisos cuyo `roles`
  incluye alguno del usuario (o no lo declara); el filtro de lectura es el `OR` de todos ellos y el de escritura,
  el `OR` de los de `access: 'write'`. Sin ninguno aplicable: ningún registro. Superusuario: todos.
  - `own`: `t.[Dueño] = @uid` · `unit`: `t.[Dueño] IN (SELECT Id FROM meta.[User] WHERE Unit = @unit)`
    (sin unidad, equivale a `own`) · `all`: sin condición. Cada uno `AND (where)`.
- **`where` → SQL** (`RecordFilter`): campos con columna del propio catálogo (incluye estado, folio y fórmulas
  guardadas), literales número/texto/`TRUE`/`FALSE`, `= <> < <= > >=`, `IN (…)`, `AND OR NOT`, paréntesis,
  `ISBLANK(campo)`, `CURRENTUSER()`, `CURRENTUNIT()`, `ISCREATOR()` (`CreatedBy = @uid`), `HASROLE('r')`
  (se resuelve a constante) y `TODAY()`. Un campo booleano solo equivale a `campo = 1`. Sin `NULL` literal (usar
  `ISBLANK`). Identificadores desde metadatos y citados; **todo valor es parámetro**. Lo que no se pueda traducir
  se rechaza en la vista previa.
- **Dónde se aplica** (servidor, `RecordService`):

| Operación | Campo | Registro |
|---|---|---|
| `GET /data/{e}` | sin columnas ocultas; búsqueda y orden ignoran campos ocultos | filtro de lectura en datos, `total` y `stateCounts` |
| `GET /data/{e}/{id}` y `/history` | sin ocultos (encabezado y partidas) | fuera de alcance → 404 |
| `GET /data/{e}/lookup` | `filterField` oculto → 400 | filtro de lectura del catálogo buscado (una partida hereda el de su documento) |
| Referencias al guardar y en `/compute` | — | el registro referido debe ser visible («no existe») |
| `POST /data/{e}` | ignora campos sin `edit` | tras crear, debe quedar en alcance de escritura (si no, 403 y se revierte) |
| `PUT /data/{e}/{id}` | ignora campos sin `edit` en el estado actual | antes y después: alcance de escritura (404 si no lo ve, 403 si solo lo lee) |
| `DELETE /data/{e}/{id}` | — | alcance de escritura |
| `POST /compute` | ignora campos sin `edit`; respuesta sin ocultos | con `id`: alcance de escritura |
| `POST /{id}/transition` | `requiredFields` ocultos → 400; respuesta sin ocultos | alcance de lectura |
| `systemManaged` | — | `POST/PUT/DELETE` → 403 «Lo administra el sistema», incluso superusuario |
| Automatizaciones | sin restricción (autoridad del motor) | sin restricción |

### Validación estática (`PlanCompiler`)
Campos existentes, activos y no repetidos; roles existentes; `editableIn` con estados del proceso (del documento
en partidas) y solo en catálogos con proceso; `owner` campo tipo usuario; `records` no vacío y solo en catálogos
raíz; `scope`/`access` válidos; `where` traducible; el campo para mostrar no admite `read`; un campo obligatorio
sin valor por defecto no admite `read`/`edit` ni un `editableIn` sin el estado inicial; máximo 200 reglas de campo
y 20 permisos de registro. También se revalida en la pasada final cuando el plan toca el catálogo (un campo
archivado o un estado eliminado no puede dejar la política rota en silencio).

## Unidad del usuario
`meta.[User].Unit nvarchar(100) NULL`. `UserDto`, `CreateUserRequest`, `UpdateUserRequest` y `Me` ganan `unit`.
Cambiar la unidad renueva el sello de seguridad (cierra sesiones), igual que cambiar roles. `GET /admin/units`
devuelve las unidades en uso (sugerencias del formulario). Función nueva del lenguaje: `CURRENTUNIT()`.

## Contrato de API (cambios)
```ts
EntityDef += { security?: SecurityPolicy /* solo modeladores */; systemManaged: boolean }
FieldDef  += { access?: 'hidden' | 'read' | 'edit' /* para el usuario actual, por rol */; editableIn?: string[] }
Operation += { op: 'setSecurity'; entity: string; security: SecurityPolicy | null }
User += { unit?: string } · Me += { unit?: string } · GET /api/admin/units → string[]
```
`GET /model/entities` no devuelve campos ocultos a quien no modela; al modelador se los devuelve con
`access: 'hidden'`. Sin cambios de forma en registros, `/compute` ni transiciones: solo se omiten valores.

## UI
- `activeFields` omite los campos `hidden`; `isComputedField` trata `access: 'read'` como bloqueado.
- El formulario aplica `editableIn` con el estado del registro (o el inicial al crear) a encabezado y partidas,
  con la pista «Solo lectura para tu rol» / «No se edita en este estado».
- `systemManaged`: sin «Nuevo», «Guardar» ni «Eliminar»; aviso «Lo administra el sistema».
- Modelador: «Seguridad (JSON)» en la sección avanzada, validado en la vista previa.
- Seguridad › Usuarios: campo «Unidad» con sugerencias.

## Compras 1.3.0 e Inventario 1.1.0 (entradas en `upgrades`)
- Compras: `Requisicion` — `MotivoRechazo` `editableIn: []`; dueño `Solicitante`; permisos: *Solicitante* `own`,
  *Comprador*, *Gerente de compras*, *Director* `all`. `OrdenCompra` — `MotivoRechazo` y `MotivoCancelacion`
  `editableIn: []`; `Subtotal`, `Descuento`, `IVA`, `Total`, `TotalMXN` con `read` para compradores, gerentes y
  directores. `OrdenCompraPartida` — precio, descuento, IVA, importes y costo MXN con el mismo `read`.
- Inventario: `Existencia` `systemManaged: true`.

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **I** Leer un campo oculto por la API (GET, listado, partidas, `/compute`, respuesta de transición o guardado) | Un solo punto de salida (`Shape`) que quita los campos ocultos de encabezado y partidas; pruebas por cada vía |
| **I** Deducir un campo oculto buscando u ordenando por él, o filtrando el buscador | La búsqueda solo usa campos visibles; el orden por un campo oculto se ignora; `filterField` oculto → 400 |
| **I** Enumerar registros ajenos por id | Lectura fuera de alcance → 404, igual que inexistente; `total` y `stateCounts` filtrados |
| **I** Referenciar un registro que no veo para leerlo por fórmulas o valores por defecto (`Ref.Campo`) | La validación de referencias aplica el filtro de lectura del catálogo referido, también en `/compute` |
| **T** Escribir un campo de solo lectura o fuera de estado | El servidor descarta el valor (no confía en la UI); los `requiredFields` de transición son la única otra vía |
| **T / E** Sacar un registro del propio alcance o crearlo en el de otro | Se verifica el alcance de escritura antes y después de guardar, dentro de la transacción; si falla, se revierte |
| **E** Inyección SQL por reglas `where` | Traductor con lista cerrada de nodos; identificadores de metadatos validados y citados; valores como parámetros; `HASROLE` y `CURRENTUNIT` también parametrizados o constantes |
| **E** Modificar a mano un catálogo del sistema | `systemManaged` se revisa en el servidor para todos, incluido el superusuario |
| **I** Fórmula visible que depende de un campo oculto (`Total` sobre `Precio`) | Riesgo residual documentado: el modelador debe ocultar también los campos derivados (Compras 1.3.0 lo hace) |
| **R** Cambiar quién ve qué sin rastro | `setSecurity` pasa por el plan de cambios (auditado); los cambios de unidad quedan en `user.change` |
| **D** Reglas `where` costosas | Máx. 1000 caracteres y 20 permisos; solo columnas propias (sin uniones arbitrarias) |
