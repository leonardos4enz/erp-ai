# Plan 047 — Diseño técnico

## 1. Modelo de datos
Ninguno. Sin tablas, columnas ni operaciones de plan nuevas: el panel usa lo que dejó la 046.

## 2. Contrato de la API
Sin rutas nuevas ni cambiadas (`docs/sdd/rutas.md` no cambia). El panel usa el contrato de la
[046, plan §2](../046-acompanante/plan.md#2-contrato-de-la-api), por `companionApi` (nuevo en `web/src/api/endpoints.ts`):

| Uso en el panel | Ruta (046) | Qué hace el panel con la respuesta |
|---|---|---|
| Abrir el panel | `GET /api/companion` | `available: false` → estado «no disponible» con `reason`; `activeId` → abre esa conversación; error → estado de error |
| Historial | `GET /api/companion/conversations` | Lista (título, `updatedAt`, `status`, `pendingActions`) |
| Empezar | `POST /api/companion/conversations { text, context? }` | 202 con la conversación → la abre y sigue su flujo; 409/429/503 → aviso en el panel (`role="alert"`) |
| Leer | `GET /api/companion/conversations/{id}` | `trail` (pasos), `actions` (tarjetas), `canSend`/`sendBlockedReason` |
| Seguir | `POST …/{id}/messages { text, context? }` | Como Studio: mensaje pendiente al instante y flujo |
| Flujo | `GET …/{id}/stream?after=n` (SSE: `step`, `session`, `live`, `end`) | Mismo analizador y reconexión que Studio (`useAgentChat`) |
| Detener | `POST …/{id}/cancel` | Lectura completa devuelta |
| Borrar | `DELETE …/{id}` | 204 → sale de la lista; 409 → aviso |
| Confirmar | `POST /api/companion/actions/{id}/confirm { hash, acceptWarnings? }` | 200 `{ action, record }` → tarjeta aplicada; 428 → avisos (`fieldErrors`) y «Confirmar con los avisos»; 400/403/409 → mensaje en la tarjeta y relectura |
| Descartar | `POST /api/companion/actions/{id}/discard` | 200 acción → tarjeta descartada; 409 → mensaje y relectura |

Corrección de tipos (sin cambio de servidor): `CompanionPreview.explicacion` → `segunElAcompanante` (lo que manda el servidor
desde la 046 F-03); el paso `StudioStep.kind` admite `'action'` (con `output: { accion: id }`).

## 3. SPA
```
Layout (Shell)
 ├─ topbar: botón «Acompañante» (avatar chico, aria-expanded, aria-controls)
 └─ <aside class="cmp-panel" aria-label="Acompañante">  (escritorio: columna derecha; ≤ 900 px: flota con fondo)
      CompanionPanel
       ├─ cabecera: CompanionAvatar · «Acompañante» · Conversaciones · Nueva · Cerrar
       ├─ estado: cargando | error | no disponible | historial | bienvenida | conversación
       ├─ CompanionChat (useAgentChat + piezas de studio/ChatView: UserBubble, LiveArea, Notice, ToolGroup, Markdown, LiveText)
       │    └─ ActionCard por paso `action` (con la acción de `actions[]`)
       └─ Composer (de Studio) + «Viendo: …» (contexto de la pantalla)
```
- **Reutilización** (CLARIFY 2): `web/src/pages/studio/useStudioChat.ts` pasa a `useAgentChat<S>(id, api, …)` con
  `api = { load, send, cancel, streamUrl, sent }`; `useStudioChat` queda como envoltura de una línea. `Composer` gana `label`
  y un `id` de `useId`. `ToolGroup` gana `labels` (nombre para personas) y `plain` (sin entrada ni salida). `groupTurns`
  agrega el segmento `action`. `liveLabel` acepta los nombres para personas.
- **Lógica sin DOM** (`web/src/lib/companion.ts`, Vitest): `avatarState(conversación, en vivo, error)`; `actionView(acción)`
  (título, tono, texto del estado); `screenContext(ruta)` (`/c/:entity`, `/c/:entity/new`, `/c/:entity/:id`);
  `TOOL_LABELS`; `warningsOf(error 428)`.
- **Avatar** (`web/src/components/companion/CompanionAvatar.tsx` + `web/src/styles/companion.css`): SVG con las dos píldoras,
  párpados como máscara, línea de ojo cerrado, rubor y seis accesorios; un `requestAnimationFrame` por avatar que corre solo si
  es visible (`IntersectionObserver`) y la pestaña está activa; mueve `transform`, `d`, `opacity` y `stroke-width` como
  atributos (no hay `style` en línea en el marcado); los accesorios se animan con `@keyframes` de la hoja (nombre en el atajo,
  lección 032) y `prefers-reduced-motion` los deja quietos. Colores: `--accent` (cara), `--on-accent` (ojos) y el tono del
  estado (`--text-subtle`, `--accent`, `--warning`, `--success`, `--danger`).
- **Accesibilidad**: el panel es `<aside aria-label="Acompañante">`; el estado del avatar se anuncia en una región `status`
  («Pensando…», «Esperando tu confirmación»); las tarjetas son `article` con nombre («Propuesta: Editar Pedido n.º 12»); los
  rechazos y los avisos de 428 son `role="alert"` dentro de la tarjeta (guardia de la 030). En pantallas angostas, Esc cierra
  y el foco vuelve al botón.
- **Estado del panel**: abierto/cerrado en `localStorage` (`erp.companion.open`, con `safeStorage*`); en ≤ 900 px arranca
  cerrado. La conversación abierta se conserva al navegar (el panel vive en el `Shell`, fuera del `Outlet`).

## 4. Pruebas
- **Vitest** (`web/src/lib/__tests__/companion.test.ts`, nuevo): estado del avatar por conversación (cola, pensando,
  herramienta, propuesta pendiente, respondida, fallida); vista de cada estado de acción; contexto por ruta (y ninguno fuera
  de `/c/`); avisos de un 428; **todas** las herramientas de `CompanionTools.cs` tienen nombre para personas; `groupTurns` con
  pasos `action`. `pantallas.test.ts` sigue igual (sin rutas nuevas).
- **E2E** (`web/e2e/acompanante.spec.ts`, nuevo; proveedor simulado como `studio.spec.ts`, en `PORTS.asistente`): un escenario
  por criterio (H1–H6), con un usuario sin rol de modelador; doble clic en «Confirmar»; captura de referencia del panel en
  claro, oscuro y móvil (con el avatar quieto: `reducedMotion: 'reduce'`).
- **Studio no cambia**: `studio.spec.ts` y las pruebas de `lib/studio` corren en la corrida completa; el botón nuevo de la
  barra superior cambia las capturas de `visual*.spec.ts` (se revisan una por una).
- Integración: sin cambios de servidor; las filas de concurrencia y fallas parciales del servidor ya tienen su prueba en
  `CompanionTests` (046) y se citan abajo.

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas y caminos nuevos | Ninguna ruta nueva: el panel llama diez rutas existentes con su capacidad (046). Un camino de UI nuevo a una escritura: «Confirmar» en la tarjeta, que manda el `hash` que el servidor entregó con la vista previa y nada más (ni catálogo, ni cuerpo, ni registro). | `acompanante.spec.ts › H5 CA2 · CA3: confirmar manda la huella y aplica una vez (doble clic); la tarjeta enlaza al registro` · `RouteInventoryTests` (sin cambios) |
| Caminos alternos | ¿Otro camino al mismo dato o efecto? | Las acciones solo se confirman por la tarjeta (o la API de la 046, con las mismas reglas). La tarjeta no arma la vista previa: la pinta del servidor (`preview`); sin vista previa (decidida o caducada) no hay botón. Studio y el Acompañante comparten el hook pero no la API ni las tablas. | `› H5 CA3: aplicándose, caducó y «sin confirmar si se aplicó» se dicen en la tarjeta` · `companion.test.ts › H5 CA1/CA3: la tarjeta por tipo y estado; sin vista previa no se ofrece confirmar` · `studio.spec.ts` (corrida completa) |
| Lectura de datos | ¿`RecordFilter`, `FieldAccess`, empresa? | Lo aplica el servidor (046). El panel manda el contexto de la pantalla sin revisar permisos: el servidor lo ignora si la persona no ve el catálogo (046 H2 CA3). El historial y la conversación son de la persona (404 a otra). Al cambiar de empresa el `Shell` vuelve a montar el panel. | `CompanionTests › H2_CA3_el_contexto_de_un_catalogo_ajeno_se_ignora` · `› H1_CA2_las_conversaciones_son_de_su_duena` · `acompanante.spec.ts › H6 CA1: manda el contexto de la pantalla y lo muestra` |
| Delegación | ¿Alguien obtiene lo que no tiene? | No: el panel no otorga nada; confirmar se ejecuta con la sesión de quien hace clic (046). Un usuario sin rol de modelador usa el panel sin ver Studio. | `acompanante.spec.ts › H1 CA1 · CA3: una persona sin rol de modelador abre el panel en cualquier pantalla, con teclado, y lo recuerda` · `CompanionTests › H4_CA3_otra_persona_no_confirma_y_diez_a_la_vez_ejecutan_una` |
| Concurrencia | Invariantes | Doble clic en «Confirmar»: el botón queda ocupado (ignora clics) desde el primero y el servidor ejecuta una vez (diez a la vez → una). Dos lecturas que se cruzan no pisan una más nueva (turnos de la 037 F-04, ahora en `useAgentChat`); una lectura vieja tampoco devuelve a «pendiente» una acción que la tarjeta ya vio decidida (revisión adversarial F-04). «Revisar → aplicar»: la tarjeta confirma con la huella de la acción que pinta (fija por acción) y la acción lleva la versión del registro de su vista previa: si el registro cambió, 409 y la tarjeta relee. | `acompanante.spec.ts › H5 CA2 · CA3: confirmar manda la huella y aplica una vez (doble clic); la tarjeta enlaza al registro` · `acompanante.spec.ts › H5 CA2 · CA3: un rechazo se muestra en la tarjeta y descartar la cierra` · `companion.test.ts › Regresión (revisión adversarial 047, F-04): …` · `CompanionTests › H4_CA3_…diez_a_la_vez_ejecutan_una` · `› H4_CA2_una_huella_distinta_no_se_aplica` |
| Fallas parciales | Caída entre pasos | Si la respuesta de confirmar no llega (red, 5xx), la tarjeta dice que no se sabe si se aplicó, relee la conversación y muestra el estado real (aplicada, `applying` o `unknown`), nunca repite sola; una falla al releer el estado del panel no desmonta la conversación (revisión adversarial F-01, F-02). El flujo SSE se reanuda con `after` y, sin conexión, lo dice. | `acompanante.spec.ts › Regresión (revisión adversarial 047, F-01 · F-02): …` · `› H5 CA3: aplicándose, caducó y «sin confirmar si se aplicó» se dicen en la tarjeta` · `CompanionTests › Fallas_una_accion_que_quedo_aplicandose_no_se_repite` · `studio.spec.ts` (reconexión, mismo hook) |
| Límites | Topes | Compositor con `maxPromptChars` del servidor; historial: las 50 de la API; avisos de 428: los que manda el servidor (≤ 50 al reenviar, como `CompanionActions`: con más de 50 la persona no puede aceptarlos todos, [R-047-5](../../riesgos.md#047)); un solo `requestAnimationFrame` compartido por todos los avatares, detenido fuera de vista. Un 429 al empezar o seguir se muestra como el aviso de cualquier rechazo (`role="alert"`, guardia de la 030). | `companion.test.ts › H5 CA2: avisos de un 428 (los mensajes tal cual, sin repetir, a lo más 50)` · `acompanante.spec.ts › H1 CA2: cargando, error con «Intentar de nuevo» y no disponible sin clave` |
| Semántica | Nulos, fechas | «(vacío)» para un antes o un después nulo; las fechas relativas con `relativeTime` de Studio (zona del navegador); los valores de la vista previa se muestran como texto, nunca como marcado. | `companion.test.ts › Semántica: un valor vacío se muestra (vacío) y nada se pinta como marcado` · `acompanante.spec.ts › H5 CA1 · H3 CA1: vista previa aparte de la explicación, como texto, y el avatar espera la confirmación` |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** El texto del modelo se hace pasar por la vista previa («esto solo cambia el teléfono») | Media | La vista previa se pinta de `preview.cambios`/`desde`/`hacia` (servidor); la explicación va en otro bloque, con el título «Según el Acompañante» y como texto plano (sin Markdown ni enlaces); el comentario de una transición, que también escribe el modelo y es lo que se guardará, va marcado como tal (revisión adversarial F-08). | `acompanante.spec.ts › H5 CA1 · H3 CA1: vista previa aparte de la explicación, como texto, y el avatar espera la confirmación` · [R-046-2](../../riesgos.md#046) |
| **T** La persona confirma algo distinto de lo que ve (la tarjeta muestra una versión vieja) | Alta | La tarjeta manda la huella de la acción que pinta; el servidor rechaza otra (409) y la tarjeta relee. Una acción que dejó de estar pendiente pierde sus botones al releer. | `CompanionTests › H4_CA2_una_huella_distinta_no_se_aplica` · `› H4_CA2_un_registro_que_cambio_no_se_sobrescribe` · `acompanante.spec.ts › H5 CA2 · CA3: un rechazo se muestra en la tarjeta y descartar la cierra` (409 por la versión del registro; desde la UI no se puede mandar otra huella) |
| **I** XSS desde un registro o desde el modelo (texto hostil en la respuesta, en la vista previa o en el título) | Alta | Todo se pinta como texto de React o con el Markdown seguro de la 037 (sin HTML, sin enlaces); los valores de la vista previa son texto. | `acompanante.spec.ts › H5 CA1 · H3 CA1: vista previa aparte de la explicación, como texto, y el avatar espera la confirmación` (texto hostil, sin `window.__xss`) |
| **I** El panel queda abierto con una conversación de otra empresa | Baja | El `Shell` vuelve a montar el panel al cambiar de empresa (`key` de la empresa) y olvida la conversación; el servidor da 404 fuera de su empresa (046 F-07) y el panel vuelve a la bienvenida. Sin prueba de pantalla. | `CompanionTests › Regresion_F07_fuera_de_la_empresa_no_se_lee_ni_se_confirma` |
| **D** El avatar consume CPU en segundo plano | Baja | Un rAF que se detiene sin visibilidad (`IntersectionObserver`, `visibilitychange`) y con movimiento reducido. | [R-047-1](../../riesgos.md#047) |
| **E** Confirmar sin leer (la persona hace clic por costumbre) | Media | La tarjeta muestra qué cambia antes del botón, el botón dice qué hace («Confirmar y aplicar») y los avisos piden otra confirmación explícita. | [R-046-2](../../riesgos.md#046) · `acompanante.spec.ts › H5 CA2: los avisos piden confirmar otra vez (428) y la transición se aplica` |
