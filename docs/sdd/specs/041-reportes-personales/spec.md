# Spec 041 — Reportes personales y exploración al vuelo

**Estado:** Implementado · **Depende de:** 006 (`RecordFilter`, `FieldAccess`), 007 (reportes, vistas personales), 010 (empresa),
039 (gráficas, calculadas, periodos, dos saltos), 040 (`InterfaceRules.MaxJoins`) · **Origen:** pedido del dueño
(2026-10-31, 039 CLARIFY 1: «reportes personales, explorar al vuelo, tableros personales»); roadmap punto 12.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | Un reporte solo existe como metadato del modelo (`setReports` en `ReportDef`); `ReportsEditor` vive en el modelador y lo aplica un plan de cambios (capacidad `modelador`). | Quien no es modelador no puede armar un reporte: para «monto por proveedor de este trimestre» pide uno al modelador. |
| 2 | `ReportPage` solo corre el reporte tal como se declaró (`GET /api/reports/{entity}/{name}`): filtros sí, agrupación, medida y gráfica no. | No se puede preguntar «¿y por mes?» o «¿y como pastel?» sin cambiar el modelo para todos. |
| 3 | `ReportService.RunAsync` recibe un `ReportDef` del modelo; no hay una entrada que valide una definición ajena al modelo contra **quien** la corre. | Una definición armada por una persona tendría que pasar por la misma validación (rutas, saltos, `MaxJoins`, gráfica, calculadas) **y** por la visibilidad de esa persona. |
| 4 | Lo personal de cada usuario hoy son vistas del listado (`meta.SavedView`, sin versión: el último que guarda gana). | No hay dónde guardar un reporte propio ni protección contra dos pestañas que lo editan a la vez. |

## Principio
Capacidad **genérica del motor**: un reporte personal es una definición con la misma forma que `ReportDef` (agrupaciones,
medidas, calculadas, gráfica, orden, límite) más filtros estructurados (`ViewFilter`, como las vistas), sin roles ni
filtro de expresión. Se guarda como dato personal (`meta.PersonalReport`), no como modelo. **Siempre corre con quien lo
pide**: `ReportService` con su `RecordFilter` (alcance y empresa) y su `FieldAccess`, revalidado en cada corrida (lo que
dejó de ver deja de correr). Explorar al vuelo es correr una definición sin guardarla por la misma entrada. Pantalla,
exploración y exportación pasan por el mismo `ReportService`; ningún módulo agrega código.

## Historias

**H1 — Explorar al vuelo.** Como *cualquier usuario que consulta un reporte*, quiero cambiar la agrupación, las medidas o
la gráfica y ver el resultado sin guardar nada, para responder otra pregunta sin pedírsela al modelador.
- CA1: desde un reporte del modelo, «Explorar» abre el explorador con su definición (agrupaciones, medidas, gráfica,
  orden y límite); cambiar cualquiera vuelve a calcular el resultado sin guardarlo ni cambiar el reporte del modelo.
- CA2: si el reporte del modelo tiene un filtro fijo, el explorador lo dice (no lo copia: los filtros del explorador son
  los de la pantalla).
- CA3: la definición que no tiene sentido (pastel con dos medidas, calculada con un nombre que no existe, más uniones de
  las permitidas) responde con el mensaje de qué cambiar, en la vista, sin perder lo que se estaba armando.

**H2 — Armar un reporte propio.** Como *usuario sin permiso de modelador*, quiero armar un reporte desde cero con los
catálogos y campos que puedo ver, para tener mis propias cifras.
- CA1: «Nuevo reporte» en Reportes ofrece los catálogos que el usuario puede leer (y sus partidas) y, de cada uno, solo
  los campos que ve, propios y a uno o dos saltos (por referencia o por el documento), para agrupar y medir.
- CA2: ofrece lo mismo que el editor del modelador: hasta 2 agrupaciones (fechas por día, semana, mes, trimestre o año),
  hasta 6 medidas (conteo, suma, promedio, mínimo, máximo y calculada), siete tipos de gráfica e indicador, apilar,
  valores y leyenda, orden y límite.
- CA3: los filtros se arman con el mismo diálogo de filtros del listado (solo campos que ve).

**H3 — Mis reportes.** Como *usuario*, quiero guardar, abrir, cambiar y borrar mis reportes, para volver a ellos.
- CA1: «Guardar» pide un nombre; el reporte aparece en «Mis reportes» en Reportes, solo para su dueño; otro usuario que
  pide su id recibe lo mismo que si no existiera (404).
- CA2: cambiar y borrar llevan la versión que se abrió: si otra pestaña lo cambió antes, responde 409 y no se pierde lo
  de la otra («abrir → guardar» con versión).
- CA3: «Guardar como» crea otro; el nombre es único por persona y hay un máximo de 50 reportes personales por persona,
  también con muchas peticiones simultáneas.
- CA4: cada alta, cambio o borrado queda en la bitácora (quién, cuándo, antes y después).

**H4 — Con la seguridad de quien lo ve.** Como *responsable de seguridad*, quiero que un reporte personal o explorado
nunca muestre más de lo que su usuario ve en el listado, hoy, aunque lo haya guardado cuando veía más.
- CA1: corre con `RecordFilter` (alcance y empresa activa) y `FieldAccess` de quien lo pide; cada salto une el catálogo
  destino con su filtro y lo que no se ve sale «(sin acceso)», como en la 039.
- CA2: un campo, catálogo o salto que el usuario no ve responde igual que uno que no existe (no es un oráculo de nombres).
- CA3: si el usuario dejó de ver un campo que su reporte guardado usa, el reporte deja de correr (se dice que usa algo
  que ya no puede ver) y en «Mis reportes» aparece como no disponible; editarlo sigue siendo posible.
- CA4: respeta los topes de la 039/040: 2 agrupaciones, 6 medidas, 2 saltos, 16 uniones, 1 000 filas, 30 s de consulta y
  el límite por persona de lo que agrega (`aggregate`).

**H5 — Exportar.** Como *usuario*, quiero exportar a CSV o Excel lo que estoy viendo en el explorador, para trabajarlo
fuera.
- CA1: la exportación del explorador da exactamente las mismas filas y totales que la pantalla, con la misma seguridad.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Lo pedido en el roadmap (reportes personales, explorar al vuelo y tableros personales): más de 6
   historias (≈7: no) · más de ~2 500 líneas (≈2 500: al límite) · seguridad (rutas que escriben, datos por persona) junto
   con UI nueva (**sí**) · más de una pantalla principal (explorador de reportes y tablero personal con su editor de
   tarjetas: **sí**). → **Se divide** (dos criterios): **041** reportes personales y explorar al vuelo (esta);
   **042 — Tableros personales y compartir** (tableros propios junto a los del modelo, con tarjetas de reportes del
   modelo o personales, y compartir reportes y tableros personales por rol). Ventas pasa a la **043** y MCP 2 a la **044**.
2. **Sin compartir en la 041.** Un reporte personal lo ve solo su dueño. Compartir va con los tableros (042), donde se
   decide quién comparte con quién (no con roles que no tiene: 021-1). La regla que ya rige aquí y que la 042 hereda: un
   reporte corre **siempre** con los permisos de quien lo ve, nunca con los de quien lo armó (el roadmap decía «del
   dueño»; con un solo lector, dueño y lector son el mismo, y al compartir manda el lector).
3. **Sin filtro de expresión.** Los reportes del modelo tienen `filter` (expresión que `RecordFilter.Translate` vuelve
   SQL); los personales usan filtros estructurados (`ViewFilter` + `FilterSql.Build`), los mismos del listado. Traducir
   expresiones de cualquier usuario a SQL agranda la superficie sin necesidad. Por eso explorar un reporte del modelo con
   filtro fijo no lo copia y lo dice (H1 CA2).
4. **Explorar es una lectura por POST** (`POST /api/reports/explore`, efecto «ninguno»): la definición no cabe bien en una
   URL. Mismo límite `aggregate` que correr un reporte. La SPA espera 400 ms tras el último cambio antes de pedir.
5. **Revalidar al correr, no al guardar.** Se valida al guardar (para avisar pronto) y **otra vez** en cada corrida con el
   usuario y el modelo de ese momento: un campo que se ocultó, un catálogo que se archivó o un rol que se quitó cuentan.
6. **Lo que se ofrece lo decide el servidor** (`GET /api/reports/sources/{entity}`): solo campos que ve y caminos por
   catálogos que puede leer, con la misma regla de `ReportService.Available` (el campo para mostrar de una referencia a un
   catálogo que no lee sí, como en el buscador).
7. **Límite de 50 reportes personales por persona** (como las vistas) y nombre único por persona, con candado de rango
   para que diez altas simultáneas no pasen el tope.
8. **Sin colores por serie en el explorador.** La paleta automática de la 039 (cada serie su lugar fijo) basta para
   explorar; los colores con nombre siguen siendo del modelo. Se guardan si vienen de un reporte del modelo explorado.

## Fuera de alcance (anotado en el roadmap)
- **042:** tableros personales, tarjetas de reportes personales y compartir reportes y tableros por rol.
- Reportes personales por MCP, OData o Studio (no los exponen; si se agregan, pasan por la misma validación).
- Filtro de expresión en reportes personales; elegir colores por serie en el explorador.
- Programar el envío de un reporte por correo.
