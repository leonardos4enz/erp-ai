# Plan 049 — Diseño técnico

## 1. Modelo de datos
Sin tablas ni columnas nuevas. Cambios:

| Dónde | Cambio |
|---|---|
| `meta.CompanionConversation.ErrorCode` | Nuevo valor `disabled` (cerrada por el interruptor) y `too_long` (el proveedor rechazó la entrada) |
| `meta.CompanionAction.Error` | Una propuesta descartada sin la persona dice por qué: «La persona detuvo la conversación.» (ya existía), «El turno terminó antes de preguntarle a la persona (…)» o «El Acompañante se apagó en este entorno.» |
| `CompanionOptions.MaxInputTokens`, `StudioOptions.MaxInputTokens` | Nuevas: 180 000 por omisión, de 1 000 a 2 000 000 (`Companion:MaxInputTokens`, `Studio:MaxInputTokens`) |
| `StudioOptions.EstimateInput(chars, lastInput, lastChars)` | Nueva, compartida: entrada medida del turno + caracteres nuevos / 2; sin medición, caracteres / 3 |
| `AnthropicAssistantModel.TooLong(message)` | Nueva: reconoce el 400 «prompt is too long» → código `too_long` |
| `DerivedAccess.NamesHidden(e, expresión, modelo, persona)` | Nueva (O-02): ¿la expresión nombra un campo que no ve, o un catálogo que no lee al otro lado de una referencia? Ilegible = oculta |
| `ErpHost.RestartAsync` (pruebas) | Nueva: detiene la instancia principal y arranca otra con otras opciones sobre la misma base |

## 2. Contrato de la API
**Ninguna ruta nueva** (siguen las de [`rutas.md`](../../rutas.md)). Cambios de comportamiento:

| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `POST /api/companion/actions/{id}/confirm`, `…/discard` | sesión de la persona | con `Companion:Enabled=false` | **503** «El Acompañante está apagado en este entorno.» (antes de leer o escribir nada) |
| `GET /api/companion/conversations/{id}` | sesión | conversación cerrada por el interruptor | `status: "failed"`, `error`: «El Acompañante se apagó en este entorno: la conversación se detuvo y lo que proponía se descartó.» |
| `GET /api/companion/conversations/{id}` | sesión | turno detenido por `MaxInputTokens` o por el 400 del proveedor | `status: "failed"`, `error`: «Lo que el modelo tendría que leer de una vez ya no cabe: pide algo más acotado o empieza una conversación nueva.» (Studio: igual en `GET /api/studio/sessions/{id}`) |
| `GET /api/companion/conversations/{id}` | sesión | turno que salió a media respuesta tras proponer | Sus acciones `proposed` vienen `discarded` con `error`; confirmarlas da 409 «Esta propuesta se descartó.» |
| `POST /api/companion/conversations/{id}/cancel`, `DELETE /api/companion/conversations/{id}` | sesión | fuera de la empresa de la conversación | **404** «La conversación no existe.» si ya no está en ella; **409** «Esta conversación es de otra empresa: …» si está pero trabaja en otra |
| `GET /api/model/entities/{name}` | autenticada | `filledBy` | Sin las automatizaciones que actúan sobre partidas cuyo campo `lines` la persona no ve (salvo quien modela) |
| `GET /api/model/entities/{partida}`, `GET /api/model/entities?includeChildren=true` | autenticada | partida cuyo campo `lines` no ve (ni con permiso de captura) | **403** / se omite de la lista (revisión adversarial F-02) |
| `GET /api/model/entities`, `/entities/{name}` | autenticada, quien no modela | regla, condición de transición o valor por omisión que nombra un campo oculto (o de un catálogo que no lee) | La regla llega sin `assert`, `when` ni el `field` oculto (con su nombre y mensaje); la transición sin `when` (con `whenMessage`) ni campos ocultos en `requiredFields` y `set`; el campo sin `defaultValue`. Las automatizaciones llegan sin las acciones sobre partidas ocultas (F-01) y todas sus acciones sin expresiones (`when`, `filter`, `sum`, valores de `set`…) (O-02, H8) |
| `POST /api/companion/actions/{id}/confirm` | sesión | propuesta de otra empresa, también caducada | **409** «La propuesta es de otra empresa…» antes de caducarla o reanudar (F-03) |
| `POST /api/companion/actions/{id}/confirm` | sesión | propuesta de una conversación que ya no corre ni espera | **409** «Esta propuesta se descartó.»; se descarta sin aplicarse (F-04) |

Trabajador (`CompanionDispatcher`) con `Enabled=false`: no reclama; cierra `queued` y `waiting` como `failed`/`disabled` (una
transacción por conversación: conversación, propuestas, bitácora `companion.finish`); el barrido caduca y pasa a «sin
confirmar» pero no reanuda. `FinishAsync` no reanuda apagado. `LoopAsync` falla con `disabled` antes de pedir la clave.

## 3. SPA
Sin cambios: los mensajes nuevos llegan en `error` de la conversación (el panel ya los muestra) y en el 503/409 de la acción
(el aviso `role="alert"` que ya existe).

## 4. Pruebas
- **Integración nueva:** `CompanionApagadoTests` (H1: reinicia la API apagada); `CompanionTests.Hardening049.cs` (H2 CA1–CA4,
  H3 CA1–CA2, H4, H5, H6) con catálogos de nómina y una persona más en el anfitrión; en `McpTests`, H7 (O-05).
- **Unitarias nuevas:** `Hardening8Tests` (estimador, `MaxInputTokens` acotado, `TooLong` y los mensajes).
- **E2E:** ninguno nuevo ni modificado (sin cambios de pantalla).
- **Contra el código anterior:** `npm --prefix web run it:regresion -- a1341b4 H049`: las 9 de los defectos y observaciones
  fallan; la de O-05 pasa (no había interbloqueo: queda como guardia). `Hardening8Tests` no compila contra `a1341b4` (usa
  `TooLong` y `MaxInputTokens`, que no existían).

## 5. STRIDE
**Las 8 filas son obligatorias.**

| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones o caminos agrega o cambia? ¿Qué capacidad pide cada una? | Ninguna ruta. Cambian confirmar y descartar (503 apagado), detener y borrar (empresa), la ruta del modelo (`filledBy`), el trabajador del Acompañante (no reclama ni reanuda apagado; cierra lo pendiente) y los bucles de Studio y del Acompañante (tope de entrada). Las capacidades no cambian: sesión de la persona en `/api/companion` y autenticada en `/api/model` | `CompanionApagadoTests › H049_H1_apagado_no_reclama_no_reanuda_y_decidir_responde_503`; `CompanionTests › H049_H6_CA1_detener_y_borrar_piden_la_empresa_de_la_conversacion` |
| Caminos alternos | ¿Todos los caminos al mismo efecto aplican la misma regla? | Apagado: los cinco caminos que llaman al proveedor o escriben (empezar, enviar/responder, confirmar, descartar, reanudar por barrido o cierre) y el reclamo de la cola miran el interruptor; detener y borrar no llaman al proveedor y siguen. Descarte de propuestas: los tres cierres sin la persona (`FinishAsync` no esperando, `ExpireAsync`, `CloseDisabledAsync`) usan `DiscardPendingAsync`; detener esperando, el mismo. Empresa: leer, seguir, enviar, confirmar, descartar, detener y borrar. Partidas ocultas: registro, reportes, OData, bitácora (045) y ahora procedencia, búsqueda, automatizaciones y definición de la partida en la ruta del modelo (F-01, F-02). Expresiones sobre campos ocultos (O-02): la ruta del modelo es la única fuente de `como_funciona`, `buscar_en_el_sistema` y la SPA, así que basta con quitarlas ahí | `CompanionApagadoTests › H049_H1…`; `CompanionTests › H049_H2_CA1…`, `› H049_H2_CA2…`, `› H049_H2_CA3…`, `› H049_H5_CA1…`, `› H049_H5_CA2_la_ruta_del_modelo_no_entrega_lo_de_partidas_ocultas`, `› H049_H6_CA1…`, `› H049_H8_CA1_una_regla_o_condicion_sobre_un_campo_oculto_llega_sin_su_expresion`, `› Regresion_049_F03…`, `› Regresion_049_F04…` |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | La procedencia y la búsqueda usan `FieldAccess.SeesLines` (o ver el campo de partidas con permiso de captura) para el ámbito de cada acción; detener y borrar piden la empresa como `GetAsync`. La ruta del modelo quita, para quien no modela, las expresiones que nombran lo oculto (`NamesHidden`, la misma lectura de rutas que la 029 usa con las fórmulas). El resultado de una propuesta descartada que vuelve al modelo lo arma el servidor con palabras fijas (sin valores del registro) | `CompanionTests › H049_H5_CA1_la_procedencia_y_la_busqueda_no_nombran_partidas_ocultas`; `› H049_H2_CA1…` (texto del resultado) |
| Delegación | ¿Alguien puede otorgar, aprobar o heredar algo que no tiene? | La propuesta de un turno cerrado ya no se puede confirmar: nadie aprueba algo que nunca se le preguntó. Reanudar con el sello de quien decide solo cambia de quién es la identidad revalidada si es la misma dueña (la acción exige `UserId`); el turno sigue revalidando identidad y empresa en cada paso | `CompanionTests › H049_H4_CA1_tras_cambiar_la_contrasena_decidir_reanuda_con_la_sesion_nueva`; `› H049_H2_CA1…` (409 al confirmar) |
| Concurrencia | ¿Qué invariante entre filas protege? ¿10 a la vez? | Mismo orden de bloqueos que la 048 (conversación y luego acción) en los cierres nuevos: `FinishAsync` y `ExpireAsync` actualizan la conversación antes de descartar; `CloseDisabledAsync` igual, una por una y con `WHERE Status IN ('queued','waiting')` para no pisar un turno que otra instancia acaba de reclamar. Confirmar y cerrar a la vez: o confirmar ya pasó la acción a `applying` (sigue y guarda su resultado; la conversación ya no está esperando y no se reanuda) o el cierre la descartó y confirmar da 409. MCP: registro y renovaciones a la vez no se interbloquean | `McpTests › H049_O05_registro_dinamico_y_renovaciones_a_la_vez_no_se_interbloquean`; `CompanionTests › H049_H2_CA2…` (detener durante una consulta retenida con `HoldAsync`); confirmar contra cerrar a la vez: [R-049-3](../../riesgos.md#049) |
| Fallas parciales | ¿Qué pasa si el proceso cae entre dos pasos? | Descartar va en la transacción del cierre (no hay propuesta confirmable de una conversación cerrada). `ExpireAsync` descarta en la suya. `CloseDisabledAsync` falla por conversación y se reintenta en la vuelta siguiente. Una acción que ya se aplicaba al apagarse termina y guarda su resultado; el barrido la pasa a «sin confirmar» si el proceso cae, sin reanudar | `CompanionTests › H049_H2_CA3_un_turno_sin_trabajador_descarta_lo_que_propuso`; `CompanionApagadoTests › H049_H1…` |
| Límites | Tamaño, paginación, topes en memoria, tasa | `MaxInputTokens` por llamada (180 000, acotado) además de `MaxTokens` acumulado; el estimador no es el peor caso: un texto muy denso (CJK, símbolos) puede pasar el estimado y llegar al 400, que ahora se dice con su mensaje ([R-049-4](../../riesgos.md#049)) | `CompanionTests › H049_H3_CA1_una_llamada_que_no_cabe_se_detiene_antes_del_proveedor`, `› H049_H3_CA2…`; `Hardening8Tests › H3_CA1…` |
| Semántica | Nulos, vacíos, monedas, fechas, redondeo | No aplica a valores de negocio. Una conversación sin empresa (`CompanyId` nulo) se detiene y borra como antes (`InCompany` la acepta y la empresa actual también es nula). El sello nulo al reanudar sin persona conserva el guardado (`ISNULL`) | `CompanionTests › H049_H4_CA1…`; las de la 048 sobre caducar y reanudar siguen en verde |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** | Un turno reanudado con un sello viejo (falla en lo seguro) | Reanudar con el sello de quien decide | `CompanionTests › H049_H4_CA1…` |
| **T** | Confirmar una propuesta que nunca se le presentó a la persona como pregunta, o con el Acompañante apagado | Descartar en todo cierre que no espera; 503 apagado | `CompanionTests › H049_H2_CA1…`, `› H049_H2_CA2…`, `› H049_H2_CA3…`; `CompanionApagadoTests › H049_H1…` |
| **R** | Cierres sin rastro | `companion.finish` con `disabled`; el descarte deja `Error` en la acción | `CompanionApagadoTests › H049_H1…` |
| **I** | **Media:** con el Acompañante apagado por un incidente, el historial seguía viajando al proveedor; etiquetas de automatizaciones sobre partidas ocultas; nombre y umbral de un campo oculto en reglas y condiciones | No reclamar, no reanudar y cerrar; `SeesLines` en procedencia, búsqueda y ruta del modelo; `NamesHidden` en `ForUser` | `CompanionApagadoTests › H049_H1…`; `CompanionTests › H049_H5_CA1…`, `› H049_H5_CA2…`, `› H049_H8_CA1…` |
| **D** | Turnos perdidos por un 400 genérico; costo que sigue corriendo apagado | Tope de entrada con mensaje; nada se llama apagado | `CompanionTests › H049_H3_CA1…`; `CompanionApagadoTests › H049_H1…` |
| **E** | — (nadie obtiene permisos nuevos) | — | — |
