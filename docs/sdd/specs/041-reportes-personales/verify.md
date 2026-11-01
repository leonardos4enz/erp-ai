# Verify 041 — Reportes personales y exploración al vuelo

## Métricas
- inicio: 2026-11-01 00:31
- cierre: 2026-11-01 08:27
- unitarias: 1020 (+34: 728 de Erp.Tests, +18; 219 de integración, +14; Vitest 73, +2)
- e2e: 439 (+16)
- corridas completas: 1 (dotnet test 1 min 50 s + E2E 12 min 06 s, limpia; una E2E anterior se cortó al cerrarse la sesión y no cuenta); la segunda es CI
- corridas parciales: 13 (iteración de `reportes-personales` + `canario` 6; contra el código anterior 5: integración 1, E2E 4; `--veces=3` 2)
- contra el código anterior: 5 (mutación de los arreglos de la revisión adversarial en integración y unitarias; mutaciones de F-01–F-06 y F-G en E2E con `E2E_INDEPENDENT=1`)
- hallazgos: 0/3/12 (adversarial F-A…F-I y exploratoria F-01…F-06)
- categorías: seguridad=3, motor-api=4, UI/UX=5, accesibilidad=2, pruebas=1
- revisión adversarial: 9 hallazgos
- riesgos: R-041-1, R-041-2, R-041-3, R-041-4

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0. La última corrida de CI de `main` (`58e198e`, solo `verify.md` de la 040) estaba
en **rojo**: `explorador.spec.ts › H1 CA9` leyó el foco en `null`. Se corrigió primero (commit `4ec4bda`, «Spec 040: CI en
rojo corregido»): `openDb` esperaba «N tablas», visible ya con la lista sin filtrar, y la respuesta filtrada por módulo quitaba
la caja enfocada. Con la respuesta filtrada retrasada 400 ms, el `openDb` anterior hace fallar las dos capturas del diagrama
(la misma carrera explica la captura de `ccf8588`, R-030-5) y el nuevo pasa las 42 de `compras` + `explorador`. CI de
`4ec4bda`: **success**.

## 1. Pruebas unitarias e integración
`dotnet test`: **728 unitarias** (710 de antes + 18 nuevas en `tests/Erp.Tests/PersonalReportsTests.cs`) y **219 de
integración** (205 + 14 en `tests/Erp.IntegrationTests/PersonalReportsTests.cs`), en verde, 1 min 50 s.
- Unitarias: lo oculto (salto, campo, medida, filtro) responde igual que lo inexistente; catálogo que no lee = 404 igual que
  inexistente; salto por un catálogo que no lee solo al campo para mostrar; lo que el modelo rechaza sin el prefijo del reporte;
  17 uniones rechazadas y 16 aceptadas; definición de más de 20 000 caracteres; elementos nulos (F-E); campos y fuentes que se
  ofrecen; ninguna credencial (clave de API, OAuth con todos sus alcances, MCP) llega a `/api/my-reports`.
- Integración (SQL real): alcance y empresa al explorar; oráculos; fuentes; el reporte del modelo explorado da lo mismo; la
  exportación da lo mismo; solo el dueño (404 igual); versión (409); diez altas simultáneas con 45 guardados → cinco 201 y cinco
  400, 50 exactos; diez altas simultáneas con el mismo nombre → un 201; un guardado deja de correr al ocultarse un campo; alta y
  bitácora en una transacción (con la bitácora fallando no queda el reporte); bitácora sin la definición y bajo el documento
  (F-B, F-D); sin cambio de catálogo (F-C); versión antes que validación (F-F).
- Vitest: 73 (71 + 2 de `lib/__tests__/personalReport.test.ts`); la guardia de pantallas exige las tres capturas nuevas.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa | 439 en verde | 12 min 06 s (12.1 m) |
| 2 | `--veces=3` sobre `e2e/reportes-personales.spec.ts`, `e2e/canario.spec.ts`, `e2e/compras.spec.ts`, `e2e/explorador.spec.ts` (lento) | 3 × 67 en verde (la primera vez falló `H2` en la vuelta 1, ver abajo) | 7 min 23 s |

La primera `--veces=3` falló en la vuelta 1: `H2` leía las opciones de «Catálogo» antes de que llegara `/api/my-reports/sources` (carrera de la prueba que solo sale con `--lento`); ahora espera la opción. La corrida completa sin `--lento` la había pasado.

Archivos nuevos: `reportes-personales.spec.ts` (15 pruebas: una por CA, más preparar y tres capturas). Modificados:
`canario.spec.ts` (`explore` y `explore/export` en el recorrido de lo que no es GET, con 21 definiciones por las fórmulas al
secreto, el campo oculto, dos saltos, otra empresa y filtros por lo oculto; y `H5 (041)` con el control: el administrador sí
ve el canario por esos caminos) y `explorador.spec.ts` (`openDb`, arriba). Capturas nuevas: `explorador-light.png`,
`explorador-dark.png` y `explorador-movil.png`, revisadas una por una (la de móvil se rehízo tras F-03; las de escritorio,
tras F-01 y F-02).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff de la spec y la tabla STRIDE del plan. Revisó campos y catálogos
ocultos, oráculos, alcance y empresa, reportes de otro, credenciales, despachadores, concurrencia, fallas parciales, límites
y la SPA. Sin hallazgos altos ni fugas de datos verificadas. Encontró:

| ID | Severidad | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|
| F-A | Media | Explorar deja a cualquiera armar el peor caso; `aggregate` limita la tasa, no la concurrencia | — | [R-041-1](../../riesgos.md#041) |
| F-B | Media | La bitácora (acción de catálogo) mostraba a un auditor la definición del reporte personal de otro: campos ocultos, valores de filtros | Solo nombre y huella SHA-256 | IT `Bitacora_no_lleva_la_definicion_y_va_bajo_el_documento` |
| F-C | Baja | Cambiar de catálogo dejaba la definición anterior bajo el catálogo nuevo | 400: no cambia de catálogo | IT `Un_reporte_guardado_no_cambia_de_catalogo` |
| F-D | Baja | El de una partida no lo veía ningún auditor (la bitácora filtra por catálogo raíz) | Se registra bajo el documento | IT `Bitacora_no_lleva_la_definicion_y_va_bajo_el_documento` (misma prueba; falla primero por F-B) |
| F-E | Baja | `groupBy: [null]` daba 500 | 400 con mensaje | unit `Check_con_elementos_nulos_da_400` |
| F-F | Baja | La versión se comprobaba después de validar (400 y luego 409) | Versión primero | IT `Version_vieja_da_409_antes_que_la_validacion` |
| F-G | Baja | Pasar de un guardado a explorar uno del modelo sin volver a montar dejaba «Guardar» apuntando al guardado | `setSaved(null)` al explorar | `reportes-personales.spec.ts › H3 CA3` |
| F-H | Baja | La definición del reporte del modelo llega también a claves de API y OAuth | — | [R-041-2](../../riesgos.md#041) |
| F-I | Baja | Citas de STRIDE que no coincidían con el código (candado, unitarias citadas como integración, límites) | Tabla corregida | — (documentación) |

Contra el código anterior: con `SaveAsync`/`DeleteAsync` y `Check` previos, las cuatro pruebas nuevas (F-B/F-D, F-C, F-E, F-F)
fallan; con el código actual pasan.

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision -- --sin-compilar` (puerto 5950, base `ErpAi_Revision_E2E`), con un catálogo de 40
pedidos, campo «Margen interno» solo para Administrador y alcance «Zona <> Corporativo» para el rol de la lectora (sin
modelador). Pantallas: Reportes, Nuevo reporte, explorador (dos agrupaciones, columnas apiladas), guardar, guardar como,
reporte del modelo con «Explorar», en oscuro a 1024 px y en claro a 390 px (sin desplazamiento horizontal).

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Baja | UI/UX | Sin periodo, el campo de la agrupación quedaba en un tercio de la fila (~115 px) | Ocupa la fila | `reportes-personales.spec.ts › H2 …` (ancho > 200 px) |
| F-02 | Baja | UI/UX | «Nombre para calcular» no cabía en su campo | «Nombre»; la ayuda explica | `… › H2 …` (el texto de ejemplo cabe, medido con `measureText`) |
| F-03 | Media | UI/UX | En una columna, el resultado quedaba bajo el formulario largo: al cambiar algo no se veía | Resultado primero | captura `explorador-movil.png` |
| F-04 | Baja | UI/UX | La miga en `/explore` decía solo «Inicio» | «Reportes › Explorar / Mi reporte» | `… › H2 …` (la miga lleva a Reportes) |
| F-05 | Baja | accesibilidad | Tras «Guardar como mío» el foco caía en el contenido principal | Enfoca «Guardar» | `… › H3 CA1` (no reproduce la falla en Playwright: [R-041-3](../../riesgos.md#041)) |
| F-06 | Baja | accesibilidad | El diálogo de nombre enfocaba «Cerrar» | `data-autofocus` en el nombre | `… › H3 CA1` (el nombre tiene el foco) |

### Contra el código anterior
Mutaciones con `E2E_INDEPENDENT=1` (no hay commit intermedio con el código previo a cada arreglo): revirtiendo F-01–F-05 y
F-G fallan `H2` (F-04), `H3 CA3` (F-G) y la captura de móvil (F-03); revirtiendo F-01, F-02 y F-06 fallan `H2` (F-01) y
`H3 CA1` (F-06, y en cascada las que usan su reporte); revirtiendo solo F-02 falla `H2`. F-05 no falla con el código anterior
en Playwright (R-041-3).

### Lista de comprobación
- [x] Cada CA con su escenario E2E (`H1 CA1` … `H5 CA1`).
- [x] axe sin violaciones serias en el explorador (`H1 CA1`, `H2 …`).
- [x] Claro, oscuro y móvil (capturas revisadas), sin desplazamiento horizontal a 390 px.
- [x] Teclado: el diálogo de nombre se guarda con Enter; el foco queda en «Guardar».
- [x] Errores 400 en `role="alert"` sin perder lo armado (`H1 CA3`); 409 con «Volver a abrir» (`H3 CA2`).
- [x] Rutas nuevas en el inventario (`docs/sdd/rutas.md`) y en el canario.

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#041): R-041-1 (sin tope de concurrencia al explorar), R-041-2 (definición del
reporte del modelo para credenciales), R-041-3 (F-05 sin prueba que falle), R-041-4 («Volver a abrir» descarta lo no guardado).

## 6. CI
PENDIENTE
