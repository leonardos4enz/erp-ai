# Plan 052 — Diseño técnico

## 1. Modelo de datos
Sin tablas ni columnas nuevas: el filtro sigue en `meta.Field.LookupFilterJson`. La columna pasa de `nvarchar(400)` a
`nvarchar(4000)` (10 condiciones con rutas no caben en 400; `Bootstrap` la amplía si es más chica).

**Metadatos** (`Models.cs`):
```jsonc
"lookupFilter": {
  "field": "Pedido", "source": "Factura.Pedido",          // forma de la 005: equivale a { field, op: "eq", source }
  "conditions": [                                            // spec 052: hasta 10, unidas por Y
    { "field": "Activo", "op": "eq", "value": true },
    { "field": "Categoria", "op": "in", "source": "Orden.Proveedor.Categorias" },
    { "field": "Etiquetas", "op": "contains", "value": 7 }   // referencia por id (decisión 4)
  ]
}
```
`LookupFilter` deja de ser posicional (`Field`, `Source` y `Conditions` opcionales); `LookupCondition(Field, Op, Source?, Value?)`.
`LookupFilters.Of(lf)` da la lista efectiva (la forma corta primero). Un `updateField` con `lookupFilter` sin `field` ni
`conditions` lo quita (como hoy con `field` vacío).

**Operadores** (los de `FilterSql`, 007 y 050), por tipo del campo destino y con constante u origen:

| Destino | Operador | Constante | Origen |
|---|---|---|---|
| simple (texto, número, fecha, sí/no, opción, estado, referencia, persona) | `eq`, `ne`, `gt`…`lte` (los que admite su tipo) | la de `FilterSql` (`$today`, `$me`, id) | campo simple del mismo tipo (en referencias, al mismo catálogo) |
| texto | `contains` | texto | texto |
| texto, número, referencia | `in` | lista de 1 a 50 | campo de varios valores al mismo catálogo («es uno de») |
| varios valores | `contains` | un id | referencia o persona al mismo catálogo («contiene») |
| varios valores | `in` | lista de ids | campo de varios valores al mismo catálogo («contiene alguno de») |
| cualquiera | `blank`, `notBlank` | — | — |

**Origen:** ruta de 1 a 3 segmentos desde el registro que se captura. El primero es un campo propio o, en una partida, el campo
padre (que lleva al documento **en memoria**, el borrador); los intermedios son referencias o el padre; el último, un campo con
columna o de varios valores. La valida `PlanCompiler` (`LookupFilters.Validate`) con los mismos mensajes para el modelador.

**Resolución** (`LookupFilters.Build`): por cada condición, el valor del origen sale del borrador (primer salto) y, después, de
`RelatedLoader.Row`/`Members` (con `viewer` cuando captura una persona). Origen vacío, roto o fuera de alcance → **el filtro no
ofrece nada** (no se ejecuta SQL). El SQL de cada condición lo arma `FilterSql.Condition` (extraído de `FilterSql.Build`): columna
`t.[Campo]` de los metadatos o `EXISTS` sobre `JoinTable`; los valores siempre como parámetros (`@lf0`, `@lf1`…).

**Guardar** (`RecordService.Lookup.cs`, `CheckLookupFilters`): lo llaman `Validate` (crear, editar, importar, transición de una
persona o del motor, `create` de una automatización) y `SystemUpdateAsync` (`update` de una automatización, solo el registro que
cambió). `DocDraft.Original`/`OriginalLines` (copias de `LoadDraftAsync`) dicen qué cambió: se revisa una referencia no vacía si
es nueva, si cambió, o si cambió el primer salto propio de un origen (también del encabezado, en una partida). La revisión:
1. Con persona (profundidad 0): si una condición con origen nombra un campo que no ve (destino o cualquier salto) → error
   «…usa un campo que tu rol no ve». Los saltos se resuelven con su alcance.
2. Con persona, si ya no ve el elegido (referencia conservada), se rechaza sin evaluar sus datos (F-02).
3. `SELECT 1 … WHERE Id = @id AND IsDeleted = 0 AND <condiciones>` en la transacción del guardado (sin `UPDLOCK`: F-01). Falla →
   `ValidationFailed` en `Campo` o `Partidas[i].Campo`, después de los mensajes de las reglas del campo: «{Etiqueta}: el
   registro elegido no cumple el filtro del buscador.» El `update` de una automatización solo revisa lo que asigna (F-06).

## 2. Contrato de la API
| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `POST /api/data/{e}/lookup/{campo}` (nueva; `campo` = `Campo` o `Partidas.Campo`) | `Records`, solo lectura (`ReadsOnly`) | `{ search?: string, id?: number, values?: object, line?: number }`: el borrador como en `/compute`; `line` = índice de la partida | 200 `[{ id, label }]` (máx. 20, a su alcance, que cumplen el filtro; `[]` si falta un origen). 400 «El campo no es una referencia de {e}» / «La partida no existe» / «El filtro del buscador usa un campo que tu rol no ve». 403 sin permiso de crear (sin `id`) o editar (con `id`) `{e}`, o sin ver el campo. 404 el registro `id` no existe o está fuera de su alcance de escritura |
| `GET /api/data/{e}/lookup?filterField=&filterValue=` | igual que hoy | igual | igual (decisión 7) |
| `POST/PUT /api/data/{e}`, `/transition`, `/import`, Acompañante, MCP, automatizaciones | las de hoy | — | 400 `{ errors: { "Campo" \| "Partidas[i].Campo": ["{Etiqueta}: el registro elegido no cumple el filtro del buscador."] } }` sin escribir |
| `POST /api/model/plans/*`, paquetes, Studio, asistente | las de hoy | `lookupFilter.conditions` | 400 `errors[]` con «{E}.{C} (filtro del buscador): …» |

## 3. SPA
- `types.ts`: `LookupFilter { field?, source?, conditions?: LookupCondition[] }`; `dataApi.lookupFor(entity, field, body, signal)`.
- `draft.ts`: `lookupFilterFor` devuelve `{ waiting?: etiqueta del primer origen vacío, key: firma de los valores de origen }`;
  `ReferenceSelect` recibe `filter: { waiting?, key, request }` y vuelve a pedir cuando cambia `key`. `EntityFormPage` y
  `LinesEditor` arman `request` con `buildPayload` del borrador (y el índice de la partida).
- Modelador (`LookupFilterEditor.tsx`, nuevo, en las opciones de un campo de referencia): renglones «campo · operador · valor»;
  el valor es «fijo» (control según el tipo: texto, número, fecha, sí/no, opción, «yo», un registro con `ReferenceSelect`, o una
  lista para «es uno de») o «del registro» (texto con sugerencias de rutas compatibles, `datalist`). Botones «Agregar condición»
  y «Quitar condición N» con nombre accesible; `fieldset`/`legend`. `buildOperations` manda `updateField.lookupFilter` (o
  `{ conditions: [] }` para quitarlo) y `fieldChanged` lo cuenta. Lógica pura en `lib/lookupFilter.ts` (Vitest).

## 4. Pruebas
- **Unitarias** (`tests/Erp.Tests/LookupFilterTests.cs`, nuevo): validación del plan (forma corta, operadores por tipo, constantes,
  rutas de 1 a 3 saltos y 4 → error, tipos incompatibles, `in` con lista, constante y origen juntos, más de 10); SQL (solo
  identificadores de los metadatos, valores como parámetros, `EXISTS` con la tabla de unión; origen vacío → no ofrece nada); campos
  ocultos (`HiddenFor`); ida y vuelta del JSON y `ModelDiff`.
- **Integración** (`tests/Erp.IntegrationTests/LookupFilterTests.cs`, nuevo, SQL real): el buscador con borrador (constantes,
  ruta de tres saltos, «contiene», «es uno de», origen vacío, partidas con el padre en memoria, alcance, campo oculto → 400, el
  cliente no puede mandar otro filtro); guardar por POST, PUT, transición (campo capturado y asignación), importación, `create` y
  `update` de una automatización → 400 sin escribir; conservar lo que tenía; cambiar el origen vuelve a revisar; **concurrencia**:
  otra conexión desactiva el elegido mientras se guarda (`HoldAsync`) → espera y 400; diez altas a la vez con el mismo elegido
  mientras se desactiva: ninguna guardada después del cambio; Compras 1.13.0 y Ventas (la forma corta ya se exige).
- **E2E** (`web/e2e/filtros-busqueda.spec.ts`, nuevo): H1 CA1 y H4 CA1–CA2 (el editor arma dos condiciones y la vista previa
  las describe), H2 CA1–CA2 (el buscador ofrece solo los que cumplen y dice qué falta), H3 CA1 (la API rechaza lo que el buscador
  no ofrece; el formulario muestra el error junto al campo).
- Vitest (`web/src/lib/__tests__/lookupFilter.test.ts`, nuevo): renglones ↔ JSON, operadores por tipo, rutas sugeridas, firma.
- Con `--veces=3`: `filtros-busqueda.spec.ts`, `compras.spec.ts` (instala 1.13.0) y los que cambien por la forma corta exigida.

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas, operaciones de plan, acciones, OData, módulo/paquete/asistente | Ruta nueva `POST /api/data/{e}/lookup/{campo}` (`Records`, solo lectura, permiso de crear o editar `{e}` y ver el campo; con `id`, alcance de escritura). `updateField`/`addField`/`createEntity` aceptan `conditions`; las valida `PlanCompiler` para todos (UI, paquete, Studio, asistente). El SQL solo usa nombres de los metadatos (`Quote`, `AppTable`, `JoinTable`) y los valores van como parámetros; el filtro no viaja en la petición | unit › `El_SQL_usa_solo_nombres_de_los_metadatos_y_valores_como_parametros`; IT › `Sin_permiso_de_capturar_el_catalogo_el_buscador_responde_403` |
| Caminos alternos | UI, API, importación, exportación, OData, webhooks, automatización, tarea, paquete, reversión, impresión, asistente, módulo | Escribir una referencia: POST/PUT (formulario, Acompañante y MCP usan esas rutas), importación (`CreateInTransactionAsync`), transición (capturado y `set`), automatización `create`/`createLines` (`InsertDraftAsync` → `Validate`) y `update` (`SystemUpdateAsync`). Todos llaman `CheckLookupFilters`. Leer opciones: la ruta nueva y el `GET` de siempre, ambos con `RecordFilter` del destino | IT › `Guardar_por_POST_PUT_e_importacion_rechaza_lo_que_no_cumple`; IT › `Transicion_y_automatizaciones_rechazan_una_referencia_que_no_cumple` |
| Lectura de datos | ¿`RecordFilter`, `FieldAccess` y empresa? | Opciones con `RecordFilter.ForRecordOrParent` del destino (empresa incluida) y el campo para mostrar que ve (029). Orígenes con `RelatedLoader` con `viewer`: un salto fuera de alcance no ofrece nada. Una condición con origen exige ver el campo destino y cada salto (`LookupFilters.HiddenFor`); con constante, no (decisión 6) | IT › `Un_origen_o_destino_oculto_no_es_oraculo`; IT › `Una_ruta_por_un_registro_fuera_de_alcance_no_ofrece_nada` |
| Delegación | ¿Otorgar, aprobar o heredar lo que no tiene? | No aplica: el filtro solo **reduce** lo que se ofrece y se guarda; no da lectura ni escritura. Elegir sigue exigiendo ver el registro (006) | IT › `El_buscador_no_ofrece_registros_fuera_de_alcance_aunque_cumplan` |
| Concurrencia | Invariante; 10 a la vez; «abrir → guardar» | Invariante: el guardado equivale a un orden serial con los cambios al elegido (lo valida con lo confirmado; un cambio sin confirmar lo hace esperar, READ COMMITTED). Sin `UPDLOCK` (revisión adversarial F-01: dos guardados con los mismos elegidos en otro orden se interbloqueaban). La `rowversion` del propio registro protege su origen. Un cambio posterior al elegido o a un registro intermedio no vuelve a revisar (decisión 2) | IT › `Si_otro_desactiva_el_elegido_mientras_se_guarda_se_espera_y_se_rechaza`; IT › `Diez_altas_a_la_vez_mientras_se_desactiva_el_elegido`; IT › `F01_dos_altas_que_eligen_los_mismos_registros_en_otro_orden_no_se_interbloquean`; R-052-1, R-052-5 |
| Fallas parciales | ¿Cae entre dos pasos? | La revisión ocurre antes de escribir, en la misma transacción que el guardado, las automatizaciones y la bitácora; un rechazo revierte todo (también si lo provoca una automatización encadenada) | IT › `Una_automatizacion_que_deja_una_referencia_que_no_cumple_revierte_la_operacion` |
| Límites | Tamaño, paginación, memoria, tasa | 10 condiciones, rutas de 3 saltos, listas de 50, `TOP 20` opciones, búsqueda de 100 caracteres (`MaxSearchLength`); `LookupFilterJson` 4 000; el borrador del buscador tiene los topes de `/compute` (partidas, varios valores); una consulta por guardado y referencia revisada | unit › `Mas_de_diez_condiciones_o_cuatro_saltos_es_error_del_plan` |
| Semántica | Nulos, vacíos, monedas, fechas, redondeo | Origen vacío (o lista vacía) → no se ofrece nada y no se guarda un valor nuevo; una referencia vacía no se revisa (la obligatoriedad es de `required`). `ne` deja pasar el vacío del destino (como `FilterSql`). Fechas `$today` en la zona del servidor (007); números como `decimal` | unit › `Origen_vacio_no_ofrece_nada`; IT › `Conservar_la_referencia_que_ya_tenia_aunque_ya_no_cumpla` |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** suplantación | — | `$me` es la sesión; en el motor, quien disparó (o 0 en tareas) | unit › `Me_es_quien_captura` |
| **T** el cliente cambia el filtro | Alta | La ruta nueva no recibe el filtro; guardar lo vuelve a exigir con los metadatos | IT › `Guardar_por_POST_PUT_e_importacion_rechaza_lo_que_no_cumple` |
| **R** repudio | Baja | Un rechazo no escribe; lo aceptado queda en la bitácora como cualquier guardado | — |
| **I** oráculo de campos ocultos o fuera de alcance | Alta | Condición con origen sobre un campo que no ve → 400 en el buscador y al guardar; saltos con su alcance; una referencia conservada fuera de alcance se rechaza sin evaluar sus datos (F-02); el texto del rechazo no dice qué condición falló | IT › `Un_origen_o_destino_oculto_no_es_oraculo`; IT › `F02_una_referencia_conservada_fuera_de_alcance_se_rechaza_igual_cumpla_o_no`; R-052-2 |
| **D** consultas costosas | Media | Topes de condiciones, saltos y listas; índices de la tabla de unión (050) | Límites (arriba) |
| **E** guardar lo que el buscador no ofrece | Alta | Mismo filtro en el servidor al guardar, por todos los caminos | IT (Caminos alternos) |
