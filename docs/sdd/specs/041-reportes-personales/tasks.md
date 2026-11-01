# Tareas 041

## Motor
- [x] T1 `meta.PersonalReport` en `Bootstrap`; acción de bitácora `report.personal` clasificada como de catálogo.
- [x] T2 `PersonalReports`: `PersonalReportDef` → `ReportDef`; `Check` (catálogo, rutas visibles y legibles, filtros,
      `ValidateReports`, tamaño); `Sources` y `Fields`.
- [x] T3 `PersonalReportService`: listar, abrir, alta (tope y nombre con candado), cambio y borrado con versión, bitácora.
- [x] T4 Rutas: `sources`, `explore`, `explore/export`, `my-reports` (CRUD); `definition` y `fixedFilter` en el reporte
      del modelo; inventario regenerado.

## SPA
- [x] T5 API y tipos; `ReportExplorerPage` (diseño, resultado en vivo, guardar, guardar como, borrar, exportar, 409).
- [x] T6 `ReportsPage` («Nuevo reporte», «Mis reportes») y «Explorar» en `ReportPage`; rutas en `App.tsx`.

## Pruebas
- [x] T7 Unitarias `PersonalReportsTests.cs`.
- [x] T8 Integración `PersonalReportsTests.cs` (dueño, versión, concurrencia, revalidar, empresa, credenciales, bitácora).
- [x] T9 E2E `reportes-personales.spec.ts`; canario con `explore` y `explore/export`.

## VERIFY
- [x] T10 Suites completas, lo nuevo con `--veces=3`, revisión adversarial, exploratoria, `verify.md`, riesgos, roadmap,
      commit y push.
