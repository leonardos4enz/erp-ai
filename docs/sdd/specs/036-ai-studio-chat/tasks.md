# Tasks 036 — ERP AI Studio como chat: motor conversacional y streaming

## 0. Apertura
- [x] `git rev-list --count origin/main..main` = 0 (`23a1901`).
- [x] Última corrida de CI en `main` (`23a1901`, «Spec 035: ERP AI Studio, pantalla»): verde.
- [x] Fase 1: [análisis de Claude web](analisis-claude-web.md), medido en claude.ai con la sesión del dueño (Chrome), sin
  iniciar sesión.
- [x] CLARIFY 1: se divide (motor 036, pantalla de chat 037). Roadmap renumerado (MCP 038 con la auditoría 7, Ventas 039,
  CFDI 4.0 040) y referencias en ADR-0008, `riesgos.md` y las specs 033 a 035.

## 1. Datos y modelo (H1, H3, H4)
- [x] Bootstrap: `Turns`, `PlanStatus` (con relleno), `LiveJson`, `SandboxToken`, `TurnStartedAt`, `LastActivityAt`;
  `StudioStep.Turn`; `meta.StudioMessage`.
- [x] `StudioTranscript`: serializar, leer, `Normalize` (roles seguidos, usos sin resultado) y `Purge`.
- [x] `IStudioModel.NextAsync(…, onText, …)`; `AnthropicStudioModel` en streaming (`CreateStreaming` + agregador del SDK).

## 2. Turnos (H1, H2, H3)
- [x] `POST …/messages` (`StudioService.SendAsync`): bloqueos, dueña, topes de la conversación y del día, identidad y empresa
  actuales, paso `user`, mensaje del historial, `studio.message`.
- [x] `StudioAgent`: contadores acumulados, minutos por turno, historial cargado y escrito con el lease, sin empujón,
  resultado de «entregar» en el historial, `FinishAsync` con `PlanStatus`.
- [x] Aplicar y descartar por `PlanStatus` y nunca con un turno en curso; lista por última actividad; DTO con `turns`,
  `planStatus`, `canSend`, `turn` por paso.

## 3. Copia aislada y lo leído (H4)
- [x] `SandboxFactory.OpenAsync` con la marca `meta.StudioSandboxMarker`; `CreateAsync` la escribe.
- [x] Se conserva al terminar el turno; `ReleaseSandboxesAsync` (sin uso y una viva por persona); barrido que conserva las
  asignadas; vencimiento la suelta.
- [x] Purga por turno en pasos e historial.

## 4. En vivo (H5)
- [x] `StudioSignals` (aviso, detener en el proceso, tope de flujos).
- [x] `LiveJson` con el lease (≤ `LiveMs`; se borra al guardar el paso); texto parcial conservado al detener.
- [x] `GET …/stream` (`StudioStream`): pasos, `session`, `live`, `end`, `Last-Event-ID`, identidad, duración, latido.

## 5. Pruebas
- [x] `tests/Erp.Tests/StudioChatTests.cs` (9, con F-02 y F-08).
- [x] `tests/Erp.IntegrationTests/StudioTests.Chat.cs` (19, con F-01, F-03 y F-06/F-07 de la revisión adversarial); `StudioProviderTests` con el proveedor en SSE; pruebas de la
  033/035 ajustadas al comportamiento nuevo (sin empujón, la copia se conserva, el pedido es el primer paso).
- [x] `web/e2e/studio.spec.ts`: el proveedor simulado responde en streaming.
- [x] `docs/sdd/rutas.md` regenerado (151 rutas).

## 6. VERIFY
- [x] Suites, `--veces=3 --lento` de `studio.spec.ts`, revisión adversarial, `verify.md`, commit y push.
