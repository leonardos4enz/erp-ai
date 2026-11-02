# Spec 046 — Acompañante (motor): un agente para cualquier persona, con su alcance y su confirmación

**Estado:** Implementado · **Depende de:** 033/036 (bucle agéntico, historial, flujo SSE, *lease*, purga de lo leído),
034 (despachador con límites y capacidad), 029 (capacidad y efecto declarados por ruta), 040 (empresa de la conversación,
cola que no se atora), 021 (huella en «revisar → aplicar») · **Origen:** pedido del dueño (2026-11-01): «un agente que
acompaña al usuario dentro del sistema, como el de ERP AI Studio pero para todos». Se divide en CLARIFY: la 046 es el motor y
la **047** el panel lateral con el avatar.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | Todas las rutas de `/api/studio` piden `Capability.Modeler` (`StudioEndpoints.cs`) y `StudioAgent.IdentityAsync` corta la sesión si la persona no `CanManageModel`. | Quien captura, aprueba o consulta no tiene agente: no puede preguntar «¿cómo registro una devolución?» ni «¿por qué no puedo aprobar este pedido?». |
| 2 | El prompt de Studio (`StudioPrompt.Intro`) habla de Planes de Cambios, compilar y copia aislada; sus herramientas leen el explorador, el inventario de rutas, la base física y la matriz de seguridad (`StudioTools.All`). | Es un agente de programación: su lenguaje y lo que ve no sirven (ni deben mostrarse) a una persona sin rol de modelador. |
| 3 | `RouteDispatcher.SendAsync(..., allowWrites: false)` en el entorno real devuelve 403 a toda ruta que escriba; la constitución II.8 dice que el LLM «solo propone Planes de Cambios». | Ningún agente puede hacer por la persona lo que ella sí puede (crear un registro, mover una transición), ni siquiera con su confirmación. |
| 4 | El registro ya trae lo que explica un bloqueo: `_access` (crear/editar/eliminar) y `_workflow.transitions[].allowed/reason` (`RecordService.LoadOutputAsync`); `/api/auth/me` da el permiso por catálogo y los roles; `/api/model/entities/{name}` da proceso, reglas y fórmulas sin los campos ocultos (`ModelEndpoints.ForUser`). | Nadie lo junta para la persona: el botón deshabilitado dice el motivo en un *tooltip*, pero no qué hacer ni quién puede. |
| 5 | `meta.AuditLog` no tiene origen (`Bootstrap.cs`): un cambio por la pantalla, por OData o por un agente se ven iguales. | Si un agente llega a escribir con la confirmación de la persona, la bitácora no lo distinguiría. |

## Principio
Capacidad **genérica del motor**, sin código por módulo (constitución I) y sin privilegios propios (II.8, enmendada por el
[ADR-0010](../../../adr/0010-acompanante-acciones-confirmadas.md)):
- **Leer no pregunta; escribir sí** (decisión del dueño). El Acompañante lee con las herramientas de siempre —rutas de la API
  ejecutadas por `RouteDispatcher` con la identidad recargada de la persona, su empresa, `RecordFilter` y `FieldAccess`—. Toda
  escritura es una **acción propuesta**: queda guardada con su vista previa y su huella, y solo se ejecuta cuando la persona la
  confirma, una por una, por la misma ruta y el mismo despachador que el navegador. El modelo nunca se cambia desde aquí (eso
  sigue siendo Studio).
- **El motor conversacional es el de Studio**: proveedor (`IStudioModel`, en *streaming*), historial exacto
  (`StudioTranscript`: normalizar, purgar), delimitación de lo no confiable (`StudioPrompt.Untrusted`), señales y formato SSE
  (`StudioSignals`, `StudioStream.WriteAsync`), costo (`StudioOptions.CostMicros`) y clave (`AssistantKeys`). Lo propio del
  Acompañante son sus tablas, su catálogo de herramientas, su prompt en lenguaje de usuario y las acciones.
- **Las reglas del negocio ya son metadatos**: explicar un proceso, una regla, una fórmula o un bloqueo es leer el modelo y el
  registro con el alcance de la persona, no programar explicaciones.

## Historias

**H1 — Conversar con el Acompañante.** Como *cualquier persona del sistema*, quiero preguntarle al Acompañante en mis palabras
y retomar conversaciones anteriores, para aprender y resolver sin saber cómo está hecho el sistema.
- CA1: `POST /api/companion/conversations { text, context? }` crea una conversación mía y encola su primer turno (202); con
  `POST …/{id}/messages` la sigo con todo el historial. Cualquier usuario activo que no sea cuenta de servicio puede (sin ser
  modelador); una cuenta de servicio recibe 403.
- CA2: `GET /api/companion/conversations` lista solo las mías (título, estado, última actividad), de la más reciente a la más
  vieja; `GET …/{id}` da sus pasos y sus acciones; de otra persona, 404 en todas las rutas.
- CA3: el avance llega en vivo por `GET …/{id}/stream` (SSE, los mismos eventos que Studio: `step`, `session`, `live`, `end`);
  «Detener» corta el turno y conserva lo escrito.
- CA4: el modelo es el del entorno (`Assistant:Model`): ninguna ruta acepta elegirlo. Sin clave de Anthropic o con
  `Companion:Enabled = false`, `GET /api/companion` dice `available: false` con el motivo, y crear o seguir una conversación da
  503 con ese mensaje.

**H2 — Lee lo que yo puedo ver, sin preguntar.** Como *persona*, quiero que el Acompañante consulte mis catálogos, registros,
procesos y reglas con mis permisos, para que me explique con mis datos sin ver más que yo.
- CA1: sus herramientas solo leen (rutas con efecto «ninguno»: el despachador rechaza toda otra) y corren como la persona:
  un catálogo que no puede leer da 403, un campo oculto no viaja al proveedor (canario de la 029) ni un registro fuera de su
  alcance.
- CA2: explica en lenguaje de usuario: el prompt prohíbe términos técnicos (rutas, JSON, SQL, nombres internos) y las
  herramientas devuelven etiquetas, no solo nombres internos.
- CA3: *contexto de la pantalla*: el mensaje puede traer `context { entity, id }`; si la persona puede ver el catálogo, el
  Acompañante sabe qué está viendo (etiqueta y número); si no, se ignora sin decir por qué (no revela que existe).
- CA4: lo que leyó de registros se borra del historial al terminar cada turno (como Studio con `datos`): en el turno
  siguiente el proveedor no recibe el canario aunque lo haya leído antes.

**H3 — «¿Por qué no puedo?» y «¿qué me toca?».** Como *persona*, quiero saber qué regla, estado o permiso me impide una acción y
qué tengo pendiente, para no depender de que alguien me lo explique.
- CA1: `por_que_no_puedo` junta el permiso de la persona sobre el catálogo, el estado del registro, las transiciones posibles
  con su motivo (`_workflow.transitions[].reason`) y si el registro está fuera de su alcance de escritura, sin revelar campos
  ocultos ni registros que no ve.
- CA2: `mis_pendientes` lista, por catálogo que la persona lee, los registros en estados desde donde ella puede ejecutar una
  transición (por rol), con un tope por catálogo y en total.

**H4 — Escribe solo con mi confirmación.** Como *persona*, quiero que el Acompañante me proponga crear, editar o mover un
registro y que nada pase hasta que yo lo confirme, para que me ayude sin actuar por su cuenta.
- CA1: la herramienta `proponer_accion` (crear, editar, transición) no escribe nada: revisa que la persona tenga el permiso y
  guarda la acción con su vista previa (antes → después, con etiquetas) y su huella; el modelo recibe «propuesta n, la persona
  decide».
- CA2: `POST /api/companion/actions/{id}/confirm { hash }` la ejecuta **una vez** por la ruta de datos de siempre, con la
  identidad, la empresa y los permisos actuales de la persona y la versión del registro de la vista previa: si el registro
  cambió, 409 y no se aplica; si ya no tiene permiso, 403; si la regla la rechaza, 400 con el mensaje; un aviso que se acepta
  (428) vuelve con sus mensajes y se confirma otra vez aceptándolos. `…/discard` la descarta.
- CA3: diez confirmaciones a la vez de la misma acción ejecutan una; otra persona recibe 404; una acción vieja
  (`Companion:ActionHours`) caduca.
- CA4: la bitácora del cambio lleva el origen «acompañante» (columna nueva `Origin` de `meta.AuditLog`) y la acción guarda su
  resultado (el registro creado o el error).

**H5 — No le hace caso a lo que dicen los registros.** Como *persona*, quiero que un texto escrito por otra persona en un
registro no pueda darle órdenes al Acompañante, para que nadie me use a través de él.
- CA1: todo lo que devuelven las herramientas va delimitado como datos no confiables (`StudioPrompt.Untrusted`) y el prompt
  lo dice; aunque el modelo «obedezca» una nota que pide aprobar algo, lo más que puede hacer es una propuesta, que no cambia
  nada hasta que la persona la confirma.
- CA2: una conversación no puede proponer más de `Companion:MaxPendingActions` acciones pendientes a la vez ni más de
  `Companion:ActionsPerDay` por persona al día.

**H6 — Costo, cola y retención.** Como *dueño del entorno*, quiero topes por persona y que las conversaciones con datos no se
queden para siempre, para controlar el gasto y lo que guarda el sistema.
- CA1: topes por conversación (mensajes, pasos, herramientas, tokens, costo), por turno (minutos) y por persona al día
  (conversaciones y costo, aparte de Studio); el trabajo corre en una cola con *lease* como Studio: una conversación activa
  por persona, diez mensajes a la vez encolan uno, un turno sin trabajador queda interrumpido y no se repite.
- CA2: la conversación es de la empresa en que empezó (040 D-07): un mensaje desde otra empresa da 409 con el motivo.
- CA3: la persona borra una conversación suya (`DELETE`, 204; 409 si un turno corre); las conversaciones sin actividad en
  `Companion:RetentionDays` (90) se borran solas con sus pasos, mensajes y acciones (la bitácora se queda).

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios: más de 6 historias (6, no) · más de ~2 500 líneas de producto (~2 300 el motor más la
   pantalla: sí) · seguridad o concurrencia junto con UI nueva (sí) · más de una pantalla principal (panel e historial: sí).
   → **Se divide** (dueño, 2026-11-01): la **046** es el motor (este documento, sin pantalla, como la 036) y la **047** el panel
   lateral derecho contraíble con el avatar, el historial, los efectos del chat de Studio y las tarjetas de confirmación.
2. **Escrituras (constitución II.8).** Dueño: «lectura no pregunta, escritura sí». Lee con el alcance de la persona; cada
   escritura es una propuesta que ella confirma una por una, con huella, por el mismo despachador; nunca cambia el modelo.
   Se registra en el [ADR-0010](../../../adr/0010-acompanante-acciones-confirmadas.md), que enmienda II.8. Se descartó actuar sin
   confirmar (rompe II.8 y deja la inyección de instrucciones con efecto directo) y solo leer (el dueño pidió que tome acciones).
3. **Qué acciones.** Crear, editar y ejecutar una transición (las tres rutas de datos con efecto `Data` y versión del
   registro). Eliminar, importar, imprimir, exportar y todo lo que no sea un registro quedan fuera: eliminar es destructivo
   (IV.4 pide confirmación con vista previa; se puede agregar después con la misma vía) y el resto tiene su pantalla.
4. **Retención.** Dueño: purga por turno + 90 días. Lo leído de registros se reemplaza por un aviso al cerrar cada turno (en
   pasos e historial); queda lo que el Acompañante respondió. Sin actividad en 90 días la conversación se borra; la persona puede
   borrar las suyas. Las acciones se borran con su conversación; la bitácora del cambio se queda (II.4).
5. **Ideas del dueño que entran.** «Por qué no puedo» y «mis pendientes» (herramientas, H3) y el contexto de la pantalla
   (H2 CA3; la 047 lo manda desde el panel). **Fuera** (roadmap): guías paso a paso que señalan el control en pantalla y el modo
   instructor con recorridos.
6. **Motor compartido, tablas propias.** Las conversaciones del Acompañante viven en `meta.Companion*`, no en
   `meta.StudioSession`: Studio y el Acompañante tienen capacidades, topes y retención distintos, y ninguna consulta de Studio
   debe poder devolver una conversación del Acompañante (ni al revés). Se reutiliza todo lo que no depende de la tabla (ver
   Principio); el bucle con *lease* sigue el de `StudioAgent`. Unificar los dos bucles en uno genérico queda como riesgo
   (R-046-1), no como trabajo de esta spec.
7. **Costo.** Topes propios (`Companion:*`) más bajos que los de Studio: 2 USD por persona al día y por conversación,
   30 conversaciones al día, 5 minutos por turno. Sin clave del entorno no hay Acompañante (no se pide clave a la persona).
8. **Avatar.** Es de la 047. Decisión anotada para ella: `<fleet-eyes>` (`Grok Bot Harness/agent-fleet/orchestrator/panel`,
   derivado de `<agent-eyes>` de *AI Engineer*; autor: el dueño, sin licencia de terceros) se porta a un componente React con
   SVG y CSS de la SPA: la CSP es `style-src 'self'`, así que el `<style>` dentro de su Shadow DOM no cargaría; los estados de la
   flota se cambian por los del chat (en reposo, pensando, usando una herramienta, esperando tu confirmación, listo, error) y
   `prefers-reduced-motion` deja la cara quieta.

## Fuera de alcance (anotado en el roadmap)
- **047 — Acompañante: panel lateral** (pantalla, avatar, historial, tarjetas de confirmación, contexto de la pantalla).
- Guías paso a paso que señalan el control en pantalla; modo instructor con recorridos.
- Eliminar registros y otras acciones (imprimir, exportar, importar) por el Acompañante.
- Unificar el bucle de Studio y el del Acompañante (R-046-1).
