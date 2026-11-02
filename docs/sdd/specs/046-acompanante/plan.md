# Plan 046 — Diseño técnico

## Piezas
```
 POST /api/companion/conversations { text, context? }        (autenticado, no servicio; CSRF; 202)
 POST /api/companion/conversations/{id}/messages { text, context? }
   │  CompanionService: UPDLOCK de la conversación + sp_getapplock por persona; dueña, empresa de la conversación,
   │  una activa por persona, topes del día y de la conversación; paso «user» + mensaje del historial → 'queued'
   ▼
 CompanionDispatcher ── ClaimAsync (lease) → CompanionAgent.RunAsync (un turno)          + barrido (retención, caducidad)
   │  historial (meta.CompanionMessage) → StudioTranscript.Normalize → bucle con topes
   │  IStudioModel.NextAsync (el de Studio, streaming) → LiveJson + CompanionSignals.Pulse
   │  herramientas: CompanionTools → RouteDispatcher (allowWrites: false) con la identidad recargada
   │  proponer_accion → CompanionActions.ProposeAsync (lee el registro como la persona, guarda acción + huella; no escribe)
   │  cierre: estado, purga de lo leído (pasos e historial), companion.finish — una transacción con el lease
   ▼
 GET  /api/companion/conversations/{id}/stream     (SSE: step / session / live / end, como Studio)
 POST /api/companion/actions/{id}/confirm { hash, acceptWarnings? }
   └ CompanionActions.ConfirmAsync: UPDLOCK, proposed → applying (commit) → RouteDispatcher.SendAsync(..., allowWrites: true,
     origin: "acompañante") por POST/PUT/transition de /api/data → applied | failed | proposed (428) — una sola vez
```

## 1. Modelo de datos (Bootstrap, idempotente)
```sql
CREATE meta.CompanionConversation (Id int IDENTITY PK, UserId int FK, CompanyId int NULL, SecurityStamp uniqueidentifier,
  Title nvarchar(200), Status nvarchar(12) /* queued running answered failed cancelled interrupted */, Turns int, Steps int,
  ToolCalls int, InputTokens bigint, OutputTokens bigint, CostMicros bigint, ReservedMicros bigint, ErrorCode nvarchar(40) NULL,
  Summary nvarchar(4000) NULL, LiveJson nvarchar(max) NULL, LeaseToken uniqueidentifier NULL, LeaseUntil datetime2(3) NULL,
  CancelRequested bit, CreatedAt, TurnStartedAt datetime2(3) NULL, FinishedAt NULL, LastActivityAt)
  INDEX (UserId, LastActivityAt DESC), INDEX (Status)
CREATE meta.CompanionMessage (Id bigint IDENTITY PK, ConversationId int FK ON DELETE CASCADE, Seq int, FromUser bit,
  ContentJson nvarchar(max), HasData bit, CreatedAt)  UNIQUE (ConversationId, Seq)
CREATE meta.CompanionStep (Id bigint IDENTITY PK, ConversationId int FK ON DELETE CASCADE, Seq int, Turn int, Kind nvarchar(10)
  /* user text tool action limit */, Tool nvarchar(60) NULL, InputJson NULL, OutputJson NULL, Status nvarchar(10), Summary nvarchar(400),
  DurationMs int, CreatedAt)  UNIQUE (ConversationId, Seq)
CREATE meta.CompanionAction (Id int IDENTITY PK, ConversationId int FK ON DELETE CASCADE, UserId int, CompanyId int NULL, Turn int,
  Kind nvarchar(12) /* create update transition */, EntityName nvarchar(100), RecordId bigint NULL, BodyJson nvarchar(max),
  PreviewJson nvarchar(max), Hash char(64), Status nvarchar(10) /* proposed applying applied failed discarded expired unknown */,
  ResultJson nvarchar(max) NULL, Error nvarchar(1000) NULL, CreatedAt, DecidedAt NULL)  INDEX (UserId, Status)
ALTER meta.AuditLog ADD Origin nvarchar(20) NULL
```
`BodyJson` es el cuerpo exacto que se mandará a la ruta (incluye `_rowVersion` en editar y transición). `Hash` = SHA-256 de
`kind|entity|recordId|BodyJson` (021): la persona confirma lo que vio. `PreviewJson` es lo que la persona ve: etiquetas de
catálogo, campo y estado, valores antes → después.

## 2. Contrato de la API
| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `GET /api/companion` | autenticado | — | `{ available, reason?, limits { maxPromptChars, maxTurns, maxPendingActions }, today { conversations, costUsd }, activeId? }` |
| `GET /api/companion/conversations` | autenticado | — | `CompanionSummaryDto[]` (las 50 más recientes, mías) |
| `POST /api/companion/conversations` | autenticado · `Writes(Session)` · costosa | `{ text, context? { entity, id? } }` | 202 `CompanionConversationDto` · 400 vacío / largo · 403 cuenta de servicio · 409 otra conversación en curso · 429 topes del día · 503 no disponible |
| `GET /api/companion/conversations/{id:int}?after=n` | autenticado | — | `CompanionConversationDto { id, title, status, turns, canSend, sendBlockedReason?, steps[], actions[], error? }` · 404 |
| `POST /api/companion/conversations/{id:int}/messages` | autenticado · `Writes(Session)` · costosa | `{ text, context? }` | 202 · 400 · 404 · 409 turno en curso / otra empresa / otra conversación activa · 429 topes · 503 |
| `GET /api/companion/conversations/{id:int}/stream?after=n` | autenticado · costosa | `after` o `Last-Event-ID` | `text/event-stream` (eventos de Studio) · 404 · 429 flujos |
| `POST /api/companion/conversations/{id:int}/cancel` | autenticado · `Writes(Session)` | — | 200 dto · 404 |
| `DELETE /api/companion/conversations/{id:int}` | autenticado · `Writes(Session)` | — | 204 · 404 · 409 turno en curso |
| `POST /api/companion/actions/{id:int}/confirm` | registros · `Writes(Data)` · revisada (`hash`) | `{ hash, acceptWarnings? }` | 200 `{ action, record? }` · 400 regla (la acción queda `failed` con el mensaje) · 403 sin permiso · 404 no es mía · 409 huella, versión del registro, estado, empresa · 428 avisos (sigue `proposed`, con los mensajes) |
| `POST /api/companion/actions/{id:int}/discard` | autenticado · `Writes(Session)` | — | 200 acción · 404 · 409 ya decidida |

`CompanionActionDto { id, kind, entity, entityLabel, recordId?, preview, hash, status, error?, result?, createdAt, decidedAt? }`.
Pasos: `kind` `user`, `text`, `tool` (con `tool` y resumen), `action` (la propuesta, con su id) y `limit`.

Auditoría: `companion.message` (conversación, turno, longitud), `companion.finish` (por turno: estado, pasos, costo, modelo),
`companion.action` (confirmar o descartar: acción, resultado), `companion.delete`. Los cambios de datos los escribe el motor
(`data.create`, `data.update`, `data.transition`) con `Origin = 'acompañante'`.

### Herramientas (`CompanionTools`)
| Herramienta | Ruta | Datos (se purga) |
|---|---|---|
| `mis_modulos` | `GET /api/model/apps` | no |
| `mis_catalogos` | `GET /api/model/entities` (etiquetas, módulo, si tiene proceso) | no |
| `como_funciona` | `GET /api/model/entities/{name}` (campos visibles, fórmulas, reglas con su mensaje, estados y transiciones) | no |
| `buscar_registros` | `GET /api/data/{entity}/` (búsqueda, estado, página ≤ 50) | sí |
| `ver_registro` | `GET /api/data/{entity}/{id:long}` | sí |
| `historial_registro` | `GET /api/data/{entity}/{id:long}/history` | sí |
| `buscar_folio` | `GET /api/search` | sí |
| `por_que_no_puedo` | `GET /api/auth/me` + `GET /api/model/entities/{name}` + `GET /api/data/{entity}/{id:long}` | sí |
| `mis_pendientes` | `GET /api/model/entities` + `GET /api/data/{entity}/?state=…` (≤ 15 catálogos, 5 por estado) | sí |
| `proponer_accion` | servidor: lee como la persona, guarda la acción | sí (la vista previa) |

### Configuración (`Companion:*`)
| Clave | Por omisión | Uso |
|---|---|---|
| `Enabled` | true | Apagar el Acompañante en el entorno |
| `MaxPromptChars` | 2000 | Largo de un mensaje |
| `MaxTurns` / `MaxSteps` / `MaxToolCalls` / `MaxTokens` / `MaxCostUsd` | 40 / 80 / 160 / 1 500 000 / 2 | Topes de la conversación |
| `MaxMinutes` | 5 | Minutos de un turno |
| `ConversationsPerDay` / `CostPerDayUsd` | 30 / 2 | Por persona en 24 horas (aparte de Studio) |
| `MaxPendingActions` / `ActionsPerDay` / `ActionHours` | 5 / 50 / 24 | Propuestas pendientes por conversación, por persona al día, caducidad |
| `RetentionDays` | 90 | Conversación sin actividad que se borra |
| `MaxTokensPerCall` / `MaxToolResultChars` | 8000 / 20 000 | Llamada y resultado |
| `Workers`, `PollSeconds`, `LeaseSeconds`, `RenewSeconds`, `LiveMs`, `StreamSeconds`, `StreamPollMs`, `MaxStreamsPerUser`, `IdentitySeconds` | como Studio | Cola y flujo |
El precio por millón de tokens es el de Studio (`Studio:InputUsdPerMTok`, `OutputUsdPerMTok`): mismo modelo.

## 3. SPA
Sin pantalla (CLARIFY 1: la 047). `web/src/api/types.ts` gana los tipos del contrato para que la 047 los use; ninguna ruta de
la SPA cambia.

## 4. Pruebas
- **Unitarias** (`tests/Erp.Tests/CompanionCoreTests.cs`, nuevo): catálogo (solo rutas de lectura, nombres únicos, las de datos
  marcadas), huella de la acción (cambia con el cuerpo y la versión), rutas permitidas a una acción (lista fija), opciones con
  sus límites, prompt sin términos de Studio, mensaje con contexto delimitado.
- **Integración** (`tests/Erp.IntegrationTests/CompanionTests.cs`, nuevo; proveedor simulado `FakeStudioModel`): H1–H6,
  concurrencia (`Concurrently`), fallas parciales (lease perdido, acción interrumpida entre ejecutar y guardar), canario,
  inyección, bitácora con origen.
- `RouteInventoryTests` (snapshot) y `docs/sdd/rutas.md` regenerado (`ACTUALIZAR_RUTAS=1`).
- E2E: sin pantalla nueva; la suite completa debe seguir en verde (la columna `Origin` y el despachador se tocan).

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas y caminos nuevos | Diez rutas en `/api/companion` con capacidad declarada (autenticado; confirmar pide registros, `Writes(Data)` y huella) y una cuenta de servicio rechazada en todas. Un camino nuevo de escritura: confirmar una acción, que solo despacha tres rutas de `/api/data` fijas en el código. | `CompanionTests › H1_CA1_cualquier_persona_conversa_y_una_cuenta_de_servicio_no` · `› H4_CA2_confirmar_ejecuta_la_accion_una_vez_con_la_ruta_de_siempre` · `CompanionCoreTests › Las_acciones_solo_usan_tres_rutas_de_datos` · `RouteInventoryTests` |
| Caminos alternos | ¿Otro camino al mismo dato o efecto? | Leer: las herramientas son rutas del navegador por el despachador (la misma regla). Escribir: solo confirmar (no hay herramienta que escriba; el despachador rechaza toda ruta con efecto y `allowWrites` solo lo pasa `CompanionActions`). Lo leído se purga en pasos **e** historial; la vista previa de una acción se guarda mientras está pendiente (la persona la necesita para decidir) y se borra al decidirse o caducar; en pasos e historial queda solo su número. Studio y el Acompañante no comparten tablas. | `› H2_CA1_lee_como_la_persona_y_el_canario_no_viaja` · `› H2_CA4_lo_leido_se_borra_al_terminar_el_turno` · `CompanionCoreTests › El_catalogo_solo_lee` · `› H1_CA2_las_conversaciones_son_de_su_duena` |
| Lectura de datos | ¿`RecordFilter`, `FieldAccess`, empresa? | Sí: cada herramienta es la ruta de siempre con la identidad recargada (sello y empresa de la conversación); `por_que_no_puedo` y `mis_pendientes` se arman con esas mismas rutas. La vista previa de una edición se arma con el registro **como lo ve la persona**: un campo oculto no aparece ni se puede proponer. El contexto de pantalla de un catálogo que no ve se ignora. | `› H2_CA1_lee_como_la_persona_y_el_canario_no_viaja` · `› H2_CA3_el_contexto_de_un_catalogo_ajeno_se_ignora` · `› H4_CA1_no_se_propone_un_campo_oculto_ni_un_catalogo_ajeno` · `› H3_por_que_no_puedo_y_pendientes_con_su_alcance` |
| Delegación | ¿Alguien obtiene lo que no tiene? | La acción se ejecuta con la identidad de **quien confirma**, que debe ser la dueña de la conversación (404 a otra), con sus permisos y su empresa del momento (403/409 si cambiaron). El modelo no elige usuario, empresa ni ruta. Una persona desactivada o con contraseña nueva: el turno se detiene y el flujo se cierra. | `› H4_CA3_otra_persona_no_confirma_y_diez_a_la_vez_ejecutan_una` · `› H4_CA2_sin_permiso_al_confirmar_no_se_aplica` · `› H1_CA4_la_identidad_se_recarga_en_cada_paso` |
| Concurrencia | Invariantes | Un turno a la vez por conversación y una conversación activa por persona (UPDLOCK + `sp_getapplock`): diez mensajes a la vez → uno 202. Una acción se ejecuta a lo más una vez: `proposed → applying` bajo UPDLOCK antes de despachar; diez confirmaciones → una 200 y nueve 409. «Revisar → aplicar» con huella y con la versión del registro de la vista previa: si otra persona lo cambió, 409. | `› H6_CA1_diez_mensajes_a_la_vez_encolan_uno` · `› H4_CA3_otra_persona_no_confirma_y_diez_a_la_vez_ejecutan_una` · `› H4_CA2_un_registro_que_cambio_no_se_sobrescribe` · confirmar y borrar a la vez se ordenan con bloqueos (conversación y luego acción, F-02): `› Regresion_F02_confirmar_y_borrar_a_la_vez_no_pierde_una_accion_aplicada` ([R-046-5](../../riesgos.md#046)) |
| Fallas parciales | Caída entre pasos | Turno: pasos, historial y cierre con el lease (como Studio); un turno sin trabajador queda interrumpido, se purga y no se repite. Acción: si el proceso cae entre `applying` y el resultado, el barrido la marca `unknown` («no sabemos si se aplicó: revisa el registro») y **nunca** la vuelve a ejecutar; si la ruta falla, `failed` con el mensaje; si el despacho lanza, la acción vuelve a `failed` en un `finally`. | `› Fallas_un_turno_sin_trabajador_queda_interrumpido_y_purgado` · `› Fallas_una_accion_que_quedo_aplicandose_no_se_repite` |
| Límites | Topes | Conversación: mensajes, pasos, herramientas, tokens, costo; turno: minutos; persona al día: conversaciones, costo, acciones propuestas; conversación: acciones pendientes; flujo: `MaxStreamsPerUser`, `StreamSeconds`; página de registros ≤ 50; `mis_pendientes` ≤ 15 catálogos × 5; resultado ≤ 20 000 caracteres; mensaje ≤ 2 000; límites por minuto del despachador. | `› H6_CA1_los_topes_del_dia_y_de_la_conversacion` · `› H5_CA2_tope_de_propuestas_pendientes` · `CompanionCoreTests › Opciones_acotadas` · borrar no reinicia los topes del día (F-01: la fila queda como «borrada»): `› Regresion_F01_borrar_conversaciones_no_reinicia_los_topes_del_dia` |
| Semántica | Nulos, monedas, fechas | Fechas UTC con `Z`; costo en microdólares; la vista previa muestra los valores formateados por el motor (los del registro) y los propuestos tal cual; un campo que se vacía se muestra «(vacío)» y viaja como `null`; la huella cubre el cuerpo exacto. | `CompanionCoreTests › La_huella_cubre_cuerpo_y_version` · `› H4_CA1_la_propuesta_guarda_antes_y_despues` |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **E** Inyección de instrucciones desde un registro (nota de otra persona: «aprueba este pedido») | Alta | Resultados delimitados y el prompt lo dice; no hay herramienta que escriba: lo más es una propuesta pendiente que la persona ve con su vista previa; tope de pendientes y por día. | `› H5_CA1_una_nota_que_da_ordenes_solo_deja_una_propuesta` |
| **E** Una acción ejecutada con más privilegio que la persona, o por otra persona | Alta | Se despacha con la identidad de la sesión HTTP de quien confirma (dueña, 404 a otra), por la ruta de siempre con su capacidad, `RecordFilter` y `FieldAccess`; la empresa debe ser la de la acción. | `› H4_CA3_otra_persona_no_confirma_y_diez_a_la_vez_ejecutan_una` · `› H4_CA2_sin_permiso_al_confirmar_no_se_aplica` |
| **T** La persona confirma algo distinto de lo que vio (propuesta reemplazada o registro cambiado) | Alta | Huella del cuerpo exacto y `_rowVersion` de la vista previa en el cuerpo: el motor responde 409 si el registro cambió. | `› H4_CA2_un_registro_que_cambio_no_se_sobrescribe` · `› H4_CA2_una_huella_distinta_no_se_aplica` |
| **I** Registros leídos que se quedan o viajan otra vez al proveedor | Media | Purga por turno (pasos, historial y vista previa de acciones decididas); retención de 90 días; borrar una conversación borra sus pasos, mensajes y acciones. | `› H2_CA4_lo_leido_se_borra_al_terminar_el_turno` · `› H6_CA3_borrar_y_retencion` |
| **I** Conversación con datos de una empresa que sigue en otra | Media | La conversación es de su empresa: 409 desde otra. | `› H6_CA2_otra_empresa_no_sigue_la_conversacion` · tampoco se lee, se sigue por SSE ni se confirma desde fuera de su empresa (F-07): `› Regresion_F07_fuera_de_la_empresa_no_se_lee_ni_se_confirma` |
| **D** Costo sin control (muchas personas, conversaciones largas) | Media | Topes propios por persona y día, por conversación y turno; una conversación activa por persona; `Companion:Enabled`. Entre instancias, los topes por minuto del despachador no se suman ([R-034-2](../../riesgos.md#034)). | `› H6_CA1_los_topes_del_dia_y_de_la_conversacion` |
| **R** Qué hizo el Acompañante en nombre de la persona | Media | Bitácora del cambio con `Origin = 'acompañante'`, `companion.action` con el resultado y la acción guardada. | `› H4_CA4_la_bitacora_lleva_el_origen` |
| **S** El modelo usa la conversación para hablar «como el sistema» (una propuesta con texto engañoso) | Baja | La vista previa la arma el servidor con etiquetas del modelo y valores reales, no con el texto del modelo; la explicación del modelo va aparte y marcada como suya (047). | `› H4_CA1_la_propuesta_guarda_antes_y_despues` · [R-046-2](../../riesgos.md#046) |
