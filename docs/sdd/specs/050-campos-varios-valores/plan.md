# Plan 050 — Diseño técnico

## 1. Modelo de datos
**Tipos nuevos** (`FieldTypes`): `multireference` (con `referenceEntity`, un catálogo raíz) y `multiuser`. `FieldTypes.IsMulti`;
`IsVirtual` los incluye (no tienen columna en la tabla del catálogo), así **todo camino que no los conoce los ignora por
omisión** (orden, búsqueda, fórmulas, rutas, mapeos de automatizaciones, agrupaciones de reportes): denegar por defecto.
`EntityDef.MultiFields` (activos) y `EntityDef.ValueFields` (los guardados más los de varios valores: lo que la persona captura
y exporta).

**Tabla de unión** (DDL, `DdlGenerator.JoinTable`), por campo:
```sql
CREATE TABLE [app].[Proveedor__Categorias] (
    [OwnerId] bigint NOT NULL CONSTRAINT [FKO_Proveedor_Categorias] REFERENCES [app].[Proveedor] ([Id]),
    [ValueId] bigint NOT NULL CONSTRAINT [FK_Proveedor_Categorias] REFERENCES [app].[CategoriaProducto] ([Id]),
    CONSTRAINT [PKM_Proveedor_Categorias] PRIMARY KEY ([OwnerId], [ValueId])
);
CREATE INDEX [IXM_Proveedor_Categorias] ON [app].[Proveedor__Categorias] ([ValueId], [OwnerId]);
```
`multiuser`: `[ValueId] int` con llave a `[meta].[User]`. El nombre se arma con `Identifiers.JoinTable(catálogo, campo)`, que
valida cada parte con la lista blanca (II.1) y une con `__` (prohibido en nombres del modelo: no choca con un catálogo).
`createEntity` y `addField` la crean; archivar el campo la conserva; reactivarlo no crea nada.

**Validación del plan:** `referenceEntity` existente y activo, no una partida; el dueño no es una partida; sin `unique`,
`defaultValue`, `formula`, `lookupFilter`. `ExprAnalyzer` rechaza leer el campo («tiene varios valores; «pertenece a» llega en
la 051»), también como último salto de una ruta; `PrintRules` admite `{{ Campo }}` propio (se imprimen las etiquetas).
`InterfaceRules.Resolve` (reportes, exploración, tableros) da el mismo mensaje. `ArchiveEntity` ya rechaza archivar un catálogo
al que apunta un `referenceEntity`; `ValidateFinal` agrega el destino archivado.

**Paquete:** Compras 1.11.0 (`upgrades`): `CategoriaProducto` ↔ `Proveedor.Categorias` («Categorías que surte»,
`multireference`) y `Departamento.Responsables` (`multiuser`). Ejemplos inventados.

## 2. Contrato de la API
Sin rutas nuevas: cambia el cuerpo de las que ya existen.

| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `POST /api/data/{e}` · `PUT /api/data/{e}/{id}` · `POST …/compute` · transiciones con `values` | las de hoy (`Data`) | `"Categorias": [3, 7]` (números o textos numéricos; `null`/`[]` = vacío; repetidos cuentan una vez) | 200/201 con `"Categorias": [3, 7]`, `"Categorias__label": ["Limpieza", "Papelería"]` (orden por etiqueta); 400 `{ errors: { Categorias: ["…"] } }` si no es lista, hay más de 100, un id no existe o no se ve («El registro seleccionado no existe.»); 409 si la versión no coincide |
| `GET /api/data/{e}` · `GET /api/data/{e}/{id}` · webhooks · MCP · Acompañante | las de hoy | `filters=[{"field":"Categorias","op":"contains","value":3}]`; ops `contains`, `in` (lista ≤ 50), `blank`, `notBlank`; `multiuser` + `contains` admite `"$me"` | igual que arriba; un valor fuera del alcance de lectura de quien mira: etiqueta `"(sin acceso)"` (el id sí, como una referencia) |
| `GET /api/data/{e}/export` | `Data` (lectura) | columnas | celda = etiquetas unidas por `"; "` |
| `POST /api/data/{e}/import/*` | las de hoy | celda `"Limpieza; Papelería"` o `"#3; #7"` | error por fila: «No existe «X» en Categorías de producto.» |
| `GET /odata/{set}?$expand=Categorias` | OData (lectura) | `$expand` | `"Categorias": [ { …registro visible… } ]`; sin `$select`/`$filter`/`$orderby` sobre el campo (400 «no existe») |
| `GET /api/print/{e}/{id}` | lectura | plantilla con `{{ Categorias }}` | etiquetas unidas por «, »; «(sin acceso)» por valor que no ve |

## 3. SPA
- `MultiReferenceSelect` (nuevo, `components/`): lista de etiquetas (`<ul>` con «Quitar X», botones con `aria-label`) y un
  `ReferenceSelect` para agregar (excluye lo ya elegido; tras agregar, el foco sigue en el buscador). Con `multiuser` usa el
  buscador de personas. Solo lectura: etiquetas separadas por coma.
- `FieldControl`: casos `multireference`/`multiuser`; `draft.ts` (`buildPayload`, `draftFromRow`, `isEmptyValue`) maneja
  listas; `entity.tsx` (`formatCell`) une etiquetas en la lista.
- Modelador (`EntityEditor`): los dos tipos en «Agregar campo», con catálogo destino para `multireference`.
- `FilterBuilder`: operadores «contiene», «contiene alguno de», «vacío», «con valores» para el campo; el valor con el buscador.
- Accesibilidad: etiquetas en una lista con nombre («Categorías que surte, 2 elegidas»), anuncio en vivo al agregar o quitar.

## 4. Pruebas
- **Unitarias** (`tests/Erp.Tests/MultiValueTests.cs`, nuevo): DDL de la tabla de unión (nombres citados, `int` para personas,
  rechazo de identificadores inválidos), `Identifiers.JoinTable`, validación del plan (partida, destino partida, `unique`,
  `defaultValue`), `ExprAnalyzer` (campo y último salto), `FilterSql` (operadores, `$me`, SQL con `EXISTS` y parámetros),
  importación de celdas.
- **Integración** (`tests/Erp.IntegrationTests/MultiValueTests.cs`, nuevo): alta y edición con ids, visibilidad (006),
  conservar lo que ya no se ve, tope, obligatorio, bitácora antes/después, 10 ediciones a la vez con la misma versión (una gana,
  el resto 409, la tabla de unión = la ganadora), falla después de escribir la tabla de unión (alcance de escritura) sin
  rastro, filtros, exportación → importación, OData `$expand` con alcance, campo oculto por rol (lectura, filtro, escritura),
  impresión.
- **E2E** (`web/e2e/varios-valores.spec.ts`, nuevo): H1 CA4 (modelador), H2 CA1/CA4 (formulario, teclado, lista), H5 CA1
  (filtro de la lista). **Canario** (`web/e2e/canario.spec.ts`, modificado): H6 CA1.
- Con `--veces=3`: `varios-valores.spec.ts`, `canario.spec.ts` y los que cambien por el paquete Compras.

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones de plan, acciones de automatización, opciones de OData o caminos de módulo/paquete/asistente agrega o cambia? ¿Qué capacidad pide cada una? | Ninguna ruta nueva. Dos tipos de campo en `createEntity`/`addField` (capacidad del modelo, igual que hoy); el cuerpo de las rutas de datos acepta listas; `filters` acepta 4 operadores para el tipo; `$expand` acepta el campo. Identificadores de la tabla de unión solo de metadatos validados | `MultiValueTests` (unit) › «La tabla de unión cita cada parte y rechaza un identificador inválido»; `MultiValueTests` (IT) › «Un id que no es número, una lista de más de 100 o un objeto: 400 sin escribir» |
| Caminos alternos | UI, API, importación, exportación, OData, webhooks, automatización, tarea, paquete, reversión, impresión, asistente, módulo | Escritura: API, formulario, importación, Acompañante y MCP pasan por `ParseIntoAsync` (misma visibilidad y tope). Las automatizaciones no pueden asignarlo (mapeos rechazan campos virtuales). Lectura: `SelectClause` (lista, registro, OData, exportación, webhooks, MCP, Acompañante), `LoadDraftAsync` (bitácora, cálculo), impresión con su propio salto visible | IT › «La importación aplica la misma visibilidad que la API»; IT › «Exportar y volver a importar conserva los valores»; unit › «Una automatizacion ni una transicion lo asignan con una expresion»; canario › «H6 CA1: …» |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | El registro dueño ya pasó su filtro. Cada etiqueta pasa el `RecordFilter.ForRecordOrParent` del destino (empresa incluida) y `DisplayHidden`; fuera de alcance, «(sin acceso)». El campo oculto se quita con `FieldAccess.Strip`, no se filtra (400) y no se escribe. OData `$expand` filtra el destino con su `RecordFilter` | IT › «Una etiqueta fuera del alcance del lector dice (sin acceso)»; IT › «Un campo oculto por rol no se lee, no se filtra y no se escribe»; IT › «OData $expand solo trae los registros que el lector ve»; canario |
| Delegación | ¿Alguien puede otorgar, aprobar o heredar algo que no tiene? | Elegir un valor exige poder verlo (006, como una referencia): no se gana lectura de otro registro por enlazarlo. Sin delegación de privilegios (eso es la 051, «pertenece a» en la seguridad) | IT › «Elegir un registro que no ve: 400; conservar uno que ya tenía: sí» |
| Concurrencia | Invariante entre filas; 10 a la vez; «abrir → guardar» sin huella | La tabla de unión solo cambia en la transacción que actualiza la fila dueña (`UPDATE … WHERE RowVer = @rv`), así que su `rowversion` es la huella de los valores: 10 ediciones con la misma versión → 1 guarda, 9 reciben 409 y la tabla tiene exactamente la lista ganadora. Escrituras del motor (sin versión) bloquean la fila antes de leer el borrador (014 H6) | IT › «Diez ediciones a la vez con la misma versión: una gana y la tabla de unión tiene sus valores» |
| Fallas parciales | ¿Qué pasa si cae entre dos pasos? | Un solo `SqlTransaction`: fila dueña, tabla de unión, partidas, bitácora y automatizaciones; un 403 por alcance de escritura después de escribir la tabla de unión revierte todo | IT › «Si el registro queda fuera del alcance de escritura, la tabla de unión no cambia» |
| Límites | Tamaño, paginación, `$expand`, memoria, tasa | 100 valores por campo y registro; filtro `in` ≤ 50; la lista trae los valores con una subconsulta por fila de la página (≤ 200); `$expand` del campo con el tope de las partidas (`MaxExpandedLines`, 400 si se pasa) | IT › «Más de 100 valores: 400»; IT › «OData $expand respeta el tope de expansión» |
| Semántica | Nulos y vacíos, monedas, fechas, redondeo | `null` y `[]` son vacío (obligatorio exige ≥ 1; «vacío» en filtros = sin filas); repetidos cuentan una vez; el orden de salida es por etiqueta (estable, no el de captura); un registro destino borrado (lógico) conserva su etiqueta como una referencia | IT › «null, [] y repetidos»; unit › «Un campo obligatorio vacío es error» |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** suplantación | — | Sin identidad nueva: escribe quien está en la sesión, con sus permisos | (no aplica) |
| **T** inyección por nombre de tabla | Alta | `JoinTable` valida catálogo y campo con la lista blanca antes de citar; los ids siempre como parámetros | unit › «La tabla de unión cita cada parte y rechaza un identificador inválido» |
| **R** cambio sin rastro | Media | La lista completa va en la bitácora antes/después de la fila dueña | IT › «La bitácora guarda la lista antes y después» |
| **I** fuga por etiqueta | Alta | Etiqueta con el alcance del destino y su campo para mostrar visible; canario | IT y canario (arriba) |
| **D** registro con miles de valores | Media | Tope de 100 por campo; OData con el tope de expansión | IT › «Más de 100 valores: 400» |
| **E** leer por enlazar | Alta | Elegir exige ver (006) | IT › «Elegir un registro que no ve: 400; conservar uno que ya tenía: sí» |
