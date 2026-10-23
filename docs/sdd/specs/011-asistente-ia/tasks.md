# Tareas 011

## Motor
- [x] `AssistantOptions` (modelo, URL base solo https o loopback, límites, clave opcional del servidor) y registro en `Program.cs`
- [x] `AssistantContext` (solo metadatos, filtros de vistas sin valores, `<`/`>` escapados, resumen por presupuesto, huella)
- [x] `AssistantPrompt` (instrucciones, referencia del Plan de Cambios y del lenguaje de expresiones)
- [x] `ProposalParser` (tamaño, JSON, operaciones conocidas, ≤ 100) y `PlanWarnings` (incluye lo que quitan las operaciones set*)
- [x] `IAssistantModel` + `AnthropicAssistantModel` (SDK oficial, salida estructurada, verificación de la clave, errores sin fugas)
- [x] `AssistantKeys`: clave guardada desde la app, cifrada con Data Protection (`meta.AssistantKey`); la del servidor manda
- [x] Esquema `meta.AssistantRequest`; `AssistantService` (límites, conversación, corrección con errores del compilador, aplicar, descartar, reemplazar, clave)
- [x] Endpoints `/api/assistant/**` (modelador; la clave, superusuario) y auditoría `assistant.*`
- [x] Unitarias: contexto sin datos, parser hostil, advertencias, opciones, formato de la clave

## UI
- [x] Tipos y endpoints
- [x] Configuración › Asistente IA: conectar con la clave, reemplazar/quitar, conversaciones, hilo, propuesta con vista previa, aplicar con confirmación, descartar

## Verificación
- [x] E2E por criterio (`asistente.spec.ts`) con proveedor simulado en 127.0.0.1 (captura la petición enviada)
- [x] Regresiones corridas contra el código anterior
- [x] `dotnet test` y E2E en verde dos veces
- [x] Revisión exploratoria en `ErpAi`
- [x] `verify.md`, ADR-0003, roadmap, índice, constitución/README/CLAUDE.md (MiniMax → Anthropic); revisión de secretos y artefactos; commit y push
