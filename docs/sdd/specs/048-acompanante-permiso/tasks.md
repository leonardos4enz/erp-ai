# Tareas 048 — Acompañante 2: permiso en la conversación y de dónde sale cada campo

- [x] T1 Spec, plan (STRIDE) y tareas; CI de la 047 anotada en su `verify.md`.
- [x] T2 Bootstrap: `ToolUseId`, `Reason` en `meta.CompanionAction`; estado `waiting`.
- [x] T3 Motor: pausa en `proponer_accion` (`FinishAsync('waiting')`), resultado sustituido al cargar, `ResumeAsync` al
  decidir (confirmar, descartar con motivo, mensaje como «no», barrido); detener y borrar al esperar; «activa» incluye esperar.
- [x] T4 `FieldSources` y `filledBy` en `GET /api/model/entities/{name}`; `buscar_en_el_sistema` (pedido del dueño); `como_funciona` con `editable` y `loLlena`; rechazo
  de `proponer_accion` con la procedencia.
- [x] T5 Prompt: creer el resultado del sistema y explicar de dónde sale un campo.
- [x] T6 Pruebas de integración (`CompanionPermisoTests`) contra el código anterior; ajuste de `CompanionTests`; unitarias.
- [x] T7 Panel: tarjeta con «Sí, adelante», «No», «No, y dile qué cambiar»; una por una; compositor y avatar en espera; tipos.
- [x] T8 Vitest y E2E (`acompanante.spec.ts`), captura revisada.
- [x] T9 VERIFY: revisión adversarial y exploratoria, suites, `verify.md`, `riesgos.md`, roadmap; commit y push.
