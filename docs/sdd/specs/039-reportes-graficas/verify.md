# Verify 039 — Reportes 2: gráficas personalizables y medidas calculadas

## Métricas
- inicio: n/d (la sesión abrió la mañana del 2026-10-31; la spec se escribió a las 10:5x)
- cierre: 2026-10-31 12:00
- unitarias: 965 (+48: 704 de Erp.Tests, +23; 190 de integración, +12; Vitest 71, +13)
- e2e: 422 (+10, `reportes-graficas.spec.ts`)
- corridas completas: 1 (dotnet test 1 min 38 s + E2E 10 min 13 s, limpia); la segunda es CI
- corridas parciales: 7 (iteración de `reportes-graficas.spec.ts` ×4; `--veces=3` sobre `reportes-graficas.spec.ts` 1 min 32 s; `Reports039Tests` de integración ×3; dotnet test completo antes de la revisión adversarial)
- contra el código anterior: 7 (F-A, F-B, F-C y F-E: las pruebas nuevas corridas con el código sin corregir, 4 fallas; F-01, F-02 y F-03 con su lógica anterior)
- hallazgos: 0/3/4 (F-D no se cuenta: ya era R-039-2)
- categorías: seguridad=2, motor-api=2, UI/UX=2, visual=1
- revisión adversarial: 5 hallazgos (F-A…F-E; F-D ya era R-039-2)
- riesgos: R-039-1, R-039-2, R-039-3, R-039-4

## 0. Apertura
- `git rev-list --count origin/main..main` = 0.
- CI de `main` en verde: la corrida 41073794484 de `31d0daf` («Spec 038: CI en rojo corregido») terminó en `success`
  (anotado por la regla de apertura; la 038 cerró antes de verla terminar).
- **Auditoría 7:** pendiente (tocaba al cerrar la 038). El dueño decidió en CLARIFY que vaya **después de la 039**, en
  sesión aparte; cubrirá 034–039.
- El servidor de desarrollo `erp-api` (5080) no estaba corriendo: no hubo que apagarlo. Solo se detuvo la instancia de la
  revisión exploratoria (5950), arrancada en esta sesión. La base `ErpAi` y la contraseña de `admin` no se tocaron; nunca se
  llamó a la API de Anthropic.

## 1. Pruebas unitarias e integración
`dotnet test`: **704 unitarias** (681 + 23 de `Reports039Tests`: cada combinación de gráfica y su mensaje, paleta y periodos
cerrados, medidas calculadas (campos, rutas, funciones prohibidas, encadenadas, booleanas, largo, aridad, orden), vacíos y
`/ 0`, rutas de dos saltos y de tres rechazadas, disponibilidad con un salto oculto, tarjeta kpi por periodo, semana ISO en
el cambio de año, periodo anterior y F-C) y **190 de integración** (178 + 12 de `Reports039Tests` con SQL real: alcance del
intermedio y del final, catálogo por empresa en el salto, campo oculto en el camino → 404 (también en la exportación),
calculadas en filas y total, semana y trimestre, `DATEFIRST` 1 y 7, comparación con el alcance del usuario, exportación y
tablero iguales a la pantalla, F-A, F-B y F-E). `InterfaceTests` ajusta un mensaje (la gráfica ahora nombra su tipo).
`npm --prefix web test`: **71** (58 + 13 de `reportChart.test.ts`: periodos ida y vuelta, relleno con tope y con orden,
series de la segunda agrupación, «Otros», combinada, colores, escala, indicador, F-01, F-02, años de dos cifras).

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa | 422 en verde | 10 min 13 s |
| 2 | `--veces=3` (lento) sobre `e2e/reportes-graficas.spec.ts` | 3 vueltas en verde (10 + 10 + 10) | 1 min 32 s |

`reportes-graficas.spec.ts` (nuevo, no depende de otro archivo): un escenario por criterio. Columnas apiladas con colores y
meses en cero; combinada en un eje, dona con valores, área por semana, línea por trimestre y barras agrupadas; indicador en el
reporte y en el tablero; calculada en filas y total; el lector a dos saltos («(sin acceso)», sin el reporte con un campo
oculto, CSV igual); el modelador arma un reporte a dos saltos con calculada, combinada y colores sin JSON; y el error de una
combinación sin sentido. `visual-modelador.spec.ts` cambia una línea: el selector se llama ahora «Tipo de gráfica»
(«Gráfica» es la sección).

Capturas nuevas (revisadas una por una): `tablero-graficas-light.png`, `-dark.png` y `-movil.png`. La primera versión tenía
texto del doble de tamaño en las tarjetas anchas y diminuto en las angostas (viewBox fijo) y barras en cero sobre el eje; no
se aprobó: la gráfica ahora mide su contenedor y no dibuja barras en cero. Sin rutas nuevas en la SPA ni en la API
(`RouteInventory` sin cambios).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff (`git diff HEAD` y los archivos nuevos), las preguntas de las 8
filas STRIDE y las amenazas sin sus mitigaciones. Revisó SQL de periodos y `DATEFIRST`, lista blanca de calculadas y de
colores, uniones por salto, comparación y totales, caminos alternos (pantalla, exportación, tableros, compilador de planes),
delegación y límites.

| ID | Severidad | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|
| F-A | Media | Las uniones aplicaban alcance y empresa pero no el **permiso de lectura** del catálogo destino: quien no lee Categorías agrupaba por `Producto.Categoria.Publica` (preexistente a un salto desde la 007; la 039 lo extendía a dos). | `Available` exige leer cada catálogo del camino; sin permiso, solo su campo para mostrar al final (lo que da el buscador). | `Reports039Tests › F_A_sin_leer_el_catalogo_del_salto_solo_su_campo_para_mostrar` |
| F-B | Media | Agrupar por una referencia daba una fila «(sin acceso)» **por cada registro oculto** (el `GROUP BY` llevaba su id): se sabía cuántos había y cuánto sumaba cada uno (preexistente desde la 007). | Sin acceso, id y nombre agrupan como nulo: una sola fila. | `Reports039Tests › F_B_los_registros_ocultos_de_una_referencia_van_en_una_sola_fila` |
| F-C | Baja | Un registro fechado en el año 1 hacía fallar el periodo anterior: 500 en todo el tablero. | `PreviousPeriod` da null; la comparación queda sin anterior. | `Reports039Tests › F_C_el_periodo_anterior_al_anio_1_no_existe` |
| F-D | Baja | El indicador toma el último periodo **con datos** (una fecha futura lo mueve). | Ya registrado: R-039-2 (decisión de CLARIFY 5). | — |
| F-E | Baja | Las calculadas usaban valores ya redondeados (93.33 × 3 = 279.99). | Se evalúan sobre el valor sin redondear; solo el resultado se redondea. | `Reports039Tests › F_E_la_calculada_usa_los_valores_sin_redondear` |

También señaló (sospecha) que `Date.UTC` toma los años 0–99 como 19xx en la SPA: corregido con prueba en Vitest.

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`), administrador; datos de 10 meses con un
mes sin ventas, categorías y productos con nombres largos. Pantallas: tablero con seis tarjetas, reporte de margen, editor
del modelador; claro y oscuro a 1280 px; móvil (390 px) en la captura de la suite. Consola sin errores de JS ni CSP (solo un
401 al recargar tras reiniciar la instancia).

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | visual | Con «mostrar valores», la combinada en una tarjeta angosta encimaba las etiquetas de columnas y línea (se contaban las marcas, no el ancho). | `valueLabelsFit`: los valores se escriben solo si caben en el ancho de la categoría. | `reportChart.test.ts › 039 F-01` |
| F-02 | Baja | UI/UX | Pastel y dona no ordenaban las rebanadas: «Otros» podía juntar rebanadas grandes y una categoría tomaba otro color que en la apilada del mismo tablero. | `pieSlices`: de mayor a menor. | `reportChart.test.ts › 039 F-02` |
| F-03 | Baja | UI/UX | En el editor, con «Automático» la muestra de color salía vacía. | La muestra enseña el color del lugar fijo. | `reportes-graficas.spec.ts › H6 CA1 …` (muestra «azul») |

### Contra el código anterior
F-A, F-B, F-C y F-E: las pruebas se escribieron primero y se corrieron con el código sin corregir (3 fallas en integración,
1 en unitarias; F-B dio además un 500 a medio corregir, por el `ORDER BY`, que se corrigió). F-01 y F-02: la regla anterior
(contar marcas; orden de filas) no cumple las aserciones nuevas. F-03: la muestra anterior era transparente.

### Lista de comprobación
**Flujo**
- [x] El camino feliz de cada historia se completa sin ayuda ni conocimiento técnico
- [x] Los mensajes de error dicen qué pasó y cómo resolverlo (combinación sin sentido en la vista previa del plan)
- [x] Recargar a mitad de la edición (el borrador del modelador se conserva como antes)
- [x] Datos límite: nombres largos (se recortan con «…» y el valor completo va en el cursor y en la tabla), mes sin ventas, ceros

**Diseño**
- [x] Claro y oscuro, 1280 y 390 px: nada tapado ni desbordado
- [x] Solo iconos Lucide (flechas del indicador), sin emojis
- [x] Estados vacíos («Sin datos todavía»), carga y error sin cambios

**Accesibilidad**
- [x] Todo el editor se usa con teclado (controles nativos); la tabla es el camino de teclado para los valores (R-039-3)
- [x] axe sin violaciones serias en el reporte y el tablero (`expectAccessible`)

**Seguridad**
- [x] Un usuario sin permiso no ve el reporte ni llega por URL (404)
- [x] Consola sin errores ni violaciones de CSP
- [x] Caminos alternos con usuario sin permiso: API, exportación y tablero (`Reports039Tests`)
- [x] Dos peticiones a la vez: no aplica (solo lectura, sin estado)

**Cierre**
- [x] Pasada con teclado por el editor del modelador
- [x] Hermanos de un componente genérico: el arreglo de tamaño se aplica a columnas, línea, área, combinada y barras; el pastel es de tamaño fijo
- [x] Alcance congelado: nada nuevo del dueño durante VERIFY

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#039): R-039-1 (sumas de varias monedas), R-039-2 (el indicador toma el último
periodo con datos), R-039-3 (valores por cursor; la tabla es el camino con teclado), R-039-4 (semana y trimestre heredan
R-007-1, fechas con hora en UTC).
