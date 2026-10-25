# Tareas 023

## H1 — Listas que se fusionan
- [x] T1. `Models.cs`: `Merge` en `SetViewsOp`, `SetReportsOp`, `SetWebhooksOp`, `SetSchedulesOp`.
- [x] T2. `PlanCompiler`: ayudante `MergeByName` en las seis listas; descripciones; topes sobre el resultado.
- [x] T3. `PlanWarnings`: sin «quita» con `merge`; aviso cuando una regla pasa a `warning`.
- [x] T4. `ModelDiff.Extended` + `Desired`/instantáneas/reversión con creados ∪ extendidos.
- [x] T5. `costeo.json` 1.1.0: `setViews` con `merge`; prueba de paquetes (sin listas ajenas reemplazadas).
- [x] T6. Tipos del cliente (`merge` en las operaciones) y prompt del asistente.

## H2 — Reglas de aviso
- [x] T7. `BusinessRule.Severity`, `WorkflowTransition.OnWarning`, `TransitionRequest.AcceptWarnings`; validación en el compilador.
- [x] T8. `RecordService`: `RuleErrors` también recoge avisos, `Validate` los devuelve, `VisibleWarnings`, auditoría `_warnings`.
- [x] T9. Transición: `confirm`/`block`/`allow`, 428 con avisos visibles, automatizaciones.
- [x] T10. `_warnings` en la salida del registro, `warnings` en `/compute` y en la importación (`WarningSink`).
- [x] T11. UI: avisos en el formulario, diálogo de la transición, importación; modelador (pista de reglas y «Si hay avisos»).

## H3 — Rutas de tres niveles
- [x] T12. `PathRef(Head, Rest)`, parser con tope de 4 segmentos; consumidores actualizados.
- [x] T13. Analyzer por saltos; evaluador con `RelatedPath` y `RelatedLoader.Walk`.
- [x] T14. `RecordFilter` (acumulado) con subconsultas anidadas; `ReadsHidden` por saltos.
- [x] T15. Inventario 1.7.0 (pendiente 4 de la 022).

## Cierre
- [x] T16. Unitarias (regresiones verificadas contra el código anterior).
- [x] T17. E2E `valuacion-rutas-avisos.spec.ts` + accesibilidad + visual.
- [x] T18. Suites en verde dos veces; revisión exploratoria (teclado, claro/oscuro, móvil, modelador); `verify.md`.
- [x] T19. Roadmap e índice; commit y push.
