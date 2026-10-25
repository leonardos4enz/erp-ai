# Plan 025 — Diseño técnico

Todo vive en `src/Erp.Api/Explorer/` (servidor) y `web/src/pages/explorer/` (SPA). Sin metadatos nuevos, sin tablas nuevas,
sin dependencias nuevas. Las piezas de cálculo son puras (como `PlanCompiler`) para probarlas sin base de datos.

## 1. Contrato de la API (`/api/explorer`, exige `CanManageModel`)
```ts
GET /api/explorer/database?schema=app|meta&app=&q=   → DbSchema        // meta: solo superusuario (403); otro valor: 400
GET /api/explorer/database/ddl?schema=&app=&q=&tables=A,B → text/plain (attachment «esquema-app.sql»)
GET /api/explorer/relations?app=&q=                  → RelGraph
GET /api/explorer/entities/{name}                    → EntityCard       // 404 si no existe
GET /api/explorer/usages?entity=X&field=Y            → Usage[]          // field opcional; 404 si el catálogo/campo no existe
GET /api/explorer/automations                        → FlowGraph
GET /api/explorer/security                           → SecurityMatrix
GET /api/explorer/navigation                         → NavApp[]

DbSchema { schema, tables: DbTable[], external: { schema, name }[], drift: DbDrift[], apps: AppRef[] }
DbTable  { schema, name, entity?: { name, label, app, appLabel, isArchived, isChild }, columns: DbColumn[],
           primaryKey?: { name, columns: string[] }, foreignKeys: DbForeignKey[], indexes: DbIndex[], driftCount }
DbColumn { name, type /* nvarchar(200), decimal(18,2), rowversion… */, nullable, identity, default?: { name, definition },
           computed?: string, system: boolean, field?: { name, label, type }, status: 'ok'|'system'|'archived'|'orphan'|'mismatch',
           expectedType? }
DbForeignKey { name, columns, refSchema, refTable, refColumns, nullable /* de las columnas */, unique /* índice único en ellas */, onDelete }
DbIndex  { name, unique, primaryKey, uniqueConstraint, filter?, columns, included }
DbDrift  { kind: 'orphanTable'|'archivedTable'|'missingTable'|'orphanColumn'|'archivedColumn'|'missingColumn'|'typeMismatch',
           table, column?, message }
AppRef   { name, label, icon, order }

RelGraph { apps: AppRef[], nodes: RelNode[], edges: RelEdge[] }
RelNode  { name, label, labelPlural, icon, app, isChild, hasWorkflow, perCompany, fields: RelField[] }
RelField { name, label, type, resultType?, target?, required, unique, formula, autonumber, hasDefault, state }
RelEdge  { from, field, to /* catálogo o «$user» */, kind: 'reference'|'lines'|'parent'|'user' }

EntityCard { entity: EntityDef /* completo, como lo ve el modelador */, app: AppRef, table: 'app.X', tableExists,
             packages: { id, name, created: boolean, installedVersion? }[], dashboards: { name, label }[],
             referencedBy: { entity, field, kind }[], roles: RoleAccess[] }
RoleAccess { role, read, create, update, delete, inheritedFrom: string[] /* roles que aportan algo heredado */,
             scopes: { access: 'read'|'write', scope, where? }[], hidden: string[], readOnly: string[] }

Usage { entity /* dónde está la mención */, kind, location /* «Regla Total positivo (condición)» */, expression?,
        target: { entity, field? }, tab: 'catalogo'|'formulario'|'proceso' }
kind ∈ formula | default | rule | transition | layout | automation | schedule | webhook | access | view | report | dashboard
       | lookup | ledger | display | reference

FlowGraph { nodes: { name, label, app }[], edges: FlowEdge[] }
FlowEdge  { from, to, owner /* catálogo que declara */, name, label, trigger /* «Al crear», «Al entrar a Recibida»… */,
            mode: 'sync'|'async'|'schedule', action: 'update'|'transition'|'create'|'rollup'|'cost'|'capture',
            detail, triggers: boolean /* el destino tiene automatizaciones de ese evento */, cycle: boolean }

SecurityMatrix { roles: { name, description, isSuperuser, inherits: string[] }[], entities: { name, label, app }[],
                 cells: (RoleAccess & { entity })[] }
NavApp { name, label, icon, entities: { name, label, labelPlural, icon, hasWorkflow, layout: boolean,
         sections: { name, label, tab?, fields: { name, label, type }[] }[], lines: string[] }[] }
```

## 2. Catálogo de SQL Server (`Explorer/DbCatalog.cs`)
- Esquemas permitidos: `app` (modelador) y `meta` (superusuario). El parámetro llega como texto y se compara contra la lista
  blanca; a SQL va el valor **de la lista** como parámetro (`s.name = @schema`), nunca el de la petición.
- Una sola ida (`QueryMultiple`) con seis consultas parametrizadas sobre `sys.tables`, `sys.columns` + `sys.types` +
  `sys.default_constraints` + `sys.computed_columns` + `sys.identity_columns`, `sys.indexes`, `sys.index_columns`,
  `sys.foreign_keys` y `sys.foreign_key_columns`, filtradas por `@schema`. Sin `sys.partitions` (sin conteos).
- `SqlTypes.Format(type, maxLength, precision, scale)` (puro): `nvarchar(n)` = `max_length/2`, `-1` = `max`; `varchar`,
  `varbinary`, `char`, `nchar`, `binary` igual; `decimal`/`numeric(p,s)`; `datetime2`/`time`/`datetimeoffset(scale)`; el
  resto por su nombre (`bigint`, `int`, `bit`, `date`, `rowversion` — `timestamp` en `sys.types` se muestra como
  `rowversion` —, `uniqueidentifier`…).
- `PhysicalModel.Build(catalog, model, apps)` (puro): liga cada tabla `app.X` con el catálogo `X`, marca columnas de sistema
  (`Identifiers.SystemColumns`), compara tipos con `DdlGenerator.ColumnType` y produce la deriva. Filtros `app` (módulo) y `q`
  (texto en tabla, catálogo o columna) se aplican aquí, en el servidor; las tablas fuera del filtro que una llave foránea
  menciona van en `external`.
- `DdlScript.For(tables)` (puro): `CREATE TABLE [esquema].[tabla]` con columnas (tipo, `IDENTITY(1,1)`, `NULL`/`NOT NULL`,
  `CONSTRAINT [DF_…] DEFAULT …`), `CONSTRAINT [PK_…] PRIMARY KEY`, luego `ALTER TABLE … ADD CONSTRAINT [FK_…] FOREIGN KEY …
  REFERENCES …` y `CREATE [UNIQUE] INDEX … ON … (…) [INCLUDE (…)] [WHERE …]`. Los nombres se citan con `QUOTENAME`
  equivalente (`[` + `]]` escapado + `]`). Encabezado con el esquema y la fecha; sin `INSERT`.

## 3. Modelo lógico (`Explorer/ModelExplorer.cs`, puro)
- `Relations(model, apps, app?, q?)`: catálogos activos (también partidas) → `RelNode`/`RelEdge`.
- `Usages`: índice de menciones de todo el modelo, en caché por instancia del modelo (`ConditionalWeakTable` sobre el
  diccionario que entrega `MetadataStore`: cambia cuando cambia el modelo, igual que `ModelVersion`) junto con los tableros.
  Cada sitio con expresión se analiza con `ExprParser.Parse` en su **ámbito** (el mismo que usa el compilador: el registro,
  la partida con `lines`, el registro creado en `capture`, el catálogo de periodos en `open`). Una ruta `A.B.C` agrega una
  mención por salto: (ámbito, A), (destino de A, B), (destino de B, C); `SUM(P.X)` agrega (ámbito, P) y (partida, X);
  `UNITCOST('Libro', …)` agrega (Libro). Los nombres de campo sueltos (columnas de vistas, `requiredFields`, `set.field`,
  `match`, campos de reportes, `fields` de webhooks y seguridad, libro) se resuelven en su catálogo. Una expresión inválida
  se ignora (el compilador ya no la deja entrar). Sin código por módulo.
- `Flow(model)`: por cada automatización y tarea, cada acción → arista (ámbito/destino con `AutomationRules.Scope` y
  `Target`), con `triggers` = el destino tiene automatizaciones del evento de `AutomationRules.Effects`; ciclos por
  componentes fuertemente conexas (Tarjan) sobre las aristas que disparan.
- `Navigation(model, apps)`: `Apps.All` → catálogos (orden del menú) → secciones del layout (o «Formulario» derivado con los
  campos activos en orden) → campos.
- `Security(model, roles, perms, edges)`: por rol, cierre de herencia (`RoleGraph.Expand`), unión de permisos y marca de lo
  que solo llega por herencia; alcance de `security.records` que menciona el rol (o todos), y campos ocultos (`read` sin el
  rol) y de solo lectura (`edit` sin el rol).
- `EntityCard`: lo anterior para un catálogo + paquetes de `modules/` cuyo plan (cadena completa) crea o toca el catálogo,
  con la versión instalada de `meta.Module`.

## 4. SPA
- Ruta `/admin/explorer` (con `Guard` de modelo) y entrada «Explorador» en el grupo *Modelo* de `adminNav.ts`.
- `ExplorerPage`: `PageHeader` + `Tabs` (Base de datos, Relaciones, Ficha, ¿Dónde se usa?, Automatizaciones, Seguridad y
  navegación); la vista y el catálogo en la URL (`?vista=&catalogo=&campo=`).
- `components/BoxGraph.tsx`: lienzo SVG genérico de cajas con filas y aristas (ER, relaciones y flujo): zoom (botones,
  Ctrl+rueda, `+`/`-`/`0`), desplazamiento arrastrando el fondo o con barras, foco itinerante (una sola parada de tabulación;
  flechas para recorrer, Enter para abrir), marcadores pata de gallo (`one`, `zeroOne`, `many`, `zeroMany`) y flecha.
- `lib/graphLayout.ts` (puro): capas por camino más largo sobre un DAG (los ciclos se rompen por DFS), partición de capas
  de más de N cajas en subcolumnas, nodos virtuales en las columnas intermedias (carriles para las flechas largas), barrido
  de baricentros (8 pasadas) con transposición de vecinos, vecindad a k niveles (`neighborhood`) y acomodo de etiquetas
  (`placeLabels`). `BoxGraph` reparte las puntas que llegan a la misma fila y resalta las relaciones de la caja elegida.
- `lib/svgExport.ts`: clona el SVG, copia los estilos calculados como atributos (sirve en claro y oscuro, sin `style` en
  línea por la CSP) y descarga SVG o PNG (lienzo a 2×, imagen `data:` permitida por `img-src 'self' data:`).
- Vistas: `DatabaseView` (diagrama/tabla, esquema, módulo, texto, foco, niveles, solo llaves, solo deriva, exportar SVG/PNG/
  DDL, panel de detalle), `RelationsView`, `EntityCardView` (con `WorkflowDiagram` de solo lectura sobre `layoutDiagram` de
  la 019), `UsagesView`, `AutomationsView`, `SecurityView` (matriz + árbol con `details`/`summary`).
- `EntityListPage`: «Ver en el explorador» (icono `Network`) para quien administra modelo.

## 5. Pruebas
- Unitarias (`ExplorerTests.cs`): `SqlTypes.Format`; deriva (tabla archivada, sin metadato, faltante, columna archivada,
  huérfana, tipo distinto, sistema); filtro por módulo y externos; `DdlScript` (identidad, DF, PK, FK, índice filtrado,
  INCLUDE, nombres con `]` escapados, sin INSERT); esquema fuera de la lista blanca; usos (fórmula, ruta de tres niveles,
  `SUM`, `UNITCOST`, acción con `target`/`lines`/`capture`, `match`, regla de acceso, reporte, vista, tablero, libro,
  `lookupFilter`, referencia; expresión inválida no rompe; caché por instancia); flujo (modos, ciclo entre dos catálogos y
  a sí mismo, arista que no dispara); seguridad (herencia, alcance, ocultos); navegación (layout y derivado);
  `graphLayout` se prueba en E2E por su efecto (cajas sin traslape).
- E2E `explorador.spec.ts`: un escenario por criterio (`H1 CA1 …`), por la UI, con datos por API; deriva con un campo
  archivado; 403/404 y ruta sin permiso; esquema `meta` solo superusuario; DDL descargado; exportar SVG/PNG; teclado en el
  diagrama y en la tabla; 100+ tablas (se crean por plan) con foco y filtro; axe en cada vista; móvil 390 px sin desborde.
  Visual: base de datos en claro y oscuro.

## 6. STRIDE
| Amenaza | Vector | Mitigación |
|---|---|---|
| **S**uplantación | — | Sin cambios de autenticación: cookie de sesión y CSRF de siempre |
| **T**ampering | Usar el explorador para cambiar el modelo | Solo `GET`; no hay operación de escritura; las ediciones siguen por el Plan de Cambios |
| **R**epudio | — | Sin escrituras que auditar |
| **I**nformación | Un usuario sin *administrar modelo* ve la estructura | Todos los endpoints exigen `CanManageModel` (403); la opción y la ruta dependen de la capacidad |
| **I**nformación | Tablas del sistema (`meta.User`, claves, tokens) | `schema=meta` solo superusuario; solo estructura (nombres y tipos), nunca filas; con `app` solo se nombra la tabla externa |
| **I**nformación | Datos de negocio por el explorador | No se lee ninguna fila de `app` ni conteos (`sys.partitions`); el DDL es solo estructura |
| **I**nformación / inyección | `schema`, `tables`, `app`, `q` en SQL | `schema` se valida contra la lista blanca y a SQL va el valor de la lista como parámetro; `tables`, `app` y `q` filtran en memoria lo que ya devolvió el catálogo; el DDL cita nombres escapando `]` |
| **D**enegación | Catálogos enormes o consultas repetidas | Una ida de seis consultas al catálogo del sistema (metadatos, sin barrer datos); usos en caché por versión del modelo; `q` ≤ 100 caracteres y `tables` ≤ 500 nombres |
| **E**levación | Ver el esquema `meta` sin ser superusuario | Comprobación en el servidor antes de consultar; el selector solo aparece al superusuario |
