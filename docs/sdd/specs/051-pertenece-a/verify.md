# Verify 051 — «Pertenece a» en expresiones y en la seguridad por registro

## Métricas
- inicio: 2026-11-02 13:02
- cierre: 2026-11-02 14:12
- unitarias: 1297 (+50: 817 de Erp.Tests, +31; 394 de integración, +17; Vitest 86, +2)
- e2e: 490 (+3: 2 en `pertenece-a`, 1 en `canario`)
- corridas completas: 1 de .NET (2 min 13 s) y 1 E2E (11 min 30 s, 487 en verde y 3 fallas: 2 por la versión de Compras fija en `visual-modelador`, corregidas, y la intermitente conocida de `reportes-personales`, R-050-9); la segunda completa es la CI del push
- corridas parciales: 10 (integración de `MembershipTests` 5: iteración, Compras, tras la revisión adversarial y sin la corrección de F-01/F-02; E2E de iteración 2: `pertenece-a` + `canario`; lo nuevo y lo modificado tres veces 3: la primera cortada por dependencias faltantes y una carrera de la prueba, la segunda por las intermitentes de R-050-9, la tercera, 55 × 3 en verde)
- contra el código anterior: 4 (`it:regresion -- 1f6421e MembershipTests`: las 15 fallan; `e2e:regresion -- 1f6421e e2e/pertenece-a.spec.ts e2e/canario.spec.ts`: fallan las 2 nuevas y el canario completo, cuya preparación usa la pertenencia; F-01 y F-02 sin la corrección en esta carpeta: fallan sus 2 pruebas)
- hallazgos: 0/3/6 (revisión adversarial F-01…F-07: F-01 a F-04 corregidos, F-05 a F-07 a riesgos; revisión exploratoria F-08, corregido; suite F-09, carrera de `pertenece-a` con `--lento`, corregida)
- categorías: motor-api=3, seguridad=3, UI/UX=1, pruebas=2
- revisión adversarial: 7 hallazgos
- riesgos: R-051-1, R-051-2, R-051-3, R-051-4, R-051-5

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0 (`1f6421e`, spec 050). La CI de `1f6421e` estaba en curso al abrir (unitarias en
verde, integración y E2E corriendo) y terminó en verde (`success`, 19:21 UTC) antes de cambiar nada que dependiera de ella. El
servidor de desarrollo `erp-api` estaba detenido.
CLARIFY con el dueño (2026-11-02): **se divide** (la 051 es «pertenece a» y asignar o quitar un valor; los filtros de búsqueda
compuestos pasan a la 052 y lo demás se recorre a 053–057), **ambas sintaxis** (`X IN Campo` y `CONTAINS`) y rutas de **hasta
tres saltos**.

## 1. Pruebas unitarias e integración
`dotnet test`: **817 unitarias** (786 + 31) y **394 de integración** (377 + 17), en verde (2 min 13 s). Vitest: 86 (+2).
- Unitarias nuevas (`tests/Erp.Tests/MembershipTests.cs`, 30): el parser (`IN Campo` = `CONTAINS`, `IN (…)` igual, `NOT` y `AND`
  alrededor), el analizador (propio y hasta tres saltos; lista que no es de varios valores, valor incompatible, cuatro saltos),
  el evaluador (vacía, nula, ruta rota, fuera de alcance → desconocida, `NOT`), la traducción a `EXISTS` (propio, un salto, tres
  saltos con el dueño vigente en cada uno, columna propia sin parámetro, valor como parámetro), sin modelo falla cerrado, las
  reglas de acceso que no son pertenencia válida, acumulados (`CURRENTUSER()` rechazado), archivar la lista usada, `mode` en
  transiciones, automatizaciones y la captura, y la forma canónica sin `mode`. `PrintTests` (+1): «pertenece a» en una plantilla
  con el alcance de quien imprime (F-03).
- Modificadas a propósito: `MultiValueTests` (los mensajes de la 050 que nombraban la 051), `NavigationTests` y
  `ProvenanceTests` (Compras instalado pasa a 1.12.0).
- Integración nueva (`tests/Erp.IntegrationTests/MembershipTests.cs`, 16, y `MembershipTests.Compras.cs`, 1, SQL real): un
  permiso por pertenencia limita **lista, registro, bitácora, impresión, buscador, búsqueda global, reporte, exportación y
  OData**; fuera del alcance de escritura (PUT, transición, borrar); quitar a la persona de la lista o borrar el dueño quita el
  acceso; regla y condición de transición con pertenencia; agregar y quitar desde una transición con bitácora antes/después;
  valor inexistente o un equipo → 400 sin escribir; una transición que agrega y luego falla una regla no deja nada; **dos
  transiciones con la misma versión** (200 y 409, la lista es la ganadora); **diez automatizaciones a la vez** agregan diez
  valores a la misma lista y quitar también; el valor 101 rechaza la operación completa; filtro de reporte evaluado con quien lo
  pide; en vivo, la lista de un registro que no se ve no cuenta (`deletableWhen`); con `NOT`, una lista fuera de alcance es
  desconocida (F-01); acumulado en memoria con pertenencia (F-02); quien no puede editar la lista no se agrega; una condición
  que nombra una lista oculta llega sin su texto; Compras 1.12.0 (el responsable del departamento consulta sin modificar y
  «Quienes intervinieron» recibe a quien envía).
- `docs/sdd/rutas.md`: sin cambios (ninguna ruta nueva).

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | `pertenece-a` + `canario` (iteración) | 17 en verde | 18 s |
| 2 | Completa | 487 en verde; 3 fallas: `visual-modelador › H1 CA1…` y `› H2 CA1…` esperaban «Compras 1.11.0» (corregido a 1.12.0) y `reportes-personales › H3 CA3` (intermitente conocida, R-050-9) | 11 min 30 s |
| 3 | `--veces=3` (con `--lento`) sobre `compras`, `integraciones`, `visual-modelador`, `canario`, `pertenece-a` y `reportes-personales` | Vuelta 1: 83 en verde, 5 fallas: `visual-modelador` (4) necesita los módulos que instalan `inventario` y `valuacion-*` (no estaban en la lista) y `pertenece-a › H3 CA4` leía el registro antes de que respondiera la transición (carrera de la prueba con `--lento`: ahora espera la respuesta, `waitForResponse` registrado antes del clic) | 4 min 1 s |
| 4 | Lo mismo con `inventario` y `valuacion-*` | Vuelta 1: 130 en verde y las 2 intermitentes de R-050-9 (`reportes-personales › H3 CA3`, «Guardar como» dio 201; `visual-modelador › H3 CA1`, la lista de funciones tras «ISB»), ajenas a la spec | 4 min 16 s |
| 5 | `--veces=3` sobre `compras`, `integraciones`, `canario` y `pertenece-a` | 55 × 3 en verde | 4 min 22 s |

- Nuevo: `web/e2e/pertenece-a.spec.ts` (H1 CA5 · H2 CA1: el constructor arma `CURRENTUSER() IN Centro.Responsables` en un permiso
  y la persona solo ve en la lista y por URL lo de sus centros; H3 CA4: la asignación de transición pide agregar o quitar, sin
  modo en un campo simple, y enviar agrega a quien la ejecuta; con la regresión F-08 en 390 px).
- Modificados: `canario.spec.ts` (H2 CA4 (051): una ficha fuera de alcance por pertenencia entra al recorrido de todas las rutas
  del inventario, con control positivo del administrador), `integraciones.spec.ts` y `visual-modelador.spec.ts` (Compras 1.12.0).
- Capturas de referencia: ninguna cambió. Las pantallas que toca la spec (seguridad y proceso del modelador) ya tienen captura;
  el selector de modo y «contiene» no aparecen en ellas (no hay campos de varios valores en esos catálogos).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff de producto (`git diff` contra `1f6421e`), el inventario de rutas y las
8 preguntas STRIDE sin las respuestas del plan. Revisó `RecordFilter`, `Scopes`, `ApplyMember`/`SystemUpdateAsync`, transiciones,
avisos, `DerivedAccess`, reportes, tareas y acumulados, impresión e importación.

| ID | Severidad | Hallazgo | Decisión |
|---|---|---|---|
| F-01 | Media | Con el alcance de una persona, la lista de un registro que no ve daba **falso**: `NOT X IN Lista` se volvía verdadero (p. ej. `deletableWhen` dejaba eliminar) | Corregido: fuera de alcance es desconocido; ruta rota por los datos sigue siendo falso. IT › `Con_NOT_una_lista_fuera_de_alcance_es_desconocida_y_no_elimina` y unit (fallan sin la corrección) |
| F-02 | Media | Un acumulado evaluado en memoria (filtro no traducible) leía las filas sin sus listas: la pertenencia daba falso en silencio | Corregido: se cargan las listas si el filtro usa `CONTAINS`. IT › `Un_acumulado_en_memoria_con_pertenencia_suma_lo_que_cumple` (falla sin la corrección) |
| F-03 | Baja | La impresión no respondía la pertenencia (siempre falso en `visibleWhen`/`IF`) | Corregido: `PrintScope.Members` con el alcance de quien imprime en cada salto. `PrintTests › Pertenencia_en_una_plantilla_con_el_alcance_de_quien_imprime` |
| F-04 | Baja | `add CURRENTUSER()` del motor (id 0) rechazaba la operación entera | Corregido: id 0 no cambia nada |
| F-05 | Baja | En el filtro de un reporte, la ruta a la lista no aplica el alcance de quien lo corre | R-051-2 |
| F-06 | Baja | El modelo es opcional en `RecordFilter.For`: un camino nuevo sin él falla cerrado con 500 | R-051-3 |
| F-07 | Baja | Si `add` agrega un registro que quien ejecuta no ve, no se le preguntan los avisos | R-051-4 |

Sin hallazgos en entradas (ningún valor concatenado; identificadores solo de metadatos), caminos alternos (todos pasan el
modelo), delegación (la lista de Compras no la edita el solicitante; `Participantes` es del sistema), concurrencia (bloqueo de
la fila dueña antes de leer la lista; versión en transiciones) y fallas parciales (una transacción).

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`) con Compras 1.12.0; personas «Ana
Responsable» (Solicitante, responsable de Producción) y «Beto Solicitante». Recorrida con un script de Playwright que inicia sesión
leyendo el archivo de la instancia (la contraseña no se copia a ningún lado) y captura cada pantalla: claro y oscuro, 1280 y
390 px.
- Beto envía su requisición: «Quienes intervinieron» muestra «Beto Solicitante» (solo lectura).
- Ana ve la requisición de Beto en su lista y en el registro, sin poder editarla; los botones de aprobar aparecen deshabilitados
  (no tiene el rol), como antes.
- Modelador › Seguridad: la regla 2 se lee en renglones («Departamento › · Responsables · contiene · quien usa el sistema»).
- Modelador › Proceso: la asignación de «Enviar a aprobación» muestra «Agregar a la lista»; con teclado, Tab desde el campo
  llega al modo («Qué hace la asignación 1 con Participantes»).

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-08 | Media | UI/UX | En 390 px, con el selector de modo, el valor de la asignación quedaba sin ancho (no se podía escribir) | La fila se parte: campo y modo arriba, el valor con su botón abajo (`.wf-assign.has-mode`, `.assign-row.has-mode`) | `pertenece-a › H3 CA4` (ancho del valor > 200 px en 390 px) |

### Contra el código anterior
`npm --prefix web run e2e:regresion -- 1f6421e e2e/pertenece-a.spec.ts e2e/canario.spec.ts`: fallan las dos de `pertenece-a`
(el constructor no ofrece «contiene» y el plan rechaza `mode`) y todo el canario (su preparación declara un permiso por
pertenencia, que el compilador anterior rechaza).

### Lista de comprobación
- [x] El camino feliz de cada historia se completa sin ayuda
- [x] Mensajes de error en español claro («… tiene varios valores: indica si se agrega o se quita», «En «X IN Campo», X debe ser…»)
- [x] Claro y oscuro, 1280 y 390 px (F-08 corregido)
- [x] Solo iconos Lucide
- [x] Teclado: el modo se alcanza con Tab; el constructor de condiciones ya era accesible (axe sin violaciones serias)
- [x] Un usuario sin permiso no ve el registro ni por URL directa (E2E y canario)
- [x] La consola no muestra errores (solo el 401 previo al inicio de sesión)
- [x] Cada camino alterno del STRIDE probado con un usuario sin permiso (IT, por API)
- [x] Dos peticiones a la vez sobre la operación nueva (IT: transiciones con la misma versión; diez automatizaciones)
- [x] Hallazgos de un componente genérico: el selector de modo vive en `assignMode.tsx` y lo usan el proceso y las acciones de
      automatizaciones y tareas; ambos con su arreglo de ancho
- [x] Alcance congelado: nada nuevo entró en VERIFY

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#051): R-051-1 (delegación por datos: quien edita la lista da el acceso), R-051-2,
R-051-3, R-051-4 y R-051-5.
