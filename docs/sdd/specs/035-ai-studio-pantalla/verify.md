# Verify 035 — ERP AI Studio: pantalla

## Métricas
- inicio: 2026-10-27 19:50
- cierre: 2026-10-27 20:58
- unitarias: 765 (+7: 650 de Erp.Tests, +1; 115 de integración, +6; además Vitest 43, +5)
- e2e: 389 (+16: studio.spec.ts; cambia navegacion.spec.ts)
- corridas completas: 2 (7 min 34 s, 1 falla: F-06; 8 min 20 s, 389 en verde)
- corridas parciales: 9 (studio.spec.ts al iterar ×4; --veces=3 en modo lento sobre studio.spec.ts ×3, la primera con la falla F-08; compras + inventario + navegacion ×2; integración completa ×2 y filtrada ×4)
- contra el código anterior: 3 (it:regresion 9e70886 de H5 ×3; it:regresion 9e70886 de F01, F03 y la copia de más de 100 operaciones; e2e:regresion 9e70886 de compras, inventario y navegacion)
- hallazgos: 1/3/6
- categorías: seguridad=2, pruebas=2, motor-api=2, concurrencia=1, UI/UX=3
- revisión adversarial: 5 hallazgos (0 altos, 1 medio, 4 bajos)
- riesgos: R-035-1, R-035-2 (y R-033-4, R-033-10, R-033-11 actualizados)

## 0. Apertura
`git rev-list --count origin/main..main` = 0 y la última corrida de CI en `main` (`9e70886`, «Spec 034: CI en verde en
main») en verde. CLARIFY: un solo criterio de división (seguridad junto con UI nueva, por la purga de R-033-4); no se divide.

## 1. Pruebas unitarias e integración
`dotnet test`: **765 correctas**: 650 de `tests/Erp.Tests` (649 + 1) y 115 de `tests/Erp.IntegrationTests` (109 + 6).
`npm --prefix web test`: **43** (38 + 5, `studio.test.ts`).
- `StudioTests.Pantalla.cs` (nuevo, la clase pasa a `partial`): H5 CA1 al cerrar (lo leído está mientras corre y no
  después; entrada, estado y resumen se conservan) y al vencer; H5 CA2 (con `after` no hay plan, vista previa ni huella; sin
  `after`, sí; fechas con `Z`); F01 (al arrancar se purga lo que dejaron sesiones cerradas antes de la 035, no la que corre);
  F03 (una falla en la auditoría del cierre deja la sesión «running» con lo leído; sin la falla, vence y se purga).
- `StudioModulesTests › H3_CA1_la_copia_aislada_de_un_modelo_con_mas_de_100_operaciones_se_aplica_por_tramos` (F-06).
- `StudioCoreTests › Las_herramientas_de_datos_son_las_que_purga_el_arranque` (la lista en SQL de `Bootstrap` y el catálogo).
- Cambiada: `StudioTests › H1_CA3_una_herramienta_responde_el_mismo_403_que_la_API` leía la salida guardada de
  `leer_registros` después del cierre; ahora comprueba el 403 en lo que recibió el modelo y que la salida ya no está.
- `studio.test.ts`: `mergeTrail` (sin duplicados ni desorden), `consumption`, `elapsedMinutes`, `reportNotes` (R-033-11) y
  `planFile`.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa | 378 en verde, 1 falla (`studio.spec.ts › H2 CA1 CA2`: la copia aislada no se creaba con el modelo de la suite → F-06), 10 sin correr | 7 min 34 s |
| 2 | Completa, con F-06 corregido | **389 en verde** | 8 min 20 s |
| 3 | `--veces=3 --lento` sobre `studio.spec.ts` | vuelta 3: 1 falla (F-08) | 2 min 43 s |
| 4 | `--veces=3 --lento` sobre `studio.spec.ts` (con F-08) | 16 × 3 en verde | 3 min 13 s |
| 5 | `--veces=3 --lento` sobre `studio.spec.ts` (con F-09 y F-10, final) | 16 × 3 en verde | 3 min 16 s |

- `studio.spec.ts` (nuevo): proveedor simulado de Anthropic en `PORTS.asistente` con guion por pedido (`tool_use` reales
  del SDK; el motor de la 033 corre entero: herramientas, copia aislada, entrega). Un escenario por criterio, axe en el
  formulario y en la propuesta, y `page.route` solo para dos estados que la suite no puede producir: servidor sin copia
  aislada (H1 CA2) y entorno con aprobación (H3 CA3; la respuesta real la prueba `StudioTests › H4_CA2_sin_cambios_directos_no_se_aplica`).
- Capturas nuevas, revisadas una por una: `studio-propuesta-light.png`, `-dark.png` y `-movil.png` (de `main`, con vista
  alta porque el contenido se desplaza dentro de `main`; fechas, duraciones, minutos y el tiempo de preparar la copia
  enmascarados). En móvil, sin desplazamiento horizontal y el historial debajo de la sesión.
- `navegacion.spec.ts`: `H3 CA1 · CA2` lista «ERP AI Studio» en el grupo Modelo. `configuracion-*.png` no se regeneró:
  pasó en las dos corridas completas dentro de la tolerancia (1 %). Al revisarla se ve que la referencia ya no tenía
  «Imágenes» (027): no protege la lista de tarjetas (la protege la aserción de H3 CA1). Anotado en el roadmap.
- Contra el código anterior (`e2e:regresion 9e70886` con compras, inventario y navegacion): fallan H3 CA1 (espera la
  opción nueva) y H1 CA1 y H2 CA1 de navegación, que también fallan con `9e70886` corriendo sin el resto de la suite
  (dependen de catálogos que crean otros archivos). Por eso `navegacion.spec.ts` no va con `--veces=3`: la cubren las dos
  corridas completas.
- `Studio__PollSeconds=1`, `Studio__RenewSeconds=1` y `Studio__SessionsPerDay=50` en `playwright.config.ts`.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió: `git diff 9e70886` (motor, SPA, pruebas) y la tabla STRIDE. Revisó: la
purga (todos los estados finales, la transacción, carreras con un paso tardío), texto como datos (`dangerouslySetInnerHTML`,
`href` con datos), «revisar → aplicar» (de qué lectura sale la huella, doble clic), el sondeo (carreras, detención, cambio de
sesión), control de acceso y calidad de las pruebas. Encontró F-01 a F-05 (abajo). Sin hallazgos en texto como datos,
huella, acceso ni carreras de la purga.

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`) con un proveedor simulado propio en
loopback (`Assistant__BaseUrl`, clave ficticia) y el navegador integrado. Rol: administrador. Oscuro a 800 px y móvil
(375 px). Pedido con acentos, «ñ» y comillas; alcances con teclado (Tab y Espacio, el de «modelo» se salta por estar
deshabilitado); propuesta, diálogo con Esc (el foco vuelve a «Revisar y aplicar…»), doble clic en «Aplicar cambios» (un
solo conjunto de cambios y un aviso), foco al pedido tras aplicar; recargar con la sesión trabajando (retoma con sus pasos);
Atrás (la apertura automática reemplaza la entrada del historial); una sesión sin «aislado» cuyas herramientas aisladas se
niegan; consola sin errores (solo los 401 antes de iniciar sesión).

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | seguridad | Revisión adversarial: las sesiones cerradas antes de la 035 conservaban lo leído (la purga solo corre al cerrar). | `Bootstrap` purga, idempotente, las sesiones cerradas; la lista en SQL se compara con `StudioTools.DataTools`. | `StudioTests › F01_al_arrancar_se_borra_lo_leido_en_sesiones_cerradas_antes_de_la_035` (falla con `9e70886`) · `StudioCoreTests › Las_herramientas_de_datos_son_las_que_purga_el_arranque` |
| F-02 | Baja | seguridad | Revisión adversarial: las entradas que escribe el modelo también pueden copiar valores leídos, y el aviso prometía borrar «los registros». | Aviso: se borran las respuestas de las herramientas de datos; lo que el agente escriba se conserva. R-035-1 ampliado. | Riesgo [R-035-1](../../riesgos.md#035) |
| F-03 | Baja | pruebas | Revisión adversarial: nada probaba que la purga fuera atómica con el cierre. | Prueba con falla inyectada en la auditoría del cierre. | `StudioTests › F03_la_purga_va_en_la_transaccion_del_cierre` (falla con `9e70886`, que no purga) |
| F-04 | Baja | UI/UX | Revisión adversarial: la lista de herramientas de datos estaba fija en la SPA. | Se deriva de `GET /api/studio/tools` (`scope === 'datos'`). | `studio.spec.ts › H2 CA1 CA2` y `H2 CA4` (pasos con su texto) |
| F-05 | Baja | pruebas | Revisión adversarial: la E2E no vería pasos duplicados durante el sondeo (la lectura final los reemplaza). | Aserción de un solo `listar_catalogos` mientras trabaja. | `studio.spec.ts › H2 CA1 CA2` (y `studio.test.ts › mergeTrail…`) |
| F-06 | Alta | motor-api | Corrida completa: con más de 100 operaciones el plan de la copia aislada no compilaba; el alcance `aislado` no servía en un entorno con muchos catálogos (motor de la 033). Partirlo a ciegas tampoco: un documento cita su catálogo de partidas, creado después. | La copia se aplica por tramos: el prefijo más largo (≤ 100) que compila contra lo que ya tiene la copia. | `StudioModulesTests › H3_CA1_la_copia_aislada_de_un_modelo_con_mas_de_100_operaciones_se_aplica_por_tramos` (falla con `9e70886`) y `studio.spec.ts › H2 CA1 CA2` en la corrida completa |
| F-07 | Media | motor-api | Al escribir la pantalla: las fechas de Studio salían sin zona (UTC guardado, leído como `Unspecified`) y se mostraban como hora local. | `DateTimeKind.Utc` en los DTO. | `StudioTests › H5_CA2_sondear_con_after_no_compila_ni_da_la_huella` (fechas con `Z`; falla con `9e70886`) |
| F-08 | Media | concurrencia | `--veces=3 --lento`: tras un 409, el aviso «La propuesta se actualizó» salía antes de la lectura nueva y «Revisar y aplicar» podía mandar la huella vieja (otro 409). | El aviso y el botón esperan la lectura completa (botón ocupado mientras tanto). | `studio.spec.ts › H3 CA1 CA2` con `--lento` (falló en la vuelta 3 antes de corregirlo; la versión anterior de la pantalla no está en ningún commit) |
| F-09 | Baja | UI/UX | Exploratoria: el historial seguía «En cola» con la sesión ya trabajando. | El sondeo avisa al cambiar de estado y el historial se recarga. | `studio.spec.ts › H2 CA1 CA2` (el historial dice «Trabajando») |
| F-10 | Baja | UI/UX | Exploratoria: en móvil el resumen plegado de un paso (hasta 400 caracteres) ocupaba media pantalla. | Tres líneas como máximo en móvil. | Sin prueba: es CSS; las capturas móviles no tienen resúmenes de más de tres líneas (riesgo aceptado como cosmético) |

### Lista de comprobación
**Flujo**
- [x] El camino feliz de cada historia se completa sin ayuda ni conocimiento técnico
- [x] Los mensajes de error dicen qué pasó y cómo resolverlo, en español claro (409 de sesión activa, 409 de modelo cambiado, entorno con aprobación, sin clave)
- [x] Doble clic, Atrás del navegador, recargar a mitad
- [x] Datos límite: texto largo, acentos y ñ, comillas, texto hostil (`H2 CA4`)

**Diseño**
- [x] Claro y oscuro, escritorio y móvil: nada tapado, cortado ni desbordado (F-10 corregido)
- [x] Solo iconos Lucide, sin emojis
- [x] Estados vacíos («Aún no hay sesiones», «Todavía no hay pasos»), de carga y de error con diseño

**Accesibilidad**
- [x] Todo con teclado; foco visible y en orden lógico (tras aplicar, cancelar o descartar, al pedido)
- [x] El diálogo atrapa el foco y se cierra con Esc
- [x] axe sin violaciones serias (formulario y propuesta)

**Seguridad**
- [x] Sin capacidad de modelador no ve la opción ni llega por URL (`H1 CA1`)
- [x] Consola sin errores ni violaciones de CSP
- [x] Caminos alternos de la tabla STRIDE con un usuario sin permiso: `StudioTests › H4_CA2_otra_persona_no_aplica_ni_ve_la_sesion`
- [x] Dos peticiones a la vez: doble clic en aplicar (exploratoria) y dos pestañas pidiendo (`H1 CA3`)

**Cierre**
- [x] Pasada solo con teclado por la pantalla nueva
- [x] Hallazgos de un componente genérico: el resumen de paso es propio de Studio; `PlanPreviewView` se reutiliza sin cambios
- [x] Alcance congelado: F-06 entró porque bloquea el alcance `aislado`; lo demás pedido en el roadmap

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md): [R-035-1](../../riesgos.md#035) (lo que escribe el modelo puede citar
valores leídos), [R-035-2](../../riesgos.md#035) (sondeo sin límite propio). Cerrados: R-033-4 y R-033-10; R-033-11
atendido en su primera mitad.

## 6. CI
El push a `main` corre integración y la E2E completa (segunda corrida completa). El resultado lo anota la spec 036 al abrir
(sin un commit aparte que solo actualice este archivo).
