# Spec 051 — «Pertenece a» en expresiones y en la seguridad por registro

**Estado:** Implementado · **Depende de:** 002, 005, 006, 014, 016, 023, 026, 029, 050 · **Origen:** roadmap, «Specs planeadas (después
de la 049): piezas para procesos complejos» (revisión del dueño, 2026-11-02, de un proceso de compras real que **no** se
documenta). Segunda pieza genérica: «cada persona usa solo sus centros», «el responsable del centro ve sus requisiciones». Los
ejemplos son inventados.

## Problema
Verificado en el código de `1f6421e`.

| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `ExprAnalyzer.CheckPath` y `PathError` rechazan cualquier lectura de un campo de varios valores con `MultiValued` («llega «pertenece a», spec 051») | Una regla, condición de transición o automatización no puede preguntar «¿la persona es responsable del departamento?» |
| 2 | `ExprParser.ParseCmp` solo acepta `IN (` con lista literal; no hay función de pertenencia | No hay forma de escribir «está en la lista» |
| 3 | `RecordFilter.Translator.Column` rechaza rutas en una regla de acceso («solo usa campos del propio catálogo») y `Stored` rechaza los campos virtuales | Los permisos por registro solo comparan un campo con `CURRENTUSER()`: no hay «solo ve las requisiciones de los departamentos que tiene a su cargo» |
| 4 | `AutomationRules.WritableFields` y la validación de transiciones (`PlanCompiler`) rechazan asignar un campo de varios valores («no se asigna con una expresión») | Una transición no puede «agregarme a quienes intervinieron» ni una automatización «quitar al responsable» |

## Principio
Una capacidad **genérica del lenguaje de expresiones** (constitución I.2): la pertenencia de un valor a un campo de varios valores
(050), propio o al final de una ruta de hasta tres saltos (023). Una sola forma interna (`CONTAINS`) que el evaluador resuelve
en memoria (`DocScope`/`RelatedLoader`) y el traductor de reglas resuelve en SQL con `EXISTS` sobre la tabla de unión, con
identificadores de los metadatos y valores como parámetros (II.1). Toda regla que ya pasa por esos dos caminos la gana: reglas,
avisos, condiciones de transición y de automatización, `deletableWhen`, condiciones de layout, fórmulas sí/no, **permisos por
registro** (lectura y escritura, en todos los caminos que ya aplican `RecordFilter`), filtros de reportes y de tareas programadas.
Además, las asignaciones de transiciones y automatizaciones aprenden a **agregar o quitar** un valor. Lo ejerce Compras 1.12.0
con metadatos.

## Historias

**H1 — Escribir «pertenece a».** Como *administrador del modelo*, quiero preguntar si un valor está en un campo de varios valores
para escribir reglas y condiciones sobre listas.
- CA1: `X IN Campo`, `X IN Ref.Campo` (hasta tres saltos por referencias o el documento padre) y `CONTAINS(Campo, X)` son
  equivalentes; `X IN (a, b)` (con paréntesis) sigue siendo la lista de siempre.
- CA2: vale donde vale una condición: reglas (y su «cuándo»), avisos, `when` de transiciones, `when` de automatizaciones,
  `deletableWhen`, condiciones de layout y fórmulas sí/no.
- CA3: el plan rechaza con un mensaje para el modelador lo que no es pertenencia: `CONTAINS` sobre algo que no es un campo de
  varios valores, o más de tres saltos. Leer el campo como un valor simple sigue rechazado y el mensaje sugiere `X IN Campo`.
- CA4: semántica: lista vacía, ruta rota (referencia vacía o borrada) o `X` vacío → **falso**, como el `EXISTS` de la base; así
  una condición con `NOT` también decide. Lo que se calcula con el alcance de una persona (layout, avisos, `deletableWhen`) y
  pasa por un registro que no ve es **desconocido**, también bajo `NOT` (revisión adversarial F-01).
- CA5: el modelador lo ofrece: el autocompletado sugiere los campos de varios valores después de `IN` y dentro de `CONTAINS(`, y el
  constructor de condiciones tiene «contiene» (con «yo» para personas).

**H2 — Seguridad por registro con «pertenece a».** Como *administrador*, quiero dar acceso a un registro a quien está en una lista
para que cada persona vea solo lo de sus departamentos.
- CA1: un permiso por registro con `where: "CURRENTUSER() IN Departamento.Responsables"` limita la lectura en **todos los
  caminos** que aplican `RecordFilter`: lista, registro, buscador, búsqueda global, reportes, exportación, OData, MCP,
  Acompañante, impresión, bitácora y webhooks. Se traduce a `EXISTS` parametrizado sobre la tabla de unión.
- CA2: con acceso de escritura, también limita editar, ejecutar transiciones y eliminar. Quitar a la persona de la lista le quita
  el acceso desde la siguiente petición.
- CA3: los filtros de reportes y de tareas programadas (y el filtro de un acumulado) lo traducen igual; en tareas y acumulados
  `CURRENTUSER()` sigue rechazado (los ejecuta el motor).
- CA4: el canario de la 029 incluye un registro fuera del alcance por pertenencia: ninguna ruta lo devuelve al lector.

**H3 — Agregar o quitar un valor desde una transición o una automatización.** Como *administrador del modelo*, quiero que el
proceso mantenga la lista para que no dependa de que alguien la capture.
- CA1: una asignación (`set`) de una transición, de una acción `update` o de una acción `create` acepta `mode: "add"` o
  `"remove"` en un campo de varios valores, con una expresión que da **un** id (`CURRENTUSER()`, `Departamento`…). Un campo de
  varios valores sin `mode`, o `mode` en un campo simple, es error del plan.
- CA2: un valor vacío no cambia nada; uno que no existe (registro borrado, persona inexistente o un equipo) o que pasa el tope de
  100 rechaza la operación completa con un mensaje, sin escribir nada.
- CA3: el cambio va en la bitácora antes/después del registro; diez automatizaciones que agregan cada una un valor distinto al
  mismo registro a la vez dejan los diez (ninguno se pierde).
- CA4: el modelador, al elegir un campo de varios valores en una asignación, pide «Agregar a la lista» o «Quitar de la lista».

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios: más de 6 historias (las tres piezas del roadmap, ~7: sí) · más de ~2 500 líneas de producto
   (~2 800 con los filtros compuestos: sí) · seguridad o concurrencia junto con UI nueva (el editor de filtros compuestos: sí) ·
   más de una pantalla principal (no).
   → **Se divide** (dueño, 2026-11-02): la **051** es «pertenece a» (expresiones, reglas, transiciones y seguridad por registro
   con `EXISTS`) y asignar o quitar un valor desde una transición o una automatización. La **052** son los filtros de búsqueda
   compuestos (`lookupFilter` con varias condiciones, constantes y «pertenece a», aplicados en el servidor al guardar, en la
   importación y en el Acompañante, con su editor). La matriz y lo demás se recorren un número (053–057).
2. **Sintaxis** (dueño): ambas, `X IN Campo` (sin paréntesis) y `CONTAINS(Campo, X)`. El parser convierte la primera en la
   segunda: un solo nodo que recorren el analizador, el evaluador, el traductor, el índice de usos y la seguridad derivada.
3. **Rutas** (dueño): hasta tres saltos, como la 023 (`Requisicion.Departamento.Responsables`). En SQL, la llave del dueño de la
   lista se arma con las mismas subconsultas por Id que ya usan los acumulados; en memoria, con `RelatedLoader.Walk`.
4. **En una regla de acceso las rutas solo valen como lista de una pertenencia.** Comparar `Departamento.Nombre = 'X'` sigue
   rechazado en permisos por registro (se decide en otra spec si hace falta): la 051 no abre más de lo que pide.
5. **Dos valores, salvo lo que no se ve.** La pertenencia es un `EXISTS`: en SQL no hay «desconocido»; el evaluador en memoria
   da lo mismo (vacío o nulo → falso) para que lista, registro y reglas coincidan. En VERIFY (F-01) se matizó: con el alcance de
   una persona, una lista detrás de un registro que no ve es desconocida (si no, `NOT … IN …` le abría `deletableWhen`).
6. **Asignar** es agregar o quitar **un** valor por asignación (no reemplazar la lista): reemplazar con una expresión no tiene
   sentido mientras el lenguaje no tenga listas. Las asignaciones de partidas (`createLines`) y `capture` siguen sin admitirlo
   (los campos de varios valores no van en partidas, 050).
7. **Quién escribe.** La transición escribe con la autoridad del motor (como cualquier `set`), así que agregar un valor no exige
   verlo (como una automatización); sí exige que exista. Es el administrador quien escribe la expresión.

## Fuera de alcance (anotado en el roadmap)
- Filtros de búsqueda compuestos: **052**.
- Intersección de dos listas («alguna de mis categorías está en las del proveedor»), `COUNT(Campo)` de un campo de varios valores
  y reemplazar la lista entera desde una asignación.
- Rutas a valores simples en permisos por registro (`Departamento.Nombre = …`).
