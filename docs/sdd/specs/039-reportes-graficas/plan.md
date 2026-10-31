# Plan 039 — Diseño técnico

Sin tablas ni rutas nuevas: todo es metadato del reporte (`setReports`, `setDashboard`) y lo corre el mismo
`ReportService` que ya usan la pantalla, la exportación y los tableros.

## 1. Modelo de datos (metadatos de `ReportDef`, en `ReportsJson`)
```jsonc
{
  "groupBy": [{ "field": "OrdenCompra.Proveedor.Nombre", "period": "week|quarter|day|month|year" }],
  "measures": [
    { "fn": "sum", "field": "Importe", "name": "Ventas" },
    { "fn": "sum", "field": "Costo", "name": "Costo" },
    { "fn": "calc", "expr": "ROUND((Ventas - Costo) / Ventas * 100, 1)", "label": "Margen %", "scale": 1 }
  ],
  "chart": "column|bar|line|area|pie|donut|combo|kpi",
  "chartOptions": {
    "measures": [0, 1],      // medidas que se dibujan (índices; por omisión [0])
    "lines": [1],            // combinada: cuáles van como línea (mismo eje; nunca dos escalas)
    "stacked": true,         // columnas, barras o área con más de una serie
    "colors": ["azul", "naranja"], // paleta cerrada, en el orden de las series
    "values": true,          // escribe el valor de cada marca
    "legend": false          // por omisión: leyenda si hay más de una serie
  }
}
```
- **Rutas:** `Campo`, `A.Campo` o `A.B.Campo`; cada salto es una referencia o el documento padre (`FieldPath` pasa de un
  `Via` a una lista de `Hops`). Tope: 2 saltos.
- **Periodos:** `week` agrupa por el lunes (`DATEADD(day, -((DATEPART(weekday, c) + @@DATEFIRST + 5) % 7), CAST(c AS date))`,
  independiente de `DATEFIRST`) y se etiqueta con la semana ISO de ese lunes (`2026-S05`, `ISOWeek`); `quarter`, por el primer
  día del trimestre (`2026-T1`). Ordenan por la fecha, no por la etiqueta.
- **Medida calculada** (`fn: calc`): `expr` del lenguaje de la 002 con un subconjunto cerrado: literales numéricos, nombres de
  medidas **no calculadas** con valor numérico, `+ - * /`, comparaciones, `AND/OR/NOT` y `IF`, `COALESCE`, `ISBLANK`, `ROUND`,
  `ABS`, `MIN`, `MAX`. Se evalúa en C# (`ReportCalc`, evaluador propio del subconjunto) sobre cada fila y sobre el total; nunca
  va a SQL. A diferencia de las fórmulas (vacío = 0, 022 F-04), **un vacío se propaga** como en SQL: `/ 0` o un promedio sin
  datos dejan vacía la medida, y `ROUND` de un vacío es vacío; `COALESCE(x, 0)` lo trata como cero. `scale` 0–6 (por omisión 2). El `name` de una medida es un identificador
  único en el reporte, obligatorio solo si una calculada lo usa.
- **Comparación (H4):** si el reporte se muestra como indicador (`chart: kpi`, o tarjeta `kpi` de un tablero) y su única
  agrupación es una fecha con periodo, `RunAsync` hace una segunda consulta `TOP (2) … ORDER BY periodo DESC` con el mismo
  `WHERE` y devuelve `comparison`. El anterior cuenta solo si es el periodo **inmediato** anterior del calendario; si no hubo
  registros, `previous` va con 0 en conteo y suma y vacío en las demás.
- **Paleta:** 12 nombres → `--chart-1…12` (claro y oscuro, en `app.css`; las 8 primeras son las de la 007, las 4 nuevas validadas
  con el validador de paletas en ambos modos). Lista cerrada en `InterfaceRules.Palette` y en la SPA.

## 2. Contrato de la API (sin rutas nuevas)
| Método y ruta | Capacidad | Cambio |
|---|---|---|
| `GET /api/reports/{entity}/{name}` | `records` (sin cambio) | La respuesta agrega `chartOptions`, `period` en las columnas de grupo de fecha y `comparison` cuando aplica. |
| `GET /api/reports/{entity}/{name}/export` | `records` | Igual que la pantalla: columnas de las calculadas, etiquetas de semana y trimestre. |
| `GET /api/dashboards/{name}` | `records` | Las tarjetas `kpi` de un reporte por periodo traen `comparison`. |
| `POST /api/model/plans…` (`setReports`, `setDashboard`) | modelo (sin cambio) | Valida las propiedades nuevas (§1) con mensajes para el modelador. |

```jsonc
// ReportResult (agregados)
"columns": [{ "key": "g0", "label": "Fecha", "kind": "group", "type": "text", "period": "month" }, …],
"chartOptions": { … },                     // tal como se declaró (o null)
"comparison": {                            // solo indicador sobre un reporte por periodo
  "current":  { "label": "2026-10", "values": { "m0": 1200.5 } },
  "previous": { "label": "2026-09", "values": { "m0": 980 } } | null   // null: no hay periodo con datos
}
```
Errores: los de la 007 (404 si no existe o no está disponible; 400 de validación en el plan).

## 3. SPA
- `web/src/lib/reportChart.ts` (nuevo, puro, con Vitest): series (pivote de la segunda agrupación o varias medidas), «Otros»
  más allá de 12 series, relleno de periodos sin registros (día, semana ISO, mes, trimestre, año; tope 1 000), periodo
  anterior y cambio porcentual del indicador, paleta.
- `ReportChart.tsx`: columnas, barras, línea, área (agrupadas o apiladas), pastel, dona, combinada (columnas y líneas sobre un
  solo eje) e indicador. Leyenda (`ul` visible) y valores opcionales; `role="img"` con resumen; `<title>` por marca; la tabla
  sigue debajo y es el camino para teclado y lector de pantalla. Flechas del indicador con Lucide (`ArrowUpRight`,
  `ArrowDownRight`, `Minus`), nunca solo color.
- `ReportsPage` y `DashboardsPage` pasan `chartOptions`, `comparison` y la medida de la tarjeta.
- Modelador (`ViewsReportsEditor.tsx`): semana y trimestre; campos a uno y dos saltos (referencia y documento padre) en
  agrupaciones y medidas; medida «Calculada» con nombre, expresión y decimales; sección «Gráfica»: tipo, medidas que se
  dibujan, cuáles van como línea, apilar, valores, leyenda y color de cada serie con muestra.
- Prompt del asistente y de Studio: las opciones nuevas de `setReports`.

## 4. Pruebas
- **Unitarias** (`tests/Erp.Tests/Reports039Tests.cs`, nuevo): validación de cada combinación de gráfica, paleta, medidas
  calculadas (nombres, encadenadas, funciones prohibidas, campos), periodos, rutas de dos saltos (y tres rechazadas),
  disponibilidad con un campo oculto en un salto intermedio; etiquetas de semana ISO en el cambio de año y periodo anterior.
- **Integración** (`tests/Erp.IntegrationTests/Reports039Tests.cs`, nuevo, SQL real): dos saltos con alcance «propios» en el
  catálogo intermedio y en el final («(sin acceso)»), catálogo por empresa en el salto, campo oculto en el camino → 404;
  calculadas en filas y total, `/ 0`; semana y trimestre (orden y etiquetas, `SET DATEFIRST` distinto); comparación con
  hueco y sin anterior; exportación CSV y tablero idénticos a la pantalla.
- **Vitest** (`web/src/lib/reportChart.test.ts`, nuevo).
- **E2E** (`web/e2e/reportes-graficas.spec.ts`, nuevo): un escenario por CA, editor del modelador por la UI, capturas claro,
  oscuro y móvil de un tablero con los tipos nuevos. Con `--veces=3` al cerrar junto con `interfaz.spec.ts` y
  `visual-modelador.spec.ts` (los toca el cambio).

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones de plan…? | Ninguna ruta nueva. Cambian `setReports` y `setDashboard` (capacidad de modelo, sin cambio) y las respuestas de tres GET con `records`. La expresión de una calculada es entrada del modelador (o del asistente): se valida con lista blanca de nodos y funciones antes de guardar. | `Reports039Tests.Calculada_rechaza_campos_funciones_y_encadenadas` (unit); `RouteInventory` sin cambios |
| Caminos alternos | ¿Por qué otros caminos se llega al mismo dato? | Pantalla, exportación CSV/Excel y tarjetas de tablero llaman a `ReportService.RunAsync`; el asistente y Studio solo proponen planes (pasan por la misma validación); paquetes y reversión copian `ReportsJson` y se compilan igual. OData y MCP no corren reportes. | IT `Exportacion_y_tablero_iguales_a_la_pantalla`; unit `Paquete_con_reporte_invalido_no_compila` |
| Lectura de datos | ¿`RecordFilter`, `FieldAccess` y empresa? | Cada salto une el catálogo destino filtrado por `RecordFilter.ForRecordOrParent` (alcance y empresa) y el grupo sale «(sin acceso)» si la llave existe y la unión no la trae, en **cualquier** salto. `Available` exige ver el campo final y **cada** campo del camino, y (revisión adversarial F-A) **leer** cada catálogo al que llega un salto; sin ese permiso, solo su campo para mostrar al final del camino, lo mismo que da el buscador. Si no, 404 como en la 007. Los registros ocultos de una referencia van en **una** fila «(sin acceso)» (F-B: antes, una por registro). La pantalla de visualización del campo final (`DisplayHidden`) también vale en el segundo salto. | IT `Dos_saltos_respetan_el_alcance_del_intermedio_y_del_final`, `Dos_saltos_con_catalogo_por_empresa`, `Campo_oculto_en_el_camino_da_404`, `F_A_sin_leer_el_catalogo_del_salto_solo_su_campo_para_mostrar`, `F_B_los_registros_ocultos_de_una_referencia_van_en_una_sola_fila`; unit `Disponible_exige_ver_cada_salto` |
| Delegación | ¿Alguien otorga lo que no tiene? | No aplica: no hay roles, credenciales ni aprobaciones nuevas. Un reporte del modelo corre siempre con el usuario que lo ve (no con el modelador que lo definió). | IT `Dos_saltos_respetan_el_alcance…` (mismo reporte, dos usuarios, dos resultados) |
| Concurrencia | ¿Invariante entre filas? | No aplica: solo lectura, sin estado entre peticiones; las calculadas se evalúan por petición sobre valores inmutables. Guardar un reporte sigue la huella del plan (012-5). | — (sin escritura nueva) |
| Fallas parciales | ¿Qué pasa si cae entre dos pasos? | No aplica: la comparación es una segunda consulta de la misma lectura (mismo comando y lote); si falla, la petición entera falla (500 genérico), no se muestra un indicador a medias. *Nota de la 040 (auditoría 7, D-11):* son tres SELECT del mismo lote en READ COMMITTED, no una sola lectura: con escrituras simultáneas el total puede no cuadrar con las filas ([R-040-2](../../riesgos.md#040)). | — (sin escritura nueva) |
| Límites | Tamaño, paginación, topes | Uniones acotadas: ≤ 2 agrupaciones + 6 medidas, ≤ 2 saltos, uniones repetidas reutilizadas (≤ 16; *nota de la 040, D-09:* el tope no existía en el código, lo valida `InterfaceRules.MaxJoins` desde la 040); `TOP (@take)` ≤ 1 000 y 30 s de comando (007); relleno de periodos en la SPA con tope 1 000; series ≤ 12 (resto en «Otros»); expresión ≤ 500 caracteres. Tasa: `Aggregate` y `Bulk` (031) sin cambio. | unit `Rutas_de_tres_saltos_y_expresiones_largas_se_rechazan`; Vitest `relleno con tope` y `Otros` |
| Semántica | Nulos, monedas, fechas, redondeo | Vacío en una medida: en la calculada se propaga (aritmética, `ROUND`, comparaciones) y `/ 0` → vacío; `COALESCE` lo vuelve 0; la calculada usa los valores **sin redondear** y solo su resultado se redondea a `scale` con `AwayFromZero` (F-E). Un periodo anterior al año 1 no existe (F-C). Semana ISO del lunes (el 2027-01-01 es `2026-S53`); `DATEFIRST` no la mueve. Fechas con hora: se agrupan por el valor guardado, como en la 007 (R-007 existente). Sumas de montos de varias monedas se suman tal cual (como en la 007): el modelador usa un campo convertido (R-039-1). | unit `Semana_iso_en_el_cambio_de_anio`, `F_C_el_periodo_anterior_al_anio_1_no_existe`; IT `Semana_no_depende_de_datefirst`, `Calculadas_en_filas_y_total`, `F_E_la_calculada_usa_los_valores_sin_redondear`; R-039-1, R-039-4 |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **I** — un grupo a dos saltos revela el nombre de un registro de un catálogo que el usuario no ve | Alto | Unión filtrada por `RecordFilter` en cada salto; «(sin acceso)» | IT `Dos_saltos_respetan_el_alcance_del_intermedio_y_del_final` |
| **I** — una medida calculada lee un campo oculto o un registro por su nombre | Alto | La expresión solo admite nombres de medidas; las medidas ya pasaron por `Available` | unit `Calculada_rechaza_campos_funciones_y_encadenadas` |
| **I** — la comparación trae un periodo calculado sin el alcance del usuario | Medio | Mismo `WHERE` (alcance, filtro fijo, filtros del usuario) en la segunda consulta | IT `Comparacion_con_el_alcance_del_usuario` |
| **T** — un nombre de color o de periodo llega a CSS o a SQL | Medio | Listas cerradas en el servidor (rechazo al guardar) y en la SPA (se ignora un nombre desconocido) | unit `Paleta_y_periodos_cerrados` |
| **D** — un reporte con muchas uniones o una expresión enorme | Bajo | Topes de §Límites; 30 s de comando | unit `Rutas_de_tres_saltos_y_expresiones_largas_se_rechazan` |
| **E** — no aplica: sin privilegios nuevos | — | — | — |
