# Tareas 007

## Motor
- [x] Modelos: `Layout`, `ViewDef`, `ReportDef`, `DashboardDef` y operaciones `setLayout`, `setViews`, `setReports`, `setDashboard`, `archiveDashboard`
- [x] Esquema: `LayoutJson`, `ViewsJson`, `ReportsJson`, `meta.Dashboard`, `meta.SavedView` (migración idempotente); carga y guardado
- [x] `PlanCompiler`: validación de layouts, vistas, reportes y tableros (y revalidación en la pasada final)
- [x] `RecordService.EvaluateLayout` en `/compute` y `GET /data/{e}/{id}`; obligatorios ocultos por condición no se exigen
- [x] Filtros de listado (`filters`) con seguridad por campo; vistas personales (`/api/views`)
- [x] `ReportService`: resolvedor de rutas, SQL parametrizado, totales, seguridad 006; exportación CSV y XLSX
- [x] Tableros: listado y resultado por usuario
- [x] Unitarias: compilador, resolvedor/SQL de reportes, filtros, condiciones de layout, CSV/XLSX

## Paquetes
- [x] Compras 1.4.0 en `upgrades`
- [x] Inventario 1.2.0 en `upgrades`

## UI
- [x] Tipos y endpoints
- [x] Formulario con secciones, pestañas y visibilidad condicional
- [x] Listado: vistas, columnas, filtros, guardar vista
- [x] Reportes (tabla, gráfica, filtros, exportar) y tableros; sección «Análisis» del menú
- [x] Modelador: JSON de layout, vistas y reportes; tableros en su página; F-03

## Verificación
- [x] E2E por criterio (`interfaz.spec.ts`) y ajuste de pruebas afectadas
- [x] Regresiones corridas contra el código anterior
- [x] `dotnet test` y E2E en verde dos veces
- [x] Actualización real Compras 1.3.0 → 1.4.0 e Inventario 1.1.0 → 1.2.0 en `ErpAi`, revisada en navegador
- [x] `verify.md`, roadmap e índice; revisión de secretos y artefactos; commit y push
