# Spec 007 — Metadatos de interfaz: layouts, vistas guardadas, reportes y tableros

**Estado:** Implementado · **Depende de:** 002, 005, 006 · **Origen:** [roadmap](../../roadmap.md#007--metadatos-de-interfaz)

## Problema
1. El formulario se deriva del orden de los campos: una orden de compra con 25 campos es una sola rejilla larga,
   sin secciones ni pestañas, y el tipo de cambio aparece aunque la moneda sea pesos.
2. El listado solo muestra las columnas marcadas «en listado»: no se pueden elegir columnas, filtrar por campo
   ni guardar una consulta frecuente («Mis requisiciones», «Pendientes de recibir»).
3. No hay reportes: para saber cuánto se compró por proveedor o por mes hay que exportar a mano.
4. No hay tableros con indicadores.
5. Todo lo anterior debe respetar la seguridad de la 006 **en el servidor**: quien no ve un campo no puede
   agruparlo, sumarlo, filtrarlo, ordenarlo ni exportarlo; quien no ve un registro no lo cuenta en un total.
6. Hallazgo F-03 de la 006: el modelador ofrece «Revisar cambios» sin cambios cuando el orden de los campos
   guardado no es consecutivo (p. ej. *Orden de compra*).

## Historias

**H1 — Layouts del formulario.** Como *modelador*, quiero organizar el formulario en secciones y pestañas y
mostrar partes solo cuando aplican.
- CA1: Un layout declara secciones (título, descripción opcional, una o dos columnas) con sus campos en orden;
  las secciones pueden agruparse en pestañas.
- CA2: Una sección o un campo puede tener **visibilidad condicional** (`visibleWhen`, lenguaje de expresiones).
  Se evalúa en el servidor y se actualiza mientras se captura. Un campo obligatorio oculto por su condición no
  se exige.
- CA3: Los campos que el layout no menciona aparecen en una sección final «Otros datos»: nunca se pierde un campo.
- CA4: El layout no es seguridad: un campo oculto por la 006 no aparece aunque el layout lo mencione, y una
  condición que usa un campo que el usuario no ve no se evalúa con él (no revela su valor).
- CA5: El plan se valida en la vista previa: campos existentes y no repetidos, condiciones válidas, pestañas y
  secciones con nombre; las partidas no tienen layout propio.

**H2 — Vistas guardadas.** Como *usuario*, quiero elegir columnas, filtrar y ordenar el listado, y guardar esa
consulta para volver a ella.
- CA1: Desde el listado elijo columnas, agrego filtros por campo (igual, distinto, contiene, mayor, menor, vacío,
  es uno de…) y orden; el resultado se refleja en la URL (se puede compartir el enlace).
- CA2: Guardo la consulta como **vista personal** con un nombre; solo yo la veo y la puedo borrar.
- CA3: El modelador declara **vistas compartidas** por rol en el catálogo (p. ej. «Mis requisiciones» con
  `Solicitante = $me`); una vista puede ser la predeterminada al abrir el listado.
- CA4: El servidor rechaza filtrar u ordenar por un campo oculto para el usuario (400) y no ofrece una vista
  compartida que lo use. Las columnas ocultas simplemente no se muestran.

**H3 — Reportes.** Como *usuario*, quiero reportes con agrupaciones y totales que pueda exportar.
- CA1: Un reporte agrupa (hasta dos niveles) por un campo del catálogo, de un catálogo relacionado
  (`Proveedor.Categoria`) o por periodo de una fecha (día, mes, año), con medidas conteo, suma, promedio,
  mínimo y máximo, total general, filtro fijo, orden y límite.
- CA2: Se puede declarar sobre un catálogo de partidas (p. ej. productos más comprados) y hereda el alcance de
  su documento.
- CA3: Solo cuenta los registros que el usuario puede ver (006); si el reporte usa un campo que el usuario no ve,
  no se le ofrece (404). Puede limitarse a ciertos roles.
- CA4: Se muestra como tabla y, si se declara, como gráfica (barras, líneas o pastel) con alternativa textual.
- CA5: Se exporta a **CSV** y **Excel (.xlsx)** con los mismos datos que se ven; el CSV no permite inyectar
  fórmulas en la hoja de cálculo.
- CA6: El usuario puede agregar filtros al reporte (mismos operadores que las vistas).

**H4 — Tableros.** Como *usuario*, quiero tableros con indicadores y gráficas.
- CA1: Un tablero se declara como metadato (nombre, icono, roles) con tarjetas: **indicador** (un número),
  **gráfica** o **tabla**, cada una basada en un reporte.
- CA2: Cada tarjeta respeta la seguridad del reporte: si el usuario no puede ver el reporte, la tarjeta no
  aparece; si no ve ninguna, el tablero no se le ofrece.
- CA3: Los tableros y reportes disponibles aparecen en el menú (sección «Análisis»).

**H5 — Modelador.** Como *modelador*, quiero declarar layout, vistas y reportes del catálogo como JSON validado
en la vista previa (igual que reglas, proceso, automatizaciones y seguridad), y los tableros desde su pantalla.
- CA1: El modelador ya no ofrece «Revisar cambios» si no hay cambios, aunque el orden guardado no sea
  consecutivo (F-03); al reordenar, solo se envían los campos que cambiaron de lugar.

**H6 — Compras 1.4.0 e Inventario 1.2.0.** Como *usuario de Compras e Inventario*, quiero que los módulos usen
estas capacidades, como actualizaciones versionadas sin perder datos.
- CA1: La orden de compra tiene layout con pestañas y la requisición con secciones; el tipo de cambio solo
  aparece si la moneda no es MXN; en la orden, los motivos de rechazo y cancelación solo cuando aplican.
- CA2: Vistas compartidas: «Mis requisiciones», «Por aprobar», «Pendientes de recibir», «Por autorizar».
- CA3: Reportes: compras por proveedor, compras por mes, requisiciones por estado, productos más comprados;
  tablero «Compras». Inventario: existencias por almacén y tablero «Inventario».
- CA4: El almacenista no ve reportes ni tarjetas con importes (la 006 los oculta).

## Decisiones (CLARIFY)
Sesión sin interlocutor disponible: se tomaron estas decisiones y se registran para revisión.
- **Vistas compartidas = metadato; vistas personales = preferencia del usuario.** Las compartidas pasan por el
  plan de cambios (auditables, empaquetables en módulos). Las personales las guarda cada usuario por API, sin
  permiso de modelador, y quedan en la auditoría.
- **La visibilidad condicional se evalúa en el servidor** (el mismo intérprete de expresiones): `/compute` y el
  registro devuelven qué secciones y campos están ocultos. Así no hay un segundo evaluador en el navegador que
  pueda discrepar del que valida al guardar.
- **Un campo oculto por condición conserva su valor** (no se borra): la condición es de presentación. Para
  ocultar datos por rol se usa la seguridad de la 006.
- **Un reporte que usa un campo oculto para el usuario no se le ofrece** (en lugar de mostrarlo incompleto): un
  total sin una columna engaña, y quitar la medida en silencio cambiaría el significado del reporte.
- **Relaciones a un nivel** (`Referencia.Campo`, `Documento.Campo`): suficiente para «por proveedor», «por
  categoría del producto» o «por estado del documento», y acota el costo de las consultas. El registro
  relacionado se une con su propio filtro de lectura: si el usuario no lo ve, cuenta como «(sin acceso)».
- **Filtros fijos de reportes**: el mismo subconjunto traducible a SQL de las reglas `where` de la 006.
- **Valores especiales en filtros**: `$me` (usuario actual) y `$today` (fecha de hoy).
- **Gráficas con SVG propio**, sin bibliotecas nuevas; cada una con descripción para lectores de pantalla y la
  tabla de datos debajo.
- **Exportación**: solo reportes (máx. 1000 filas, igual que en pantalla). El Excel se genera sin fórmulas (celdas
  de texto y número); en CSV se antepone `'` a los textos que empiezan con `= + - @`.
- **Fuera de alcance**: exportar listados, tableros por usuario, layouts de partidas, editor visual (los
  metadatos se editan como JSON validado).
