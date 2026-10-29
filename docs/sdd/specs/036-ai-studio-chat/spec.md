# Spec 036 — ERP AI Studio como chat: motor conversacional y streaming

**Estado:** Implementado · **Depende de:** 011 (clave), 021 (huella, *lease*), 030 (integración), 033 (motor de Studio),
034 (catálogo endurecido), 035 (pantalla, purga de lo leído) · **Origen:** pedido del dueño (2026-10-29): la pantalla de la
035 no le gustó; esperaba «un chat como Claude» y hoy Studio es *one-shot*. Fase 1:
[análisis del comportamiento de Claude web](analisis-claude-web.md). CLARIFY 1 divide la spec: la pantalla de chat es la 037.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `StudioAgent.LoopAsync` arma `messages` en memoria desde `claim.Prompt` y lo pierde al cerrar; no hay ruta para mandar otro mensaje. | Después de la propuesta no se le puede pedir nada más: para ajustar algo hay que empezar de cero (otra sesión, otro costo, otra copia aislada). |
| 2 | `StudioAgent.RunAsync` borra la copia aislada en `finally`; `SandboxFactory.CreateAsync` siempre la recrea (033 F-09). | Cada pedido vuelve a copiar el modelo (segundos o minutos con un modelo grande, 035 F-06). |
| 3 | La pantalla sondea `GET …?after=n` cada 1,5 s; el agente solo escribe un paso cuando el modelo terminó **toda** su respuesta o la herramienta terminó (`StepAsync`). | No hay texto en vivo ni «herramienta corriendo»: la persona ve saltos de varios segundos, lejos del ritmo de ~240 caracteres/s que mide el análisis. |
| 4 | Cancelar marca `CancelRequested`; el trabajador lo lee al renovar el *lease* (`Studio:RenewSeconds`, 10 s). | «Detener» tarda hasta 10 s y lo que el modelo ya escribió de esa respuesta se pierde. |
| 5 | Aplicar exige `Status = 'proposed'`; el estado es de la sesión entera. | En una conversación, una pregunta posterior («¿por qué ese campo?») dejaría la propuesta sin poder aplicarse, y una propuesta nueva podría aplicarse con la huella de la anterior si no se distingue. |

## Principio
Capacidad genérica del motor, sin código por módulo: una **conversación** de Studio es una sesión con varios **turnos**. El
historial que se manda al modelo se guarda tal cual (bloques de texto, uso de herramientas y sus resultados delimitados),
los topes son de la conversación y del día, y la vía de aplicar no cambia: la persona aplica con la huella de lo que revisó
(constitución II.8). El avance se publica como **eventos** (SSE) que leen la misma base, con la misma capacidad y el mismo
dueño que la lectura de la sesión.

## Historias

**H1 — Seguir la conversación.** Como *modelador*, quiero mandar otro mensaje en la misma conversación, para pedir ajustes o
preguntar sin empezar de cero.
- CA1: `POST /api/studio/sessions/{id}/messages { text }` encola un turno nuevo (202): el agente recibe todo el historial
  anterior (sus textos, sus herramientas con entrada y resultado) más el mensaje nuevo.
- CA2: el mensaje queda en el registro de la conversación como paso `user` con su número de turno; cada paso dice su turno.
- CA3: no se acepta mientras un turno corre (409), ni si la persona tiene otra conversación en curso (409), ni en una sesión
  anterior a la 036 (409), ni vacío o largo (400); otra persona recibe 404.
- CA4: la conversación toma la identidad y la empresa actuales de la persona en cada mensaje.

**H2 — Topes de la conversación.** Como *dueño*, quiero que los topes sumen todos los turnos, para que conversar no sea una
forma de rebasarlos.
- CA1: pasos, herramientas, tokens y costo son de la conversación (se acumulan); los minutos son por turno; hay un tope de
  mensajes por conversación (`Studio:MaxTurns`).
- CA2: un mensaje que ya no cabe (tope de la conversación o costo del día) se rechaza con 429 y su motivo, antes de encolar.

**H3 — La propuesta en la conversación.** Como *modelador*, quiero refinar la propuesta en el chat y aplicarla cuando esté
lista, con la vía de siempre.
- CA1: una entrega nueva reemplaza la propuesta vigente; una respuesta sin plan no la retira.
- CA2: aplicar exige la huella de la propuesta vigente: la de una anterior da 409; mientras un turno corre, 409.
- CA3: aplicar o descartar no cierra la conversación: se puede seguir pidiendo.

**H4 — Lo que se conserva y lo que no.** Como *modelador*, quiero que la copia aislada siga entre mensajes; como *dueño*,
quiero que los registros leídos no se queden.
- CA1: la copia aislada se reutiliza en el turno siguiente (sin volver a copiar el modelo) si es la que creó esa
  conversación (marca propia); si no, se crea otra.
- CA2: una persona tiene a lo más una copia viva: al empezar un turno de otra conversación, la anterior se borra; una copia
  sin uso en `Studio:SandboxIdleMinutes` se borra.
- CA3: al terminar **cada turno** se borran las salidas de las herramientas de `datos`, en los pasos y en el historial que
  verá el modelo (que recibe un aviso en su lugar).

**H5 — En vivo y detener.** Como *modelador*, quiero ver el texto mientras se escribe y la herramienta que corre, y poder
detener al instante sin perder lo escrito.
- CA1: `GET /api/studio/sessions/{id}/stream?after=n` (SSE) manda los pasos nuevos, el estado de la conversación y el
  estado en vivo (texto parcial, herramienta en curso); termina cuando el turno acaba o a los `Studio:StreamSeconds`.
- CA2: se reanuda con `after` o `Last-Event-ID` sin perder ni repetir pasos.
- CA3: solo la persona dueña (404 a otra), con capacidad de modelador; si se desactiva o cambia su contraseña, el flujo se
  cierra; a lo más `Studio:MaxStreamsPerUser` flujos a la vez (429).
- CA4: «Detener» corta el turno en menos de un segundo en el mismo proceso; el texto parcial queda como paso y en el
  historial.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (5 del motor + las de la pantalla: sí) ·
   más de ~2 500 líneas de producto estimadas (motor ~1 300 + pantalla ~1 800: **sí**) · seguridad o concurrencia junto con
   UI nueva (**sí**: multi-turno y SSE con la pantalla) · más de una pantalla principal (1: no). → **Se divide**: la **036**
   es el motor conversacional y el streaming (H1–H5, probado en integración) con el análisis de Claude web; la **037 —
   ERP AI Studio: pantalla de chat** reemplaza la pantalla de la 035 aplicando el análisis. Se renumera: **038** servidor MCP
   (auditoría 7 al cerrarla), **039** Ventas, **040** CFDI 4.0. Mientras tanto la pantalla de la 035 sigue funcionando (sin
   seguimiento): el contrato de la 033 no se rompe.
2. **Historial exacto, no reconstruido.** Se guarda lo que se mandó al modelo (`meta.StudioMessage`), no se rearma desde los
   pasos: los pasos se recortan (8 000), no guardan ids de herramienta ni firmas de razonamiento. Al cargarlo se normaliza:
   mensajes seguidos del mismo rol se unen y un uso de herramienta sin resultado (turno interrumpido) recibe un resultado de
   error, porque la API lo exige.
3. **Propuesta = la última entrega.** `PlanStatus` (`proposed`, `applied`, `discarded`) separa la propuesta del estado del
   turno. La huella ya cubre el contenido del plan: una propuesta nueva tiene otra huella, así que la revisión de la anterior
   no sirve para aplicarla (409).
4. **Lo leído se borra al terminar cada turno** (no al cerrar la conversación, que ya no existe como evento): la regla de
   la 035, aplicada a cada turno. El modelo ve un aviso y vuelve a leer si lo necesita (cuesta otra herramienta).
5. **Una copia viva por persona** y caducidad por inactividad (60 min): acota cuántas bases `_SBX` hay a la vez sin pedirle a
   la persona que «cierre» conversaciones. La marca (`meta.StudioSandboxMarker` con un GUID que guarda la sesión) impide
   reutilizar una base que no creó esa conversación (033 F-09).
6. **SSE leyendo la base, despertado en el proceso.** El trabajador escribe el estado en vivo (`LiveJson`) a lo más cada
   `Studio:LiveMs` (250 ms) y lo borra en la misma sentencia que guarda el paso; el flujo despierta con una señal en el mismo
   proceso o, en otra instancia, al vencer `Studio:StreamPollMs` (1 s). Una sola fuente (la base) para reconexión y varias
   instancias; nada en memoria que se pierda.
7. **Sin empujón a entregar.** En un chat, contestar una pregunta es un final válido del turno: se quita el mensaje
   `Nudge` de la 033 (costaba otra llamada por pregunta). Las instrucciones piden entregar cuando se proponen cambios.
8. **Editar o reintentar el mensaje anterior** es un mensaje nuevo para el motor (no reescribe el historial, que es
   auditoría); la 037 decide cómo se ve.
9. **Los alcances son de la conversación** (se eligen al crearla): cambiar `datos` a mitad cambiaría lo que ya viajó al
   proveedor. Para otros alcances, otra conversación.

## Fuera de alcance (anotado en el roadmap)
- La pantalla de chat (**037**).
- Razonamiento extendido (*thinking*) visible: el adaptador no lo pide hoy; la 037 muestra «pensando» con el estado en vivo.
- Ramas de la conversación (editar un mensaje viejo y seguir desde ahí).
- Cambiar de alcances dentro de una conversación; borrar o renombrar conversaciones.
