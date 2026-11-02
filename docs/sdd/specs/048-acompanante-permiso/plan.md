# Plan 048 — Diseño técnico

## Piezas
```
 CompanionAgent.LoopAsync
   respuesta con tool_use → corre todas; proponer_accion guarda la acción con ToolUseId
   ¿alguna propuesta guardada? → resultados con «esperando» → FinishAsync('waiting')   (purga, sin lease, companion.finish)
                                                                     │
 la persona decide ────────────────────────────────────────────────┤
   POST /actions/{id}/confirm {hash}  → applied | failed (428: sigue proposed)
   POST /actions/{id}/discard {reason?}
   POST /conversations/{id}/messages {text} (esperando) → descarta todas con el texto
   barrido: expired | unknown
       └─ en la misma transacción: CompanionActions.ResumeAsync → 'waiting' → 'queued' si no queda proposed/applying
                                                                     │
 CompanionDispatcher.ClaimAsync ('queued' → 'running', lease) ──────┘
   LoadAsync: el tool_result de cada ToolUseId = el resultado de su acción (CompanionActions.Outcome)
   → el modelo sigue el mismo turno
```

## 1. Modelo de datos (Bootstrap, idempotente)
```sql
ALTER meta.CompanionAction ADD ToolUseId nvarchar(100) NULL, Reason nvarchar(2000) NULL
CREATE INDEX IX_CompanionAction_Conversation ON meta.CompanionAction (ConversationId, Status)
-- meta.CompanionConversation.Status admite 'waiting' (nvarchar(12), sin CHECK)
```
`ToolUseId` es el `id` del `tool_use` del modelo (Anthropic: `toolu_…`, ≤ 100). `Reason` es el «no» de la persona.

`FieldDef.FilledBy` (solo salida, como `Access`): `FieldSource { kind: "automation" | "transition", entity, entityLabel,
name, label, on?, state?, transition?, action? }`. Lo calcula `FieldSources.For(target, model, canSee)`.

## 2. Contrato de la API
| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `GET /api/model/entities/{name}` | autenticado | — | como hoy; cada campo con `filledBy[]` (solo de catálogos que la persona puede ver; omitido si no hay) |
| `GET /api/companion` | autenticado | — | `activeId` también de una conversación `waiting` |
| `GET /api/companion/conversations/{id}` | autenticado | — | `status` puede ser `waiting`; `canSend` es true al esperar (el mensaje es el «no») |
| `POST /api/companion/conversations` | igual | `{ text, context? }` | 409 también si otra conversación espera respuesta |
| `POST /api/companion/conversations/{id}/messages` | igual | `{ text, context? }` | Esperando: 202 y descarta lo pendiente con el texto como motivo (el turno sigue, `turns` no cambia) · 409 si una propuesta se está aplicando |
| `POST /api/companion/conversations/{id}/cancel` | igual | — | Esperando: 200, `cancelled` y lo pendiente descartado |
| `POST /api/companion/actions/{id}/confirm` | igual | `{ hash, acceptWarnings? }` | igual; al decidirse la última pendiente, la conversación pasa a `queued` |
| `POST /api/companion/actions/{id}/discard` | igual | `{ reason? }` (opcional, ≤ `MaxPromptChars`) | 200 acción con `reason` · 400 motivo largo · 404 · 409 ya decidida |

`CompanionActionDto` gana `reason`. `CompanionSummaryDto.status` y el evento `session` del flujo pueden decir `waiting`; el
flujo termina (`end`, `idle`) al detenerse a esperar, como al responder.

Resultado para el modelo (`tool_result` de la llamada, `CompanionActions.Outcome`):
```
<datos_no_confiables herramienta="proponer_accion">{"accion":12,"resultado":"aplicada","registro":34,"catalogo":"Gato"}</datos_no_confiables>
… "resultado": "rechazada" | "no se aplicó" (con "error") | "caducó" | "sin confirmar si se aplicó" | "detenida"
<pedido>texto de la persona</pedido>         (solo si dio motivo)
```
Auditoría: `companion.finish` con `status: waiting` al detenerse; `companion.action` con `reason` (largo, no el texto);
`companion.message` con `answer: true` cuando el mensaje fue el «no».

## 3. SPA
- `lib/companion.ts`: `avatarState` → `waiting` por el estado de la conversación; `AVATAR_TEXT.waiting` = «Esperando tu
  respuesta»; `askingAction(c)` = la primera pendiente; `askPosition` («Propuesta 1 de 3»).
- `ActionCard`: con `asking`, pregunta «¿Lo hago?» y tres botones: «Sí, adelante» (confirmar), «No» (descartar), «No, y dile
  qué cambiar» (abre un campo de texto con «Enviar»); las demás pendientes muestran «En espera: te pregunto después».
- `CompanionChat`: el compositor, al esperar, dice el aviso y envía el «no»; al decidir, `chat.reconnect()` sigue el turno.
- `types.ts`: `waiting` en `CompanionConversationStatus`, `reason` en `CompanionAction`; `companionApi.discard(id, reason?)`.

## 4. Pruebas
- **Integración** (`tests/Erp.IntegrationTests/CompanionPermisoTests.cs`, nuevo: parte de la clase `CompanionTests`, con su
  host; Gato, Vacuna aplicada y una Clínica que las personas de prueba no ven): H1–H5 y concurrencia/fallas (decidir dos veces, decidir y detener, caída entre aplicar y reanudar,
  caducar). `CompanionTests` (046) se ajusta: las propuestas ahora dejan la conversación esperando.
- **Unitarias** (`tests/Erp.Tests/CompanionCoreTests.cs`): `FieldSources` (update con/sin target, partidas, create, capture,
  rollup, cost, transición con set, catálogo no visible); prompt.
- **Vitest** (`companion.test.ts`): avatar `waiting`, `askingAction`.
- **E2E** (`acompanante.spec.ts`): «Sí, adelante» sigue el turno; «No, y dile qué cambiar»; escribir mientras espera; dos
  propuestas una por una; captura de la tarjeta preguntando.

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas y caminos nuevos | Sin rutas nuevas. Cambian: `discard` acepta `{ reason }` (texto de la persona, ≤ `MaxPromptChars`, viaja al modelo en `<pedido>`); `messages` sobre una conversación que espera es el «no»; `GET /api/model/entities/{name}` gana `filledBy`. Un camino asíncrono nuevo: la reanudación (la cola de siempre, con *lease*). | `CompanionTests › H1_CA2_no_y_dile_que_cambiar_vuelve_al_modelo_como_pedido` · `› H1_CA3_escribir_mientras_espera_es_un_no_con_ese_texto` · `› Entradas_un_motivo_largo_se_rechaza` · `RouteInventoryTests` |
| Caminos alternos | ¿Otro camino a lo mismo? | Decidir: confirmar, descartar, escribir, detener, borrar y el barrido (caducar, sin confirmar). Todos pasan por `CompanionActions.ResumeAsync` en la transacción que cambia la acción, salvo detener y borrar, que no reanudan. Procedencia: la ruta del modelo y el rechazo de proponer usan el mismo `FieldSources` y el mismo filtro de visibilidad. | `› H2_CA3_caducar_reanuda_con_caduco` · `› H1_CA6_detener_mientras_espera_descarta_y_no_vuelve_al_modelo` · `› H3_CA1_la_procedencia_solo_de_catalogos_visibles` · `› H4_CA1_el_rechazo_dice_de_donde_sale` |
| Lectura de datos | ¿`RecordFilter`, `FieldAccess`, empresa? | `filledBy` sale de `ForUser`: solo campos que la persona ve y automatizaciones de catálogos que puede leer o capturar (`CanSee`, la misma regla que `GET /api/model/entities`). El resultado que vuelve al modelo trae el número del registro, no sus valores; leerlo es `ver_registro` con su alcance. La reanudación recarga la identidad y la empresa en cada paso (046). | `› H3_CA1_la_procedencia_solo_de_catalogos_visibles` · `› H1_CA4_aplicada_vuelve_al_modelo_y_el_turno_sigue` · `CompanionTests › H1_CA4_la_identidad_se_recarga_en_cada_paso` |
| Delegación | ¿Alguien obtiene lo que no tiene? | Decidir sigue siendo de la dueña (404 a otra) con sus permisos del momento (046). La reanudación corre con la identidad de la conversación revalidada (sello y empresa): si la persona cambió de contraseña mientras esperaba, el turno se detiene por identidad. Nadie decide por otra persona ni reanuda su conversación. | `› Delegacion_otra_persona_no_decide_ni_reanuda` · `CompanionTests › H4_CA3_otra_persona_no_confirma_y_diez_a_la_vez_ejecutan_una` |
| Concurrencia | Invariantes | Una decisión por acción (bloqueo de la conversación y luego de la acción, el orden de la 046 F-02, ahora también en descartar y en el «no» por mensaje); la reanudación es un `UPDATE … WHERE Status = 'waiting' AND NOT EXISTS (pendientes)` en esa transacción: se encola una vez. Esperar cuenta como activa: nunca dos turnos de la misma persona. Confirmar y detener a la vez: o la acción quedó aplicándose y detener no la toca (termina aplicada, sin reanudar), o detener la descartó y confirmar da 409. | `› H2_CA1_confirmar_y_descartar_a_la_vez_reanudan_una_vez` · `› H2_CA2_decidir_y_detener_a_la_vez` · `› H1_CA6_esperando_cuenta_como_activa` |
| Fallas parciales | Caída entre pasos | Detenerse: cierre, purga y auditoría en una transacción con el *lease* (como responder). Aplicar y reanudar: misma transacción. Caída entre despachar y guardar: el barrido marca `unknown` y reanuda en la misma vuelta (y reanuda toda conversación `waiting` sin pendientes). Caída del trabajador al reanudar: el turno queda `interrupted` (046) y no se repite; la acción ya está guardada. | `› H2_CA3_una_caida_entre_aplicar_y_reanudar_reanuda_sin_repetir` · `› H2_CA3_caducar_reanuda_con_caduco` · `CompanionTests › Fallas_un_turno_sin_trabajador_queda_interrumpido_y_purgado` |
| Límites | Topes | Los de la 046 se aplican en cada tramo (pasos, consultas, tokens, costo de la conversación y del día) y el tramo reanudado tiene sus `MaxMinutes`; la espera no consume trabajador. Motivo ≤ `MaxPromptChars`; propuestas por respuesta ≤ `MaxPendingActions`; una espera caduca a las `ActionHours`. `filledBy`: a lo más automatizaciones × acciones del modelo (20 × 20 por catálogo), calculado solo en la ruta de un catálogo. | `› Entradas_un_motivo_largo_se_rechaza` · `CompanionTests › H5_CA2_tope_de_propuestas_pendientes` · `CompanionTests › H6_CA1_los_topes_del_dia_y_de_la_conversacion` |
| Semántica | Nulos, fechas | El resultado de la acción es su estado final, en palabras fijas (no texto del modelo); un motivo vacío es «no» sin texto (no `<pedido>` vacío). `filledBy` se omite si está vacío. | `› H1_CA2_no_sin_motivo` · `CompanionCoreTests › FieldSources_*` |

`buscar_en_el_sistema` (pedido del dueño en IMPLEMENT): lee `GET /api/model/entities` como la persona (solo catálogos y campos
que ve; de una automatización nombra solo catálogos y campos visibles del destino), ≤ 8 palabras y 25 resultados:
`CompanionTests › H3_CA3_buscar_en_el_sistema_encuentra_lo_que_no_esta_en_pantalla` · [R-048-5](../../riesgos.md#048).

Revisión adversarial (VERIFY): «Detener» durante el cierre en espera gana (`› Regresion_048_F01_detener_mientras_el_turno_se_detiene_a_esperar_gana`);
el motivo cabe hasta `MaxPromptChars` (`› Regresion_048_F02_el_motivo_cabe_hasta_el_maximo_de_un_mensaje`); escribir sin nada
pendiente es un turno nuevo (`› Regresion_048_F04_escribir_sin_nada_pendiente_es_un_mensaje_nuevo`); descartar pide la empresa
(`› Regresion_048_F05_descartar_pide_la_empresa_de_la_propuesta`).

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **E** El modelo (o un registro hostil) hace pasar texto como si fuera la persona en el resultado de la acción | Alta | El resultado lo arma el servidor con palabras fijas dentro de `<datos_no_confiables>` (escapado: un `</datos_no_confiables>` o `<pedido>` del error de una regla no cierra el bloque); solo el motivo de la persona va en `<pedido>`. | `› E_un_error_de_regla_hostil_no_se_hace_pasar_por_la_persona` |
| **T** Una reanudación con un resultado distinto al real (dos decisiones, o el barrido y la confirmación a la vez) | Alta | Una decisión por acción bajo bloqueo; el resultado se lee de la acción al cargar el historial, no se copia. | `› H2_CA1_confirmar_y_descartar_a_la_vez_reanudan_una_vez` · `› H2_CA3_una_caida_entre_aplicar_y_reanudar_reanuda_sin_repetir` |
| **D** Reanudaciones que cuestan sin que la persona esté (caducar a las 24 h vuelve al modelo) | Baja | Topes de la conversación y del día; la identidad se revalida; una reanudación por espera. Aceptado: [R-048-1](../../riesgos.md#048). | [R-048-1](../../riesgos.md#048) |
| **I** `filledBy` revela un catálogo o una automatización que la persona no ve | Media | `CanSee` sobre el catálogo de la automatización (como la lista del modelo); campos ocultos ya no salen de `ForUser`. | `› H3_CA1_la_procedencia_solo_de_catalogos_visibles` |
| **R** Qué decidió la persona y con qué motivo | Baja | `companion.action` con estado y largo del motivo; la acción guarda el motivo hasta la retención. | `› H1_CA2_no_y_dile_que_cambiar_vuelve_al_modelo_como_pedido` |
