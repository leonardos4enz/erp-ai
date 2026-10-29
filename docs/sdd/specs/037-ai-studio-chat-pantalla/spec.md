# Spec 037 — ERP AI Studio: pantalla de chat

**Estado:** Implementado · **Depende de:** 033 (motor de Studio), 035 (pantalla, purga de lo leído), 036 (motor
conversacional, SSE y [análisis medido de Claude web](../036-ai-studio-chat/analisis-claude-web.md)) · **Origen:** pedido del
dueño (2026-10-29): «un chat que se comporte como Claude web, incluso en animaciones, espera, tamaños, scroll y
transiciones». La 036 se dividió en CLARIFY: el motor quedó en la 036 y esta es la pantalla.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `StudioPage.tsx` (035) es un formulario de un pedido: `Composer` solo llama a `studioApi.start`; no usa `POST …/messages`. | Tras la respuesta no se puede escribir nada más en la conversación, aunque el motor de la 036 ya lo admite. |
| 2 | `SessionView` filtra `kind !== 'user'` y muestra el pedido una sola vez arriba. | Los mensajes de seguimiento (pedidos por API) no se ven: la conversación no se entiende. |
| 3 | La pantalla sondea `GET …?after=n` cada 1,5 s (`POLL_MS`) y no lee `…/stream`. | No hay texto en vivo ni «herramienta corriendo»: la respuesta aparece a saltos, lejos del ritmo medido (~240 car/s, fundido por palabra). |
| 4 | Los pasos son tarjetas `details` con insignias, y la respuesta va aparte en «Propuesta / Respuesta del agente». | Se lee como una bitácora, no como un chat; el texto del modelo se ve como texto plano (sin listas, tablas ni código). |
| 5 | El área de texto es fija (5 filas) dentro de una tarjeta; no hay Enter para enviar, detener en el lugar del botón, reintentar, editar, copiar, «Ir al final», ni móvil propio. | Nada de los patrones medidos en el análisis (§1–§4, §6, §7). |

## Principio
Es una pantalla sobre el contrato de la 036; **sin capacidad nueva del motor ni rutas nuevas**. El texto del modelo y lo que
devuelven las herramientas son **entrada no confiable** (constitución II.8; R-033-4, R-035-1, R-036-5): se pintan como texto o
con un subconjunto de Markdown construido con elementos de React (sin HTML, sin enlaces activos, sin imágenes). La propuesta
se aplica por la vía de siempre: la persona revisa la vista previa y aplica con la huella de lo que revisó. **Comportamiento
sí, marca no:** los patrones de interacción de Claude web, con los tokens, Lucide, claro/oscuro y textos de ERP AI.

## Historias

**H1 — Conversaciones.** Como *modelador*, quiero ver mis conversaciones a la izquierda y empezar una nueva desde una
bienvenida, para retomar o pedir sin perderme.
- CA1: la barra lista las conversaciones por última actividad (`updatedAt`), marca la abierta y la que está trabajando; un
  clic la abre (la URL lleva `?s=id`) y «Nueva conversación» vuelve a la bienvenida.
- CA2: la bienvenida saluda por nombre, ofrece sugerencias de pedidos de ERP (rellenan el compositor) y los alcances de la
  conversación: `datos` apagado con su aviso, `aislado` según el servidor. Sin clave, explica cómo configurarla y no ofrece
  escribir; un lector no ve la opción ni llega por URL.
- CA3: al entrar con una conversación trabajando, se abre esa; el primer mensaje con otra en curso se rechaza con su motivo
  en la vista.

**H2 — Compositor y envío.** Como *modelador*, quiero escribir como en Claude, para conversar sin fricción.
- CA1: compositor fijo abajo, centrado en una columna de ~736 px (~768 px el compositor); empieza en una línea y crece con el
  texto hasta 384 px, después se desplaza por dentro; al vaciarse vuelve a una línea.
- CA2: Enter envía y Shift+Enter inserta una línea; tras enviar, el compositor queda vacío y con el foco. El primer mensaje
  crea la conversación; los siguientes son mensajes de seguimiento.
- CA3: el mensaje enviado aparece al instante como burbuja a la derecha y **sube al borde superior** de la vista; la
  respuesta crece debajo **sin auto-scroll**. Si el final queda fuera de la vista aparece «Ir al final».
- CA4: si no se puede enviar (`canSend` falso), el compositor dice por qué (`sendBlockedReason`) y no envía; un rechazo del
  servidor (409/429) deja su aviso en la vista y conserva el texto.

**H3 — Respuesta en vivo.** Como *modelador*, quiero ver lo que el agente escribe y hace mientras lo hace, para seguirlo y
detenerlo a tiempo.
- CA1: la pantalla sigue el turno con el flujo SSE (`…/stream`): mientras espera, un punto que late; el texto en vivo entra
  suavizado y con fundido por palabra; la herramienta en curso se ve como «Usando *herramienta*…». Al terminar el turno hace
  una lectura completa (propuesta y huella).
- CA2: las herramientas de cada respuesta quedan en una línea plegable («Usó 3 herramientas»); al abrirla, una fila por
  herramienta con su estado y duración, que a su vez abre su entrada y su salida; un error o una negación se ve con su
  ícono y su motivo. Lo leído con `datos` y ya purgado lo dice.
- CA3: el texto del modelo se ve con un subconjunto seguro de Markdown (párrafos, títulos, listas, negritas, cursivas,
  código en línea, bloques de código con «Copiar» y tablas); HTML, enlaces e imágenes se ven como texto.
- CA4: si el flujo se corta, la pantalla se reconecta sola desde el último paso recibido (sin repetir ni perder pasos) y lo
  dice; si no puede, muestra «Sin conexión» con «Intentar de nuevo».

**H4 — Detener, reintentar, editar y copiar.** Como *modelador*, quiero controlar la respuesta como en Claude.
- CA1: mientras el agente trabaja, el botón de enviar se vuelve «Detener respuesta»; al detener, lo escrito se conserva, aparece
  el aviso «Detuviste la respuesta» con «Editar el mensaje» y «Intentar de nuevo», y el foco vuelve al compositor.
- CA2: «Reintentar» (en la última respuesta terminada) manda otra vez el último mensaje de la persona; un error del turno
  (tope, proveedor, interrupción) se ve como aviso al final con «Intentar de nuevo».
- CA3: «Editar» (en el último mensaje de la persona, con el turno terminado) lo vuelve editable en su lugar; Esc o
  «Cancelar» lo deja como estaba; «Enviar» (habilitado solo si cambió) lo manda como mensaje nuevo, avisando que el anterior
  queda en el historial.
- CA4: «Copiar» en cada mensaje y en cada bloque de código copia el texto tal cual y lo confirma.

**H5 — La propuesta en el chat.** Como *modelador*, quiero revisar y aplicar la propuesta sin salir de la conversación.
- CA1: la respuesta que entregó la propuesta vigente muestra una tarjeta con el resumen, «Lo que el agente informa» (como
  afirmación suya, con los avisos de R-033-11), los avisos y la vista previa del Modelador.
- CA2: «Revisar y aplicar…» confirma y aplica con la huella; si el modelo cambió (409), la tarjeta se recarga y lo dice; una
  propuesta que una entrega posterior reemplazó se ve como reemplazada, sin acciones.
- CA3: en un entorno con aprobación no se aplica y se descarga el plan; «Descartar» la cierra; aplicar o descartar no cierra
  la conversación.
- CA4: el consumo de la conversación (pasos, herramientas, tokens, costo, mensajes) se ve contra sus topes en un panel
  plegable de la cabecera.

**H6 — Claro, oscuro, móvil y accesibilidad.** Como *persona con cualquier pantalla o forma de navegar*, quiero usar el
chat igual de bien.
- CA1: claro y oscuro con los tokens del proyecto; en ≤ 900 px la barra de conversaciones es un panel que se abre con un
  botón, la columna usa el ancho con 16 px de margen y el compositor queda abajo; sin desborde horizontal.
- CA2: la conversación es un registro (`role="log"`) con mensajes navegables; el estado («Pensando», «Usando…», «Respuesta
  lista», «Reconectando») se anuncia en `aria-live="polite"`, nunca palabra por palabra; cada control tiene nombre accesible.
- CA3: con movimiento reducido no hay fundidos por palabra, ni entrada animada, ni desplazamiento suave; sin violaciones AA
  serias (axe).

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios: más de 6 historias (6: no) · más de ~2 500 líneas de producto (~1 900 estimadas: no) ·
   seguridad o concurrencia junto con UI nueva (no: sin cambio del motor) · más de una pantalla principal (una: no).
   → **No se divide.**
2. **¿El motor necesita algo más?** No. Todo lo que pide el análisis (§8) ya está en la 036: varios turnos, eventos en vivo
   (`waiting`, `text` con el texto acumulado, `tool` con nombre y entrada), detener conservando el texto (queda como paso
   `text`), `after`/`Last-Event-ID`, `planStatus`, `canSend`/`sendBlockedReason`, `turn` por paso y lista por `updatedAt`.
   Sin rutas nuevas: `docs/sdd/rutas.md` no cambia.
3. **Editar y reintentar = mensaje nuevo.** El motor no reescribe el historial (análisis §8.4; la 036 dejó las ramas fuera).
   La pantalla manda el texto editado (o el mismo, al reintentar) como seguimiento y lo dice en el editor («El mensaje
   anterior y su respuesta quedan en la conversación»). Solo sobre el **último** mensaje y con el turno terminado.
4. **Flujo con `fetch` y no con `EventSource`:** `EventSource` reconecta solo cuando el servidor cierra tras `end` (bucle), no
   distingue 404/429 y no se puede abortar con el resto de la pantalla. Con `fetch` + `ReadableStream`: el analizador SSE es
   lógica sin DOM (Vitest), la reconexión usa `after` = último `seq` con espera creciente (1, 2, 4, 8 s, hasta 15 s) y tras
   4 fallas seguidas se detiene con «Sin conexión». `end: timeout` reconecta sin aviso; `identity`/`gone`, lectura completa.
5. **Ritmo del texto:** el estado en vivo llega cada ≥ 250 ms con el texto acumulado. La pantalla lo revela a ~240 car/s
   (más rápido si se atrasa más de 2 s) por palabras completas, y cada palabra nueva entra con fundido de 0,4 s. Al guardarse
   el paso `text`, la pantalla lo pinta ya completo (sin volver a animar lo que ya se vio).
6. **Markdown seguro:** analizador propio en `lib` (sin dependencia nueva) que devuelve un árbol; el componente lo pinta con
   elementos de React. Títulos `#`–`###` se ven como texto destacado (no `h1`–`h3`: no rompen la jerarquía de la página),
   negritas con `strong`, sin `a`, `img`, `b` ni HTML.
7. **Alcances por conversación:** se eligen en la bienvenida (primer mensaje) y no cambian (fuera de alcance de la 036). La
   cabecera de la conversación los muestra.
8. **Sesiones anteriores a la 036** (sin historial): se abren para leer; el compositor muestra el motivo de `sendBlockedReason`.
9. **Lo que no se adopta** (análisis §9): saludo, fuentes, paleta e ícono de Anthropic, voz, adjuntos, proyectos, ramas,
   renombrar o borrar conversaciones.

## Fuera de alcance (anotado en el roadmap)
- Ramas de la conversación (editar un mensaje anterior que no sea el último), renombrar y borrar conversaciones.
- Razonamiento extendido visible (la 036 no lo publica).
- Cambiar los alcances a mitad de la conversación.
- Resaltado de sintaxis en los bloques de código.
