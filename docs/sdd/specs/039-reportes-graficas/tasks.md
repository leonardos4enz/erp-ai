# Tareas 039

## Motor
- [x] T1 `FieldPath` con lista de saltos; `InterfaceRules.Resolve` hasta 2 saltos (referencia o padre); `CanSee` por salto.
- [x] T2 Validación: periodos `week`/`quarter`; medida `calc` (`name`, `expr`, `scale`) con lista blanca; `chart` y
      `chartOptions` (combinaciones, paleta, índices); tarjeta `kpi` sobre un reporte por periodo.
- [x] T3 `ReportService`: uniones encadenadas con `RecordFilter` en cada salto y «(sin acceso)» en cualquiera; semana y
      trimestre; calculadas en filas y total; `chartOptions`, `period` y `comparison` en la respuesta.
- [x] T4 Tableros: `comparison` en tarjetas `kpi`; prompts del asistente y Studio con las opciones nuevas.

## SPA
- [x] T5 `lib/reportChart.ts` (series, pivote, «Otros», relleno de periodos, comparación, paleta) + Vitest.
- [x] T6 `ReportChart.tsx`: siete tipos + indicador, apiladas, leyenda, valores, colores; paleta de 12 en `app.css`.
- [x] T7 `ReportsPage` y `DashboardsPage` con opciones, comparación y medida de la tarjeta.
- [x] T8 Editor del modelador: periodos, rutas a dos saltos, calculadas, sección «Gráfica».

## Pruebas
- [x] T9 Unitarias `Reports039Tests.cs`.
- [x] T10 Integración `Reports039Tests.cs` (seguridad por salto, empresa, calculadas, periodos, comparación, caminos alternos).
- [x] T11 E2E `reportes-graficas.spec.ts` (un escenario por CA, editor, capturas).

## VERIFY
- [ ] T12 Suites completas, lo nuevo con `--veces=3`, revisión adversarial, exploratoria, `verify.md`, riesgos, commit y push.
