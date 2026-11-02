# Tasks 051 — «Pertenece a» en expresiones y en la seguridad por registro

- [x] T1 · Lenguaje: `X IN Campo` → `CONTAINS`; `ExprAnalyzer` (ruta a campo de varios valores, X compatible, mensaje de
  `MultiValued`); `IExprScope.Members`, `DocScope`, `RelatedLoader.Members`; `ExprEvaluator` (dos valores).
- [x] T2 · Traductor: `RecordFilter` recibe el modelo (`For`, `Build`, `Check`, `HasGrants` sin traducir); `CONTAINS` → `EXISTS`;
  llamadores actualizados (registro, lista, OData, búsqueda, webhooks, importación, auditoría, reportes, tareas, acumulados).
- [x] T3 · Asignaciones: `FieldAssignment.Mode`; validación (transiciones, `update`, `create`; rechazo en `createLines`/`capture`);
  ejecución en `Advance`, `SystemUpdateAsync` y `create` (existencia, equipo, tope).
- [x] T4 · Compras 1.12.0: `Requisicion.Participantes` y su asignación en transiciones; permiso por pertenencia;
  `Departamento.Responsables` editable solo por «Gerente de compras».
- [x] T5 · SPA: autocompletado, constructor de condiciones «contiene», `mode` en asignaciones (proceso y automatizaciones), tipos.
- [x] T6 · Asistente y Studio: el prompt y la referencia del lenguaje describen `IN Campo`, `CONTAINS` y `mode`.
- [x] T7 · Pruebas unitarias (`tests/Erp.Tests/MembershipTests.cs`).
- [x] T8 · Pruebas de integración (`tests/Erp.IntegrationTests/MembershipTests.cs`).
- [x] T9 · E2E (`web/e2e/pertenece-a.spec.ts`) y canario (H2 CA4).
- [x] T10 · VERIFY: corrida completa, lo nuevo `--veces=3`, contra el código anterior, revisión adversarial, revisión
  exploratoria, `verify.md`, riesgos, roadmap (renumerar 052–057, 052 = filtros compuestos), índice; commit y push.
