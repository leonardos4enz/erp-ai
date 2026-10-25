# Verify 030 — Integración y CI

## Métricas
- inicio: 2026-10-25 12:10
- cierre: 2026-10-25 16:10
- unitarias: 687 (+59: 628 de Erp.Tests sin cambios y 59 nuevas de integración con SQL real)
- e2e: 351 (+1; −50 que pasaron a integración)
- corridas completas: 7 (7 min 36 s la segunda corrida de la 029; 4 min 42 s, 5 min 24 s y 6 min 06 s con las guardias encontrando defectos; 6 min 48 s en verde; 6 min 00 s con las mutaciones de las guardias; 6 min 54 s la final tras la revisión adversarial)
- corridas parciales: 5 (--veces=3 sobre capacidades, endurecimiento-2, h3-h4-datos y pendientes; integración ×3; depuración de la guardia de foco ×2; demostración de la guardia de alerta ×2)
- contra el código anterior: 5 (it:regresion cd81760 Hardening012Tests.H3; it:regresion f6c5445 Hardening021Tests.H3; suite completa con los arreglos de origen de las guardias revertidos; Vitest con 026 F-03 y una ruta sin declarar; HarnessTests con el arnés anterior a F-A)
- hallazgos: 0/5/2
- categorías: accesibilidad=3, UI/UX=1, visual=1, concurrencia=1, pruebas=1
- revisión adversarial: 12 hallazgos (0 altos, 5 medios, 7 bajos)
- riesgos: R-030-1, R-030-2, R-030-3, R-030-4, R-030-5

## 1. Pruebas unitarias
`dotnet test`: **687 correctas**: 628 de `tests/Erp.Tests` (sin cambios) y **59 nuevas** en `tests/Erp.IntegrationTests`
(13 s, ×3 sin fallas tras la revisión adversarial):
- `HarnessTests` (9): bases desechables (5 nombres rechazados, también con inyección), la configuración llega a `AddErp`
  (incluida una segunda instancia sin cambios directos), `Concurrently` lanza las 10 a la vez, una falla inyectada se deshace
  aunque la prueba falle.
- `ApiSecurityTests` (19, de `api-security.spec.ts`), `Hardening012Tests` (9, de `endurecimiento.spec.ts`, con la instancia
  «producción» como segunda instancia), `Hardening021Tests` (14, de `endurecimiento-2.spec.ts`, con la instancia de
  aprobación de seguridad y una prueba nueva: dos instancias sobre la misma bandeja, cada trabajo una vez), `PendingTests` (8,
  de `pendientes.spec.ts`; instala Compras e Inventario en su base), `ModuleRaceTests` (1, R-029-12),
  `MultiInstanceTests` (1, R-030-1).

`npm --prefix web test` (Vitest, 0.5 s): **30 correctas** en `layoutEdit`, `workflowEdit`, `conditionBuilder`,
`exprComplete`, `canonical` y `pantallas`.

### Contra el código anterior
| Comando | Resultado |
|---|---|
| `npm --prefix web run it:regresion -- cd81760 Hardening012Tests.H3` | 3 fallan: diez confirmaciones simultáneas dan **[200, 500 ×9]** (se esperaba un 200 y nueve 400; lo recibido quedó en 10, así que esta falla por los 500 y no por recibir de más: F-F); en la ronda 0, confirmar y cancelar **ganan los dos** (recepción confirmada sobre una orden cancelada); Compras sin `RecibidoNoExcede` |
| `npm --prefix web run it:regresion -- f6c5445 Hardening021Tests.H3` | 2 fallan: el trabajo que «falló» al marcar su éxito **dejó su efecto** (`Veces` = 1); el trabajo con el *lease* vencido y sin intentos se volvió a ejecutar. La de dos instancias pasa (la bandeja ya reclamaba con `UPDATE … OUTPUT`) |
| Vitest con `canonical.ts` sin el filtro de nulos | `026 F-03: los nulos del servidor no cuentan como cambio` falla |
| Vitest sin `admin/assistant` en `PANTALLAS` | `cada ruta tiene captura o una razón escrita` falla y la nombra |

026 F-06 vivía en el componente (qué texto le pasaba a la lista al reabrirse), no en una función: la 030 la extrajo
(`completeAfterChoice`) y su prueba fija el contrato; no hay «código anterior» de esa función contra el cual correrla.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa (segunda corrida de la 029, antes de cambiar código) | 400 en verde | 7 min 36 s |
| 2 | Completa con las guardias, código anterior | 31 fallan (151 pasan, 169 no corren por el modo serial) | 4 min 42 s |
| 3 | Completa tras `.content` relativo y el chip de origen | 24 fallan | 5 min 24 s |
| 4 | Completa con la guardia observando en la página y el rescate de foco | 5 fallan | 6 min 06 s |
| 5 | Completa final | **351 en verde** | 6 min 48 s |
| 6 | `--veces=3` sobre `capacidades`, `endurecimiento-2`, `h3-h4-datos` y `pendientes` | 3 × 20 en verde | 1 min 14 s |
| 7 | Completa con los arreglos de origen revertidos (demostración de guardias) | 12 fallan por guardias | 6 min 00 s |
| 8 | Completa final, tras corregir F-A…F-L | **351 en verde** | 6 min 54 s |

Archivos borrados: `api-security.spec.ts`, `endurecimiento.spec.ts`. Modificados: `helpers.ts` (guardias; afecta a todos y lo
cubre la corrida completa), `endurecimiento-2.spec.ts` y `pendientes.spec.ts` (solo lo de UI), `capacidades.spec.ts` (cabecera
`Server`, que quita Kestrel y TestServer no usa). Capturas de referencia: ninguna cambió (las 21 pasan con `.content`
relativo y el botón con `aria-busy`). Pantallas sin captura: 16 de 23, cada una con su razón (R-030-3).

### Las guardias contra el código anterior a su hallazgo
| Guardia | Origen | Código anterior | Falla |
|---|---|---|---|
| desborde | 026 F-02 (y 025 F-01, 003 F-16) | `.expr-field` y `.content` sin `position: relative` | 10 pruebas: «el documento se desplaza 0×338 px por un absoluto que escapa… `<span class="sr-only">` en `<div class="expr-msg">`» en `/admin/model`, y otras en Entornos (móvil) y en el formulario |
| alerta | 022 F-03 | el motivo del rechazo del diálogo de transición sin `role="alert"` | una prueba que solo mira el texto: «4xx sin aviso visible (role="alert" dentro de la vista) tras POST /api/data/ZGuardia/1/transition → 400»; con el código actual pasa |
| foco | 022 F-03 | el diálogo sin su vigilante de foco y el botón deshabilitado con `disabled` mientras espera | `asistente › H3 CA4` y `integraciones › H1 CA2`: «foco en body tras el rechazo de PUT …/webhooks/… → 400 (último enfocado: button «Guardar»)» |
| pantallas | 011 F-05 | una ruta sin declarar | Vitest nombra `admin/assistant` |

La guardia de alerta no detectó la reversión parcial de 018 F-01 (solo el `scrollIntoView`): esa corrección también movió el
aviso de roles arriba de la lista, y el primer error sigue a la vista.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión, solo lectura. Recibió el diff sin commitear (archivos nuevos incluidos) y las ocho
preguntas del STRIDE sin sus respuestas. Revisó el workflow (disparadores, permisos, forks), `regresion.mjs`, el arnés
(bases, inyección de fallas, `Concurrently`), la equivalencia de cada prueba movida con `git show HEAD:web/e2e/…`, las
guardias, `Button`, el rescate de foco y `.content`. Encontró 12; ninguno alto.

| ID | Severidad | Hallazgo | Corrección | Prueba |
|---|---|---|---|---|
| F-A | Media | La restricción CHECK temporal no se quitaba en tablas fuera de `dbo` (`OBJECT_ID` con el nombre solo buscaba en `dbo`): en `meta.AutomationRun` se quedaba hasta borrar la base, y la prueba del arnés lo probaba solo en `dbo` | `ALTER TABLE … DROP CONSTRAINT IF EXISTS` | `HarnessTests › H1_CA3_una_falla_inyectada_se_deshace…` ahora en el esquema `it` y mirando `sys.check_constraints` (**falla con el arnés anterior**: quedaba 1) |
| F-B | Media | `Json()` de un nodo ausente daba «null»: un `DoesNotContain` pasaba si `/compute` o el modelo respondían 403/404 (en Playwright, `expect(undefined)` fallaba) | `Json()` lanza con un nodo ausente; `/compute` de 012 H6 exige 200 | `HarnessTests › H1_CA2_una_asercion_negativa_sobre_un_nodo_ausente_falla` |
| F-C | Media | `PendingTests` H3 esperaba 500 ms fijos a que la importación tomara su bloque: intermitente en CI | `HoldAsync` devuelve su sesión y la prueba espera en `sys.dm_exec_requests` a que la importación quede bloqueada por ella | `PendingTests › H3_CA1…`, `› H3_CA2_CA3…` (integración ×3) |
| F-D | Media | La guardia de alerta se satisfacía con un `role="alert"` que ya estaba antes del rechazo | Solo cuenta un aviso nuevo o con texto distinto | corrida completa final |
| F-E | Media | Falsos negativos de la guardia: una escritura que sale tras otra petición (vista previa → aplicar) pasaba de los 300 ms; Espacio contaba como gesto al escribir; la cobertura en CLAUDE.md estaba exagerada | Una petición pedida encadena las siguientes mientras siga en curso; Espacio solo sobre botones; CLAUDE.md y `verificacion.md` dicen «en las pruebas que usan `page`». XHR (la SPA no lo usa), `browser.newContext()` y estados intermedios del desborde quedan en R-030-4 | corrida completa final |
| F-F | Baja-Media | Contra `cd81760`, 012 H3 CA1 falla por los nueve 500, no porque se reciba de más (`Recibida` = 10); H3 CA3 sí muestra el doble ganador | Se dice así en «Contra el código anterior» | — |
| F-G | Baja-Media | Las clases corren en paralelo y `PendingTests` mide tiempos (< 5 s en los cargos simultáneos) | `PendingTests` en la colección `SinParalelo` | integración ×3 |
| F-H | Baja | El `HoldAsync` de `PendingTests` no se soltaba si algo fallaba a mitad; `HoldAsync` filtraba la conexión si su SQL fallaba | `await using`; `try/catch` en `HoldAsync` | integración ×3 |
| F-I | Baja | `Concurrently` ignoraba que no todas llegaran a la barrera | Lanza `TimeoutException` | `HarnessTests › H1_CA2_Concurrently_lanza_todas_a_la_vez` |
| F-J | Baja | CI: el commit del medio de dos pushes seguidos no recibe su corrida; integración sin límite por prueba | Documentado («vale el último `main`»); `--blame-hang-timeout 10m` | — |
| F-K | Baja | «Corre en paralelo con la E2E de la misma carpeta» no vale si hay que recompilar `Erp.Api` | `verificacion.md` lo precisa | — |
| F-L | Baja | El rescate de foco no hacía nada si lo último enfocado seguía conectado pero oculto | Si no es visible (`checkVisibility`), pasa a `#main` | corrida completa final |

Revisado y en orden (según el subagente): sin `pull_request_target`, `permissions: contents: read`, sin `github.event.*` en
`run`; `regresion.mjs` sin shell y con `rev-parse --verify`; `ValidateDatabase` no puede borrar `ErpAi` ni la base de otra
clase; sin secretos; delegación de la 021 y bitácora/etiquetas/buscador de la 012 equivalentes al E2E; `Concurrently` en
paralelo de verdad; `Button` con `aria-disabled` no permite doble envío (también con Enter); `.content` relativo no mueve
popovers (usan portal y `position: fixed`). Perdido y aceptado: `installedVersion == version` de Compras en 012 H3 CA2
(con instalación limpia siempre coinciden).

Además, al correr la integración repetida apareció un intermitente propio: «La operación de E/S se anuló por una salida de
subproceso» en las llamadas SQL del arnés (SqlClient con canalizaciones con nombre de LocalDB, E/S asíncrona iniciada en un
hilo que el pool retira). El arnés usa llamadas síncronas dentro de `Task.Run`; 6 de 6 corridas de `HarnessTests` y 3 de 3
de toda la integración en verde.

## 4. Revisión exploratoria
Sin pantallas nuevas: la «exploración» fue la propia suite con las guardias encendidas, que recorre las 351 pruebas de UI
como lo haría una persona con teclado y lector de pantalla. Los defectos que encontró están abajo; cada uno quedó cubierto
por la guardia que lo detectó (falla con el código anterior: corridas 2–4 y 7). Las pantallas cambiadas a mano (botón que
espera, foco al cerrar diálogos, aviso de conflicto) se recorrieron en las pruebas existentes de cada una.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | visual | El modelador medía hasta 1 415 px (y 3 088 px con módulos instalados) más que la ventana: el `.sr-only` «Origen:» de las insignias de origen escapaba del contenedor con scroll. Cuarta vez de la misma lección | Causa raíz: `.content` (el contenedor con scroll de toda la app) pasa a `position: relative`; además `.origin-chip` | guardia `desborde` en `interfaz › H5 CA1 (F-03)`, `visual-modelador › H2 CA3`, `endurecimiento-2 › H2 CA1 (UI)` (corrida 2) |
| F-02 | Media | accesibilidad | Al cerrar un diálogo cuyo botón de origen desaparece (el «Revisar cambios» de un editor que se cierra al aplicar, la tarjeta del módulo que se vuelve a pintar tras instalar, la fila que se edita en Seguridad) el foco caía en `body`: el teclado volvía al inicio de la página. 17 avisos en 16 pruebas | `Layout.tsx`: rescate de foco global; si lo que tenía el foco desaparece, pasa a `<main tabindex="-1">` | guardia `foco` en `compras › H7`, `h1-modelador › H1`, `visual-modelador › H4 *`, `seguridad › H2 CA6`, `jerarquia › H1 CA1 (UI)` (corrida 2) |
| F-03 | Media | accesibilidad | Fuera de los diálogos, el botón que espera al servidor se deshabilitaba con `disabled` y el navegador sacaba el foco al `body`; si el servidor rechazaba (clave duplicada, clave del asistente, URL interna del webhook) la persona con teclado quedaba al inicio de la página. El arreglo de 022 F-03 solo cubría los diálogos | `Button`: mientras carga queda `aria-disabled` y `aria-busy`, enfocable, e ignora los clics (también el envío del formulario) | guardia `foco` en `h3-h4-datos › H4`, `asistente › H3 CA4`, `integraciones › H1 CA2` (corridas 2–4 y 7) |
| F-04 | Media | accesibilidad | El aviso «Otro usuario modificó este registro» tras un 409 al guardar era `role="status"` (tono de advertencia): un lector de pantalla no lo anunciaba como el rechazo que es | El aviso pasa a `tone="danger"` (`role="alert"`) | guardia `alerta` en `h3-h4-datos › H4: dos personas editando el mismo registro…` (corrida 4) |
| F-05 | Media | concurrencia | Con dos instancias sobre la misma base, la segunda no ve un catálogo nuevo hasta 30 s después (caché del modelo por instancia): responde 404 | No se corrige (se despliega una instancia): riesgo R-030-1 | `MultiInstanceTests › R_030_1_…` (documenta el alcance) |
| F-06 | Baja | pruebas | Primera versión de la guardia de alerta: revisaba 800 ms después de la respuesta, y Playwright ya había escrito el siguiente valor (el aviso se borraba): falsos positivos en el asistente y en Empresas | La guardia observa la página desde la respuesta y basta que el aviso se vea en algún momento del segundo y medio siguiente | corridas 3 y 4 |
| F-07 | Baja | UI/UX | La validación en vivo del modelador (vista previa 800 ms después de teclear) recibe 400 con un borrador a medias y lo ignora en silencio | Aceptado: es validación de fondo; «Revisar cambios» muestra el error. La guardia solo revisa escrituras que siguen a un clic o Enter (R-030-4) | — |

### Lista de comprobación
**Flujo**
- [x] El camino feliz de cada historia se completa sin ayuda (las historias son de infraestructura: arnés, CI, Vitest y guardias)
- [x] Los mensajes de error dicen qué pasó: los de las guardias y el arnés nombran la ruta, el elemento y el valor
- [x] Doble clic: el botón que espera ignora el segundo clic (`aria-disabled`); las pruebas de rowversion y de conflicto siguen en verde
- [ ] Datos límite: no aplica (sin capturas nuevas)

**Diseño**
- [x] Claro y oscuro, escritorio y móvil: las 21 capturas siguen iguales; el botón en espera conserva su opacidad
- [x] Solo iconos Lucide
- [x] Estados vacíos, de carga y de error: sin pantallas nuevas

**Accesibilidad**
- [x] Todo se puede hacer con teclado; el foco ya no cae en `body` al cerrar diálogos ni tras un rechazo (F-02, F-03)
- [x] Los diálogos atrapan el foco y se cierran con Esc (sin cambios)

**Seguridad**
- [x] Sin rutas nuevas (`rutas.md` sin cambios); el canario y el inventario siguen en verde
- [x] La consola no muestra errores ni violaciones de CSP (guardia de siempre)
- [x] Caminos alternos del STRIDE: el workflow corre en *pull requests* de forks solo con permisos de lectura y sin secretos
- [x] Dos peticiones a la vez: ahora en integración (`Concurrently`), con dos instancias sobre la misma base

**Cierre**
- [x] Pasada con teclado: la cubren las guardias de foco en todas las pruebas de UI
- [x] Hallazgos de un componente genérico: los tres arreglos son genéricos (`.content`, `Button`, `Layout`), no por pantalla
- [x] Alcance congelado: D-06, D-07, R-021-5 y R-021-6 quedan en el roadmap antes de Ventas

### CI
| Corrida | Commit | Resultado |
|---|---|---|
| [40235967108](https://github.com/leonardos4enz/erp-ai/actions/runs/40235967108) | `257c438` | Rápido (Ubuntu) **en verde** en 1 min 03 s: unitarias, build, Vitest y `npm audit`. Completo (`windows-2025`): LocalDB y `sqlcmd` presentes, integración **en verde** (1 min 13 s), **E2E falló** a los 11 min sin dejar reporte de Playwright. Los registros de un repositorio público solo se ven con sesión iniciada y la sesión no inicia sesiones: la spec se reabre y el workflow publica el motivo como anotaciones (`web/e2e/anotar-ci.mjs`, legibles por la API sin credenciales) |
| [40237335138](https://github.com/leonardos4enz/erp-ai/actions/runs/40237335138) | `a8cfa7e` | **En verde**: rápido 49 s; completo 10 min 25 s (E2E completa 8 min 13 s, misma suite y mismo código de producto que la corrida anterior). La primera falla fue intermitente y su prueba no se conoce: queda como **R-030-5**; desde esta corrida una falla deja su motivo en las anotaciones |

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#030): R-030-1 (caché del modelo por instancia), R-030-2 (workflow sin prueba fuera
de GitHub), R-030-3 (16 pantallas sin captura), R-030-4 (lo que las guardias no cubren), R-030-5 (falla intermitente desconocida en la primera corrida de CI). Actualizados: R-028-2, R-028-3 y
R-029-12.
