# Tareas 046 — Acompañante (motor)

- [x] T1 ADR-0010 y enmienda de la constitución II.8; spec, plan y tareas.
- [x] T2 Bootstrap: `meta.CompanionConversation`, `meta.CompanionMessage`, `meta.CompanionStep`, `meta.CompanionAction`; `meta.AuditLog.Origin`.
- [x] T3 `Audit.WriteAsync` escribe el origen de la petición; `RouteDispatcher.SendAsync` acepta `origin` y lo pone en la petición.
- [x] T4 `Companion/CompanionOptions.cs` (topes, cola, retención; precio de Studio) y `CompanionSignals`.
- [x] T5 `Companion/CompanionTools.cs`: catálogo de lectura, `por_que_no_puedo`, `mis_pendientes`, formas en lenguaje de usuario.
- [x] T6 `Companion/CompanionActions.cs`: proponer (vista previa, huella, topes), confirmar una vez, descartar, caducar, `unknown`.
- [x] T7 `Companion/CompanionPrompt.cs` y `Companion/CompanionAgent.cs` (bucle con lease, purga, topes, identidad y empresa).
- [x] T8 `Companion/CompanionService.cs` (crear, seguir, listar, leer, detener, borrar, estado), `CompanionStream`, `CompanionDispatcher` (cola y barrido).
- [x] T9 `Endpoints/CompanionEndpoints.cs`, registro en `ErpApp`, auditoría; tipos en `web/src/api/types.ts`.
- [x] T10 Pruebas: `CompanionCoreTests`, `CompanionTests`, `rutas.md`.
- [x] T11 VERIFY: revisión adversarial, suites, `verify.md`, `riesgos.md`, roadmap (047 y auditoría 9 adelantada).
