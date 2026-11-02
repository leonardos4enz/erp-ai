# Spec 048 — Acompañante 2: permiso en la conversación y de dónde sale cada campo

**Estado:** Implementado · **Depende de:** 046 (motor del Acompañante, acciones confirmadas, ADR-0010), 047 (panel, tarjetas de
confirmación, avatar), 005/020/022 (automatizaciones entre catálogos) · **Origen:** prueba del dueño (2026-11-02) con Gatos:
el Acompañante no supo que su propuesta ya se había aplicado y dijo que «Vacunado» era una casilla normal, aunque no es
editable y la llena la automatización «Marcar al gato como vacunado» al crear una *Vacuna aplicada*. Decisión del dueño
(2026-11-02): la 048 va antes de la auditoría 9, que pasa a cubrir 045–048, y la confirmación funciona como los permisos de
Claude Code dentro de la conversación.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `CompanionAgent.LoopAsync` devuelve al modelo, en el mismo paso, el resultado de `proponer_accion` («esperando a la persona… No digas que ya se hizo») y el turno sigue hasta responder; confirmar (`CompanionActions.ConfirmAsync`) solo cambia la acción, nunca la conversación. | El modelo jamás se entera de lo que la persona decidió: en el turno siguiente su historial sigue diciendo «esperando a la persona», y el prompt le prohíbe decir que se hizo. Dice que no sabe si se aplicó, o lo vuelve a proponer. |
| 2 | Las propuestas quedan sueltas: el turno termina «respondido» con varias pendientes y la persona puede escribir otra cosa sin decidir. | No hay un momento claro de «¿lo hago?»: las tarjetas se acumulan y el modelo sigue hablando como si nada. |
| 3 | `CompanionTools.ShapeEntity` (herramienta `como_funciona`) no manda `editable` ni las automatizaciones ni el `set` de las transiciones; `/api/model/entities/{name}` sí trae `editable: false` y las automatizaciones del catálogo, pero no las de **otros** catálogos que escriben en él. | Para el modelo, «Vacunado» es una casilla más: no sabe que la llena el sistema ni quién. Lo inventa. |
| 4 | `CompanionActions.Values` rechaza un campo no editable con «No se puede proponer «X». Campos que la persona puede capturar: …». | El modelo sabe que no puede, pero no por qué ni de dónde sale el valor; no se lo puede explicar a la persona. |
| 5 | `CompanionPrompt.System`: «Nunca digas que algo ya se hizo hasta que la persona lo confirme; si te pregunta, revisa el registro». | Sin el resultado, la instrucción empuja a dudar o a suponer, en lugar de creer al sistema. |

## Principio
La confirmación es **parte del turno**, como el permiso de una herramienta en Claude Code: el modelo pide hacer algo, el turno
se detiene a esperar a la persona, y su respuesta (sí, no, o no con un motivo) vuelve al modelo como el resultado de esa
misma llamada. No cambia **qué** se puede hacer (ADR-0010: solo lo que la persona puede, una acción a la vez, con huella, por
la ruta de siempre y sin «no vuelvas a preguntar»); cambia que el modelo se entera y sigue. Un turno detenido no ocupa al
trabajador: decidir lo vuelve a poner en la cola con sus topes. «De dónde sale un campo» se lee del modelo de metadatos con el
alcance de la persona: es capacidad genérica del motor, la misma para la pantalla y el Acompañante.

## Historias

**H1 — El turno se detiene a pedir permiso.** Como *persona*, quiero que cuando el Acompañante proponga algo se detenga y me
pregunte, para decidir en el momento y que él sepa qué decidí.
- CA1: cuando una respuesta del modelo trae `proponer_accion` y al menos una propuesta queda guardada, el turno termina las
  demás herramientas de esa respuesta y la conversación pasa a `waiting` («esperando tu respuesta»), sin llamar otra vez al
  modelo. El trabajador queda libre (sin *lease*).
- CA2: la persona responde con «Sí, adelante» (`confirm` con la huella; un 428 sigue mostrando los avisos y se confirma otra
  vez, como hoy), «No» (`discard`) o «No, y dile qué cambiar» (`discard { reason }`, hasta `Companion:MaxPromptChars`).
- CA3: escribir otro mensaje mientras espera cuenta como «no» con ese texto: descarta toda propuesta pendiente de la
  conversación con ese motivo y sigue el turno (sin turno nuevo). Si una se está aplicando, 409.
- CA4: el resultado vuelve al modelo **como el `tool_result` de su propia llamada** —aplicada (con el número del registro),
  rechazada (con el motivo de la persona, si lo dio), no se aplicó (con el mensaje), caducó o sin confirmar si se aplicó— y el
  turno sigue solo: la conversación se vuelve a encolar y el trabajador la retoma con su historial, topes y costo.
- CA5: varias propuestas en una respuesta se preguntan una por una (el panel muestra la primera pendiente) y el turno sigue
  cuando se decidieron todas. No hay «no vuelvas a preguntar».
- CA6: «Detener» mientras espera descarta lo pendiente («la persona detuvo la conversación») y la conversación queda detenida,
  sin volver al modelo. Una conversación esperando cuenta como activa: no se empieza ni se sigue otra hasta decidir o detener.

**H2 — Decidir es seguro aunque pase todo a la vez.** Como *dueño del entorno*, quiero que decidir dos veces, decidir y
detener a la vez o una caída entre aplicar y seguir no dupliquen ni atoren nada.
- CA1: confirmar y descartar a la vez la misma propuesta: una gana; la conversación se reanuda una sola vez.
- CA2: decidir y detener a la vez: la acción queda aplicada o descartada (nunca ambas), el registro existe solo si quedó
  aplicada y la conversación termina detenida sin volver al modelo después de detenerse.
- CA3: la marca del resultado de la acción y la reanudación van en una transacción; si el proceso cae entre despachar y
  guardar, el barrido marca la acción «sin confirmar si se aplicó» (046) **y** reanuda la conversación con ese resultado. Una
  propuesta que caduca también la reanuda. Nada se aplica dos veces.

**H3 — «Cómo funciona» dice qué llena el sistema y quién.** Como *persona*, quiero que el Acompañante me diga qué campos no
capturo yo y de dónde salen, para entender el proceso sin preguntarle a sistemas.
- CA1: `GET /api/model/entities/{name}` trae, por campo, `filledBy[]`: las automatizaciones —del mismo catálogo o de otros
  **que la persona puede ver**— que le asignan valor (`update` con o sin `target`, `create`, `capture`, `rollup`, `cost`,
  partidas de `createLines`) con su catálogo, su etiqueta y cuándo corren, y las transiciones con `set`. Una automatización de
  un catálogo que la persona no ve no aparece.
- CA2: `como_funciona` manda, por campo, si lo llena el sistema (`editable: false`) y `loLlena` en palabras («la automatización
  “Marcar al gato como vacunado” de Vacuna aplicada, al crearse»).
- CA3 (pedido del dueño en IMPLEMENT, 2026-11-02): la pantalla actual es solo una pista. `buscar_en_el_sistema` busca un tema
  en todo lo que la persona ve (catálogos, campos, estados, transiciones, reglas y automatizaciones, con el catálogo y los
  campos que toca cada una), sin importar acentos, para responder sobre algo que no está en pantalla.

**H4 — Si propone un campo que no se captura, sabe por qué.** Como *persona*, quiero que si el Acompañante intenta capturar
un campo que llena el sistema, sepa de dónde sale y me lo explique.
- CA1: el rechazo de `proponer_accion` dice por qué no se captura (lo llena el sistema y quién, se calcula con una fórmula, la
  persona solo lo puede ver) además de la lista de los que sí.

**H5 — Cree al sistema.** Como *persona*, quiero que el Acompañante crea el resultado que le da el sistema y explique de dónde
sale un campo en vez de suponer.
- CA1: el prompt dice que el resultado de su propuesta llega como respuesta de la herramienta y es la verdad (si dice
  aplicada, se hizo); que no proponga otra vez lo rechazado sin un cambio; y que para explicar un campo consulte «cómo
  funciona» y diga quién lo llena, sin suponer.

**H6 — El panel pregunta como Claude Code.** Como *persona*, quiero ver la pregunta clara y responder desde la tarjeta o
desde el compositor.
- CA1: la tarjeta pendiente dice «¿Lo hago?» con «Sí, adelante», «No» y «No, y dile qué cambiar» (un campo de texto con
  enviar); con varias, «Propuesta 1 de 3» y solo la primera pendiente pide decisión.
- CA2: mientras espera, el compositor dice «Esperando tu respuesta: escribe para decir que no y qué cambiar», y lo que se
  envía es el «no» con ese texto.
- CA3: el avatar y su texto dicen «Esperando tu respuesta» (estado `waiting`); al decidir, el panel sigue el turno en vivo.
- CA4 (pedido del dueño en IMPLEMENT, 2026-11-02): el avatar es solo la cara: sin los indicadores sobre la cabeza (libreta,
  llave, globo, palomita, nube) que venían de `<fleet-eyes>`; el estado lo dicen los ojos y el texto.

## Decisiones (CLARIFY)
1. **¿Dividir?** 6 historias (no más de 6) · ~1 000 líneas de producto · concurrencia con UI que **cambia** (no nueva: la
   tarjeta y el compositor existen) · una pantalla. No se divide.
2. **Estado nuevo `waiting`** en `meta.CompanionConversation` (no `answered` con propuestas): el turno no terminó. Cuenta como
   activa para «una conversación por persona» (así reanudar nunca deja dos turnos corriendo de la misma persona) pero no para
   el trabajador ni para el *lease*.
3. **El resultado se sustituye al cargar el historial**, no se reescribe el mensaje: la acción guarda el `id` de la llamada del
   modelo (`ToolUseId`) y, al armar la conversación para el proveedor, el `tool_result` de esa llamada es el resultado de la
   acción. Así la purga de lo leído (046 H2 CA4) sigue igual y la respuesta vale en todos los turnos siguientes.
4. **Purga al detenerse.** Detenerse a esperar cierra el tramo como un turno (purga lo leído): la espera puede durar hasta
   `Companion:ActionHours` y no hay razón para guardar registros leídos ese tiempo. La vista previa de la acción pendiente se
   queda (la persona la necesita para decidir, como en la 046). Al seguir, el modelo puede volver a consultar.
5. **El «no» con texto** es la voz de la persona: viaja en un bloque `<pedido>` dentro del resultado (los datos del sistema van
   en `<datos_no_confiables>`, como siempre). En la tarjeta y en el historial se ve como un mensaje de ella.
6. **Procedencia en la ruta del modelo, no en la herramienta**: `filledBy` lo calcula `FieldSources` (puro, en `Metadata`) y lo
   agrega `ModelEndpoints` a `GET /api/model/entities/{name}` con el mismo filtro de catálogos visibles que la lista; la
   herramienta y el rechazo lo leen de esa ruta. Es la misma regla para la pantalla (que podrá mostrarlo) y el Acompañante.
7. **Fuera** (roadmap): herramienta «¿qué pasa si…?» (qué automatizaciones se disparan al crear, editar o ejecutar algo);
   mostrar `filledBy` en el formulario.

## Fuera de alcance (anotado en el roadmap)
- «¿Qué pasa si…?» antes de confirmar (simular qué automatizaciones se disparan); `filledBy` como ayuda en el formulario.
- Lo pendiente de la 046/047: guías que señalan controles, modo instructor, eliminar por el Acompañante, unificar bucles (R-046-1).
