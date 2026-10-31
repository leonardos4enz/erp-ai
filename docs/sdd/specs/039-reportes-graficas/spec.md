# Spec 039 — Reportes 2: gráficas personalizables y medidas calculadas

**Estado:** Implementado · **Depende de:** 006 (`RecordFilter`, `FieldAccess`), 007 (reportes y tableros), 010 (empresa),
023 (rutas de varios saltos en expresiones), 026 (editor de reportes sin JSON) · **Origen:** pedido del dueño (2026-10-31):
«quiero ver sobre reportes, que también pueda hacer gráficas, no solo tablas» y «que se puedan personalizar más, no solo
catálogos». En CLARIFY eligió **todo**: gráficas de todos los tipos y con colores, varias series, indicador con
comparación, combinada; reportes personales, medidas y periodos, explorar al vuelo, cruzar más catálogos; tableros
personales y del modelo. La spec se divide (decisión 1): esta es la primera mitad.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `ReportChart.tsx` toma la **primera** agrupación y **una** medida (`points()`); `InterfaceRules.Charts` = bar, line, pie. | Un reporte con dos agrupaciones (mes × proveedor) o dos medidas (monto y pedidos) solo grafica una serie; no hay columnas, área, dona, apiladas ni combinadas. |
| 2 | Los colores son fijos: `.chart-bar { fill: var(--chart-1) }`; el pastel usa `--chart-1…8` por orden. | No se puede elegir el color de una serie ni mostrar los valores sobre la gráfica o la leyenda. |
| 3 | Un indicador de tablero (`type: kpi`) exige un reporte **sin** agrupación (`ValidateDashboard`): es el total y nada más. | No se ve si el mes va mejor o peor que el anterior. |
| 4 | `InterfaceRules.MeasureFns` = count, sum, avg, min, max sobre un campo. | Margen %, ticket promedio o «monto por pedido» exigen un campo fórmula por registro (y el promedio de porcentajes no es el porcentaje del total). |
| 5 | `InterfaceRules.Periods` = day, month, year. | No se agrupa por semana ni por trimestre. |
| 6 | `InterfaceRules.Resolve` acepta `Campo` o `Relacion.Campo` (un salto); el editor solo ofrece saltos por referencia, no por el documento padre, y las medidas solo campos propios. | Desde una partida no se agrupa por la categoría del producto (`Producto.Categoria.Nombre`) ni por el proveedor del documento (`OrdenCompra.Proveedor.Nombre`). |

## Principio
Capacidad **genérica del motor** sobre los reportes del modelo (`setReports`), sin código por módulo:
- **La gráfica es un metadato del reporte** (`chart` y `chartOptions`): tipo, qué medidas, apilado, colores de una paleta
  cerrada y si muestra valores. El servidor valida la combinación; la SPA solo dibuja lo que el reporte ya trae.
- **Las medidas calculadas no tocan SQL:** una expresión del lenguaje de la 002 sobre los **nombres** de otras medidas, que
  se evalúa en memoria sobre cada fila y sobre el total (el margen del total es suma ÷ suma, no el promedio de márgenes).
- **Cada salto es una unión con la seguridad del catálogo destino:** `RecordFilter` (alcance, empresa) en cada unión y
  `FieldAccess` en cada campo del camino; lo que no se ve es «(sin acceso)», como en la 007.
- Todos los caminos que corren un reporte (pantalla, exportación CSV/Excel, tableros) pasan por el mismo `ReportService`.

## Historias

**H1 — Tipos de gráfica.** Como *modelador*, quiero elegir columnas, barras, línea, área, pastel, dona o combinada para que
cada reporte se lea de la forma que mejor le queda.
- CA1: el editor de reportes ofrece los siete tipos; la pantalla del reporte y su tarjeta de tablero los dibujan.
- CA2: la combinada dibuja unas medidas como columnas y otras como línea **sobre el mismo eje** (dos escalas en una
  gráfica se leen mal: para monto y margen %, dos tarjetas).
- CA3: el servidor rechaza combinaciones sin sentido (pastel con dos medidas o con dos agrupaciones, combinada con una sola
  medida, apilada con una sola serie) con un mensaje que dice qué cambiar.

**H2 — Varias series.** Como *modelador*, quiero graficar varias series: la segunda agrupación (un color por proveedor) o
varias medidas (monto y pedidos), lado a lado o apiladas.
- CA1: con dos agrupaciones, la primera es el eje y la segunda las series; con una, cada medida elegida es una serie.
- CA2: columnas, barras y área se pueden apilar; con más de 12 series, las menores se juntan en «Otros».
- CA3: en un eje de fechas, los periodos sin registros aparecen en cero (conteo y suma) o vacíos (promedio, mínimo, máximo).

**H3 — Colores y presentación.** Como *modelador*, quiero elegir el color de cada serie de la paleta del sistema, mostrar
los valores y la leyenda, para que la gráfica sea clara y se vea bien en claro y en oscuro.
- CA1: la paleta tiene 12 tonos con nombre (se ven distintos entre sí y contra el fondo, en claro y oscuro); un nombre que no
  es de la paleta se rechaza.
- CA2: «mostrar valores» escribe el valor de cada marca; la leyenda aparece cuando hay más de una serie y se puede ocultar.
- CA3: cada gráfica tiene un resumen para lector de pantalla y su tabla debajo (el camino con teclado y lector); el valor
  de cada marca se ve al pasar el cursor.

**H4 — Indicador con comparación.** Como *usuario de un tablero*, quiero ver el valor del último periodo y cuánto cambió
contra el anterior, para saber si vamos mejor o peor.
- CA1: un reporte agrupado solo por una fecha con periodo puede mostrarse como indicador: valor del último periodo con
  datos, el periodo anterior y el cambio (flecha y %), con texto, no solo color.
- CA2: si el periodo anterior no tuvo registros, se compara contra cero (conteo, suma) o se dice «sin dato del periodo
  anterior»; con un anterior en cero no se inventa un porcentaje.
- CA3: un indicador de tablero acepta ese reporte; los reportes sin agrupar siguen siendo el total, como en la 007.

**H5 — Medidas calculadas y periodos.** Como *modelador*, quiero medidas que se calculan con otras medidas (margen %,
ticket promedio) y agrupar por semana o trimestre.
- CA1: una medida «calculada» lleva una expresión sobre los nombres de las otras medidas (`ROUND(Utilidad / Ventas * 100, 1)`);
  se calcula en cada fila y en el total; dividir entre cero o un vacío dejan vacía la medida (como en SQL; un margen sin
  ventas no es 0 %), y `COALESCE(x, 0)` lo trata como cero.
- CA2: la expresión se valida al guardar: solo nombres de medidas no calculadas, números, operadores y funciones numéricas
  (`IF`, `ROUND`, `ABS`, `ISBLANK`, `MIN`, `MAX`…); nada de campos, registros ni funciones de usuario.
- CA3: los periodos semana (ISO, de lunes; `2026-S05`) y trimestre (`2026-T1`) agrupan, ordenan y se exportan.

**H6 — Cruzar catálogos.** Como *modelador*, quiero agrupar y medir por campos a dos saltos
(`Producto.Categoria.Nombre`, `OrdenCompra.Proveedor.Nombre`), también por el documento padre, sin escribir rutas a mano.
- CA1: el editor ofrece campos propios, a un salto y a dos (por referencia o por el documento padre), en agrupaciones y
  medidas.
- CA2: un usuario que no ve un registro de un catálogo intermedio o final ve ese grupo como «(sin acceso)»; uno que no ve un
  campo del camino no ve el reporte (como en la 007).
- CA3: la exportación y las tarjetas de tablero dan exactamente lo mismo que la pantalla.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Lo pedido: más de 6 historias (≈11) · más de ~2 500 líneas (sí) · seguridad nueva (reportes
   personales, una ruta que escribe) junto con UI nueva (sí) · más de una pantalla principal (diseñador personal, exploración,
   tableros personales: sí). → **Se divide** (dueño, 2026-10-31): **039** gráficas, colores, series, medidas calculadas,
   periodos y dos saltos sobre los reportes **del modelo**; **040 — Reportes y tableros personales**: cualquier usuario arma los
   suyos con lo que ve, explorar al vuelo (cambiar agrupación, medida o gráfica sin guardar) y tableros personales junto a los
   del modelo. Ventas pasa a la **041** y MCP 2 a la **042**. La 039 queda en 6 historias, sin rutas nuevas. (La 040 recorrió la numeración: Endurecimiento 6 es la 040, los reportes personales la **041**, Ventas la **042** y MCP 2 la **043**.)
2. **Auditoría 7:** el dueño decide que vaya **después** de la 039 (cubrirá 034–039).
3. **Colores:** paleta del sistema de 12 tonos con nombre (`azul`, `naranja`, `verde`, `ambar`, `rosa`, `bosque`, `violeta`,
   `rojo`, `cian`, `oliva`, `purpura`, `lima`), con valores para claro y oscuro validados (`validate_palette.js`: separación
   para daltonismo ≥ 8 entre vecinos y ≥ 15 a simple vista); café y pizarra se descartaron por grisáceos. Sin color libre (dueño).
4. **De dónde salen las series:** de la segunda agrupación (una medida) o de varias medidas (una agrupación). No las dos a la
   vez: una matriz de grupos × medidas no se lee en una gráfica.
5. **Comparación del indicador:** último periodo **con datos** del reporte contra el inmediato anterior del calendario, no
   «hoy» del servidor: no depende de la zona horaria y se ve el periodo que se compara.
6. **Medidas calculadas:** se refieren a otras medidas por un `name` (identificador) que solo es obligatorio si alguien lo usa;
   no se encadenan (una calculada no usa otra calculada) y no ordenan el reporte (el orden va en SQL con `TOP`).
7. **Dos saltos como tope:** tres segmentos (`A.B.Campo`). Más saltos multiplican uniones con filtro; si hace falta, otra spec.

## Fuera de alcance (anotado en el roadmap)
- **040:** reportes personales, explorar al vuelo, tableros personales.
- Color libre (#hex), ejes logarítmicos, gráficas de dispersión, mapas y embudos.
- Tablas dinámicas (filas × columnas en la tabla): la tabla sigue siendo una fila por grupo.
- Reportes por MCP o por OData (hoy no los exponen; si se agregan, pasan por `ReportService`).
