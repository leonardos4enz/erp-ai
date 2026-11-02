# Spec 052 — Filtros de búsqueda compuestos

**Estado:** Implementado · **Depende de:** 002, 005, 006, 007, 023, 026, 029, 050, 051 · **Origen:** roadmap, «Specs planeadas (después
de la 049): piezas para procesos complejos» y división del CLARIFY de la 051 (dueño, 2026-11-02). Tercera pieza genérica: «solo
proveedores activos que surten la categoría de la partida». Los ejemplos son inventados.

## Problema
Verificado en el código de `f5547d6`.

| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `LookupFilter` es `record(Field, Source)`: una sola condición, siempre «igual», siempre contra un campo del registro (`Models.cs`, `PlanCompiler.ValidateLookupFilter`) | No se puede decir «activo **y** de esta categoría», ni comparar con una constante, ni preguntar «pertenece a» |
| 2 | El filtro lo resuelve el cliente (`lookupFilterFor` en `draft.ts`) y lo manda en `GET /lookup?filterField=&filterValue=`; el servidor solo valida que el campo se vea | El servidor no sabe qué filtro declaró el modelo: aplica el que le pida la petición |
| 3 | Guardar (`ParseIntoAsync` → `CheckReferenceVisible`) solo revisa que el registro elegido exista y se vea | Por la API, la importación, el Acompañante o el MCP se guarda una partida de **otra** orden aunque el buscador no la ofrezca; una asignación del motor tampoco se revisa |
| 4 | El modelador muestra el filtro como texto («se cambia con un paquete o con el asistente; aquí no tiene control», `EntityEditor.tsx`) | Quien modela no puede declararlo sin escribir JSON |

## Principio
Una capacidad **genérica del motor** (constitución I.2): el filtro del buscador de una referencia es una lista de condiciones
declaradas en el campo, `campo del catálogo destino · operador · (constante | origen)`, unidas por Y. El origen es un valor del
borrador (campo propio, del documento padre o al final de una ruta de hasta tres saltos, como la 023/051). Los operadores son los
de los filtros de vistas (007) y de varios valores (050): igual, distinto, mayor/menor, «es uno de», «contiene», vacío y con valor.
El servidor lo resuelve con el borrador (no lo recibe de la petición) en SQL parametrizado, con identificadores de los metadatos
(II.1), y **lo exige al guardar** en todos los caminos de escritura: lo que el buscador no ofrecería, no se guarda. Lo ejercen
Compras 1.13.0 y Ventas con metadatos.

## Historias

**H1 — Declarar el filtro.** Como *administrador del modelo*, quiero declarar varias condiciones del buscador de una referencia
para que solo se ofrezcan los registros que tienen sentido.
- CA1: `lookupFilter: { conditions: [{ field, op, value }|{ field, op, source }] }` con hasta 10 condiciones unidas por Y. La
  forma de la 005 (`{ field, source }`) sigue valiendo y equivale a una condición `eq` con origen.
- CA2: constantes de texto, número, sí/no, opción, estado, fecha (`$today`), persona (`$me`) y referencia por **id** (elegido en el
  editor). El origen es un campo propio, `Padre.Campo` en una partida, o una ruta de hasta tres saltos por referencias o el padre
  (`Orden.Proveedor.Categorias`).
- CA3: «pertenece a» en los dos sentidos: un campo de varios valores del destino «contiene» el origen (`Categorias contiene
  Categoria`), y un campo simple del destino «es uno de» una lista del origen (`Categoria es uno de Orden.Proveedor.Categorias`).
- CA4: el plan rechaza con un mensaje para el modelador: campo inexistente, archivado o calculado sin columna; operador que no
  aplica al tipo; constante inválida; origen inexistente, de más de tres saltos o de tipo incompatible; constante y origen juntos;
  más de 10 condiciones.

**H2 — El buscador lo resuelve el servidor.** Como *persona que captura*, quiero que el buscador me ofrezca solo lo que cumple el
filtro, calculado con lo que llevo capturado, para elegir sin equivocarme.
- CA1: `POST /api/data/{catálogo}/lookup/{campo}` (o `Partidas.Campo`) recibe el borrador y la búsqueda, y responde las opciones que
  cumplen el filtro, a su alcance (006) y con el campo para mostrar que ve (029). El cliente **no** envía el filtro.
- CA2: con un origen vacío no se ofrece nada y el buscador dice qué falta («Elige primero Proveedor»).
- CA3: un filtro que compara un origen con un campo del destino que la persona no ve, o que lee un origen que no ve (en cualquier
  salto), no se usa con ella: 400 «El filtro del buscador usa un campo que tu rol no ve» (no es oráculo). Una ruta que pasa por un
  registro fuera de su alcance no ofrece nada.

**H3 — El filtro vale al guardar.** Como *administrador*, quiero que el filtro se cumpla por cualquier camino para que la regla no
dependa de la pantalla.
- CA1: crear, editar, ejecutar una transición (con campos capturados o asignaciones), importar, el Acompañante, el MCP y las
  automatizaciones (`update`, `create`, `createLines`) rechazan con 400 `{ errors: { Campo: ["…no cumple el filtro del buscador"] } }`
  (o `Partidas[i].Campo`) una referencia que no cumple, sin escribir nada.
- CA2: se revisa cuando la referencia es nueva o cambió, **o** cuando cambió un valor propio del que depende su filtro (el primer
  salto de un origen, también el del documento padre en una partida). Sin cambios, se conserva lo que ya tenía (como la 006).
- CA3: lo que captura una persona se revisa con su alcance (lo que su buscador no le ofrecería no se guarda; una referencia
  conservada que ya no ve se rechaza siempre igual, F-02); lo que asigna el motor (automatización), sin alcance de persona y solo
  en las referencias que él mismo asigna (F-06).
- CA4: la condición se lee dentro de la transacción del guardado: si otro tiene sin confirmar un cambio al elegido, el guardado
  espera y ve lo confirmado. No se bloquea el elegido (revisión adversarial F-01: interbloqueos); lo ya guardado no se vuelve a
  revisar si el elegido cambia después (decisión 2).

**H4 — Editor en el modelador.** Como *administrador del modelo*, quiero editar el filtro en renglones para no escribir JSON.
- CA1: en las opciones de un campo de referencia, «Filtro del buscador»: renglones «campo del catálogo · operador · valor» con
  «valor fijo» o «del registro» (ruta sugerida), agregar y quitar renglón; el filtro de la 005 se abre como un renglón.
- CA2: con nombres accesibles en cada control, teclado y los errores del plan junto al campo; los cambios viajan en `updateField`
  y la vista previa del plan los describe.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios: más de 6 historias (4: no) · más de ~2 500 líneas de producto (~1 600: no) · seguridad o
   concurrencia junto con UI nueva (el editor: sí) · más de una pantalla principal (no). → **No se divide** (un criterio).
2. **Cuándo se revisa al guardar** (dueño, 2026-11-02): si la referencia cambió **o** cambió su origen (el primer salto propio o
   del padre). Un cambio en un registro a mitad de la ruta (otro documento) no vuelve a revisar los que ya lo usaban.
3. **Origen** (dueño): rutas de hasta tres saltos, como la 023/051; cada campo de la ruta debe ser visible para quien busca.
4. **Constantes de referencia** (dueño): por id, elegido en el editor. En paquetes conviene filtrar por un campo sí/no u opción
   (portátil); el id queda para lo que configura el administrador en su base. El plan comprueba el tipo, no que el id exista.
5. **El motor también** (dueño): las asignaciones de transiciones y automatizaciones que dejan una referencia que no cumple
   rechazan la operación completa, como una regla que falla. El motor no tiene alcance de persona.
6. **Constantes sobre campos ocultos.** Una condición con constante la decide quien modela, no quien captura: vale aunque la
   persona no vea ese campo del destino (como un permiso por registro). Solo las condiciones con **origen** exigen ver ambos
   lados, porque ahí la persona controla el valor y podría deducir el oculto.
7. **`GET /lookup?filterField=&filterValue=`** se conserva para otros clientes (su validación de la 006 no cambia), pero la SPA ya
   no lo usa para campos con filtro; no sustituye la revisión al guardar.

## Fuera de alcance (anotado en el roadmap)
- Condiciones con O, agrupadas o con expresiones libres (hoy solo Y de renglones).
- Filtro en el selector de un campo de varios valores (`multireference`) y en campos de persona (`user`).
- Volver a revisar las referencias guardadas cuando cambia un registro a mitad de la ruta.
