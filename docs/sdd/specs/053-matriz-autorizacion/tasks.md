# Tareas 053 — Matriz de autorización: núcleo

## Motor y metadatos
- [x] T1. `WorkflowApproval` en `Models.cs` (omitido en nulo) y `ApprovalRules.Validate` en la pasada global del plan (H1 CA2).
- [x] T2. Tablas `meta.Approval*` en `Bootstrap` (perfiles, personas, reglas, corridas, decisiones; índice único filtrado).
- [x] T3. `ApprovalMatrix` (puro): elegir regla, niveles saltados por rango, huella de condiciones (unitarias).
- [x] T4. `RecordService.Approval.cs`: entrar, salir, reiniciar, quién puede, `approvers` con candado; `SaveMultiAsync` salta
      `approvers`; ganchos en `ApplyTransitionAsync`, `UpdateAsync` y `SystemUpdateAsync`.
- [x] T5. `POST /api/data/{e}/{id}/approval` (`DecideAsync`) y `_approval` en el registro.

## Configuración
- [x] T6. `ApprovalConfig`: leer y guardar perfiles y reglas de la empresa actual, validación, versión, propia pertenencia,
      recálculo de `approvers`, bitácora.
- [x] T7. Compuerta de seguridad: tipos `approvalprofile.*` y `approvalrule.*`, despacho al aprobar y quién puede revisar.
- [x] T8. Rutas `GET/POST/PUT /api/admin/approvals*` con su capacidad; `rutas.md` regenerado.

## SPA
- [x] T9. Tipos y API; `ApprovalPanel` en el formulario (diálogos de autorizar y rechazar).
- [x] T10. `ApprovalsPage` (`/admin/approvals`), enlace en administración; razón o captura en `pantallas.test.ts`.

## Pruebas y cierre
- [x] T11. Unitarias `ApprovalTests` (plan, elección, saltos, huella, topes, semántica).
- [x] T12. Integración `ApprovalTests` (flujo, segregación, caminos, visibilidad, concurrencia, fallas parciales, compuerta).
- [x] T13. E2E `autorizaciones.spec.ts` (pantalla, panel, a11y, capturas) y canario si aplica.
- [x] T14. Revisión adversarial (subagente sin contexto), revisión exploratoria, `verify.md`, riesgos, roadmap e índice.
- [x] T15. Corrida completa + lo nuevo `--veces=3`; commit y push.
