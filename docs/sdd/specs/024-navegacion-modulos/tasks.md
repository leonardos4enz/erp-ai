# Tareas 024

## H1/H2 — Metadato de módulos
- [x] T1. `Models.cs`: `AppDef`, `SetAppOp`, `ArchiveAppOp`; `meta.App` en `Bootstrap`.
- [x] T2. `PlanCompiler`: `AppOp`, `Apps`/`TouchedApps`; `PlanService` (MERGE); `MetadataStore` (carga y caché); `ModelVersion.Base`.
- [x] T3. `Apps.Effective` (implícitos, General, orden); `GET /api/model/apps` (y `?all=true`); `apps` en la lista de tableros.
- [x] T4. `ModelDiff` (Compute, Export, Scope, Desired, instantánea, reversión) y `EnvironmentService`.
- [x] T5. Asistente (contexto, prompt, `PlanWarnings`); paquetes Compras 1.9.0, Inventario 1.8.0, Contabilidad 1.2.0.

## H5 — Búsqueda en el servidor
- [x] T6. `RecordService.SearchAsync` y `GET /api/search`.

## SPA
- [x] T7. Tipos y endpoints; `AppsContext` (módulo actual y último por usuario).
- [x] T8. `HomePage` (selector de módulos), `AppHomePage`, `AdminHomePage`; filtros `?app=` en reportes y tableros; rutas.
- [x] T9. `Layout`: menú por módulo y de configuración, barra superior (Buscar, Módulos, engrane), miga de pan.
- [x] T10. `CommandPalette` (Ctrl+K) accesible.
- [x] T11. Modelador: pestaña Módulos y campo «Módulo» con sugerencias.

## Cierre
- [x] T12. Unitarias (regresiones verificadas contra el código anterior).
- [x] T13. E2E `navegacion.spec.ts` + ajustes de las pruebas que navegaban por el menú + accesibilidad + visual (cada captura revisada).
- [x] T14. Suites en verde dos veces; revisión exploratoria (teclado, aria, claro/oscuro, 390 px, contraído); `verify.md`.
- [x] T15. Roadmap (024 navegación, 025 Ventas, 026 CFDI) e índice; commit y push.
