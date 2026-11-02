# Plan 051 — Diseño técnico

## 1. Modelo de datos
Sin tablas ni columnas nuevas: se usa la tabla de unión de la 050 (`app.[Catalogo__Campo]`, llave `(OwnerId, ValueId)` e índice
`(ValueId, OwnerId)`).

**Lenguaje** (`Expr.cs`, `ExprAnalyzer`, `ExprEvaluator`):
- `X IN Campo` (después de `IN` viene un nombre, no `(`) se analiza como `Call("CONTAINS", [PathRef Campo, X])`; `CONTAINS(Campo, X)`
  es la misma llamada. Un solo nodo: el índice de usos (`Usages`), la seguridad derivada (`DerivedAccess.NamesHidden`, `Compute`),
  las condiciones de layout (`ReadsHidden`) y el orden de fórmulas (`FormulaGraph`) ya recorren los argumentos de una llamada.
- `ExprAnalyzer.CheckCall("CONTAINS")`: el primer argumento es una ruta (propio o hasta tres saltos por referencias o el padre)
  que **termina** en un campo de varios valores activo; el segundo es un valor compatible: con `multiuser`, `CURRENTUSER()`, un
  campo `user` (propio o por ruta) o un entero; con `multireference`, un campo `reference`/`parent` al mismo catálogo (propio o
  por ruta) o un entero. Otra cosa: «En «X IN Campo», X debe ser…». `MultiValued` sugiere `X IN Campo`.
- `IExprScope.Members(campo, ruta)` (nuevo, por omisión nulo): `DocScope` devuelve la lista del borrador (campo propio), la del
  documento en memoria (desde una partida, por el padre) o `RelatedLoader.Members` (por referencias: `Walk` hasta el dueño, que
  debe existir y, con `viewer`, verse; luego `SELECT ValueId FROM <tabla de unión> WHERE OwnerId = @id`, con caché por operación).
- `ExprEvaluator`: `CONTAINS` → `true` si la lista contiene el entero; lista vacía o ruta rota, valor nulo o no entero → `false`;
  lista detrás de un registro que quien mira no ve → nulo (revisión adversarial F-01). La impresión (`PrintScope.Members`) y el
  acumulado en memoria (carga las listas de su filtro) también responden (F-02, F-03).

**Traductor de reglas** (`RecordFilter.Translator`): `BoolCall("CONTAINS")` →
```sql
EXISTS (SELECT 1 FROM [app].[Departamento__Responsables] j0
         WHERE j0.[OwnerId] = (SELECT o1.[Id] FROM [app].[Departamento] o1 WHERE o1.[Id] = t.[Departamento] AND o1.[IsDeleted] = 0)
           AND j0.[ValueId] = @sec0)
```
Llave del dueño: `t.[Id]` si el campo es propio; si no, la cadena de subconsultas por Id de `Column` (hasta tres saltos) y una
última que exige el dueño vigente. Valor: `CURRENTUSER()` (parámetro; rechazado en modo acumulado), un entero (parámetro
`bigint`) o una columna (propia; por ruta solo en acumulados, como hoy). Identificadores solo de los metadatos (`JoinTable`, `Quote`).
Para resolver las rutas el traductor recibe **el modelo** en todos los modos: `RecordFilter.For`, `Build`, `Check` y
`HasGrants` lo piden (todos los llamadores lo tienen). Una ruta solo vale como lista de una pertenencia; comparar
`Departamento.Nombre` sigue rechazado en una regla de acceso (decisión 4). `HasGrants` deja de traducir (solo pregunta si algún
permiso aplicable restringe).

**Asignaciones** (`FieldAssignment`): `mode` opcional, `"add"` o `"remove"`, solo en campos de varios valores (y obligatorio en
ellos). Lo validan `PlanCompiler` (transiciones) y `AutomationRules.WritableFields` (`update`, `create`); `createLines`, `capture`,
`rollup` y `cost` lo rechazan. La ejecución (`RecordService.Advance`, `SystemUpdateAsync`, `create`) evalúa la expresión, convierte
a id, comprueba que exista (registro vigente del catálogo destino, o persona que no es equipo) y agrega o quita sobre la lista
cargada del borrador (`LoadDraftAsync`); tope de 100 (`FieldTypes.MaxMultiValues`). Error → `ValidationFailed` en el campo (400
y nada escrito: es la misma transacción).

**Paquete:** Compras 1.12.0 (`upgrades`): `Requisicion.Participantes` (`multiuser`, «Quienes intervinieron», lo asigna el sistema)
con `add CURRENTUSER()` en las transiciones `enviar`, `aprobar`, `devolver` y `rechazar`; y el permiso por registro del
solicitante pasa a «propias **o** de un departamento del que soy responsable» (`where: "CURRENTUSER() IN Departamento.Responsables"`,
solo lectura). `Departamento.Responsables` solo lo modifica «Gerente de compras» (el único rol de Compras que actualiza
departamentos). Ejemplos inventados.

## 2. Contrato de la API
Sin rutas nuevas. Cambian los metadatos que aceptan las rutas de modelo y la semántica de las de datos.

| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `POST /api/model/plans/*` (vista previa, aplicar), paquetes, Studio, asistente | las de hoy (modelo) | expresiones con `X IN Campo` / `CONTAINS(Campo, X)`; `set: [{ field, value, mode: "add" \| "remove" }]` | 200 con el plan; 400 con `errors[]` por operación: «'Campo' no tiene varios valores…», «En «X IN Campo», X debe ser…», «'Campo' tiene varios valores: indica si se agrega o se quita (mode)», «mode solo aplica a un campo de varios valores» |
| `GET /api/model/functions` (autocompletado) | las de hoy | — | incluye `CONTAINS` (2 argumentos) |
| `GET /api/data/{e}`, `/{id}`, `/lookup`, `/export`, `/api/search`, `/api/print/…`, OData, MCP, Acompañante, webhooks, bitácora | las de hoy | — | con un permiso `where` de pertenencia, solo los registros cuyo `EXISTS` se cumple para quien pide (404 en uno fuera de alcance, como hoy) |
| `PUT /api/data/{e}/{id}`, `/transition`, `DELETE` | las de hoy | — | 403/404 fuera del alcance de escritura; una transición con `add`/`remove` responde 200 con la lista nueva en `Campo`/`Campo__label`, o 400 `{ errors: { Campo: ["…"] } }` si el valor no existe o pasa el tope |

## 3. SPA
- `exprComplete.ts`: después de `IN ` y dentro de `CONTAINS(` sugiere también los campos de varios valores (y rutas que llevan a
  ellos); en otro lugar siguen fuera. `FUNCTION_HINTS.CONTAINS`.
- `conditionBuilder.ts` / `ConditionBuilder.tsx`: los campos de varios valores entran al selector de campo con el operador
  «contiene»; el valor es «quien usa el sistema» u otro campo de personas (`multiuser`), u otro campo que apunta al mismo
  catálogo (`multireference`). Texto: `CURRENTUSER() IN Departamento.Responsables`; se vuelve a leer en renglones.
- Asignaciones (`WorkflowDesigner` y `AssignList` de `ActionsEditor`): con un campo de varios valores aparece un `Select`
  «Agregar a la lista / Quitar de la lista» (con nombre accesible) y el `mode` viaja en el plan; los demás campos no lo muestran.
- Tipos (`types.ts`): `set?: { field; value; mode?: 'add' | 'remove' }[]`.

## 4. Pruebas
- **Unitarias** (`tests/Erp.Tests/MembershipTests.cs`, nuevo): parser (`IN Campo` = `CONTAINS`, `IN (…)` igual que antes),
  analizador (propio, tres saltos, cuatro saltos, no multi, X incompatible), evaluador (vacío, nulo, falso bajo `NOT`), traductor
  (SQL con `EXISTS`, `JoinTable`, parámetros, sin texto del usuario; rechazo de ruta a valor simple; `CURRENTUSER()` en acumulado),
  validación de `mode`.
- **Integración** (`tests/Erp.IntegrationTests/MembershipTests.cs`, nuevo): permiso por registro con pertenencia en lista,
  registro, buscador, búsqueda, reporte, exportación, OData, impresión y bitácora; escritura (PUT, transición, borrar); quitar a la
  persona quita el acceso; regla y condición de transición con «pertenece a»; `add`/`remove` desde transición y automatización;
  valor inexistente o equipo → 400 sin escribir; tope; **diez automatizaciones a la vez** agregan diez valores; transición con
  `add` seguida de una regla que falla → nada escrito; filtro de reporte; tarea programada con pertenencia.
- **E2E** (`web/e2e/pertenece-a.spec.ts`, nuevo): H1 CA5 (constructor «contiene» y autocompletado), H2 CA1 (el solicitante ve
  la requisición del departamento a su cargo en la lista), H3 CA4 (asignación «Agregar a la lista» en el diseñador del proceso).
  **Canario** (`web/e2e/canario.spec.ts`, modificado): H2 CA4.
- Con `--veces=3`: `pertenece-a.spec.ts`, `canario.spec.ts` y `compras.spec.ts` (instala Compras 1.12.0).

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones de plan, acciones de automatización, opciones de OData o caminos de módulo/paquete/asistente agrega o cambia? ¿Qué capacidad pide cada una? | Ninguna ruta. El lenguaje acepta `IN Campo` y `CONTAINS`; `set` acepta `mode`. Entra por el plan (modelo), paquetes, Studio y el asistente: todos pasan por `PlanCompiler`. El SQL de la pertenencia solo usa identificadores de los metadatos (`JoinTable` valida con la lista blanca) y el valor va como parámetro | unit › «La pertenencia se traduce a EXISTS con la tabla de unión y el valor como parámetro»; unit › «CONTAINS sobre un campo que no es de varios valores o con cuatro saltos: error del plan» |
| Caminos alternos | UI, API, importación, exportación, OData, webhooks, automatización, tarea, paquete, reversión, impresión, asistente, módulo | Lectura: todos los caminos ya piden `RecordFilter.For`/`ForRecordOrParent`; la pertenencia vive dentro del predicado, así que no hay camino que la salte. Escritura: PUT, transición, borrar, importación (actualizar) y Acompañante usan `EnsureAccessAsync`/`MatchesAsync` con el filtro de escritura. Asignar `add`/`remove`: transición, `update`, `create` (y el motor desde tareas) | IT › «Un permiso por pertenencia limita lista, registro, buscador, búsqueda, reporte, exportación, OData e impresión»; IT › «Fuera del alcance de escritura por pertenencia: PUT, transición y borrar 404»; canario › «H2 CA4: …» |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | El `EXISTS` es parte del `RecordFilter` del catálogo (con su empresa). En memoria (`/compute`, layout, avisos con alcance), cada salto pasa por `RelatedLoader.Row` con `viewer` y el dueño de la lista también; una fórmula o condición que nombra un campo de varios valores oculto por rol se oculta (`DerivedAccess` recorre los argumentos) | IT › «Una condición con pertenencia sobre un campo oculto se le oculta a quien no lo ve»; IT › «En vivo, la pertenencia a un registro fuera de alcance es falsa» |
| Delegación | ¿Alguien puede otorgar, aprobar o heredar algo que no tiene? | Sí, por diseño: quien edita la lista que una regla de acceso consulta da acceso a los registros que la regla abre (como hoy el campo dueño). Mitigación: la lista es un campo y se protege con seguridad por campo (Compras: solo «Gerente de compras» edita `Responsables`); un campo oculto o de solo lectura no se escribe por ningún camino (050). El plan no lo exige: queda como riesgo R-051-1 | IT › «Quien no puede editar Responsables no se agrega a la lista»; riesgo R-051-1 |
| Concurrencia | Invariante entre filas; 10 a la vez; «abrir → guardar» sin huella | `add`/`remove` del motor: `SystemUpdateAsync` bloquea la fila dueña antes de leer la lista (014 H6), así que diez automatizaciones que agregan a la misma lista dejan los diez valores. Desde una transición de una persona, la `rowversion` del registro: dos a la vez con la misma versión → una 409 | IT › «Diez automatizaciones a la vez agregan diez valores distintos a la misma lista»; IT › «Dos transiciones con la misma versión: una gana y la otra recibe 409» |
| Fallas parciales | ¿Qué pasa si cae entre dos pasos? | La asignación ocurre en el borrador y se escribe con la fila dueña en la misma transacción que la transición, la bitácora y las automatizaciones; un error posterior (regla, valor inexistente, tope) revierte todo | IT › «Una transición que agrega y luego falla una regla no deja la lista cambiada»; IT › «Agregar un valor que no existe o un equipo: 400 sin escribir» |
| Límites | Tamaño, paginación, `$expand`, memoria, tasa | Rutas de máximo tres saltos y expresiones de 1 000 caracteres (023); `MaxGrants` 20; el `EXISTS` usa la llave `(OwnerId, ValueId)` de la tabla de unión; `RelatedLoader.Members` con caché por operación (una consulta por dueño); tope de 100 valores al agregar | unit › «Cuatro saltos: error»; IT › «Agregar el valor 101: 400» |
| Semántica | Nulos y vacíos, monedas, fechas, redondeo | Dos valores: lista vacía, dueño vacío o borrado, valor nulo → falso (en SQL y en memoria), también bajo `NOT`; `add` de un nulo no cambia nada; `remove` de un valor ausente tampoco; un entero literal se compara como id | unit › «Pertenencia con lista vacía, valor nulo y NOT»; IT › «La regla en memoria y el filtro SQL coinciden en vacío y borrado» |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** suplantación | — | `CURRENTUSER()` es la identidad de la sesión (o la del motor, 0, en tareas, donde se rechaza) | unit › «CURRENTUSER() en un acumulado o tarea: error» |
| **T** inyección por nombre de tabla o valor | Alta | Tabla de unión y columnas solo de metadatos validados; el valor siempre parámetro (`bigint` para enteros) | unit › «La pertenencia se traduce a EXISTS con la tabla de unión y el valor como parámetro» |
| **R** cambio sin rastro | Media | `add`/`remove` cambian el borrador del registro: bitácora antes/después de la transición o de la automatización | IT › «Agregar desde una transición queda en la bitácora antes y después» |
| **I** fuga por regla de acceso mal traducida | Alta | Una sola traducción para lectura y escritura, en el `RecordFilter` que usan todos los caminos; canario | IT (arriba); canario › «H2 CA4: …» |
| **D** consultas costosas | Media | Índice de la tabla de unión; tope de saltos | Límites (arriba) |
| **E** ganar acceso editando la lista | Alta | Seguridad por campo sobre la lista; riesgo documentado | IT › «Quien no puede editar Responsables no se agrega a la lista»; R-051-1 |
