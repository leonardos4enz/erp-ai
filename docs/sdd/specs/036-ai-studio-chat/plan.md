# Plan 036 — Diseño técnico

## Piezas
```
 POST /api/studio/sessions/{id}/messages { text }          (modelador; CSRF; 202)
   │  StudioService.SendAsync: UPDLOCK de la sesión + sp_getapplock por persona; dueño, no activa, sin otra activa,
   │  topes de la conversación y del día; paso «user» + mensaje del historial; identidad y empresa actuales → 'queued'
   ▼
 StudioDispatcher ── ClaimAsync (lease) → StudioAgent.RunAsync (un turno)
   │  carga meta.StudioMessage → StudioTranscript.Normalize → bucle (topes acumulados, minutos del turno)
   │  IStudioModel.NextAsync(…, onText) ── AnthropicStudioModel: CreateStreaming + agregador del SDK
   │      onText → LiveJson (≤ cada Studio:LiveMs, con el lease) + StudioSignals.Pulse
   │  cada respuesta y cada lote de resultados → meta.StudioMessage (con el lease); cada paso → meta.StudioStep (borra LiveJson)
   │  copia aislada: SandboxFactory.OpenAsync (marca) o CreateAsync; se conserva al terminar el turno
   │  cierre del turno: estado, PlanStatus, purga de «datos» (pasos e historial), studio.finish — una transacción
   ▼
 GET /api/studio/sessions/{id}/stream?after=n    (modelador; SSE)
   └ StudioStream: lee la sesión (dueño) y los pasos > after; despierta con StudioSignals o cada StreamPollMs;
     revisa la identidad cada IdentitySeconds; eventos step / session / live / end; tope por persona y duración
```

## 1. Modelo de datos (Bootstrap, idempotente)
```sql
ALTER meta.StudioSession ADD Turns int NOT NULL DEFAULT 1, PlanStatus nvarchar(10) NULL /* proposed applied discarded */,
  LiveJson nvarchar(max) NULL, SandboxToken uniqueidentifier NULL, TurnStartedAt datetime2(3) NULL,
  LastActivityAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
  -- backfill: PlanStatus = Status donde Status IN ('proposed','applied','discarded') y PlanJson no es nulo
ALTER meta.StudioStep ADD Turn int NOT NULL DEFAULT 1          -- Kind admite 'user'
CREATE meta.StudioMessage (Id bigint IDENTITY PK, SessionId int FK, Seq int, FromUser bit, ContentJson nvarchar(max),
  HasData bit NOT NULL DEFAULT 0, CreatedAt)  UNIQUE (SessionId, Seq)
-- en la copia aislada: meta.StudioSandboxMarker (Token uniqueidentifier)
```
`ContentJson` = los bloques de `StudioMessage` (`text`, `tool_use` con id/nombre/entrada, `tool_result` con id, nombre de la
herramienta del catálogo, contenido delimitado y error, `thinking` con firma, `redacted_thinking`). `HasData` marca un lote de
resultados con alguna herramienta de `datos` (lo que se purga al cerrar el turno).

## 2. Contrato de la API
| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `POST /api/studio/sessions/{id:int}/messages` | modelador · `Writes(Session)` · costosa (límite propio) | `{ text }` | 202 `StudioSessionDto` (sin compilar) · 400 vacío o > `maxPromptChars` · 404 no es mía · 409 turno en curso / otra conversación en curso / sesión anterior a la 036 · 429 tope de mensajes, pasos, herramientas, tokens o costo de la conversación, o costo del día · 503 sin clave |
| `GET /api/studio/sessions/{id:int}/stream?after=n` | modelador · costosa (límite propio) | `after` o `Last-Event-ID` | `text/event-stream` (abajo) · 404 no es mía · 429 más de `MaxStreamsPerUser` flujos |
| `POST /api/studio/sessions` | sin cambios de contrato | — | además escribe el paso `user` (turno 1) y el primer mensaje del historial |
| `POST …/apply`, `…/discard` | sin cambios de contrato | — | se rigen por `planStatus = proposed` y turno no activo (409 «el agente está trabajando…») |
| `GET /api/studio`, `…/sessions`, `…/sessions/{id}` | sin cambios de contrato | — | campos nuevos abajo; la lista se ordena por última actividad |

Campos nuevos: `StudioSessionDto { turns, planStatus?, canSend, sendBlockedReason? }`, `StudioStepDto { turn }` (y `kind`
admite `user`), `StudioSummaryDto { turns, updatedAt }`, `limits { maxTurns }`.

### Eventos SSE
```
id: <seq>        event: step     data: StudioStepDto              (un paso guardado, en orden)
                 event: session  data: { status, turns, steps, toolCalls, inputTokens, outputTokens, costUsd, planStatus?, error? }
                 event: live     data: { kind: waiting|text|tool|none, text?, tool?, input? }
                 event: end      data: { reason: idle|timeout|identity|gone }
: ping   (cada 15 s)
```
`end` con `idle` = el turno terminó y ya se mandaron todos sus pasos (la pantalla pide la lectura completa, que compila la
propuesta); `timeout` = venció `StreamSeconds` (se reconecta con `Last-Event-ID`).

Auditoría: `studio.message` (sesión, turno, longitud del texto; nunca el texto). `studio.finish` pasa a ser por turno (con
`turn`).

### Configuración nueva (`Studio:*`)
| Clave | Por omisión | Uso |
|---|---|---|
| `MaxTurns` | 20 | Mensajes de la persona por conversación |
| `SandboxIdleMinutes` | 60 | Copia aislada sin uso que se borra |
| `LiveMs` | 250 | Escritura del estado en vivo |
| `StreamSeconds` | 300 | Duración de un flujo SSE |
| `StreamPollMs` | 1000 | Relectura del flujo sin señal (otra instancia) |
| `MaxStreamsPerUser` | 3 | Flujos SSE a la vez por persona (por instancia) |
| `IdentitySeconds` | 5 | Revisión de la identidad en el flujo |

`MaxSteps`, `MaxToolCalls`, `MaxTokens` y `MaxCostUsd` pasan a ser de la conversación; `MaxMinutes`, del turno.

## 3. SPA
Sin pantalla nueva (CLARIFY 1: la 037). La pantalla de la 035 sigue funcionando: el paso `user` se ve como un paso más y la
propuesta, como antes. El proveedor simulado de la suite E2E responde en *streaming* (`stream: true`).

## 4. Pruebas
- **Unitarias** (`tests/Erp.Tests/StudioChatTests.cs`, nuevo): `StudioTranscript.Normalize` (une roles seguidos, cierra usos
  sin resultado), `StudioTranscript.Purge` (reemplaza solo los resultados de `datos`), serialización ida y vuelta de bloques,
  formato SSE.
- **Integración** (`tests/Erp.IntegrationTests/StudioTests.Chat.cs`, nuevo; proveedor simulado): H1–H5, concurrencia
  (`Concurrently`), fallas parciales (lease, interrumpida), flujo SSE leído con `HttpClient` en vivo; `StudioProviderTests`
  pasa a responder SSE como la API real.
- **E2E**: `studio.spec.ts` (035) sigue en verde con el proveedor simulado en *streaming*. Sin pantalla nueva.
- `docs/sdd/rutas.md` regenerado (`ACTUALIZAR_RUTAS=1`).

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas y caminos nuevos | Dos rutas con capacidad de modelador declarada: `…/messages` (escribe la sesión, CSRF) y `…/stream` (GET, SSE). Ningún camino nuevo al modelo real ni a datos: el turno usa las mismas herramientas y el mismo despachador. | `RouteInventoryTests` (snapshot) · `StudioTests › Chat_H1_CA3_un_seguimiento_se_rechaza_con_su_motivo` · `› Chat_H5_CA3_el_flujo_es_solo_de_la_duena` |
| Caminos alternos | ¿Otro camino al mismo dato o efecto? | Leer pasos: `GET …?after=n` y el flujo usan la misma consulta con `UserId`; aplicar: solo `…/apply` con huella (también desde el chat); el historial del modelo no se expone por ninguna ruta (solo pasos). Lo leído se purga en pasos **e** historial. | `› Chat_H5_CA1_el_flujo_manda_pasos_en_vivo_y_termina_con_el_turno` · `› Chat_H4_CA3_lo_leido_se_borra_al_terminar_cada_turno` |
| Lectura de datos | ¿`RecordFilter`, `FieldAccess`, empresa? | Igual que la 033 (las herramientas son las rutas). Nuevo: el historial guarda resultados de `datos` hasta el fin del turno y luego un aviso; el canario no viaja al proveedor en el turno siguiente. | `› Chat_H4_CA3_lo_leido_se_borra_al_terminar_cada_turno` (canario) |
| Delegación | ¿Alguien obtiene lo que no tiene? | Cada mensaje toma la identidad (sello) y la empresa actuales de quien lo manda, que es la dueña; el agente se recarga en cada paso como antes. El flujo revisa el sello cada `IdentitySeconds`. Otra persona: 404 en mensajes, flujo, aplicar. | `› Chat_H1_CA4_cada_mensaje_toma_la_identidad_actual` · `› Chat_H5_CA3_…` (desactivada → `end identity`) |
| Concurrencia | Invariantes | Un turno a la vez por conversación y una conversación activa por persona (UPDLOCK + `sp_getapplock`); diez mensajes a la vez → uno 202 y nueve 409. Aplicar contra un turno que corre: 409 (mismo bloqueo de fila). Huella: una propuesta nueva invalida la revisión de la anterior. Secuencias de pasos y mensajes: las escribe solo el dueño del lease o la API con la sesión no activa y bloqueada. | `› Chat_H1_CA3_diez_mensajes_a_la_vez_encolan_uno` · `› Chat_H3_la_propuesta_se_refina_en_la_conversacion_y_se_aplica_con_su_huella` · `› Chat_H3_CA2_no_se_aplica_ni_se_descarta_mientras_un_turno_corre` |
| Fallas parciales | Caída entre pasos | El historial y los pasos se escriben con el lease; un turno interrumpido deja usos sin resultado y `Normalize` los cierra en el siguiente. El cierre (estado, PlanStatus, purga, auditoría) es una transacción. La copia aislada se marca al crearla; si su base no está o la marca no coincide, se crea otra. | `› Chat_Fallas_un_turno_interrumpido_se_puede_continuar` · `› Chat_Fallas_un_turno_interrumpido_se_puede_continuar` (sin lease no escribe historial) · `› Chat_H4_CA1_una_base_ajena_con_el_mismo_nombre_no_se_reutiliza` · `StudioChatTests › Normalize_cierra_los_usos_de_herramienta_sin_resultado` |
| Límites | Topes | Conversación: pasos, herramientas, tokens, costo y mensajes (`MaxTurns`) acumulados, comprobados al aceptar el mensaje y antes de cada llamada; turno: minutos. Flujo: `MaxStreamsPerUser`, `StreamSeconds`, lotes de 200 pasos, `LiveJson` ≤ 8 000 caracteres. Copias: una viva por persona y caducidad. | `› Chat_H2_CA1_los_topes_suman_todos_los_turnos` · `› Chat_H2_CA2_un_mensaje_que_no_cabe_se_rechaza_antes_de_encolar` · `› F01_seguir_conversaciones_de_dias_anteriores_no_rodea_el_costo_del_dia` · `› Chat_H5_CA3_…` (429) · `› Chat_H4_CA2_una_persona_tiene_una_copia_viva` |
| Semántica | Nulos, monedas, fechas | Fechas UTC con `Z` (035); costo acumulado en microdólares; una respuesta sin plan no toca `PlanJson` ni `PlanStatus`; `turn` empieza en 1 y las sesiones anteriores son turno 1. | `› Chat_H3_la_propuesta_se_refina_en_la_conversacion_y_se_aplica_con_su_huella` |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **I** El flujo SSE de otra persona (adivinar ids) | Alta | Misma consulta con `UserId` antes de abrir y en cada lectura; 404 sin abrir el flujo. | `› Chat_H5_CA3_el_flujo_es_solo_de_la_duena` |
| **E** Una revisión vieja aplica una propuesta nueva | Alta | Huella sobre el `PlanJson` vigente (021); aplicar bloqueado mientras corre un turno. | `› Chat_H3_CA2_…` (ambas) |
| **I** Registros leídos que se quedan en el historial y viajan otra vez | Media | Purga por turno en pasos e historial, en la transacción del cierre y en la interrupción. | `› Chat_H4_CA3_lo_leido_se_borra_al_terminar_cada_turno` · `StudioChatTests › F02_un_registro_con_la_frase_del_aviso_tambien_se_purga` |
| **D** Conexiones SSE abiertas sin fin | Media | Tope por persona e instancia, duración máxima, latido; el limitador global cuenta cada conexión. Entre instancias el tope no se suma. | `› Chat_H5_CA3_…` · [R-036-1](../../riesgos.md#036) |
| **D** Bases aisladas que se acumulan con conversaciones abiertas | Media | Una viva por persona, caducidad, barrido que conserva solo las asignadas a una sesión. | `› Chat_H4_CA2_una_persona_tiene_una_copia_viva` · `› H2_CA3_cancelar_detiene_la_sesion_y_la_base_aislada_se_borra_sin_uso` · `› F03_soltar_una_copia_no_borra_la_de_otro_turno_con_el_mismo_nombre` |
| **T** El texto de un mensaje cierra el bloque `<pedido>` o el historial se manipula | Media | Los mensajes van como bloques de texto del rol `user`, nunca concatenados con resultados; el historial solo lo escribe el servidor. El texto de la persona es suyo (no es dato de terceros). | `StudioChatTests › Normalize_cierra_los_usos_de_herramienta_sin_resultado` · [R-036-2](../../riesgos.md#036) |
| **R** Qué pidió la persona en cada turno | Baja | Paso `user` en el registro; `studio.message` en la bitácora (sin el texto). | `› Chat_H1_CA1_CA2_un_seguimiento_recibe_todo_el_historial` |
