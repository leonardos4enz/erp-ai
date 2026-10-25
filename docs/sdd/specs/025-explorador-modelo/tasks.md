# Tareas 025

## Servidor
- [x] T1 `Explorer/SqlTypes.cs` y `Explorer/DbCatalog.cs`: lista blanca de esquemas y lectura del catálogo de SQL Server en una ida
- [x] T2 `Explorer/PhysicalModel.cs`: liga con catálogos, columnas de sistema, deriva, filtros por módulo y texto, externos
- [x] T3 `Explorer/DdlScript.cs`: script `CREATE TABLE` (identidad, DF, PK, FK, índices filtrados e INCLUDE), nombres escapados
- [x] T4 `Explorer/ModelExplorer.cs`: relaciones, índice de usos (en caché por modelo), flujo con ciclos, navegación y seguridad
- [x] T5 `Endpoints/ExplorerEndpoints.cs`: ocho `GET` con `CanManageModel`, `meta` solo superusuario, ficha con paquetes y tableros
- [x] T6 Pruebas unitarias `ExplorerTests.cs` (§5 del plan)

## SPA
- [x] T7 Tipos y `explorerApi`; ruta `/admin/explorer`, entrada en *Modelo* y miga de pan
- [x] T8 `lib/graphLayout.ts` y `components/BoxGraph.tsx` (zoom, desplazamiento, foco itinerante, pata de gallo)
- [x] T9 `lib/svgExport.ts` (SVG y PNG con estilos calculados)
- [x] T10 Vista Base de datos (diagrama, tabla alternativa, esquema, filtros, foco, deriva, detalle, exportar)
- [x] T11 Vista Relaciones (color por módulo, foco, campos con insignias, lista alternativa)
- [x] T12 Ficha del catálogo (con diagrama del proceso de solo lectura) y ¿Dónde se usa?
- [x] T13 Automatizaciones (diagrama y lista, modos, ciclos) y Seguridad y navegación (matriz y árbol)
- [x] T14 «Ver en el explorador» en la pantalla del catálogo
- [x] T15 Estilos claro/oscuro y móvil

## Verificación
- [x] T16 E2E `explorador.spec.ts` (un escenario por criterio, axe, móvil, 100+ tablas) y capturas visuales revisadas
- [x] T17 Suites en verde dos veces; revisión exploratoria y `verify.md`
- [x] T18 Roadmap (025 explorador, 026 Ventas, 027 CFDI), índice del README, commit y push
