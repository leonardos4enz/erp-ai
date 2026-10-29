# Plan 037 — Diseño técnico

## 1. Modelo de datos
Sin cambios (CLARIFY 2). Ni tablas, ni columnas, ni operaciones del Plan de Cambios.

## 2. Contrato de la API
**Sin rutas nuevas ni cambios de contrato:** la pantalla consume el de la 036 ([plan §2](../036-ai-studio-chat/plan.md)) al pie
de la letra. `docs/sdd/rutas.md` no cambia.

| Uso en la pantalla | Ruta |
|---|---|
| Estado, topes, conversación activa | `GET /api/studio` |
| Barra de conversaciones (por `updatedAt`) | `GET /api/studio/sessions` |
| Herramientas de `datos` (aviso de purga) | `GET /api/studio/tools` |
| Abrir una conversación, y lectura completa al terminar cada turno (propuesta y `reviewHash`) | `GET /api/studio/sessions/{id}` |
| Primer mensaje (crea la conversación con sus alcances) | `POST /api/studio/sessions {prompt, scopes}` |
| Seguimiento, reintentar, editar | `POST /api/studio/sessions/{id}/messages {text}` |
| En vivo: `step`, `session`, `live`, `end` | `GET /api/studio/sessions/{id}/stream?after=n` (con `fetch`, CLARIFY 4) |
| Detener | `POST /api/studio/sessions/{id}/cancel` |
| Aplicar (con la huella) y descartar | `POST …/apply {reviewHash}`, `POST …/discard` |

`types.ts` agrega lo que la 036 ya devuelve: `StudioSession { turns, planStatus, canSend, sendBlockedReason }`,
`StudioSummary { turns, updatedAt }`, `StudioLimits { maxTurns }`, y los eventos `StudioLive`/`StudioStreamSession`.

## 3. SPA
```
StudioPage (admin/studio)            pantalla completa dentro de .content (alto 100 %), sin PageHeader
├─ ConversationBar                   288 px; en ≤ 900 px, panel con fondo que se abre con «Conversaciones»
│    «Nueva conversación», lista por updatedAt (activa: punto que late; abierta: aria-current)
└─ ChatPane
     ├─ ChatHeader                   h1 «ERP AI Studio» · título · alcances · estado · «Consumo» (panel con medidores)
     ├─ Welcome (sin ?s)             saludo, Composer (656 px), alcances (Switch), sugerencias
     └─ Conversation (?s=id)         useStudioChat(id): lectura completa + flujo SSE + reconexión
          ├─ .chat-scroll            role=log (aria-live=off), scrollbar-gutter: stable, columna 736 px
          │    Turn × n              UserBubble (Editar · Copiar · hora) · segmentos: Markdown | ToolGroup | Deliver
          │                          | ProposalCard | aviso de tope/error; el último turno con min-height = alto visible
          │    LiveArea              punto que late «Pensando…» · LiveText (palabras con fundido) · «Usando X…»
          │    TurnNotice            detenida / error / sin conexión, con «Editar el mensaje» e «Intentar de nuevo»
          ├─ «Ir al final»           36 px, sobre el compositor; IntersectionObserver del final del contenido
          └─ Composer                768 px, radio 14, 1 línea → 384 px; Enter/Shift+Enter; Enviar ↔ Detener
```
- **Lógica sin DOM** (Vitest): `lib/studio.ts` (`groupTurns`, `currentProposalSeq`, `createSseParser`, `revealStep`,
  `nextBackoff`, `relativeTime`, `consumption` con mensajes, más lo de la 035) y `lib/markdown.ts` (bloques e inline).
- **Flujo** (`useStudioChat`): lectura completa al abrir; si está activa, `fetch` del flujo con `after` = último `seq`;
  `step` → `mergeTrail`; `session` → contadores y estado; `live` → estado en vivo; `end: idle` → lectura completa y
  actualizar la barra; `timeout` → reconecta; corte sin `end` → reconecta con espera creciente; 4 fallas → «Sin conexión».
  404 → «La conversación ya no existe»; 401 → lectura con `api()` (que lleva al inicio de sesión).
- **Scroll** (análisis §3): al enviar, `scrollTo` del contenedor hasta que la burbuja quede arriba (suave salvo movimiento
  reducido); nada desplaza la vista mientras llega texto. El último turno tiene `min-height` igual al alto visible para que
  la burbuja pueda subir aunque la respuesta sea corta.
- **Ritmo del texto** (CLARIFY 5): `requestAnimationFrame` revela hasta el siguiente límite de palabra a ~240 car/s
  (acelera si va más de 2 s atrás); cada palabra es un `span` que se anima al montarse (0,4 s `ease-out`).
- **Accesibilidad:** región `aria-live="polite"` aparte con el estado («Pensando», «Usando…», «Respuesta lista»,
  «Reconectando», «Detuviste la respuesta»); foco al compositor tras enviar, detener o cancelar la edición; Esc cancela la
  edición y cierra el panel de conversaciones; `prefers-reduced-motion` apaga fundidos, resorte y desplazamiento suave.
- **Estilos:** `styles/studio.css` nuevo (tokens del proyecto); se quitan las reglas `.studio-*` de la 035 de `app.css`.

## 4. Pruebas
- **Vitest** (`lib/__tests__/studio.test.ts` ampliado, `lib/__tests__/markdown.test.ts` nuevo): turnos y segmentos, propuesta
  vigente, analizador SSE con trozos partidos, ritmo del texto, espera creciente, hora relativa, Markdown (subconjunto,
  hostil, patológico lineal) y guardia: ningún archivo de la pantalla usa `dangerouslySetInnerHTML`.
- **E2E** (`e2e/studio.spec.ts` reescrito: un escenario por criterio; el proveedor simulado agrega `chunkMs` para escribir
  despacio): H1–H6, capturas de la bienvenida y de la conversación con su propuesta en claro, oscuro y móvil (las de la 035
  se borran). `pantallas.test.ts` apunta a las capturas nuevas. Lo nuevo, con `--veces=3` (corre lento).
- **Integración:** sin cambios (sin cambio del motor); `StudioTests` se corre en la corrida completa.

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas y caminos nuevos | Ninguna ruta ni operación nueva: la pantalla usa las de la 036 con su capacidad (modelador). Un lector no ve la opción ni llega por URL. | `RouteInventoryTests` (snapshot sin cambios) · `studio.spec.ts › H1 CA2: …un lector no ve Studio ni llega por URL` |
| Caminos alternos | ¿Otro camino al mismo efecto? | Aplicar desde el chat es la misma `…/apply` con la huella de la última lectura completa; reintentar y editar son `…/messages` (no reescriben historial). El flujo y la lectura usan la misma consulta con dueño. | `studio.spec.ts › H5 CA2: …` · `StudioTests › Chat_H3_CA2_no_se_aplica_ni_se_descarta_mientras_un_turno_corre` |
| Lectura de datos | ¿`RecordFilter`, `FieldAccess`, empresa? | No hay lectura nueva: lo que se pinta es lo que la API ya filtró para la dueña. La salida purgada de `datos` se dice como tal. | `StudioTests › Chat_H5_CA3_el_flujo_es_solo_de_la_duena` · `studio.spec.ts › H3 CA2: …` |
| Delegación | ¿Alguien obtiene lo que no tiene? | No aplica en la pantalla: no otorga nada; cada mensaje lo manda la propia persona y el servidor toma su identidad actual. | `StudioTests › Chat_H1_CA4_cada_mensaje_toma_la_identidad_actual` |
| Concurrencia | Invariantes | Un envío a la vez por pantalla (Enter repetido o doble clic: uno); otra pestaña o un turno en curso: 409 con aviso y el texto se conserva. Revisar → aplicar con huella: una propuesta reemplazada no tiene acciones y la huella vieja da 409 (la tarjeta se recarga). | `studio.spec.ts › H2 CA4: …` · `› H5 CA2: …` · `StudioTests › Chat_H1_CA3_diez_mensajes_a_la_vez_encolan_uno` |
| Fallas parciales | Caída entre pasos | Si el flujo se corta a mitad de un turno, se reanuda con `after` = último `seq` recibido; los pasos se unen por `seq` (sin repetir ni perder). Un trozo SSE partido a mitad de evento se completa con el siguiente. Si el envío se aceptó pero el flujo no abre, la lectura completa al reconectar trae lo que falte. | `studio.spec.ts › H3 CA4: …` · `studio.test.ts › createSseParser une eventos partidos entre trozos` · `› mergeTrail…` |
| Límites | Topes | Reconexión con espera creciente (1–15 s) y alto tras 4 fallas seguidas (no hay tormenta de reconexiones; el servidor además limita a `MaxStreamsPerUser`). El Markdown se analiza en tiempo lineal (sin expresiones con retroceso) sobre ≤ 8 000 caracteres por paso. El compositor respeta `maxPromptChars`. | `studio.test.ts › nextBackoff…` · `markdown.test.ts › una entrada patológica se analiza en tiempo lineal` · `studio.spec.ts › H2 CA1: …` |
| Semántica | Nulos, fechas, monedas | Fechas UTC del contrato → hora relativa local; `planStatus` nulo = sin propuesta; turnos sin paso `user` (sesiones anteriores a la 036) toman el pedido de la sesión; costo en USD con 2–4 decimales. | `studio.test.ts › groupTurns…` · `› relativeTime…` |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **T/I** El texto del modelo o una salida de herramienta inyecta HTML o script en la pantalla (XSS) | Alta | Markdown propio que devuelve un árbol de datos; el componente crea elementos de React (texto escapado); sin `dangerouslySetInnerHTML`, sin `a`, `img`, `b` ni atributos que vengan del texto. | `markdown.test.ts › el HTML y los enlaces quedan como texto` · `› ningún archivo de la pantalla usa dangerouslySetInnerHTML` · `studio.spec.ts › H3 CA3: …` |
| **S** Un enlace del modelo que imita la interfaz (phishing) | Media | No hay enlaces activos: `[texto](url)` y las URL se ven como texto. | `markdown.test.ts › el HTML y los enlaces quedan como texto` · `studio.spec.ts › H3 CA3: …` |
| **E** Aplicar una propuesta reemplazada o con una revisión vieja | Alta | Solo la propuesta vigente (`planStatus = proposed`, última entrega con plan) tiene acciones; aplicar usa el `reviewHash` de la última lectura completa, que se hace al terminar cada turno; el servidor da 409 si no coincide. | `studio.test.ts › currentProposalSeq…` · `studio.spec.ts › H5 CA2: …` · `StudioTests › Chat_H3_la_propuesta_se_refina_en_la_conversacion_y_se_aplica_con_su_huella` |
| **D** Tormenta de reconexiones o flujos abiertos de más | Media | Un flujo por conversación abierta, que se cierra al cambiar de conversación o salir; espera creciente y alto tras 4 fallas; `end: idle` cierra. | `studio.test.ts › nextBackoff…` · `studio.spec.ts › H3 CA4: …` |
| **I** El texto que el modelo escribió citando registros se sigue viendo y copiando | Baja | Es de la dueña, que lo vio con su permiso (R-035-1, R-036-5); la pantalla no lo guarda en el navegador (sin borradores en `localStorage`). | [R-035-1](../../riesgos.md#035) |
