# Spec 026 — Modelador por módulos y sin JSON

**Estado:** Implementado · **Depende de:** 004, 005, 006, 007, 008, 016, 019, 020, 022, 023, 024 · **Origen:** revisión del
modelador por el dueño (2026-10-24): separarlo por módulos, decir de dónde viene cada cosa y quitar el JSON manual,
dejándolo solo como opción avanzada oculta. Ventas pasa a la 027 y CFDI 4.0 a la 028.

## Problema
| # | Hallazgo del dueño (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | La lista de catálogos es plana | `ModelerPage` pinta una tabla sin módulo (`group`/`setApp`, 024) ni filtro, y no dice qué paquete instaló cada catálogo |
| 2 | No se distingue el origen | `Almacen` lo crea Compras y Contabilidad le agrega `CuentaInventario` y `CuentaEntradas`; Costeo le agrega una vista con `merge` (023). El editor no dice de qué paquete viene cada campo, regla o automatización, ni avisa que un cambio local puede chocar con la próxima actualización del módulo |
| 3 | Textos de ejemplo que parecen contenido real (bug) | Los `placeholder` de las áreas JSON muestran reglas y automatizaciones de ejemplo (`FechaValida`, `AlConfirmar`); en modo oscuro se leen como si fueran del catálogo |
| 4 | Ocho secciones en JSON manual | Reglas, Automatizaciones, Seguridad, Vistas, Reportes, Webhooks, Tareas programadas y Libro de partida doble solo se editan escribiendo JSON |

## Principio
No se agrega capacidad al motor: lo que el modelador escribe sigue siendo el mismo metadato, el mismo Plan de Cambios, con la
misma vista previa, huellas (021), aprobación y auditoría (constitución I.3). Los editores visuales y el JSON avanzado
escriben **el mismo borrador** (patrón `DesignOrJson` de la 019): no hay un segundo camino.

## Historias

**H1 — Catálogos por módulo.** Como *modelador*, quiero encontrar los catálogos por módulo y saber quién los instaló.
- CA1: la pestaña *Catálogos* agrupa los catálogos por módulo de navegación (el `group` de la 024, en el orden de los
  módulos; *General* al final) con un encabezado por grupo y su número de catálogos.
- CA2: se filtra por módulo (selector) y por texto (nombre o identificador); sin resultados hay un estado vacío con
  «Quitar filtros».
- CA3: cada fila dice qué lo instaló: «Compras 1.9.0» si un paquete del servidor lo creó, «Local» si se creó en este entorno,
  y qué otros paquetes lo extienden («También: Contabilidad, Costeo»).
- CA4: el filtro y la búsqueda viven en la URL (`?modulo=`, `?q=`; `?app=` ya lo usa la 024 para proponer el módulo de un catálogo nuevo): recargar los conserva, y al volver del editor se recuperan.

**H2 — Origen de cada elemento.** Como *modelador*, quiero ver de qué paquete viene cada parte de un catálogo.
- CA1: en el editor, cada campo, regla, automatización, vista, reporte, webhook y tarea programada muestra su origen
  (paquete y versión que lo dejó como está, o «Local»); el proceso, el formulario, la seguridad y el libro lo muestran como
  bloque; y las propiedades generales del catálogo, en su sección.
- CA2: un elemento de un paquete que ya se cambió en este entorno dice «Modificado localmente».
- CA3: al cambiar en el borrador un elemento que instaló un paquete, el editor avisa junto al elemento: lo instaló tal
  paquete; si una actualización de ese paquete lo vuelve a definir, la actualización reemplaza el cambio local (y su vista
  previa lo avisará).
- CA4: la vista previa de **actualizar un módulo** (Configuración › Módulos) lista los choques antes de confirmar: elementos
  modificados localmente que la actualización reescribe, y elementos locales que la actualización quitaría al reemplazar una
  lista completa (sin `merge`). Sin choques, lo dice.
- CA5: el origen se calcula en el servidor, solo para quien administra el modelo.

**H3 — Editor de expresiones.** Como *modelador*, quiero escribir condiciones y fórmulas sin memorizar nombres.
- CA1: un control de texto reutilizable para condiciones de reglas, `when`, fórmulas, valores por defecto, filtros, `where`
  y valores de acciones, con autocompletado de campos (etiqueta y tipo), rutas de hasta tres saltos por referencias (023),
  funciones con su firma y las opciones de un campo de lista después de `Campo = '`.
- CA2: se usa solo con teclado (patrón *combobox* de ARIA): flechas, Enter o Tab para elegir, Esc para cerrar; el lector de
  pantalla anuncia cuántas sugerencias hay.
- CA3: valida en vivo contra el servidor con el mismo analizador que el compilador (`ExprAnalyzer`) y en el ámbito correcto
  (el registro, la partida con `lines`, el registro creado en `capture`, el catálogo de periodos del libro); incluye los
  campos nuevos del borrador. El error se ve debajo del control y se anuncia.
- CA4: las fórmulas y valores por defecto de los campos (019) también usan este control.
- CA5 (pedido del dueño durante la spec, 2026-10-24): **constructor de condiciones**. Toda condición (asserts, `when`,
  `where` de tareas y de seguridad, filtros, condición de periodo abierto) se arma sin escribir: renglones «campo ·
  operador · valor» elegidos de listas (el campo por pasos, hasta tres saltos por referencias; los operadores según su tipo:
  es igual, distinto, mayor, menor, es uno de, está vacío…; el valor según el campo: una opción, sí/no, un número, un
  texto, hoy ± días, quien usa el sistema u otro campo), unidos por «todas (Y)» o «alguna (O)». Produce el mismo texto que
  se escribiría a mano y pasa por la misma validación. Una condición vacía abre en el constructor; una escrita se lee en
  renglones si tiene esa forma simple, y si no (fórmulas, paréntesis, Y con O) se queda en «Texto» y lo dice.

**H4 — Editores visuales de las ocho secciones.** Como *modelador*, quiero editar todo el comportamiento con formularios.
- CA1 Reglas: nombre, condición (`assert`), mensaje, campo, severidad (error o aviso) y cuándo aplica (`when`).
- CA2 Automatizaciones: «Cuando (al crear, al editar, al entrar a un estado, al ejecutar una transición) → si (condición)
  → hacer (acciones)», modo síncrono o asíncrono; cada acción por tipo con catálogo destino, campos y transiciones elegidos
  de listas: asignar (`update`), transición, crear (con `createLines`, `capture` y transición), acumular (`rollup` por
  referencia o por coincidencia con `match` y `createMissing`) y valuar (`cost` con llave, cantidad, método, costos y sus
  políticas `uncostedEntry`, `methodChange`, `totalCost`, `quantityField`), cada una con su `lines` y su `when`.
- CA3 Seguridad: por campo (roles que lo ven, roles que lo capturan, estados en que se captura), dueño, equipos dueños,
  solo el sistema escribe (`systemManaged`), y por registro: roles, alcance (propios, unidad, unidad y subunidades, todos),
  condición `where` y acceso (lectura o escritura). El «equipo» como alcance es `teamOwners` sobre el dueño (015).
- CA4 Vistas: nombre, roles, columnas (ordenadas), filtros (campo, operador, valor con `$me`/`$today`), orden, estado y
  vista predeterminada.
- CA5 Reportes: agrupaciones (hasta 2, con periodo en fechas), medidas, filtro, orden, límite, gráfica y roles.
- CA6 Webhooks: eventos (con estado o transición), condición, rol de servicio y campos que se envían.
- CA7 Tareas programadas: calendario (cada N minutos, cada hora, diaria, semanal con días, mensual con día), consulta
  `where`, rol de servicio, tamaño de lote y acciones (el mismo editor de acciones).
- CA8 Libro de partida doble: partidas, cargo y abono, fecha, estados contabilizados y catálogo de periodos con inicio, fin
  y condición de abierto.
- CA9: cada sección vacía muestra un estado vacío claro («Este catálogo no tiene reglas» · «Agregar regla»); ningún texto
  de ejemplo parece contenido del catálogo (hallazgo 3).
- CA10: cada sección tiene «Ver como JSON» (colapsado por defecto): editor visual y JSON escriben el mismo borrador y generan
  el mismo plan. Un JSON inválido no se pierde: el editor visual lo dice y ofrece corregirlo.
- CA11: los errores del compilador se ven junto a la sección (y al elemento, cuando el mensaje lo nombra) mientras se edita,
  con una vista previa en vivo; el editor no permite revisar un plan que el cliente ya sabe inválido sin decir por qué.
- CA12: lo que un editor visual no cubre se dice en la pantalla y aquí (ver «Fuera del editor visual»); el editor conserva
  las propiedades que no muestra.
- CA13 (constitución IV): iconos Lucide, contraste AA en claro y oscuro, todo con teclado, móvil a 390 px sin
  desbordamiento horizontal.

## Decisiones (CLARIFY)
Resueltas con el criterio del dueño expresado en el pedido («decide en CLARIFY») y la constitución:

1. **De dónde sale el origen: de los planes de los paquetes instalados, reproducidos en memoria.** El servidor compila con
   `PlanCompiler` (puro) la cadena de cada paquete del servidor instalado (base y actualizaciones hasta la versión
   instalada), en orden de dependencias, y anota qué paquete y versión dejó cada elemento como está. Se descartan: el
   historial `meta.ChangeSet` (mezcla instalaciones con cambios locales y no distingue elementos dentro de una lista) y un
   metadato de procedencia nuevo (necesitaría migrar lo instalado, viajaría en planes que cualquiera puede escribir y la
   IA podría falsearlo). Reproducir es determinista, no cambia el esquema y sirve para lo ya instalado.
2. **«Modificado localmente»** = la definición actual difiere de la que deja la cadena del paquete (se comparan en forma
   canónica, sin nulos ni listas vacías; en campos se ignora el orden de presentación).
3. **Al actualizar un módulo sobre algo modificado localmente gana la actualización** (comportamiento actual de
   `upgrades`; sin fusión de tres vías). Lo nuevo es que la vista previa de la actualización lista los choques y el
   administrador decide antes de confirmar. Lo local que una actualización no toca se conserva.
4. **Paquetes de Entornos (009):** su origen no se reproduce en esta spec; lo que llegó por ellos se ve como «Local»
   (explicado en la pantalla: «creado en este entorno, con el modelador, el asistente o un paquete de Entornos»).
5. **Pestañas del editor:** *Catálogo*, *Formulario*, *Proceso*, *Reglas*, *Automatizaciones*, *Seguridad*, *Vistas y
   reportes*, *Integraciones* (webhooks y tareas) y *Contabilidad* (libro). «Un registro por empresa» pasa a *Catálogo*.
6. **Errores del compilador junto al control:** vista previa en vivo del borrador (la misma `POST /api/model/plan/preview`,
   con espera de 0.8 s) y endpoint nuevo de solo lectura para validar una expresión con `ExprAnalyzer`.
7. **El editor siempre escribe la lista completa** (sin `merge`): `merge` es para paquetes (023).

## Fuera del editor visual (se edita en «Ver como JSON», y la pantalla lo dice)
- Reportes: agrupar por rutas de más de un salto (`A.B.C`); el selector ofrece campos propios y un salto.
- Vistas: el `label` de un filtro (solo presentación de la 007).
- Campos: `lookupFilter` (005) se muestra en el campo con el aviso de que se cambia por paquete o con el asistente (ya no tenía control antes de esta spec).
- Tableros y módulos: siguen en sus pantallas (007, 024).
- Constructor de condiciones: no anida grupos (Y dentro de O) ni arma fórmulas; esas condiciones se escriben en «Texto»
  (con autocompletado y validación). Las condiciones de las transiciones y de visibilidad del formulario siguen en los
  diseñadores de la 019 como texto.

## Fuera de alcance
Fusión de tres vías al actualizar; origen de paquetes de Entornos; deshacer y rehacer dentro de los editores; quitar un
elemento con `merge`.
