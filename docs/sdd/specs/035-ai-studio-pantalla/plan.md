# Plan 035 — Diseño técnico

## 1. Modelo de datos
Sin tablas ni columnas nuevas. Cambia el contenido de `meta.StudioStep.OutputJson`: al cerrar una sesión queda en `NULL`
para las herramientas del alcance `datos` (`leer_registros`, `leer_registro`; la lista sale de `StudioTools.All` por su
`Scope`, no se escribe a mano). Sin operaciones nuevas del Plan de Cambios.

## 2. Contrato de la API
Sin rutas nuevas (el inventario `docs/sdd/rutas.md` no cambia). Cambian dos comportamientos del contrato de la 033:

| Método y ruta | Capacidad | Cambio |
|---|---|---|
| `GET /api/studio/sessions/{id:int}?after=n` | modelador | Con `after`: `plan`, `preview` y `reviewHash` en `null`, `warnings` vacío y `canApply` falso (no se compila); estado, conteos, error, resumen, informe y pasos `Seq > n` como antes. Sin `after`: igual que en la 033 (compila la propuesta vigente). |
| (trabajador) cierre y vencimiento de una sesión | — | En la misma transacción que marca el estado final (`StudioAgent.FinishAsync`, `StudioService.ExpireAsync`): `UPDATE meta.StudioStep SET OutputJson = NULL WHERE SessionId = @id AND Tool IN (herramientas de «datos»)`. El resumen, la entrada, el estado y la duración del paso se conservan. Al arrancar (`Bootstrap`), lo mismo para las sesiones cerradas antes de la 035 (revisión adversarial F-01). |
| (trabajador) preparar la copia aislada | — | VERIFY (F-06): el plan de la copia se aplica por tramos de hasta `PlanCompiler.MaxOperations`, cada uno el prefijo más largo que compila solo contra lo que ya tiene la copia. Antes, un modelo con más de 100 operaciones dejaba el alcance `aislado` inservible. |
| Fechas de `StudioSessionDto`, `StudioStepDto` y `StudioSummaryDto` | — | En UTC (`…Z`): se guardaban con `SYSUTCDATETIME()` y salían sin zona (F-07). |

La pantalla usa, sin cambios: `GET /api/studio` (`configured`, `model`, `sandboxAvailable`, `limits`, `sessionsToday`,
`costTodayUsd`, `active`, `directChanges`), `GET /api/studio/sessions`, `POST /api/studio/sessions`, `…/cancel`,
`…/discard` y `…/apply { reviewHash }` (409 «El modelo cambió…» y 409 del entorno con aprobación).

## 3. SPA
- **Ruta** `admin/studio` (`?s=<id>` abre una sesión), con `Guard` de modelador; entrada en `adminNav.ts` (grupo Modelo,
  icono `Bot`); declarada en `pantallas.test.ts` con sus capturas.
- **`web/src/lib/studio.ts`** (lógica sin DOM, probada con Vitest): etiquetas y tonos de estado, `isActive`, `mergeTrail`
  (sin duplicar pasos si dos sondeos se cruzan), `consumption` (pasos, herramientas, tokens, minutos y costo contra
  `limits`; los minutos de `startedAt` a `finishedAt` o a ahora), `reportNotes` (el agente dice haber probado y no corrió
  ninguna herramienta `aislado_*`: aviso) y `planFile` (nombre y contenido del JSON para descargar).
- **`StudioPage.tsx`**: cabecera, estado no configurado (texto y enlace a Asistente IA), y en dos columnas:
  - Historial (`nav` «Sesiones») con pedido, estado y fecha; el elemento actual con `aria-current`.
  - Composer: `Textarea` con contador, `Switch` «Leer registros (datos)» apagado con `Alert` de advertencia al encenderlo,
    `Switch` «Probar en una copia aislada» deshabilitado con su motivo si `sandboxAvailable` es falso, límite del día; error
    del servidor en `Alert role="alert"` dentro del formulario.
  - Sesión: pedido, estado (`Badge`), medidores (`role="meter"` con `aria-valuenow/max` y texto), pasos en `<ol>` con
    `<details>` por paso (herramienta, estado, duración; entrada y salida en `<pre>` con `tabIndex=0`), región
    `aria-live="polite"` que anuncia el último paso, botón Cancelar mientras corre.
  - Propuesta: resumen (texto plano con `white-space: pre-wrap`), «Lo que el agente informa» (pruebas ✓/✗ con iconos
    Lucide, fallas, riesgos, aviso de `reportNotes`), avisos de `PlanWarnings`, `PlanPreviewView`, «Ver plan en JSON»,
    `Descartar`, `Descargar plan` y `Revisar y aplicar…` (diálogo de confirmación; 409 → `Alert` en el diálogo y recarga).
- **Sondeo:** mientras la sesión está activa, `GET ?after=<último seq>` cada 1.5 s (un solo pedido en vuelo, cancelado al
  cambiar de sesión o desmontar); al dejar de estar activa, una lectura completa.
- **Texto como datos:** todo lo del agente se pinta como hijo de texto de React (escapado); no se usa
  `dangerouslySetInnerHTML`, Markdown ni autolink.
- Estilos con tokens (`app.css`, prefijo `studio-`); claro/oscuro por los tokens; en móvil (≤ 900 px) una columna con el
  historial plegable.

## 4. Pruebas
- **Integración** (`tests/Erp.IntegrationTests/StudioTests.Pantalla.cs`, nuevo; la clase `StudioTests` pasa a `partial`):
  H5 CA1 (cierre e interrupción) y H5 CA2.
- **Vitest** (`web/src/lib/__tests__/studio.test.ts`, nuevo): `mergeTrail`, `consumption`, `reportNotes`.
- **E2E** (`web/e2e/studio.spec.ts`, nuevo): proveedor simulado de Anthropic en `PORTS.asistente` que responde con
  `tool_use` según un guion por pedido (`[guion:…]`); nunca llama a Anthropic. Un escenario por criterio, axe, y capturas de
  la propuesta en claro, oscuro y móvil (de `main`, sin el menú, con fechas y duraciones enmascaradas). La suite fija
  `Studio__PollSeconds=1` y `Studio__RenewSeconds=1` (cancelar en ~1 s).
- Capturas que cambian: `navegacion.spec.ts › configuracion-*` (nueva tarjeta en Configuración).
- Archivos con `--veces=3 --lento`: `e2e/studio.spec.ts` y `e2e/navegacion.spec.ts` (con `compras.spec.ts`, del que depende).

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas y caminos nuevos | Ninguna ruta nueva; una pantalla nueva (`admin/studio`) con `Guard` de modelador sobre rutas que ya exigen *modelador* (029). | `RouteInventoryTests` (sin cambios en `rutas.md`) · `studio.spec.ts › H1 CA1: solo un modelador ve ERP AI Studio en Configuración` |
| Caminos alternos | ¿Otro camino al mismo dato o efecto? | Aplicar: la pantalla usa `…/apply` con la huella, igual que el asistente y el Modelador; no hay otra vía. Lo leído por el agente: solo se ve en la sesión de su dueña (033 F-08) y desde la 035 se borra al cerrar; la bitácora solo guarda conteos (`studio.finish`). | `studio.spec.ts › H3 CA1 CA2: la propuesta se revisa y se aplica con la huella` · `StudioTests › H5_CA1_al_cerrar_la_sesion_se_borran_las_salidas_con_registros` · `StudioTests › H4_CA2_otra_persona_no_aplica_ni_ve_la_sesion` |
| Lectura de datos | ¿`RecordFilter`, `FieldAccess`, empresa? | La pantalla no lee registros: muestra lo que devolvieron las herramientas, que ya pasaron por la ruta con la identidad de la persona (033). Lo leído deja de existir al cerrar la sesión (R-033-4). | `StudioTests › H5_CA1_…` · `› H5_CA1_una_sesion_interrumpida_tambien_borra_lo_leido` · `› H5_CA1_el_canario_no_sale_por_ninguna_herramienta` |
| Delegación | ¿Alguien obtiene lo que no tiene? | No: la pantalla no da capacidades; aplica la persona dueña (404 a otra). Un modelador sin permiso de un catálogo recibe en la pantalla el mismo 403 que la herramienta registró. | `StudioTests › H4_CA2_otra_persona_no_aplica_ni_ve_la_sesion` · `studio.spec.ts › H1 CA1: …` |
| Concurrencia | Invariantes y huella | «Revisar → aplicar» con `reviewHash`; si el modelo cambió, 409 y la pantalla recarga la propuesta. Doble clic en aplicar: el botón se deshabilita y el servidor aplica una vez (`UPDLOCK`, 033). Dos pestañas pidiendo: una sesión activa (409). Sondeos cruzados: `mergeTrail` no duplica pasos. | `studio.spec.ts › H3 CA2: si el modelo cambió, se recarga la propuesta y se revisa de nuevo` · `› H1 CA3: …otra pestaña…` · `StudioTests › H4_CA2_aplicar_lo_hace_la_persona_con_la_huella` · `studio.test.ts › mergeTrail no duplica pasos` |
| Fallas parciales | Caída entre pasos | La purga va en la misma transacción que el estado final (cierre con lease y vencimiento): no queda una sesión cerrada con lo leído ni una purgada sin cerrar. La copia aislada se prepara por tramos; si uno falla, la copia no se usa y se borra (033). La pantalla, si un sondeo falla, reintenta en la siguiente vuelta y muestra el error sin perder los pasos ya vistos. | `StudioTests › F03_la_purga_va_en_la_transaccion_del_cierre` · `› H5_CA1_…` (ambos) · `StudioModulesTests › H3_CA1_la_copia_aislada_de_un_modelo_con_mas_de_100_operaciones_se_aplica_por_tramos` · `studio.spec.ts › H2 CA1 CA2: …` |
| Límites | Topes | Sondeo cada 1.5 s con un pedido en vuelo, solo pasos nuevos y sin compilar (R-033-10); pedido ≤ `limits.maxPromptChars` (contador); salidas ya recortadas por el servidor (20 000) y mostradas en un `<pre>` con scroll. | `StudioTests › H5_CA2_sondear_con_after_no_compila_ni_da_la_huella` · `studio.spec.ts › H2 CA1 CA2: …` |
| Semántica | Nulos, monedas, fechas | Costo en USD con 4 decimales (el servidor da microdólares/10⁶); fechas en UTC en el contrato y en hora local en pantalla; minutos desde `startedAt` (una sesión en cola cuenta 0). Una prueba sin `ok` cuenta como no superada. | `StudioTests › H5_CA2_sondear_con_after_no_compila_ni_da_la_huella` (fechas con `Z`) · `studio.test.ts › consumption…` · `› reportNotes…` |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **T/E** Marcado o enlaces en el texto del agente o en salidas de herramientas (un registro hostil escribe `<img onerror>` o un enlace de phishing) | Alta | Todo se pinta como texto de React; sin `dangerouslySetInnerHTML`, Markdown ni autolink; la CSP bloquea scripts en línea. | `studio.spec.ts › H2 CA4: el texto del agente se muestra como dato, sin HTML ni enlaces` |
| **I** Registros leídos que se quedan en la sesión (R-033-4) | Media | Purga al cerrar o interrumpir, en la transacción del cierre, y al arrancar para las sesiones anteriores. El texto y las entradas que escribe el modelo pueden citarlos: R-035-1. | `StudioTests › H5_CA1_…` (ambos) · `› F01_al_arrancar_se_borra_lo_leido_en_sesiones_cerradas_antes_de_la_035` · [R-035-1](../../riesgos.md#035) |
| **S/R** El informe «probado» falseado por el modelo o por una inyección (R-033-11) | Media | Se presenta como afirmación del agente junto a los pasos reales; si dice haber probado sin correr herramientas de la copia, se advierte. | `studio.test.ts › reportNotes avisa pruebas sin copia aislada` · `studio.spec.ts › H3 CA1 CA2: …` |
| **E** Aplicar sin revisar (doble clic, propuesta vieja) | Media | Diálogo de confirmación con los avisos; huella; 409 recarga. | `studio.spec.ts › H3 CA2: …` |
| **I** La persona activa `datos` sin saber que los registros salen | Media | Apagado por omisión; aviso visible al encenderlo; el estado de alcances se ve en la sesión. | `studio.spec.ts › H1 CA2: datos apagado con aviso y aislado según el servidor` |
| **D** Sondeo que carga al servidor | Baja | Un pedido a la vez, 1.5 s, sin compilar, se detiene al terminar o salir de la página. | `StudioTests › H5_CA2_…` |
