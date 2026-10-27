# Tasks 033 — ERP AI Studio (motor)

- [x] T1 Esquema `meta.StudioSession` y `meta.StudioStep` en `Bootstrap` (idempotente); acciones de auditoría `studio.*`.
- [x] T2 `StudioOptions` (`Studio:*`) y referencia del Plan de Cambios compartida con la 011 (`AssistantPrompt.PlanReference`).
- [x] T3 `RouteDispatcher`: ruta en proceso con la identidad, capacidad de la ruta, solo lectura en el destino real, errores seguros.
- [x] T4 `StudioTools` + `ToolRunner`: catálogo, alcances, datos no confiables, recorte; `GET /api/explorer/routes` y `/risks`.
- [x] T5 `StudioSandbox`: nombre validado, proveedor propio, copia del modelo por `ModelDiff` + plan, borrado y barrido.
- [x] T6 `IStudioModel` + `AnthropicStudioModel` (tool use con el SDK oficial) y `StudioAgent` (bucle, topes, reserva, entrega).
- [x] T7 `StudioService` + `StudioDispatcher` (lease, cancelar, interrumpidas) y `StudioEndpoints` (contrato del plan).
- [x] T8 Unitarias `StudioCoreTests`; regenerar `docs/sdd/rutas.md`.
- [x] T9 Integración `StudioTests` con proveedor simulado (H1–H5, concurrencia, fallas parciales, canario).
- [x] T10 VERIFY: corrida completa, `--veces=3` de lo nuevo, revisión adversarial, `verify.md`, riesgos, roadmap, commit y push.
