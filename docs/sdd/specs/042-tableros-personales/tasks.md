# Tareas 042

## Motor
- [x] T1 `meta.PersonalDashboard`, `meta.PersonalReportShare` y `meta.PersonalDashboardShare` en `Bootstrap`; acción
      `dashboard.personal` clasificada.
- [x] T2 `CostlyGate` (corridas a la vez por persona) en `explore`, `explore/export` y el tablero personal.
- [x] T3 `PersonalSharing`: roles que se pueden elegir, condición SQL de reparto revalidada (dueño activo y con el rol).
- [x] T4 Reparto de reportes: `GET /api/my-reports/shared`, `GET /{id}` para quien se le compartió (sin definición si no
      corre; filtros sin etiqueta), `PUT /{id}/share`; `shared` en la lista; roles en la bitácora.
- [x] T5 `PersonalDashboards` + servicio: CRUD con versión, tope y nombre con candado, reparto, correr tarjetas con quien ve.
- [x] T6 Rutas `/api/my-dashboards`; inventario regenerado.

## SPA
- [x] T7 API y tipos; `ShareDialog`.
- [x] T8 `PersonalDashboardPage` (ver, editar, borrar, compartir, 409, 429) y `DashboardsPage` (secciones).
- [x] T9 `ReportsPage` («Compartidos conmigo») y `ReportExplorerPage` (compartir; solo lectura para quien no es dueño).

## Pruebas
- [x] T10 Unitarias `PersonalDashboardsTests.cs`.
- [x] T11 Integración `PersonalDashboardsTests.cs`.
- [x] T12 E2E `tableros-personales.spec.ts`; canario; captura de referencia.

## VERIFY
- [x] T13 Suites completas, lo nuevo con `--veces=3`, revisión adversarial, exploratoria, `verify.md`, riesgos, roadmap,
      commit y push.
