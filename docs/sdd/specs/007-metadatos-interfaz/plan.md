# Plan 007 — Diseño técnico

## Metadatos nuevos
Todo se declara con el plan de cambios (validado, en una transacción, auditado), salvo las vistas personales.

### Layout (`setLayout`) — `meta.Entity.LayoutJson`
```ts
type Layout = { sections: LayoutSection[] };              // 1..30 secciones
type LayoutSection = {
  name: string;                 // identificador, único en el layout
  label: string;
  description?: string;
  tab?: string;                 // etiqueta de pestaña: secciones con la misma etiqueta van juntas, en orden
  columns?: 1 | 2;              // por defecto 2
  visibleWhen?: string;         // expresión; sin valor = visible
  fields: (string | { field: string; visibleWhen?: string; wide?: boolean })[];
};
Operation += { op: 'setLayout'; entity: string; layout: Layout | null }     // null lo elimina
```
- Organiza el **encabezado**: no admite partidas, documento padre ni estado (las partidas y el total de un
  documento se muestran después del encabezado, como hoy). Un campo aparece una sola vez.
- Los campos no mencionados se muestran en «Otros datos» (en un documento, las fórmulas no mencionadas siguen en
  la tarjeta de totales).
- `visibleWhen`: el analizador de expresiones de la spec 002 (campos propios, `Referencia.Campo`, funciones). Se
  muestra solo si la condición es verdadera (desconocido = oculto). Una sección oculta oculta sus campos.
- **Evaluación en el servidor** (`RecordService.EvaluateLayout`): con el borrador ya calculado; una condición que lee un campo
  oculto para el usuario por la 006 (propio o del registro relacionado) se ignora (la sección o campo se muestra),
  así el resultado nunca depende de un dato que no ve.
- Un campo oculto por condición **no se exige** al guardar (encabezado). Su valor se conserva.

### Vistas compartidas (`setViews`) — `meta.Entity.ViewsJson`
```ts
type ViewFilter = { field: string; op: FilterOp; value?: unknown };
type FilterOp = 'eq' | 'ne' | 'gt' | 'gte' | 'lt' | 'lte' | 'contains' | 'in' | 'blank' | 'notBlank';
type ViewDef = {
  name: string; label: string;
  roles?: string[];             // omitido = todos los que leen el catálogo
  columns?: string[];           // omitido = columnas «en listado»
  filters?: ViewFilter[];       // máx. 20, combinados con AND
  sort?: string; dir?: 'asc' | 'desc'; state?: string;
  default?: boolean;            // se aplica al abrir el listado (la primera predeterminada disponible)
};
Operation += { op: 'setViews'; entity: string; views: ViewDef[] }     // reemplaza la lista; máx. 30
```
Valores especiales: `$me` (id del usuario, en campos tipo usuario) y `$today` (en fechas).

### Reportes (`setReports`) — `meta.Entity.ReportsJson`
```ts
type ReportDef = {
  name: string; label: string; description?: string;
  roles?: string[];
  groupBy?: { field: string; period?: 'day' | 'month' | 'year'; label?: string }[];   // 0..2
  measures: { fn: 'count' | 'sum' | 'avg' | 'min' | 'max'; field?: string; label?: string }[];  // 1..6
  filter?: string;              // subconjunto SQL de la 006 (campos propios, CURRENTUSER(), TODAY()…)
  orderBy?: 'group' | 'measure'; dir?: 'asc' | 'desc';    // por defecto: grupo ascendente
  limit?: number;               // 1..1000, por defecto 500
  chart?: 'bar' | 'line' | 'pie';
};
Operation += { op: 'setReports'; entity: string; reports: ReportDef[] }   // también en partidas; máx. 30
```
- **Rutas de campo:** `Campo` (columna propia) o `Relacion.Campo`, donde `Relacion` es una referencia o el
  documento padre y `Campo` una columna del catálogo relacionado. Un nivel.
- Agrupables: texto, lista, sí/no, entero, folio, estado, referencia y documento padre (por su campo para
  mostrar), usuario (por nombre), fecha y fecha-hora (con `period` obligatorio). Medidas: `count` (sin campo =
  registros), `sum`/`avg` numéricos (entero, decimal, fórmula numérica), `min`/`max` numéricos o fechas.
- Vacío y relacionado no visible se muestran como «(sin valor)» y «(sin acceso)».

### Tableros (`setDashboard`, `archiveDashboard`) — `meta.Dashboard`
```ts
type DashboardDef = {
  name: string; label: string; icon?: string; description?: string;
  roles?: string[];
  widgets: { type: 'kpi' | 'chart' | 'table'; label?: string; entity: string; report: string;
             measure?: number /* índice; kpi */; wide?: boolean }[];      // 1..20
};
Operation += { op: 'setDashboard'; dashboard: DashboardDef } | { op: 'archiveDashboard'; name: string }
```
`kpi` exige un reporte sin agrupación; `chart`, uno con agrupación. El compilador recibe y devuelve los tableros
(`CompiledPlan.Dashboards`) y los revalida en la pasada final contra el modelo completo (un reporte eliminado o un
catálogo archivado no puede dejar un tablero roto en silencio). También se revalidan todos los reportes, vistas y
layouts del modelo cuando el plan toca algo (las rutas `Relacion.Campo` dependen de otros catálogos).

## Seguridad (se aplica en el servidor)
| Operación | Registro (006) | Campo (006) |
|---|---|---|
| Listado con `filters` | igual que hoy (filtro de lectura) | filtro por campo oculto → **400**; el orden por un campo oculto se ignora (como en la 006) |
| `GET /views` | — | una vista compartida que filtra/ordena por un campo oculto no se ofrece; columnas ocultas se quitan |
| Reporte | base: `RecordFilter.ForRecordOrParent`; cada relacionado se une como `(SELECT … WHERE filtro de lectura)` | cualquier campo oculto en agrupación, medida, filtro fijo o filtro del usuario → no disponible (**404**); filtro del usuario oculto → **400** |
| Exportación | mismo resultado que el reporte | ídem |
| Tablero | cada tarjeta corre su reporte con la seguridad anterior | tarjeta no disponible se omite; sin tarjetas → 404 |
| `visibleWhen` | — | condición que lee un campo oculto → se ignora |

Los parámetros de cada predicado se renombran (`SqlPredicate.Prefixed`) para combinar varios en un comando.

## Contrato de API
```ts
EntityDef += { layout?: Layout; views?: ViewDef[] /* solo modeladores */; reports?: ReportDef[] /* solo modeladores */ }
GET  /api/data/{e}?…&filters=<json ViewFilter[]>          // 400 si un filtro es inválido o usa un campo oculto
POST /api/data/{e}/compute → { values, layout?: { hiddenSections: string[]; hiddenFields: string[] } }
GET  /api/data/{e}/{id}   → Row & { _layout?: { hiddenSections; hiddenFields } }

GET    /api/views/{e}            → { shared: SavedView[]; personal: SavedView[] }
POST   /api/views/{e}            { label, columns?, filters?, sort?, dir?, state? } → SavedView   (máx. 50 por catálogo)
PUT    /api/views/{e}/{id}       → SavedView         DELETE /api/views/{e}/{id} → 204    (solo el dueño; si no, 404)
type SavedView = ViewDef & { id?: number; personal: boolean }

GET /api/reports                               → { entity; entityLabel; group?; name; label; description?; chart? }[]
GET /api/reports/{e}/{name}?filters=           → ReportResult
GET /api/reports/{e}/{name}/export?format=csv|xlsx&filters=   → archivo (Content-Disposition attachment)
type ReportResult = {
  entity; name; label; description?; chart?;
  columns: { key: string; label: string; kind: 'group' | 'measure'; type: 'text' | 'number' | 'date' | 'boolean'; scale?: number }[];
  rows: Record<string, string | number | boolean | null>[]; totals: Record<string, number | null>; truncated: boolean;
}
GET /api/dashboards           → { name; label; icon; description? }[]
GET /api/dashboards/{name}    → { name; label; icon; description?; widgets: { type; label; wide; entity; report; measure?; result: ReportResult }[];
                                  definition?: DashboardDef /* solo modeladores */ }
```

## Esquema (migración idempotente)
- `meta.Entity` + `LayoutJson`, `ViewsJson`, `ReportsJson` (`nvarchar(max)`).
- `meta.Dashboard (Name PK, Label, DefinitionJson, IsArchived, UpdatedAt)`.
- `meta.SavedView (Id, UserId FK, EntityName, Label, DefinitionJson, CreatedAt, UpdatedAt)`, índice por usuario y catálogo.

## UI
- **Formulario:** secciones con su encabezado `h2` (sin región con nombre, F-07), pestañas con el componente `Tabs`
  existente (la pestaña con un error se abre sola). Las condiciones se piden a `/compute` al editar.
- **Listado:** selector «Vista» (compartidas y personales), «Columnas» y «Filtros» en diálogos, «Guardar vista»;
  todo en la URL (`view`, `cols`, `filters`).
- **Análisis** en el menú: «Tableros» y «Reportes»; página del reporte con filtros, tabla, gráfica SVG
  (`role="img"` con resumen) y botones «CSV» y «Excel»; tablero en rejilla de tarjetas.
- **Modelador:** «Layout», «Vistas» y «Reportes» (JSON) en la sección avanzada; tableros: «Editar (JSON)» en su
  página, con la misma vista previa del plan.
- **F-03:** `buildOperations` compara la secuencia de campos existentes contra la original; si no cambió, no
  emite `displayOrder`; si cambió, renumera de forma consecutiva y solo envía los campos cuyo número cambia.

## Compras 1.4.0 e Inventario 1.2.0 (entradas en `upgrades`)
- Compras: layouts de *Orden de compra* (pestañas «General» y «Seguimiento»; tipo de cambio si hay moneda y no es MXN;
  motivos solo en *Rechazada*/*Cancelada*) y *Requisición* (secciones; el motivo de rechazo sigue a la vista con su
  pista, F-25); vistas «Mis requisiciones» (`Solicitante = $me`), «Por aprobar», «Pendientes de recibir», «Por
  autorizar»; reportes «Compras por proveedor», «Compras por mes», «Monto comprado», «Órdenes abiertas»,
  «Requisiciones por estado» y «Productos más comprados» (cantidades); tablero «Compras».
- Inventario: vista «Bajo mínimo», reportes «Existencias por almacén», «Productos bajo mínimo», «Movimientos por
  tipo» y «por mes»; tablero «Inventario».

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **I** Deducir un campo oculto agrupando, sumando, filtrando, ordenando o exportando por él | Un solo resolvedor de rutas que exige `CanSee` en cada tramo; reporte no disponible (404) y filtros/orden → 400 |
| **I** Contar o sumar registros fuera de alcance | Filtro de lectura en la base (`ForRecordOrParent`); relacionados unidos con su propio filtro |
| **I** Revelar un campo oculto por una condición de layout | La condición que lo lee se ignora para ese usuario |
| **I** Ver vistas personales ajenas | Consultas por `UserId` de la sesión; ajena = 404 |
| **T** Modificar o borrar vistas ajenas | Ídem; la vista personal queda en la auditoría |
| **T** Inyección de fórmulas en CSV/Excel | Excel sin fórmulas (texto/número); CSV antepone `'` a `= + - @` y tabuladores |
| **E** Inyección SQL por filtros, periodos, medidas o rutas | Identificadores de metadatos validados y citados; operadores, funciones y periodos de lista cerrada; valores como parámetros |
| **D** Reportes o tableros costosos | 2 agrupaciones, 6 medidas, 1000 filas, un nivel de relación, 20 filtros, 20 tarjetas, tiempo máximo de consulta 30 s |
| **R** Cambiar layouts, vistas compartidas, reportes o tableros sin rastro | Plan de cambios auditado |
