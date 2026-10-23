# Plan 002 — Diseño técnico

## Lenguaje de expresiones (fórmulas, valores por defecto, reglas, condiciones)
Se interpreta en memoria con un parser propio (descenso recursivo). **No** se traduce a SQL ni
usa `eval`. Límite: 1000 caracteres y profundidad de 50.

```
or      := and ('OR' and)*
and     := not ('AND' not)*
not     := 'NOT' not | cmp
cmp     := add (( '=' | '<>' | '<' | '<=' | '>' | '>=' ) add | 'IN' '(' expr (',' expr)* ')')?
add     := mul (( '+' | '-' | '&' ) mul)*          & concatena texto
mul     := unary (( '*' | '/' ) unary)*
unary   := '-' unary | primary
primary := número | 'texto' | TRUE | FALSE | NULL | FUNC '(' args ')' | ruta | '(' expr ')'
ruta    := Campo | CampoReferencia.Campo
```
| Función | Uso |
|---|---|
| `IF(c, a, b)`, `COALESCE(a, b, …)`, `ISBLANK(x)` | lógica |
| `ROUND(x, n)`, `ABS(x)`, `MIN(a, b)`, `MAX(a, b)` | números |
| `LEN(s)`, `UPPER(s)`, `ISEMAIL(s)` | texto |
| `TODAY()`, `NOW()`, `ADDDAYS(fecha, n)`, `DAYS(desde, hasta)` | fechas |
| `CURRENTUSER()` | id del usuario actual (para campos tipo usuario) |
| `HASROLE('Rol')`, `ISCREATOR()` | seguridad (control interno) |
| `SUM(Partidas.Campo)`, `COUNT(Partidas)` | agregados sobre partidas |

Semántica de nulos: en aritmética `NULL` vale 0. `NULL = NULL` es verdadero y `x = NULL` falso.
Comparaciones de orden (`<`, `>=`…) contra nulo dan **desconocido** (como en SQL), y la lógica
es de tres valores. Una regla desconocida **no** se considera violada (así una regla sobre un
campo opcional vacío no bloquea); una condición de transición desconocida **no** la habilita.

## Metadatos nuevos

### Tipos de campo
| Tipo | Columna | Notas |
|---|---|---|
| `autonumber` | nvarchar(30), índice único | `prefix` (p. ej. `OC-`) y `padding` (6). Se asigna al crear, con `meta.Sequence` bajo `UPDLOCK` |
| `formula` | según `resultType` | `formula` + `resultType` (`decimal`, `integer`, `text`, `boolean`, `date`). Solo lectura |
| `lines` | *(virtual, sin columna)* | `childEntity`: entidad hija. Partidas del documento |
| `parent` | bigint FK NOT NULL lógico | En la entidad hija: apunta al documento. Una por entidad |
| `state` | nvarchar(50) | La crea `setWorkflow` con el nombre `Estado`. Solo lectura |
| `user` | int FK a `meta.[User]` | Etiqueta: nombre del usuario |

Propiedad nueva para cualquier campo editable: `defaultValue` (expresión). Se aplica cuando el
campo está vacío.

### Reglas (`setRules`)
```ts
type Rule = { name: string; message: string; assert: string; when?: string; field?: string }
```
Se evalúan al guardar (crear/editar), después de calcular fórmulas. Si `when` es verdadero (o no
existe) y `assert` es falso, se rechaza con `message` asociado a `field` (o error general).

### Procesos (`setWorkflow`)
```ts
type Workflow = {
  initial: string;
  states: { name: string; label: string; color?: 'neutral'|'accent'|'success'|'warning'|'danger'; editable?: boolean }[];
  transitions: {
    name: string; label: string; from: string[]; to: string;
    roles?: string[]; when?: string; whenMessage?: string;
    requireComment?: boolean; requiredFields?: string[];
    set?: { field: string; value: string }[];      // value es expresión
    icon?: string; variant?: 'primary'|'secondary'|'danger';
  }[];
}
```
La primera vez que se define, crea la columna `Estado` y pone el estado inicial en los
registros existentes. En estados con `editable: false` el servidor rechaza ediciones.

### Operaciones nuevas del Plan de Cambios
```ts
| { op: 'setWorkflow'; entity: string; workflow: Workflow }
| { op: 'setRules'; entity: string; rules: Rule[] }
// updateField acepta además: defaultValue?, formula?
```
El compilador valida las expresiones: sintaxis, campos existentes, funciones y número de
argumentos, y ciclos entre fórmulas. Las fórmulas se evalúan en orden topológico.

## Esquema (migración idempotente sobre bases existentes)
- `meta.Entity` + `WorkflowJson`, `RulesJson`
- `meta.Field` + `DefaultValue`, `Formula`, `ResultType`, `Prefix`, `Padding`, `ChildEntity`
- `meta.Sequence (EntityName, FieldName, NextValue)`
- `meta.WorkflowHistory (Id, EntityName, RecordId, FromState, ToState, Transition, Comment, UserId, At)`
- `meta.Module (Id, Version, InstalledAt, InstalledBy)`

## Contrato de API (cambios sobre la spec 001)

### EntityDef / FieldDef
```ts
EntityDef += { workflow?: Workflow; rules?: Rule[]; isChild: boolean; parentEntity?: string }
FieldDef  += { defaultValue?: string; formula?: string; resultType?: string;
               prefix?: string; padding?: number; childEntity?: string; readOnly: boolean }
FieldType += 'autonumber' | 'formula' | 'lines' | 'parent' | 'state' | 'user'
```
`GET /model/entities` excluye las entidades hijas (`isChild`) del menú, pero las incluye con
`?includeChildren=true` (el formulario las necesita para pintar las partidas).

### Registros
- `GET /data/{e}/{id}` incluye las partidas de cada campo `lines` (`Partidas: Row[]`) y, si
  hay proceso:
  ```ts
  _workflow: { state; stateLabel; color; editable;
    transitions: { name; label; icon?; variant?; to; allowed: boolean; reason?: string;
                   requireComment: boolean; requiredFields: string[] }[] }
  ```
- `GET /data/{e}?state=PorAutorizar` filtra por estado; la respuesta incluye
  `stateCounts: { state; label; color; count }[]` cuando hay proceso.
- `POST/PUT /data/{e}` aceptan partidas: `Partidas: [{ id?: number, ...campos }]`. Es el
  conjunto completo: las que no vienen se eliminan (borrado lógico).
- `POST /data/{e}/compute` `{ id?: number, values: { ...campos, Partidas?: [...] } }`
  → `{ values }` con valores por defecto aplicados y fórmulas calculadas (incluye partidas).
  No guarda nada. Se usa para totales en vivo y sugerencias.
- `POST /data/{e}/{id}/transition` `{ transition, comment?, values?: {campo: valor}, _rowVersion }`
  → `Row`. 400 si falta comentario o campo, 403 si el rol no puede, 409 si cambió el registro.
- `GET /data/{e}/{id}/history` → `{ at, user, from, fromLabel, to, toLabel, transition, transitionLabel, comment }[]`
- Las entidades hijas no aceptan escrituras directas (400): se editan desde su documento.
- `GET /users/lookup?search=` → `{ id, label }[]` para campos tipo `user`.

### Módulos (superusuario)
- `GET /admin/modules` → `{ id, name, description, icon, version, installed, entities: string[] }[]`
- `POST /admin/modules/{id}/preview` → `PlanPreview & { roles: string[]; seedRecords: number }`
- `POST /admin/modules/{id}/install` → `{ changeSetId }` (una sola transacción: modelo, roles,
  permisos y datos iniciales)

Formato del paquete (`modules/*.json`):
```jsonc
{ "id": "compras", "name": "Compras", "version": "1.0.0", "icon": "shopping-cart", "description": "…",
  "plan": { "description": "…", "operations": [ … ] },
  "roles": [{ "name": "Comprador", "description": "…", "permissions": [{ "entity": "Proveedor", "canRead": true, … }] }],
  "seed": [{ "entity": "Moneda", "key": "Clave", "records": [{ "Clave": "MXN", "Nombre": "Peso mexicano" }] }] }
```
En `seed`, una referencia se escribe `{ "$ref": "UnidadMedida", "Clave": "PZA" }` y se
resuelve al id del registro.

## Amenazas nuevas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| Inyección de código en fórmulas | Intérprete propio sin `eval`, sin reflexión, lista cerrada de funciones, límites de tamaño y profundidad |
| DoS con fórmulas costosas | Profundidad máx. 50, longitud máx. 1000, partidas máx. 500 por documento |
| Saltar el proceso editando directo por API | El servidor valida el estado editable y que el campo `Estado` no sea escribible |
| Escalar privilegios con transiciones | Roles verificados en el servidor; `allowed` en la UI es solo informativo |
| Folios duplicados por concurrencia | `UPDLOCK, HOLDLOCK` sobre `meta.Sequence` dentro de la transacción + índice único |
| Paquetes maliciosos | Solo se leen de la carpeta `modules/` del servidor (no se suben por API) y pasan por el compilador |
