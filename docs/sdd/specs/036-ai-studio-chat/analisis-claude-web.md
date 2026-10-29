# Análisis: cómo se comporta el chat de Claude web (fase 1 de la 036)

**Para qué:** el dueño pidió (2026-10-29) que ERP AI Studio se comporte «como Claude», incluso en animaciones de carga,
espera de mensajes, tamaño de los *inputs* y de la conversación, efectos y transiciones. Este documento registra lo
observado y medido, con su fuente, para que la **037 (pantalla de chat)** lo aplique y la **036 (motor)** dé los eventos que
esa pantalla necesita. **Comportamiento sí, marca no:** se adoptan los patrones de interacción; el logo, el ícono, las
fuentes (Anthropic Serif/Sans), la paleta y los textos de Anthropic no. La pantalla sigue con los tokens, el claro/oscuro,
Lucide y la identidad de ERP AI.

## Fuentes y método
| Clave | Fuente |
|---|---|
| **M** | Medido en claude.ai, 2026-10-29, Chrome del dueño (extensión Claude in Chrome, con su sesión: no se inició sesión ni se escribió ninguna credencial), ventana 1920×945, `devicePixelRatio` 1, tema oscuro, modelo Opus 5.5. Con `getComputedStyle`, `getBoundingClientRect`, un `MutationObserver` y un muestreo cada 50 ms de la longitud del texto y del `scrollTop` del contenedor de la conversación. Se usó un **chat incógnito** (sin historial) con tres mensajes de prueba: uno con búsqueda web, tabla y código; uno con razonamiento largo; uno detenido a mitad. |
| **C** | Captura de pantalla del mismo recorrido (no se guardan en el repositorio: muestran la cuenta del dueño). |
| **CSS** | Reglas leídas de las hojas de estilo de la página (`@keyframes` y clases de animación). |
| **N** | No medido: se deja como decisión de la 037 con su razón. |

Los títulos de la barra lateral del dueño se leyeron solo para medir la lista y **no** se registran aquí.

## 1. Layout
| Elemento | Medida | Fuente |
|---|---|---|
| Barra lateral de conversaciones | 288 px de ancho, alto completo, fija a la izquierda. Elementos de 32 px de alto, radio 8 px; botón «más opciones» de 24 px con radio 6 px que aparece a la derecha de cada conversación. Secciones «Fijados» y «Recientes» plegables (28 px). Botón para ocultarla arriba a la izquierda (28 px). Abajo, la cuenta (32 px). | M |
| Columna de la conversación | Texto del asistente a **736 px** de ancho, centrado. Mensaje de la persona alineado a la derecha, ancho al contenido hasta ~620 px (el resto queda como margen izquierdo). | M |
| Compositor en la conversación | Fijo abajo, **768 px** de ancho (un poco más ancho que el texto), radio **14 px**, relleno 8 px, fondo una superficie más clara que la página, borde interior de 1 px al 8 % de blanco y sombra `0 4px 20px` al 7,5 %. Debajo, una fila de controles y una nota pequeña centrada. | M |
| Compositor en la bienvenida | Centrado vertical bajo un saludo; 656 px de ancho y 103 px de alto (dos filas: texto y controles). | M, C |
| Altura del área de texto | Mínima de **57 px** en la bienvenida y de una fila (28 px de fila + 8 px de relleno = **46 px** de tarjeta) en la conversación. Crece **21 px por línea** (`line-height` 21 px, letra 15 px) hasta **384 px** (`max-height: 24rem`); después se desplaza por dentro. Al vaciarse vuelve a la mínima. Transición de rellenos 200 ms `cubic-bezier(0.4, 0, 0.2, 1)`. | M (22 líneas insertadas una por una) |
| Teclas | **Enter** envía; **Shift+Enter** inserta una línea. Tras enviar, el foco sigue en el compositor (vacío). | M |
| Botón de enviar / detener | 28×28, radio 8 px, a la derecha del área de texto. Mientras responde se convierte en **«Detener respuesta»** (cuadrado relleno) en el mismo lugar. Sin texto escrito, el lugar lo ocupa otra acción (voz). | M, C |

## 2. Estados
### 2.1 Vacío / bienvenida
Saludo grande centrado con el nombre de la persona, compositor debajo y tarjetas de contenido reciente. En la 037: saludo
con el nombre y **sugerencias de pedidos** de ERP (no las tarjetas de Anthropic). (M, C)

### 2.2 Enviando y esperando
- El mensaje de la persona aparece al instante como burbuja: fondo blanco al **5 %**, radio **12 px**, relleno **12 px 16 px**,
  letra 15 px. Hora relativa («ahora», «hace 1 minuto») y acciones **Reintentar · Editar · Copiar** (24 px) debajo, a la
  derecha, **solo al pasar el cursor** (opacidad 0 → 1). (M, C)
- **El mensaje enviado sube al borde superior de la vista** con un desplazamiento suave de ~1,2 s (el `scrollTop` pasó de
  0 a 1 627 en ~1,25 s) y deja espacio en blanco debajo para la respuesta. No se pega al final: la respuesta crece hacia
  abajo desde ahí. (M, muestreo de `scrollTop`)
- Mientras no hay nada, un **punto pequeño del color de acento que late** (`pulse-dot`: escala 0,75 ↔ 1). (M, CSS, C)

### 2.3 Pensando / razonando
- Una línea compacta a la izquierda: **tres puntos animados**, un resumen corto de lo que piensa y un **contador de
  segundos** («6s»), con un triángulo para expandir. El resumen **se reemplaza** a medida que avanza (cambió dos veces en
  ~0,5 s); cada texto nuevo entra con `cds-morph-in`: opacidad 0 y `translateY(3px)` → 0 en **180 ms
  `cubic-bezier(0.2, 0, 0, 1)`**. (M, CSS, C)
- Al empezar el texto, la línea **se pliega** a una sola etiqueta en gris sin animación de puntos («Recalling what I
  know…»), expandible. Las etiquetas de estado tienen `aria-live="polite"`. (M, C)

### 2.4 Streaming del texto
- El contenedor de la respuesta entra con `message-fade-up`: opacidad 0 y `translateY(10px)` → 0 en **1,1 s** con una curva
  de resorte (`linear(...)` de muchos puntos; se queda ~10 % por encima y se asienta). Con movimiento reducido no se anima. (CSS, M)
- **Sin cursor parpadeante.** Cada palabra nueva entra con **fundido de 0,4 s `ease-out`** (`fadeInTextReveal`, clase
  por palabra). (CSS, M: 194 fragmentos animados en una respuesta)
- **Ritmo:** actualizaciones cada **50–110 ms** con **~18 caracteres** cada una (promedio de 237 actualizaciones);
  **~240 caracteres por segundo** sostenidos (4 365 caracteres en 18,2 s). Es decir, se suaviza por palabras, no se pinta
  por token crudo ni por bloques grandes. (M)
- Primera palabra a ~12 s del envío en el mensaje con razonamiento largo y a ~2,5 s del estado de herramienta en el mensaje
  con búsqueda. (M)
- Tipografía de la respuesta: 15 px / 21 px, párrafos sin margen extra (el espacio lo da el contenedor). (M) En ERP AI: la
  fuente del sistema de diseño, no la de Anthropic.

### 2.5 Uso de herramientas en línea
- Mientras corre: una línea **«Buscando en la web ▸»** con el punto que late. Al terminar, en pasado y en gris: **«Buscó en
  la web ▸»**. (M, C)
- Al expandirla: una **tarjeta** (sin borde visible, fondo de superficie) con **filas de 37 px**, letra **14 px**: cada uso
  de herramienta es una fila «Búsqueda en la web completada · *consulta* ▸», intercalada con las líneas de razonamiento
  («Checking whether…»). Cada fila de herramienta se expande a su vez y muestra la **salida** en un panel interior (fondo
  blanco al 5 %, radio 8 px; en la búsqueda: lista de resultados con título y dominio). Curva de la transición de plegado:
  `cubic-bezier(0.165, 0.84, 0.44, 1)`. (M, C)
- La tarjeta queda **arriba de la respuesta**, plegada por omisión una vez terminada. (C)
- **Error de herramienta:** no se provocó (no hay forma segura desde la cuenta del dueño). La 037 lo resuelve con el mismo
  patrón: la fila en pasado con un ícono de error y el mensaje seguro de la API al expandirla. (N)

### 2.6 Detener
- El botón «Detener respuesta» corta el texto **donde iba (incluso a mitad de palabra)**; las palabras ya recibidas
  terminan de fundirse (~200 caracteres más en 1,5 s). (M)
- Debajo aparece un aviso en una tarjeta: *ícono de información* · «La respuesta … fue interrumpida» · **«Editar prompt»** ·
  **«Intentar de nuevo»**. El foco vuelve al compositor. (M, C)

### 2.7 Reintentar, editar y copiar
- **Editar** (en el mensaje de la persona) lo convierte en un área editable **en el mismo lugar**, con anillo de foco del
  color de acento y **Cancelar** / **Guardar** debajo (Guardar deshabilitado hasta cambiar algo; un ícono de información
  advierte que se crea otra rama). **Esc** cancela. (M, C)
- **Reintentar** y **Copiar** debajo de cada respuesta (24 px): siempre visibles en la **última** respuesta, solo al pasar
  el cursor en las anteriores. Los bloques de código tienen su propio «Copiar al portapapeles» (28 px) arriba a la derecha. (M)

### 2.8 Error de red
No se provocó (cortar la red de la cuenta del dueño no es seguro). La 037: aviso en tarjeta como el de «detener», con
«Intentar de nuevo», y reconexión automática del flujo con `after` sin perder lo recibido. (N)

## 3. Scroll
- **No hay auto-scroll durante el streaming.** Tras enviar, el mensaje de la persona queda arriba y la respuesta crece
  hacia abajo; el `scrollTop` **no cambió** mientras llegaban 4 000+ caracteres (se quedó en 1 627 y la altura creció de
  2 514 a 3 269). Así nunca hay que «pelear» con la vista para leer. (M)
- Si la persona sube, la vista se queda donde la dejó (M: tras subir 3 muescas, `scrollTop` fijo en 1 327 hasta el final).
- **«Ir al final»**: botón redondo de **36 px** centrado sobre el compositor, fondo de superficie al 80 %, borde de 1 px al
  4 %, sombras `0 2px 4px` y `0 6px 16px` al 24 %. Aparece cuando el final queda fuera de la vista. (M)
- El contenedor tiene `scrollbar-gutter: stable` (el ancho no salta al aparecer la barra) y desvanece el borde superior e
  inferior cuando hay contenido detrás (`scroll-fade`). (M)

## 4. Animaciones y transiciones (resumen)
| Qué | Duración y curva | Fuente |
|---|---|---|
| Entrada de un mensaje | 1,1 s, resorte (`linear()`), `opacity 0 → 1`, `translateY(10px) → 0` | CSS |
| Palabra nueva en streaming | 0,4 s `ease-out`, `opacity 0 → 1` | CSS |
| Etiqueta de estado nueva | 180 ms `cubic-bezier(0.2, 0, 0, 1)`, `opacity`, `translateY(3px)` | CSS |
| Punto de espera | `pulse-dot`: escala 0,75 ↔ 1 (bucle) | CSS |
| Esqueletos de carga | `cds-skeleton-breathe` 2 s `ease-in-out`, opacidad 1 ↔ 0,6 | CSS, M |
| Compositor (fondo, borde, sombra, rellenos) | 200 ms `cubic-bezier(0.4, 0, 0.2, 1)` | M |
| Plegar y desplegar | `cubic-bezier(0.165, 0.84, 0.44, 1)` | M |
| Llevar el mensaje enviado arriba | ~1,2 s, desplazamiento suave | M |
| Acciones al pasar el cursor | opacidad 0 → 1 | M |

**Movimiento reducido:** con `prefers-reduced-motion: reduce` la entrada del mensaje no se anima (regla `@media` leída).
La 037 lo extiende a todo: sin fundidos por palabra, sin resorte y sin desplazamiento suave. (CSS)

## 5. Markdown, código y tablas
| Elemento | Medida | Fuente |
|---|---|---|
| Listas | sangría 30 px, sin margen entre puntos | M |
| Tablas | ancho de la columna (732 px), sin bordes verticales; encabezado en peso 580 con línea inferior de 1 px al 10 %; celdas con 7,5 px de relleno vertical y separadas por líneas | M, C |
| Bloques de código | radio 12 px, relleno 14 px, letra monoespaciada 14 px / 22,75 px, etiqueta del lenguaje arriba a la izquierda, botón copiar arriba a la derecha, desplazamiento horizontal propio (`overflow-x: auto`, `white-space: pre`), resaltado de sintaxis | M, C |
| Citas de fuentes | píldoras de 12 px de letra, 18 px de alto, radio completo, relleno 0 6 px | M |

**Decisión para ERP AI (constitución II.8 y R-033-4):** el texto del modelo es una entrada no confiable. La 037 interpreta
un **subconjunto seguro** (párrafos, listas, negritas, código en línea, bloques de código y tablas) **construyendo
elementos de React**, nunca HTML: sin `dangerouslySetInnerHTML`, sin enlaces activos (una URL se ve como texto), sin
imágenes. Lo que no entra en el subconjunto se ve como texto.

## 6. Móvil
No se pudo medir: la ventana del dueño no se dejó reducir desde la extensión (estaba maximizada) y no se cambió su
configuración. Las clases de la página muestran puntos de quiebre `md:` en el compositor (relleno lateral menor en
pantallas chicas). La 037 decide con el criterio de ERP AI (≤ 900 px): la barra de conversaciones pasa a un panel que se
abre con un botón, la columna ocupa el ancho con 16 px de margen y el compositor queda fijo abajo respetando el área
segura. (N)

## 7. Accesibilidad
- Las etiquetas de estado («Buscó en la web», el resumen del razonamiento) están en regiones `aria-live="polite"` (14
  regiones en la página). (M)
- Cada control tiene nombre accesible: «Enviar mensaje», «Detener respuesta», «Ir al final», «Copiar», «Reintentar»,
  «Editar», «Copiar al portapapeles». (M)
- Tras enviar y tras detener, **el foco queda en el compositor**. (M)
- La 037 agrega lo que ERP AI ya exige: el streaming **no** se anuncia palabra por palabra (solo el estado y «respuesta
  lista»), el registro de la conversación es una lista navegable y todo se usa con teclado.

## 8. Lo que esto pide al motor (036)
1. **Varios turnos** en la misma conversación, con el historial completo (también el uso de herramientas).
2. **Eventos en vivo**: el texto mientras se genera (para el ritmo de ~240 caracteres/s), el estado de la herramienta que
   corre (nombre y entrada antes de su salida), cada paso guardado y el estado de la conversación.
3. **Detener** a mitad de una respuesta conservando lo recibido, y **reanudar el flujo** tras un corte con `after`.
4. **Editar el mensaje anterior / reintentar** = un mensaje nuevo en la conversación (la 037 decide si reemplaza la
   última respuesta; el motor no reescribe el historial: queda en la bitácora).

## 9. Lo que no se adopta
La fuente serif de las respuestas, el saludo y los colores de Anthropic, el ícono del destello, el modo incógnito, la voz,
los proyectos, los artefactos y las tarjetas de «contenido reciente».
