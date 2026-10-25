# Plan 024 — Diseño técnico

## 1. Metadato: módulo de navegación (`AppDef`, `setApp`, `archiveApp`)
```ts
AppDef { name: string /* = group de sus catálogos, 1–60, sin distinguir mayúsculas */, label: string /* ≤100 */,
         icon?: string /* Lucide kebab-case, omitido = "layout-grid" */, description?: string /* ≤300 */,
         order?: number /* 0–9999, omitido = 100 */, isArchived?: boolean /* salida */ }
Operation 'setApp'     { app: AppDef }        // crea o reemplaza la declaración (conserva la capitalización guardada)
Operation 'archiveApp' { name: string }       // la declaración se conserva archivada; sus catálogos quedan en un módulo implícito
```
- `meta.App (Name nvarchar(60) PK, Label, DefinitionJson, IsArchived, UpdatedAt)`, creada por `Bootstrap` (idempotente).
- `PlanCompiler.Compile(..., apps?)` como los tableros: `AppOp` valida (nombre sin espacios al borde ni controles, 1–60;
  etiqueta 1–100; ícono con el patrón de siempre; descripción ≤300; orden 0–9999) y devuelve `CompiledPlan.Apps` y
  `TouchedApps`. `PlanService` hace `MERGE meta.App`. `MetadataStore` las carga con el modelo (misma caché e invalidación).
- `ModelVersion.Base(..., apps, touchedApps)` incluye `a:<nombre>=<huella>`: un `setApp` tampoco pisa el cambio de otro (021).
- El `group` del catálogo no cambia de forma (texto ≤60); el editor del modelador lo llama «Módulo».

## 2. Módulos efectivos y API
```ts
GET /api/model/apps            → AppSummary[]   // del usuario
AppSummary { name, label, icon, description?, order, declared: boolean, entities: string[] }
GET /api/model/apps?all=true   → AppSummary[] + isArchived   // solo modeladores: declarados (también archivados) e implícitos de todo el modelo
GET /api/search?q=texto        → SearchHit[]    // 2–50 caracteres; 400 fuera de rango
SearchHit { entity, entityLabel, id, label }
GET /api/dashboards            → DashboardSummary += { apps: string[] }
```
- `Apps.Effective(model, apps, visible)` (puro, en `Metadata/Apps.cs`): agrupa los catálogos activos no-partida que
  cumplen `visible` por `group ?? "General"` (sin distinguir mayúsculas); el módulo toma la declaración no archivada del
  mismo nombre o una implícita (etiqueta = nombre, ícono `folder`; General: `layout-grid` y descripción fija). Omite los
  módulos sin catálogos. Orden: `order`, etiqueta (General implícito al final).
- Visible para el usuario = `u.IsSuperuser || u.Access(e).Read` (lo mismo que el menú hoy). La lista de tableros calcula
  `apps` con los catálogos de sus tarjetas visibles.
- `RecordService.SearchAsync(q, user)`: catálogos activos, no partida, con `Access.Read`, con un campo `autonumber`
  visible (`FieldAccess.CanSee`); por cada uno `SELECT TOP 5 t.[Id], t.[Folio] … WHERE t.[IsDeleted] = 0 AND <RecordFilter.For>
  AND t.[Folio] LIKE @q ESCAPE '\'` (identificadores de metadatos citados, valor como parámetro con `EscapeLike`), hasta
  20 resultados en total y 30 catálogos como máximo por búsqueda.

## 3. Entornos (009/017) y asistente (011)
- `ModelDiff.Compute(..., desiredApps?, targetApps?)`: `setApp` cuando la declaración deseada (no archivada) no existe, está
  archivada o difiere; con `DiffOptions.ArchiveApps` (reversión), `archiveApp` de lo que el alcance ya no trae.
- `Export`: lleva la declaración del módulo de cada catálogo exportado (si existe). `Scope(p)` agrega `Apps` (nombres de
  `setApp`); `Desired` compila la cadena con las declaraciones y devuelve las del alcance. `ModelSnapshot` agrega `Apps` y
  `AppDefs` (anulables: instantáneas viejas = vacías). `EnvironmentService` pasa las declaraciones en cada paso.
- Asistente: `AssistantContext` lista los módulos declarados; el prompt describe `setApp`/`archiveApp` y pide usar un
  módulo existente como `group` (o declarar uno). `PlanWarnings`: «archivar un módulo» avisa que sus catálogos pierden
  ícono y descripción.
- Paquetes: Compras 1.9.0, Inventario 1.8.0 y Contabilidad 1.2.0 con un `setApp` cada uno.

## 4. SPA
- `AppsContext` (dentro de `AuthProvider`): carga `/api/model/apps` cuando cambian usuario, empresa o modelo; expone
  `apps`, `appOf(entity)`, `current` (de la ruta o el último), `remember(name)`.
- **Rutas:** `/` → `HomePage` (selector: tarjetas de módulos + «Análisis general»); `/m/:app` → `AppHomePage` (documentos,
  catálogos, tableros y reportes del módulo; 404 si no es visible); `/admin` → `AdminHomePage` (menú agrupado en
  tarjetas, `ForbiddenPage` sin capacidad); las demás sin cambios. `/reports?app=` y `/dashboards?app=` filtran.
- **Layout:** el menú lateral tiene dos modos. *Módulo*: botón del módulo actual (abre el selector), «Resumen», secciones
  «Documentos» y «Catálogos», «Análisis» (Tableros/Reportes del módulo, si hay). *Configuración* (`/admin*`): «Volver a
  {módulo}» y los cuatro grupos con sus capacidades. Barra superior: «Buscar» (Ctrl+K), «Módulos» (`/`), empresa, engrane
  (solo con alguna capacidad), tema, usuario. La miga de pan agrega el módulo y «Configuración» enlaza a `/admin`.
- **Paleta (`CommandPalette`)**: `Dialog` con `input role="combobox"` (`aria-expanded`, `aria-controls`,
  `aria-activedescendant`) y `listbox` agrupado; filtro sin acentos ni mayúsculas en cliente; documentos con
  `/api/search` (espera 250 ms, cancela la anterior); región en vivo con el número de resultados. Atajo global Ctrl/⌘+K.
- **Modelador:** pestaña «Módulos» (tabla de módulos efectivos; diálogo con etiqueta, ícono `IconPicker`, descripción y
  orden; archivar) que produce `setApp`/`archiveApp` y abre el `PlanReviewDialog`. El campo «Grupo del menú» pasa a
  «Módulo» con `datalist` de los módulos y, al crear, propone el módulo actual (si no es General).

## 5. Pruebas
- Unitarias (`NavigationTests.cs`): compilador (`setApp` válido, nombre/ícono/orden/etiqueta inválidos, `archiveApp` de
  uno inexistente, conserva la capitalización); `Apps.Effective` (implícitos, General, orden, archivado, sin catálogos
  visibles no aparece); `ModelDiff` (exportar lleva el módulo; diff emite `setApp` solo si difiere; reversión archiva el
  creado y devuelve el cambiado; instantánea vieja sin módulos); `ModelVersion.Base` cambia con el módulo; los paquetes
  de `modules/` declaran el módulo de cada `group` que crean (regresión: un `group` sin declarar en un paquete).
- E2E `navegacion.spec.ts`: H1 (selector con tarjetas en orden y descripción, elegir módulo, menú solo del módulo,
  recordar al recargar, enlace a catálogo de otro módulo cambia el menú, filtro del servidor por rol, 404 de un módulo
  ajeno), H2 (General, módulo en el editor, pestaña Módulos con plan), H3 (engrane solo con capacidad, grupos, volver,
  `/admin` sin permiso), H4 (análisis del módulo y vista general con `?app=`), H5 (Ctrl+K, catálogo, configuración,
  folio, sin resultados de un catálogo sin permiso, solo teclado), H6 (rutas viejas, contraído, 390 px), axe y visual
  (selector y área de configuración, claro y oscuro). Las pruebas existentes que navegaban por el menú plano se ajustan
  al nuevo menú.

## 6. STRIDE
| Amenaza | Vector | Mitigación |
|---|---|---|
| **S**uplantación | — | Sin cambios de autenticación |
| **T**ampering | `setApp` con nombre o ícono maliciosos (XSS, rutas) | Validación en el compilador (longitudes, sin controles, ícono con patrón); React escapa el texto; el nombre va en la URL con `encodeURIComponent` |
| **T**ampering | Un módulo cambiado por paquete sin que se note | Misma vía: plan, vista previa, permiso de modelo, `baseHash` (021), diff y aprobación de Entornos |
| **R**epudio | Quién cambió un módulo | `setApp`/`archiveApp` quedan en `meta.ChangeSet` y la auditoría del modelo como cualquier plan |
| **I**nformación | La lista de módulos revela catálogos sin permiso | `/api/model/apps` filtra en el servidor por permiso de lectura; `?all=true` solo para modeladores |
| **I**nformación | La búsqueda revela registros fuera de alcance o folios ocultos | Solo catálogos con lectura, `RecordFilter.For` (alcance y empresa), folio visible por `FieldAccess`; partidas excluidas |
| **I**nformación / inyección | Texto de búsqueda en SQL | Parámetro con `EscapeLike`; identificadores solo de metadatos, citados |
| **D**enegación | Búsquedas costosas | 2–50 caracteres, 5 por catálogo, 20 en total, 30 catálogos; el cliente espera 250 ms y cancela |
| **E**levación | Ver configuración sin capacidad | El engrane y cada opción dependen de la capacidad; las rutas siguen con `Guard` y los endpoints con su permiso |
