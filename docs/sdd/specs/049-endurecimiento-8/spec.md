# Spec 049 — Endurecimiento 8: el interruptor del Acompañante apaga lo que está en marcha y una propuesta no sobrevive a su turno

**Estado:** Implementado · **Depende de:** 033, 036, 038, 045, 046, 047, 048 · **Origen:** la **auditoría externa 9** (2026-11-02,
solo lectura al cerrar la 048, commit `a1341b4`, specs 045–048): 1 defecto medio (D-01), 2 bajos (D-02, D-03) y 5
observaciones (O-01 a O-05). El dueño pidió corregirlo todo (2026-11-02).

## Problema
Cada hallazgo se reconfirmó en el código de `b6f7a58` (igual a `a1341b4` fuera de `docs/`) antes de escribir esta spec.

| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | D-01 (media) · `Companion:Enabled` solo lo miran `StartAsync` y `SendAsync`. `CompanionDispatcher` reclama cualquier `queued` (`ClaimAsync`), `CompanionAgent.LoopAsync` solo exige la clave, `ConfirmAsync`/`DiscardAsync` reanudan y el barrido (`SweepAsync`) reanuda lo que espera sin pendientes | El dueño apaga el Acompañante por un incidente de inyección o de costo y reinicia: lo que estaba en cola corre igual; confirmar sigue escribiendo y reanuda el turno; «No, y dile qué cambiar» llama al modelo; sin nadie, a las 24 h el barrido caduca la propuesta y reanuda. En cada caso el historial viaja al proveedor y se cobra |
| 2 | D-02 (baja) · `CompanionAgent.LoopAsync` guarda los resultados de una respuesta después de recorrer **todas** sus llamadas; si sale a media lista (tope de consultas, identidad, «Detener», tiempo, lease perdido o `ExpireAsync`), la propuesta ya guardada sigue `proposed`: `FinishAsync` solo la descarta si el turno terminaba esperando | La tarjeta sigue confirmable en una conversación cerrada (contra 048 H1 CA6: detener es «no» a lo pendiente) y confirmarla no reanuda nada. En el mensaje siguiente `StudioTranscript.Normalize` rellena esa llamada con «el turno se interrumpió» y `LoadAsync` solo sustituye resultados existentes: el modelo no sabe qué pasó con su propuesta (el síntoma que originó la 048) |
| 3 | D-03 (baja) · el único tope de entrada es el acumulado de la conversación (`MaxTokens`, 1,5 M); no hay tope por llamada contra la ventana del modelo. El 400 «prompt is too long» cae en `bad_request` | Una respuesta con ocho lecturas de ~20 000 caracteres cada una lleva la siguiente llamada por encima de la ventana: el proveedor la rechaza y la persona ve «La respuesta falló por un error inesperado». Igual en el bucle de Studio (R-046-1) |
| 4 | O-01 · `ResumeAsync` reanuda con el `SecurityStamp` guardado al empezar el turno; solo enviar y responder lo renuevan | Tras cambiar su contraseña y volver a entrar, la persona confirma (se aplica con su sesión nueva) y el turno reanudado se detiene por identidad: el modelo no contesta hasta otro mensaje |
| 5 | O-02 · `como_funciona` pasa al modelo las expresiones de reglas y condiciones tal como las da `/api/model/entities/{name}` (`ForUser` no toca `Rules` ni `Workflow`) | Una regla sobre un campo oculto lo nombra (nombre y umbral, no valores); así desde la 006 en la ruta del navegador, y el Acompañante lo lleva al proveedor. **El dueño eligió quitarlas en todos los caminos** (CLARIFY 3, H8) |
| 6 | O-03 · `filledBy` (la ruta del modelo) decide si se ve un catálogo con `CanSee` del documento, no con `FieldAccess.SeesLines` (045 D-01); `buscar_en_el_sistema` nombra cualquier automatización del documento | Con el campo de partidas oculto, una automatización del documento que actúa sobre sus partidas aparece con su etiqueta en la procedencia de un campo de otro catálogo y en la búsqueda (sin valores) |
| 7 | O-04 · «Detener» y borrar no piden estar en la empresa de la conversación, a diferencia de leer, seguir, confirmar y responder (046 F-07, 048 F-05) | Desde otra empresa, o ya sacada de la de la conversación, la persona la detiene (y descarta lo pendiente) o la borra. Son suyas y no revelan nada, pero rompen la regla de los demás caminos |
| 8 | O-05 · sigue pendiente la prueba de integración de la 045 O-03: el registro dinámico del MCP (barrido y transacción serializable de topes) y las renovaciones a la vez | Sospecha de interbloqueo sin prueba |

## Principio
Ninguna capacidad de negocio nueva. Cuatro reglas del motor: **un interruptor apaga también lo que ya está en marcha** (cola,
espera, decisión y barrido); **una propuesta vive solo mientras su turno espera a la persona**, y su resultado, sea cual sea,
vuelve al modelo; **ningún tope se descubre por el error del proveedor** (se estima antes y, si aun así llega, se dice qué
pasó); y **la regla de un camino vale en todos** (empresa al detener y borrar; ver las partidas exige ver su campo también en
la procedencia y la búsqueda). Todo en el motor del Acompañante y de Studio, sin código por módulo.

## Historias

**H1 — Apagado es apagado (D-01).** Como *dueño del entorno*, quiero que `Companion:Enabled=false` detenga todo lo del
Acompañante, para cortar de inmediato un incidente de inyección o de costo.
- CA1: con el Acompañante apagado, confirmar y descartar una propuesta responden **503** «El Acompañante está apagado en este
  entorno.» y no escriben nada (como empezar y enviar).
- CA2: el trabajador no reclama ningún turno en cola; lo que estaba en cola o esperando a la persona se cierra como **fallido**
  (`disabled`, «El Acompañante se apagó en este entorno: la conversación se detuvo y lo que proponía se descartó.»), con sus
  propuestas descartadas y su cierre en la bitácora (`companion.finish`).
- CA3: el barrido sigue caducando propuestas y pasando a «sin confirmar» lo que se quedó aplicándose, pero no reanuda nada; el
  cierre del turno tampoco. El proveedor no se vuelve a llamar.

**H2 — Una propuesta no sobrevive a su turno (D-02).** Como *persona*, quiero que lo que el Acompañante propuso en un turno que
terminó sin preguntarme no quede confirmable, y que él sepa qué pasó con eso.
- CA1: si el turno sale a media respuesta por un tope (consultas, pasos, tokens, costo, tiempo) o por su identidad, lo
  propuesto en esa respuesta queda **descartado** («El turno terminó antes de preguntarle a la persona…»); confirmarlo da 409.
- CA2: «Detener» durante otra consulta de la misma respuesta descarta lo propuesto como «La persona detuvo la conversación.»
  (048 H1 CA6 también a media respuesta).
- CA3: un turno que se queda sin trabajador (`ExpireAsync`) descarta lo que propuso, en la misma transacción que lo interrumpe.
- CA4: en el mensaje siguiente el modelo recibe el resultado de su propuesta («detenida», con el motivo) en lugar de «el turno se
  interrumpió»; las demás llamadas sin resultado siguen con ese aviso.

**H3 — Un tope de entrada por llamada (D-03).** Como *persona*, quiero que, si lo que el Acompañante tendría que leer de una vez
no cabe, se me diga así y no «error inesperado».
- CA1: `Companion:MaxInputTokens` y `Studio:MaxInputTokens` (180 000 por omisión, de 1 000 a 2 000 000): antes de cada llamada se
  estima la entrada y, si lo rebasa, el turno se detiene con `limit_tokens` y «Lo que el modelo tendría que leer de una vez ya
  no cabe: pide algo más acotado o empieza una conversación nueva.»; el proveedor no se llama.
- CA2: si el proveedor responde «prompt is too long», el código es `too_long` (no `bad_request`) y la persona ve el mismo
  mensaje, en el Acompañante y en Studio.

**H4 — Decidir reanuda con la sesión de ahora (O-01).** Como *persona*, quiero que, si cambié mi contraseña y volví a entrar,
confirmar o descartar reanude el turno con mi sesión nueva.
- CA1: confirmar o descartar la última pendiente encola el turno con el sello de quien decide; el turno reanudado contesta (no
  se detiene por identidad). Sin persona (el barrido, el cierre del turno), se queda el sello guardado.

**H5 — Las partidas ocultas no se nombran (O-03).** Como *dueño de datos*, quiero que ocultar el campo de partidas oculte también
lo que actúa sobre ellas en «de dónde sale cada campo» y en la búsqueda del Acompañante.
- CA1: en `GET /api/model/entities/{name}` (`filledBy`), una automatización que actúa sobre las partidas de un documento solo
  aparece si la persona ve ese campo de partidas (o modela); `buscar_en_el_sistema` no nombra una automatización cuyas acciones
  son todas sobre partidas que no ve, ni esas acciones.

**H6 — Detener y borrar desde la empresa de la conversación (O-04).** Como *persona*, quiero que detener y borrar sigan la misma
regla de empresa que leer y confirmar.
- CA1: quien ya no está en la empresa de la conversación recibe 404 al detenerla o borrarla; quien está pero trabaja en otra,
  409 «Esta conversación es de otra empresa: vuelve a esa empresa o empieza una conversación nueva.»

**H7 — Registro y renovaciones del MCP a la vez (O-05).** Como *dueño*, quiero una prueba de que el registro dinámico y las
renovaciones no se interbloquean.
- CA1: tres vueltas de seis renovaciones y ocho registros a la vez (con un cliente viejo que el barrido borra en cada una):
  todas las renovaciones dan 200, todos los registros 201, y las conexiones siguen sirviendo.

**H8 — Las expresiones no nombran lo que la persona no ve (O-02).** Como *dueño de datos*, quiero que una regla o una condición
sobre un campo oculto no revele ese campo ni su umbral, en ningún camino.
- CA1: en `GET /api/model/entities` y `/entities/{name}`, para quien no modela, una regla cuya expresión (`assert` o `when`) nombra
  un campo que no ve (del catálogo, de sus partidas o documento, o de un catálogo que no lee al otro lado de una referencia)
  llega con su nombre y su mensaje, sin `assert`, sin `when` y sin el campo oculto al que se ata; igual la condición de una
  transición (sin `when`, con `whenMessage`), los campos obligatorios y asignaciones de una transición que son ocultos, y un
  valor por omisión. Las acciones de las automatizaciones llegan sin sus expresiones. `como_funciona` y `buscar_en_el_sistema`
  lo leen de ahí. Quien modela recibe todo.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Más de 6 historias (7, pero cinco son de un criterio) · más de ~2 500 líneas de producto (no: ~200) ·
   seguridad o concurrencia junto con UI nueva (no: sin UI) · más de una pantalla principal (0). → **No se divide.**
2. **D-01: ¿qué pasa con lo que estaba en marcha?** Se cierra (fallido, `disabled`) y se descarta lo propuesto, en lugar de
   dejarlo en cola o esperando: así encender el Acompañante otra vez no reanuda solo un turno de antes del incidente, y la
   persona no se queda con una conversación «activa» que no puede seguir (cuenta para «una por persona»). Detener y borrar
   siguen funcionando apagado (no llaman al proveedor). Propuesto en CLARIFY y **aceptado por el dueño** (2026-11-02). Con varias instancias sobre la misma base, una apagada cierra también lo de las otras: el interruptor es
   del entorno, no de la instancia (R-049-1).
3. **O-02 (expresiones de reglas sobre campos ocultos).** Opciones: (a) quitarlas en la ruta para quien no modela, que vale en
   todos los caminos; (b) solo en las herramientas del Acompañante; (c) aceptarlo como riesgo. **El dueño eligió (a)**
   (2026-11-02). Cambia la ruta del navegador desde la 006, pero el formulario solo usa `severity` de las reglas y el
   explorador que muestra expresiones es de modeladores. Qué nombra una expresión lo dice `DerivedAccess.NamesHidden` (la
   misma lectura de rutas que oculta una fórmula desde la 029); una expresión ilegible cuenta como oculta. Las acciones de las
   automatizaciones se evalúan en otros ámbitos y nadie que no modele usa sus expresiones: van siempre sin ellas.
4. **D-03: ¿cómo se estima la entrada?** Con la entrada medida en la llamada anterior del mismo turno más un token por cada dos
   caracteres nuevos; sin medición (primera llamada del turno), uno por cada tres caracteres. No es el peor caso del costo
   (que sigue con un token por carácter para reservar): un tope que rechazara conversaciones que sí caben estorbaría más que el
   400 del proveedor, que queda de respaldo con su propio código. El estimador es compartido por Studio y el Acompañante.
5. **O-03: ¿se cambia también la definición de una partida?** En PLAN se dejó como estaba; la revisión adversarial (F-02)
   mostró que así se rodeaba la regla, y se cambió: `GET /api/model/entities/{partida}` responde 403 y la lista con
   `includeChildren` la omite a quien no ve el campo de partidas (o no lo ve con permiso de captura); el formulario no la pide
   en ese caso. Una automatización del documento sobre esas partidas tampoco viaja en la ruta (F-01).
6. **O-04: ¿404 o 409?** Como los demás caminos: 404 si ya no está en la empresa (no la encuentra), 409 si está pero trabaja en
   otra (como enviar y confirmar).

## Fuera de alcance (anotado en el roadmap)
- Unificar el bucle de Studio y el del Acompañante (R-046-1): aquí solo se comparte el estimador de entrada.
- Pasar la cancelación a las consultas de datos (hoy una lectura retenida termina antes de que «Detener» corte el turno).
