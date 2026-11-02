# Spec 047 — Acompañante: panel lateral con avatar

**Estado:** Implementado · **Depende de:** 046 (motor del Acompañante, contrato de `/api/companion`), 037 (pantalla de chat de
Studio: flujo SSE, compositor, texto en vivo, herramientas), 023 (avisos que se aceptan con 428), 030 (guardias de la suite) ·
**Origen:** pedido del dueño (2026-11-01): «un agente que acompaña al usuario dentro del sistema». La 046 dejó el motor
(CLARIFY 1: se dividió); esta es la otra mitad, la pantalla. Decisión del dueño (2026-11-01): la 047 va **antes** de la
auditoría 9, que cubrirá 045–047.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `/api/companion` existe desde la 046 (diez rutas, `docs/sdd/rutas.md`), pero `web/src` no la llama: los tipos `Companion*` de `api/types.ts` no tienen uso y no hay `companionApi` en `api/endpoints.ts`. | Nadie puede usar el Acompañante: solo se llega a él por la API. |
| 2 | Lo único conversacional de la SPA es Studio (`/admin/studio`, solo modeladores) y ocupa toda la pantalla (`StudioPage`, `.chat-page`). | Una persona que captura o aprueba tendría que salir de lo que está viendo para preguntar, y el contexto («este pedido») se pierde. |
| 3 | El hook del chat (`useStudioChat`) y la vista (`ChatView`) están atados a `StudioSession` y a `studioApi`; el compositor fija su etiqueta («Mensaje para ERP AI Studio») y un `id` (`chat-composer-count`). | Reutilizarlos tal cual en un segundo chat no funciona, y copiarlos duplicaría las correcciones de la 037 (F-03, F-04, F-05, F-08). |
| 4 | Un paso `kind: 'action'` trae solo `{accion: id}`; la tarjeta se arma con `actions[]` de la conversación y su vista previa existe solo mientras la acción está `proposed` (`CompanionActions.ToDto`). El tipo `CompanionPreview` de la SPA dice `explicacion`; el servidor manda `segunElAcompanante` (046 F-03). | Sin una tarjeta clara la persona confirmaría sin leer (R-046-2), y con el tipo actual la explicación del modelo no se mostraría. |
| 5 | El avatar del dueño (`<fleet-eyes>`) es un *custom element* con Shadow DOM y un `<style>` dentro; la CSP de la SPA es `style-src 'self'` (`SecurityHeaders`). | Copiado tal cual, sus estilos no cargarían (violación de CSP en consola, que la guardia de la suite convierte en falla). |

## Principio
Es pantalla sobre una capacidad que ya existe: **no hay rutas, tablas ni permisos nuevos**. El panel habla solo con el contrato
de la 046 (plan §2) y por eso hereda sus reglas: lee con la identidad de la persona, cada escritura es una acción que ella
confirma con la huella y la ejecuta el servidor por la ruta de datos de siempre (ADR-0010). Lo que el panel agrega es que esa
confirmación sea **informada**: la vista previa la arma el servidor y se muestra aparte del texto del modelo.
El chat se **comparte** con Studio: el hook del flujo SSE pasa a ser genérico (`useAgentChat`) y Studio y el Acompañante lo
usan con su propia API; el compositor, el texto en vivo, el Markdown seguro y el grupo de herramientas son los mismos.

## Historias

**H1 — El Acompañante a la mano en toda la app.** Como *cualquier persona del sistema*, quiero abrir un panel a la derecha sin
salir de lo que estoy viendo, para preguntar en el momento.
- CA1: la barra superior tiene «Acompañante» en toda pantalla con el menú; abre y cierra un panel lateral derecho (estilo el
  chat de Cursor) que empuja el contenido en escritorio y flota sobre él en pantallas angostas (≤ 900 px), con Esc y fondo
  para cerrar. El panel recuerda si quedó abierto (por navegador) y no se cierra al navegar.
- CA2: estados con diseño: *cargando* (al pedir `GET /api/companion`), *error* (con «Intentar de nuevo»), *no disponible*
  (`available: false`, con el `reason` del servidor) y *vacío* (bienvenida con sugerencias en lenguaje de usuario).
- CA3: accesible: el botón dice si el panel está abierto (`aria-expanded`), el panel es una región con nombre, el foco entra al
  compositor al abrir y vuelve al botón al cerrar; todo se opera con teclado.

**H2 — Conversar como en Studio.** Como *persona*, quiero ver que el Acompañante está pensando, qué consulta y su respuesta
mientras la escribe, y seguir la conversación, para entender qué hace.
- CA1: al enviar, el mensaje se ve al instante; mientras el turno corre se ve «Pensando…», la consulta en curso y el texto en
  vivo (flujo SSE de la 046, el mismo código que Studio); «Detener respuesta» corta el turno.
- CA2: multi-turno: cada mensaje sigue la conversación abierta; las consultas aparecen agrupadas («Consultó 2 cosas») con
  nombres en lenguaje de usuario (no `buscar_registros`), sin entradas ni salidas técnicas.
- CA3: si el flujo se corta, «Reconectando…» y, tras varios intentos, «Sin conexión» con «Intentar de nuevo» (como Studio).

**H3 — Un avatar que dice en qué está.** Como *persona*, quiero una cara que muestre el estado del Acompañante, para saber de un
vistazo si está trabajando o si me espera.
- CA1: el avatar (portado de `<fleet-eyes>`, código del dueño) tiene seis estados: en reposo, pensando, usando una
  herramienta, esperando tu confirmación, listo y error; cambia solo según la conversación.
- CA2: es decorativo (`aria-hidden`; el estado se anuncia en texto), usa los tokens de color (claro y oscuro), con
  `prefers-reduced-motion` se queda quieto (sin parpadeo ni saltos) y no viola la CSP: sus estilos están en la hoja de la SPA
  y la animación mueve atributos SVG.

**H4 — Mis conversaciones.** Como *persona*, quiero ver mis conversaciones anteriores, retomar una y borrar las que ya no quiero,
para no repetirme y no dejar datos guardados.
- CA1: «Conversaciones» lista las mías (título, hace cuánto, si tiene propuestas pendientes o trabaja); abrir una la retoma
  con sus pasos y tarjetas; «Nueva conversación» vuelve a la bienvenida.
- CA2: borrar pide confirmación; con un turno en curso el servidor responde 409 y el panel lo dice; borrada, sale de la lista.

**H5 — Confirmo cada acción sabiendo qué hace.** Como *persona*, quiero que cada propuesta del Acompañante me muestre qué va a
cambiar y que nada pase hasta que yo la confirme, para no aplicar algo que no entendí.
- CA1: la tarjeta muestra la vista previa del servidor —crear o editar: campo, antes → después; transición: de qué estado a
  cuál y el comentario— y, aparte y marcada como suya, la explicación del Acompañante (`segunElAcompanante`, como texto).
- CA2: «Confirmar» manda la huella (`hash`); si el servidor responde 428, la tarjeta muestra los avisos y pide confirmar otra
  vez aceptándolos (`acceptWarnings`); «Descartar» la descarta. Un rechazo (400, 403, 409) se muestra en la tarjeta.
- CA3: estados de la tarjeta: pendiente, aplicándose, aplicada (con enlace al registro), no se aplicó (con su mensaje),
  descartada, caducó y «sin confirmar si se aplicó» (con el aviso de revisar el registro antes de repetir).

**H6 — Sabe qué estoy viendo.** Como *persona*, quiero que el Acompañante sepa en qué catálogo o registro estoy, para preguntar
«¿por qué no puedo aprobar este?» sin explicarlo.
- CA1: cada mensaje manda `context { entity, id }` de la pantalla actual (lista, registro nuevo o registro); el panel muestra
  «Viendo: Pedidos · n.º 12» y se actualiza al navegar. En otras pantallas no manda contexto.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios: más de 6 historias (6, no) · más de ~2 500 líneas de producto (~1 600: no) · seguridad o
   concurrencia junto con UI nueva (la seguridad es del motor de la 046; aquí, la confirmación informada: no) · más de una
   pantalla principal (un panel con dos vistas: no). → **No se divide.**
2. **Reutilizar, no duplicar.** `useStudioChat` se vuelve `useAgentChat` (genérico sobre la conversación y su API) y Studio lo
   usa igual que antes; `ChatView` exporta sus piezas (`UserBubble`, `LiveArea`, `Notice`); `Composer` recibe su etiqueta y
   usa un `id` propio; `ToolGroup` acepta nombres para personas y ocultar entrada y salida; `groupTurns` reconoce el paso
   `action`. Las pruebas de Studio (`studio.spec.ts`, Vitest) cuidan que Studio no cambie.
3. **Avatar.** `<fleet-eyes>` (`Grok Bot Harness/agent-fleet/orchestrator/panel/fleet-eyes.js`, derivado de `mascot.js` de
   *AI Engineer*; ambos del dueño, sin licencia de terceros) se porta a `CompanionAvatar.tsx`: misma física (resortes,
   sacádicos, parpadeo, salto con squash & stretch) movida con atributos SVG en un `requestAnimationFrame` que solo corre
   visible y con la pestaña activa; las poses y los accesorios de la flota se cambian por los del chat (reposo → `idle`,
   pensando → `think` con libreta, herramienta → `code` con llave, esperando → `alert` con globo y salto, listo → `happy` con
   palomita, error → `fail` con nube). Sin la etiqueta en la frente ni seguir al puntero (no aportan en un chat).
4. **Lenguaje de usuario.** Los nombres de las herramientas se muestran como acciones («Buscó registros», «Revisó por qué no
   puedes»); una prueba de Vitest lee `CompanionTools.cs` y falla si una herramienta nueva no tiene nombre para personas.
5. **Quién lo ve.** Toda persona con sesión en la SPA (las cuentas de servicio no entran a la SPA; el servidor les da 403 de
   todos modos). El botón no se oculta sin clave: el panel dice por qué no está disponible (H1 CA2).
6. **Tras confirmar**, la tarjeta enlaza al registro; la pantalla que la persona tiene abierta no se recarga sola (sería
   perder lo que está capturando). Queda como riesgo (R-047-2).

## Fuera de alcance (anotado en el roadmap)
- Guías paso a paso que señalan el control en pantalla; modo instructor con recorridos.
- Eliminar registros (y otras acciones: imprimir, exportar, importar) por el Acompañante.
- Unificar el bucle de Studio y el del Acompañante (R-046-1).
- Atajo de teclado para abrir el panel (Ctrl+L, el de Cursor, es la barra de direcciones del navegador).
