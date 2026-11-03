# Verify 052 — Filtros de búsqueda compuestos

## Métricas
- inicio: 2026-11-02 14:20
- cierre: 2026-11-02 16:45
- unitarias: 1339 (+42: 840 de Erp.Tests, +23; 408 de integración, +14; Vitest 91, +5)
- e2e: 493 (+3 en `filtros-busqueda`; el canario recorre la ruta nueva)
- corridas completas: 1 de .NET (2 min 10 s) y 1 E2E (12 min 40 s, 489 en verde y 3 fallas de la spec, corregidas: la ruta nueva sin recorrer en el canario y Compras 1.12.0 fijo en `visual-modelador`); la segunda completa es la CI del push
- corridas parciales: 12 (integración de `LookupFilterTests` 7: iteración, tras la revisión adversarial y sin cada corrección; E2E de iteración 4: `filtros-busqueda` 3, canario + `visual-modelador` con dependencias 1; lo nuevo y lo modificado tres veces 1: 125 × 3 en verde)
- contra el código anterior: 3 (`it:regresion -- f5547d6 LookupFilterTests`: las 10 de entonces fallan, el plan anterior rechaza `conditions`; `e2e:regresion -- f5547d6 e2e/filtros-busqueda.spec.ts`: fallan las 3; F-01, F-02 y F-06 sin su corrección en esta carpeta: fallan sus 3 pruebas)
- hallazgos: 0/3/6 (revisión adversarial F-01…F-07: F-01, F-02, F-06 y la parte de fechas de F-07 corregidos, F-05 no se reproduce, F-03, F-04 y el resto de F-07 a riesgos; suite F-08, interbloqueo preexistente, a riesgos; F-09, el editor descartaba el renglón nuevo, corregido)
- categorías: motor-api=2, seguridad=2, concurrencia=2, UI/UX=1, pruebas=1
- revisión adversarial: 7 hallazgos
- riesgos: R-052-1, R-052-2, R-052-3, R-052-4, R-052-5

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0 (`f5547d6`, spec 051). La CI de `f5547d6` estaba en verde (`success`, 20:37 UTC).
CLARIFY con el dueño (2026-11-02): **no se divide** (un solo criterio: seguridad con UI nueva); al guardar se revisa si cambió la
referencia **o su origen**; orígenes de **hasta tres saltos**; constantes de referencia **por id** elegido en el editor; el motor
también lo exige, **como una regla**.

## 1. Pruebas unitarias e integración
`dotnet test`: **840 unitarias** (817 + 23) y **408 de integración** (394 + 14), en verde (2 min 10 s). Vitest: 91 (+5).
- Unitarias nuevas (`tests/Erp.Tests/LookupFilterTests.cs`, 23): forma corta = condición `eq`; constantes, «contiene» y «es uno de»
  válidos; 14 errores del plan con su mensaje (campo, operador por tipo, constante, origen y constante juntos o ninguno,
  `blank` con valor, tipos o catálogos distintos, salto que no es referencia, campo sin valor, fecha con fecha y hora: F-07);
  más de 10 condiciones y cuatro saltos; el origen de una partida empieza en su campo padre; el SQL (solo nombres de los
  metadatos, valores como parámetros, `EXISTS` sobre la tabla de unión, texto con `LIKE` escapado); origen vacío → nada; `$me`;
  origen o destino oculto vuelve inusable el filtro, una constante no; la forma JSON.
- Modificadas a propósito: `NavigationTests` y `ProvenanceTests` (Compras instalado pasa a 1.13.0); `RouteInventory` regenerado
  (`docs/sdd/rutas.md`: 193 rutas, `POST /api/data/{entity}/lookup/{field}`, registros, solo lectura).
- Integración nueva (`tests/Erp.IntegrationTests/LookupFilterTests.cs`, 14, SQL real): el buscador con el borrador (constantes y
  «contiene», búsqueda, origen vacío, alcance por registro, campo sin filtro); en una partida, la ruta de tres saltos con el
  documento en memoria; una ruta por un registro fuera de alcance no ofrece nada ni se guarda; origen o destino oculto → 400 en el
  buscador y al guardar; sin permiso de capturar → 403; POST, PUT e importación rechazan lo que no cumple sin escribir; conservar
  lo que tenía y volver a revisar si cambia el origen (encabezado y partidas); transición y automatizaciones (`update`, `create`)
  rechazan y revierten; **otra conexión desactiva el elegido mientras se guarda** (espera y 400); **diez altas a la vez** mientras se
  desactiva (ninguna guardada); F-01 (20 altas cruzadas sin interbloqueo), F-02, F-05 y F-06.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | `filtros-busqueda` (iteración) | H1/H4 falló: el renglón nuevo sin campo se descartaba al instante (F-09, corregido: estado local del editor) | 38 s |
| 2 | `filtros-busqueda` | H1/H4 falló por «condiciónes» en la vista previa (corregido) y H3 buscaba «Guardar» (el alta dice «Crear»; carrera nula, error de la prueba) | 41 s |
| 3 | `filtros-busqueda` | 3 en verde | 12 s |
| 4 | Completa | 489 en verde; 3 fallas: `canario › H2 CA2` (guardia: la ruta nueva no estaba en su recorrido; ahora la llama con y sin filtro) y `visual-modelador › H1 CA1…` y `› H2 CA1…` (Compras 1.12.0 fijo) | 12 min 40 s |
| 5 | `compras`, `inventario`, `valuacion-*`, `canario`, `visual-modelador` | 107 en verde | 1 min 48 s |
| 6 | `--veces=3` (con `--lento`) sobre los de la fila 5 más `integraciones` y `filtros-busqueda` | 125 × 3 en verde | 10 min 41 s |

- Nuevo: `web/e2e/filtros-busqueda.spec.ts` (H1 CA1 · H4 CA1–CA2: el editor arma «Activo es igual a Sí y Categorías contiene la
  categoría del registro» y la vista previa dice «2 condiciones»; H2 CA1–CA2: «Elige primero categoría», el buscador pide por POST
  con el borrador, sin el filtro, y ofrece solo el que cumple, y cambia con la categoría; H3 CA1: la API rechaza un inactivo y el
  formulario muestra el error junto al campo si el elegido deja de cumplir antes de guardar).
- Modificados: `canario.spec.ts` (un campo con filtro, `Filtrado`, y la ruta nueva en el recorrido de las que devuelven datos),
  `integraciones.spec.ts` y `visual-modelador.spec.ts` (Compras 1.13.0).
- Capturas de referencia: ninguna cambió. El editor aparece solo al desplegar las opciones de un campo de referencia (las capturas
  del modelador lo muestran plegado); revisado a mano en la sección 4.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión, de solo lectura. Recibió el diff (`git diff` y los archivos nuevos) y las 8 preguntas
STRIDE sin las respuestas del plan.

| ID | Severidad | Hallazgo | Decisión |
|---|---|---|---|
| F-01 | Media | El `UPDLOCK, HOLDLOCK` sobre cada elegido, en el orden de los campos, interbloqueaba dos guardados con los mismos elegidos en otro orden (1205 → 500) y no protegía ningún invariante (lo guardado no se revisa después) | Corregido: sin bloqueo; la condición se lee en la transacción (READ COMMITTED espera lo no confirmado). IT › `F01_dos_altas_que_eligen_los_mismos_registros_en_otro_orden_no_se_interbloquean` (falla sin la corrección) |
| F-02 | Media | Una referencia conservada fuera del alcance de quien edita se volvía a evaluar con sus datos al cambiar el origen: oráculo (cumple / no cumple) sobre un registro que no ve | Corregido: se rechaza siempre igual («ya no está a tu alcance; elige otro»). IT › `F02_…` (falla sin la corrección) |
| F-03 | Baja | Un origen calculado (fórmula con saltos) se calcula con el alcance de quien captura en el buscador y con el del motor al guardar | R-052-3 |
| F-04 | Baja | El diálogo de una transición que pide una referencia con filtro usa el buscador sin filtro | R-052-4 (al roadmap) |
| F-05 | Baja | En modo tolerante, una partida que no es objeto correría el índice | No se reproduce: `ParseIntoAsync` ya da 400 también en modo tolerante. IT › `F05_…` como regresión |
| F-06 | Baja | El `update` de una automatización revisaba también referencias que no asigna cuando un cálculo cambiaba su origen (fallas en cascada) | Corregido: el motor solo revisa lo que asigna. IT › `F06_…` (falla sin la corrección) |
| F-07 | Baja | Con `ne`, un origen vacío no ofrece nada; una fecha comparada con una fecha y hora solo coincide a medianoche UTC | Fechas corregido (el plan exige el mismo tipo; unit); `ne` con vacío a R-052-4 (la regla de la 005: sin origen, nada) |

Sin hallazgos en inyección (identificadores de los metadatos, valores parámetros, `LIKE` escapado), tope de parámetros, campos
ocultos (`HiddenFor`), alcance de los saltos, caminos de escritura (todos revisan) y la ruta nueva (permiso de capturar, alcance de
escritura, ver el campo).

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`) con Compras 1.13.0. Recorrida con un script de
Playwright que inicia sesión leyendo el archivo de la instancia (la contraseña no se copia) y captura claro y oscuro, 1280 y 390 px.
- Modelador › Orden de compra › Proveedor: el filtro de Compras 1.13.0 se abre como un renglón («Activo · es igual a · un valor
  fijo · Sí»); agregar «Categorías que surte · contiene · un dato del registro» sugiere «Proveedor › Categoría» (la única ruta
  compatible). En 390 px los controles bajan uno por línea, sin desborde horizontal.
- Registrar orden de compra: el buscador de Proveedor ofrece los tres proveedores activos del módulo; Tab desde el buscador pasa al
  siguiente campo (no a `body`).
- Consola: solo el 401 previo al inicio de sesión.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-08 | Media | concurrencia | (Suite, al escribir la IT de F-01) Altas concurrentes de documentos con partidas se interbloquean al releer las partidas en `LoadOutputAsync`: código anterior a la spec | Fuera del alcance (congelado): R-052-5, al roadmap como endurecimiento | sin prueba (la IT de F-01 evita las partidas) |
| F-09 | Media | UI/UX | El editor descartaba el renglón recién agregado (sin campo todavía): «Agregar condición» no hacía nada | Estado local del editor; al plan solo llegan los renglones con campo | `filtros-busqueda › H1 CA1 · H4…` |

### Contra el código anterior
`npm --prefix web run e2e:regresion -- f5547d6 e2e/filtros-busqueda.spec.ts`: fallan las 3 (no hay editor, el plan rechaza
`conditions` y no hay ruta nueva).

### Lista de comprobación
- [x] El camino feliz de cada historia se completa sin ayuda
- [x] Mensajes de error en español claro («…no cumple el filtro del buscador», «…usa un campo que tu rol no ve», «…ya no está a tu alcance; elige otro»)
- [x] Claro y oscuro, 1280 y 390 px
- [x] Solo iconos Lucide
- [x] Teclado: el editor son controles nativos con nombre; axe sin violaciones serias (E2E)
- [x] Un usuario sin permiso no usa el buscador nuevo (403) ni ve opciones fuera de su alcance (IT y canario)
- [x] La consola no muestra errores (solo el 401 previo al inicio de sesión)
- [x] Cada camino alterno del STRIDE probado (IT: POST, PUT, importación, transición, `update`, `create`)
- [x] Dos o más peticiones a la vez sobre la operación nueva (IT: diez altas mientras se desactiva el elegido; 20 altas cruzadas)
- [x] Hallazgos de un componente genérico: `FilterSql.Condition` lo comparten vistas, reportes y el filtro del buscador
- [x] Alcance congelado: F-04 y F-08 van al roadmap

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#052): R-052-1 (lo guardado no se revisa después), R-052-2, R-052-3, R-052-4 y
R-052-5 (interbloqueo preexistente de altas con partidas).

## 6. CI del push
Corrida [41538958164](https://github.com/leonardos4enz/erp-ai/actions/runs/41538958164) de `9f98e6b`: **en verde** (`success`,
terminó 23:05 UTC; unitarias, integración y E2E completa). Comprobado al abrir la 053.
