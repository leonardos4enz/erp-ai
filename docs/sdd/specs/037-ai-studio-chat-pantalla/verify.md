# Verify 037 — ERP AI Studio: pantalla de chat

## Métricas
- inicio: 2026-10-29 11:45
- cierre: 2026-10-29 15:40
- unitarias: 852 (+16: 659 de Erp.Tests, sin cambios; 135 de integración, +1; Vitest 58, +15)
- e2e: 402 (+13: studio.spec.ts reescrito, de 15 a 28 pruebas)
- corridas completas: 4 (tras CI: 385 + 1 falla F-16; 402 en verde con F-15 y F-16. Antes del push: 9 min 14 s, 402 en verde, antes de las correcciones de la revisión exploratoria; 9 min 19 s, 402 en verde, con el código final salvo la línea de foco de F-14)
- corridas parciales: 25 (studio.spec.ts al iterar ×6 y con las correcciones ×6; --veces=3 --lento ×6, cuatro en verde; contra el código anterior ×6)
- contra el código anterior: 9 (F-01 ×2 con dotnet test, F-02 en node, F-03, F-05, F-07, F-11, F-12 y F-13 en studio.spec.ts)
- hallazgos: 1/4/9
- categorías: motor-api=1, seguridad=2, concurrencia=3, accesibilidad=5, visual=1, UI=2
- revisión adversarial: 8 hallazgos (1 alto, 2 medios, 5 bajos; 2 descartados al comprobarlos)
- riesgos: R-037-1, R-037-2, R-037-3

## 0. Apertura
`git rev-list --count origin/main..main` = 0 y la última corrida de CI en `main`
([40916888982](https://github.com/leonardos4enz/erp-ai/actions/runs/40916888982)) es la de `a1faebf` («Spec 036: ERP AI
Studio como chat (motor conversacional y streaming)»): **completada en verde** (`conclusion: success`), el pendiente que dejó
la 036. El servidor de desarrollo `erp-api` (5080) estaba prendido al abrir la sesión; antes de compilar se le preguntó al
dueño, que aprobó apagarlo; al ir a detenerlo ya no había ningún proceso escuchando en 5080 ni `Erp.Api` corriendo.
CLARIFY: no se divide (6 historias, ~1 700 líneas de producto, sin seguridad ni concurrencia del motor junto con la UI, una
pantalla) y el motor no necesita nada más; en IMPLEMENT apareció un bug del motor (F-01) que se corrigió con prueba de
integración.
Producto: ~1 700 líneas nuevas en `web/src` (barra, bienvenida, conversación, compositor, flujo, Markdown, estilos) y 2 en
el motor (F-01); pruebas: ~900.

## 1. Pruebas unitarias e integración
`dotnet test`: **794 correctas**: 659 de `tests/Erp.Tests` (sin cambios) y 135 de integración (134 + 1:
`StudioProviderTests › El_adaptador_real_reconoce_una_respuesta_cortada_y_un_rechazo`, F-01).
`npm --prefix web test` (Vitest): **58** (43 + 15). `studio.test.ts` (reescrito, 15: turnos y segmentos, entrega cortada o
mal tipada, propuesta vigente y reemplazada, estado en vivo sin el texto, analizador SSE con eventos partidos, espera
creciente, ritmo de revelado, palabras, hora relativa, consumo con mensajes) y `markdown.test.ts` (nuevo, 6: subconjunto,
títulos, código sin cierre, tablas, HTML y enlaces como texto, entradas patológicas lineales, y la guardia: ningún archivo
de la pantalla usa `dangerouslySetInnerHTML`, `<a`, `<img` ni `<b>`). `pantallas.test.ts` apunta a las capturas nuevas.

## 2. E2E
`e2e/studio.spec.ts` reescrito por criterio (H1–H6) más las regresiones de VERIFY: **28 pruebas** (antes 15). El proveedor
simulado agrega `chunkMs` (escribe despacio), `stop` (otro motivo de parada) y guiones por llamada (`calls`), porque una
respuesta detenida o rechazada no queda en el historial.

Corridas, con su duración:
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1–6 | `studio.spec.ts` al iterar (capturas nuevas; F-01, F-10) | de 5/25 a 25/25 | ~1,5 min c/u |
| 7 | `--veces=3 --lento` sobre `studio.spec.ts` | falló en la vuelta 1: carrera de la prueba (`H5 CA2 CA3` cambiaba el modelo antes de la lectura con la huella) | 1 min |
| 8 | `--veces=3 --lento` | falló en la vuelta 1: F-12 (`H4 CA2`, el anuncio del final) | 1,5 min |
| 9 | `--veces=3 --lento` | 3 vueltas en verde (25) | 6 min 31 s |
| 10–15 | `studio.spec.ts` con las correcciones de la revisión adversarial y contra el código anterior (abajo) | 28/28 con lo nuevo | ~2 min c/u |
| 16 | `--veces=3 --lento` sobre `studio.spec.ts` | 3 vueltas en verde (28 + preparación) | 8 min 42 s |
| 17 | Completa (antes de las correcciones de la revisión exploratoria) | 402 en verde (389 + 13) | 9 min 14 s |
| 18–20 | Contra el código anterior: F-11, F-12 y F-13 (una corrección quitada por corrida) | cada una falla en su prueba (`H3 CA1 H2 CA2`, `H5 CA2 CA3`, `H6 CA1 CA2`) | 0,3–1,3 min c/u |
| 21 | `--veces=3 --lento` sobre `studio.spec.ts` con el código final | falló en la vuelta 2: F-14 (`H4 CA1 CA2 CA3`, foco tras detener) | 4 min 52 s |
| 23 | `--veces=3 --lento` con F-14 corregido | 3 vueltas en verde (29) | 8 min 36 s |
| 22 | **Completa** con el código final (salvo la línea de F-14) | **402 en verde** | 9 min 19 s |

Capturas de referencia: se borraron las tres de la 035 (`studio-propuesta-*`) y hay seis nuevas, **revisadas una por una**:
`studio-chat-{light,dark,movil}.png` (la conversación abierta al final, con la propuesta) y
`studio-bienvenida-{light,dark,movil}.png`. En la primera revisión mostraban el botón de cerrar la barra y el de abrir
conversaciones en escritorio, un anillo de foco interior en el compositor y el título de la barra sin elipsis (F-10); como
la diferencia cabía en la tolerancia del 1 %, `--update-snapshots` no las reescribía: se borraron y se regeneraron. Las de
móvil se regeneraron de nuevo con F-13.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff (`git diff a1faebf` y los archivos nuevos) y la tabla STRIDE sin
las conclusiones del plan, con el encargo de romper XSS, retroceso de expresiones, carreras del flujo y de las lecturas,
reconexión, foco y la línea del motor. Encontró 8 hallazgos (1 alto, 2 medios, 5 bajos); 6 se corrigieron con prueba o
riesgo y 2 se descartaron al comprobarlos (F-04 y F-06, abajo). Sin hallazgos en: elementos de React con texto del
modelo (`title`, `aria-*`, `key`, `start`, `lang`), `RULE`/`BULLET`/`ORDERED`, el analizador SSE, aplicar solo con
`canApply` y la huella de la lectura completa, `aria-live` sin el texto en vivo, y el cambio a `Raw()` del motor.

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`) con un proveedor simulado propio en
loopback (`Assistant__BaseUrl`, clave ficticia guardada por API) y el navegador integrado (1024 × 768, tema oscuro, y
390 × 844). Rol: administrador. Pedido con acentos, «ñ», comillas angulares y «&» enviado con Enter; Markdown con tabla,
lista, código, un enlace y `<b>` (como texto); seguimiento con propuesta; «Revisar y aplicar…» con teclado, Esc (el foco
vuelve al botón), doble clic en «Aplicar cambios» (un solo conjunto de cambios y un aviso; foco al compositor); detener a
mitad (aviso, «Editar el mensaje», «Intentar de nuevo», foco al compositor, «Detuviste la respuesta.» en `aria-live`);
móvil (panel de conversaciones, Esc, foco de vuelta, sin desborde). Consola sin errores (solo el 401 antes de iniciar
sesión). Las entregas que no compilan se ven como herramientas con error (el proveedor simulado repetía una entrega con un
catálogo ya creado: artefacto del simulador, no de la pantalla).

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | motor-api | (IMPLEMENT, E2E `H4 CA2`) El motivo de parada del SDK se leía con `ToString()` del enum envuelto: `max_tokens` y `refusal` nunca se reconocían y una respuesta cortada o rechazada quedaba «Respondida». Mismo error en el asistente de la 011. | `StopReason?.Raw()` en `StudioModel` y `AssistantModel`. | `StudioProviderTests › El_adaptador_real_reconoce_una_respuesta_cortada_y_un_rechazo` (con el código anterior: `answered`; con el asistente sin corregir, también falla) |
| F-02 | Alta | seguridad | (Adversarial F-01) La expresión de títulos de `markdown.ts` retrocedía en O(n³): un título del modelo con miles de espacios congelaba la pestaña en cada cuadro del texto en vivo y al abrir la conversación (87 s con 8 000 espacios). | Títulos y separador de tabla analizados a mano, en tiempo lineal. | `markdown.test.ts › una entrada patológica se analiza en tiempo lineal` (las expresiones anteriores: 87 s y 80 s en las dos entradas nuevas) |
| F-03 | Media | concurrencia | (Adversarial F-02) Una lectura que llegaba tras cambiar de conversación abría un flujo que nadie cerraba (ocupa un cupo de `MaxStreamsPerUser`). | `follow` no abre flujos tras desmontar. | `studio.spec.ts › Regresión F-03: cambiar de conversación…` (sin la corrección: 3 flujos de la conversación que se dejó) |
| F-04 | — | concurrencia | (Adversarial F-03, **descartado**) Mandar un mensaje mientras llega la lectura final del turno anterior dejaría el turno nuevo sin flujo. No se alcanza desde la pantalla: el evento `session` no trae `canSend` y el compositor sigue bloqueado hasta esa lectura. Se dejó la defensa (lecturas numeradas; el flujo se libera en su `finally` y sigue si alguien pidió seguir mientras cerraba). | — | `studio.spec.ts › Revisión adversarial F-04: …no se puede enviar…` (documenta la protección; pasa también con el código anterior) |
| F-05 | Baja | concurrencia | (Adversarial F-04) La cuenta de fallas se reiniciaba con cualquier evento: un flujo que se corta tras su saludo, o un evento mal formado, reconectaba cada segundo para siempre. | Solo un paso nuevo reinicia la cuenta; un evento mal formado se ignora. | `studio.spec.ts › Regresión F-05: …llega a «Sin conexión»` (con el hook anterior nunca llega) |
| F-06 | — | seguridad | (Adversarial F-05, **descartado**) Un «"plan"» escrito por el modelo en una entrada cortada marcaría la entrega equivocada como vigente. En el JSON crudo esas comillas van escapadas; se dejó la condición más estricta (clave sin escapar seguida de un objeto) y el resto va a R-037-3. | — | `studio.test.ts › deliveryOf tolera una entrada cortada…` |
| F-07 | Baja | accesibilidad | (Adversarial F-06) Al reintentar o enviar una edición, el botón pulsado desaparecía y el foco quedaba en `body` hasta que respondía el servidor. | El foco pasa al compositor antes de esperar. | `studio.spec.ts › Regresión F-07: al reintentar, el foco pasa al compositor…` (falla con el código anterior) |
| F-08 | Baja | seguridad | (Adversarial F-07) Sin conexión, la salida de las herramientas de `datos` del turno se quedaba en la vista (el servidor la borra al terminar). | Se quita de la vista al pasar a «Sin conexión». | sin prueba automática: [R-037-2](../../riesgos.md#037) |
| F-09 | Baja | UI | (Adversarial F-08) Avisos repetidos con la misma clave de React. | Clave por posición. | sin prueba (solo un aviso de desarrollo) |
| F-10 | Media | accesibilidad | (IMPLEMENT) Tras el primer mensaje (que abre la conversación) el foco no quedaba en el compositor; en las capturas, botones de móvil visibles en escritorio, anillo de foco interior y título de la barra sin elipsis. | Foco al compositor al abrir (con puntero fino); reglas más específicas; `minmax(0, 1fr)` en la lista. | `studio.spec.ts › H3 CA1 H2 CA2: …el compositor queda listo` (`toBeFocused`) · capturas `studio-*` |
| F-11 | Baja | visual | (Exploratoria) La fuente monoespaciada convertía `>=` en «≥» en los bloques de código. | `font-variant-ligatures: none` en el código. | `studio.spec.ts › H3 CA1 H2 CA2` (`fontVariantLigatures`) |
| F-12 | Baja | accesibilidad | (IMPLEMENT, `--lento`) Si el turno terminaba antes de la primera lectura, no se anunciaba cómo terminó. Y (exploratoria) la fecha de «Propuesta aplicada» terminaba en «p.m..». | El estado final se escribe siempre (una región `aria-live` no lee lo que tiene al montarse); sin el punto extra. | `studio.spec.ts › H4 CA2: un turno que falla…` (anuncio) · `› H5 CA2 CA3: …` (fecha) |
| F-14 | Baja | accesibilidad | (`--veces=3 --lento`, intermitente) Al detener, «Detener respuesta» se deshabilita mientras espera al servidor y el foco caía en `body` hasta que respondía. | El foco pasa al compositor antes de esperar (como F-07). | `studio.spec.ts › H4 CA1 CA2 CA3` (`toBeFocused` tras detener; falló 1 de 3 vueltas lentas antes de corregir) |
| F-15 | Media | concurrencia | (CI de `e7b5500`, reproducible) En Seguridad › Usuarios, si la lista de empresas llegaba con el diálogo «Nuevo usuario» abierto, el efecto de apertura (que dependía de `companies`) borraba usuario, nombre y contraseña: «Crear usuario» solo mostraba «Requerido». Le pasa a cualquiera con red lenta; anterior a la 037 (la 010), lo destapó el runner más lento. | El diálogo se limpia solo al abrirse; las empresas predeterminadas se completan al llegar sin tocar lo escrito ni lo que la persona marcó; lista vacía estable. | `h5-h6-seguridad.spec.ts › H5: crear un usuario desde la UI` (retiene `/api/admin/companies` hasta llenar el formulario; con el código anterior falla: usuario vacío) |
| F-16 | Baja | accesibilidad | (Corrida completa tras F-15, intermitente) En el asistente (011), «Guardar y probar» se deshabilita mientras espera y, si el servidor rechazaba la clave (400), el foco caía en `body` (guardia de la suite). | Tras el rechazo, el foco va al campo con el error. | `asistente.spec.ts › H3 CA4: sin clave…` (`toBeFocused` en el campo; con el código anterior falla siempre: «inactive») |
| F-13 | Baja | UI | (Exploratoria) En móvil había dos encabezados «ERP AI Studio» y dos íconos de panel iguales (el menú de la app y las conversaciones). | En ≤ 900 px el título queda para el lector de pantalla y el botón usa otro ícono. | `studio.spec.ts › H6 CA1 CA2: en móvil…` · capturas `studio-*-movil` |

Carrera de la prueba (no del producto), corregida en la prueba: `H5 CA2 CA3` esperaba al fin del turno, pero no a la
lectura con la huella antes del cambio ajeno.

### Contra el código anterior
- F-01: `dotnet test --filter El_adaptador_real_reconoce` con `ToString()` de nuevo en Studio (falla: `answered`) y luego en
  el asistente (falla).
- F-02: las expresiones anteriores con las entradas nuevas, en `node` (87 s y 80 s).
- F-03, F-05, F-07: `studio.spec.ts` con el hook anterior (F-05 falla: nunca «Sin conexión»), sin la línea de F-03 (falla:
  3 flujos huérfanos) y sin el foco de F-07 (falla: foco en `body`). F-04 pasa también con el código anterior (descartado).
- F-11, F-12 (fecha), F-13: `studio.spec.ts` quitando cada corrección (script de una corrida por hallazgo): fallan
  `H3 CA1 H2 CA2` (ligaduras), `H5 CA2 CA3` (doble punto) y `H6 CA1 CA2` (título visible en móvil). F-12 (anuncio) falló
  en la corrida 8 antes de corregirse.
- F-15: `h5-h6-seguridad.spec.ts` con la prueba nueva y `SecurityPage.tsx` sin cambiar (falla: `Expected: "ui-capturista"`,
  `Received: ""`).
- F-16: `asistente.spec.ts` con `AssistantPage.tsx` de `e7b5500` (falla: el campo no tiene el foco).

### Lista de comprobación
**Flujo**
- [x] El camino feliz de cada historia se completa sin ayuda (pedir, seguir, propuesta, aplicar, detener, reintentar, editar)
- [x] Los mensajes de error dicen qué pasó y cómo resolverlo (409 con el motivo del servidor, sin conexión, tope)
- [x] Doble clic (aplicar: un conjunto de cambios), doble Enter (un mensaje), recargar a mitad de un turno (lo retoma)
- [x] Datos límite: acentos, ñ, comillas, `&`, HTML y enlaces del modelo, textos largos (40 párrafos), Markdown patológico

**Diseño**
- [x] Claro y oscuro, 1280/1024 px y móvil (390 px): nada tapado ni desbordado
- [x] Solo íconos Lucide, sin emojis; sin logo, fuentes, paleta ni textos de Anthropic
- [x] Estados vacío (bienvenida), carga, espera, error, detenido y sin conexión con diseño

**Accesibilidad**
- [x] Todo con teclado (Enter, Shift+Enter, Esc en la edición, el diálogo y el panel); foco visible
- [x] El diálogo atrapa el foco y se cierra con Esc; el foco vuelve a quien lo abrió
- [x] `role="log"` con `aria-live="off"`; el estado en una región aparte, nunca palabra por palabra; movimiento reducido

**Seguridad**
- [x] Un lector no ve la opción ni llega por URL (`H1 CA2`); sin rutas nuevas (`rutas.md` sin cambios)
- [x] Consola sin errores ni violaciones de CSP
- [x] Caminos alternos del STRIDE con la API directa: los de la 036 siguen en verde en integración (`StudioTests`)
- [x] Dos peticiones a la vez: doble Enter, otra pestaña (409 con su motivo), doble clic en aplicar

**Cierre**
- [x] Pasada solo con teclado por la pantalla nueva
- [x] Hermanos del hallazgo F-01 (`grep StopReason`): el asistente de la 011, corregido con la misma prueba
- [x] Alcance congelado: ramas, renombrar y borrar conversaciones, razonamiento visible y resaltado de código quedan en el roadmap

## 5. CI (segunda corrida completa)
Corrida [40958150085](https://github.com/leonardos4enz/erp-ai/actions/runs/40958150085) de `e7b5500`: **en rojo**. Unitarias,
build, Vitest, auditoría e integración en verde; E2E 397 en verde, 1 falla y 4 sin correr (las siguientes del mismo archivo
en serie): `h5-h6-seguridad.spec.ts › H5: crear un usuario desde la UI`, también sobre base nueva (reproducible). La spec se
reabrió:
- En local pasaba sola y en las dos completas; con `--veces=3` (lento) falló en la primera vuelta. El contexto del error
  mostró el diálogo con los campos vacíos y la casilla del rol marcada: F-15.
- Prueba con la respuesta de empresas retenida: falla con el código anterior (usuario vacío) sin necesidad de `--lento`;
  con la corrección, `h5-h6-seguridad.spec.ts --veces=3` en verde (3 × 6). Los otros diálogos con efecto de apertura
  dependen del registro que abren, no de listas que llegan tarde (`UnitDialog` recibe `units`, ya cargada al abrir).
- El artefacto `playwright-report` pesaba 18 KB: `upload-artifact@v4` omite las carpetas que empiezan con punto
  (`.report`, `.results`) y solo subía el log. `ci.yml` agrega `include-hidden-files: true`.
- Primera completa con F-15: 385 en verde, 1 falla y 16 sin correr: `asistente.spec.ts › H3 CA4`, foco en `body` tras el
  rechazo de la clave (F-16, intermitente, de la 011). Corregido con prueba; `asistente.spec.ts --veces=3` en verde (3 × 19).
- Completa con F-15 y F-16: **402 en verde** (9 min 16 s). El resultado de CI de este commit lo anota la spec siguiente al
  abrir (regla de apertura).

## 6. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#037): R-037-1 (editar y reintentar son mensajes nuevos), R-037-2 (lo leído
con `datos` en memoria tras un 401 a mitad de turno) y R-037-3 (la entrega vigente se reconoce por su entrada). Se cerró
R-035-2 (ya no hay sondeo).
