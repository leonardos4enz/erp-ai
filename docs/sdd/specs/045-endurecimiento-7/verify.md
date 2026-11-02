# Verify 045 — Endurecimiento 7 (auditoría 8)

## Métricas
- inicio: 2026-11-01 20:17
- cierre: 2026-11-01 21:20
- unitarias: 1125 (+19: 757 de Erp.Tests, +2; 295 de integración, +17; Vitest 73, +0)
- e2e: 468 (+1: `canario.spec.ts › H1 (045)`)
- corridas completas: 2 (dotnet test 2 min 14 s; E2E 468 en verde, 13 min 54 s); la segunda completa fue CI en `main` (`2a307c0`), en verde
- corridas parciales: 9 (integración de `Hardening045Tests` 2, `SalesTests` 1, `Mcp2Tests` H045 2, clases afectadas tras la revisión adversarial 1, `Hardening034Tests`+`McpTests.F04` 1; E2E de compras→ventas + canario 2); `--veces=3` 1
- contra el código anterior: 5 (integración: las 17 nuevas contra `63bd34a` en tres pasadas; E2E: `canario.spec.ts` contra `63bd34a`; renovación con el bucle anterior puesto a mano)
- hallazgos: 0/1/8 (revisión adversarial F-01…F-09: F-01, F-02, F-03, F-05 y F-07 corregidos; F-04, F-06, F-08 y F-09 a riesgos); exploratoria sin hallazgos
- categorías: concurrencia=2, seguridad=3, motor-api=4
- revisión adversarial: 9 hallazgos
- riesgos: R-045-1, R-045-2, R-045-3, R-045-4, R-045-5, R-045-6, R-045-7, R-045-8

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0 (`d7cc96e`, el roadmap de la auditoría 8; la 044 con CI en verde anotada en
`63bd34a`). Sin `gh` en esta máquina; la última corrida de CI de `main` registrada en `verify.md` de la 044 estaba en verde.

## 1. Pruebas unitarias e integración
`dotnet test`: **757 unitarias** (755 + 2 en `tests/Erp.Tests/Hardening7Tests.cs`) y **295 de integración** (278 + 17), en verde
(2 min 14 s). Vitest: 73.
- Unitarias (`Hardening7Tests`): `WriteLeaseSeconds` acotado a la mitad de `IdempotencyHours` (D-10); `ForwardedProxies` como
  texto (O-02).
- Integración nueva: `Hardening045Tests` (7: partidas con su campo oculto en fuentes, exploración, exportación, guardado,
  reportes del modelo, tarjeta compartida y saltos; `deletableWhen` y su validación; rastro `data.unique` sin el valor en el
  alta y en la vista previa de importación; `data.export` del explorador y del reporte del modelo bajo el catálogo raíz; tope
  de 100 compartidos). `Mcp2Tests.Hardening045.cs` (5: la clave se consume si el resultado no cabe; la renovación de la clave
  y de la copia sobrevive a una falla inyectada; el barrido no borra una clave en curso; el barrido del registro no borra un
  código vigente; una falla del barrido no tumba el registro). `SalesTests` (5: sin límite capturado el vendedor no confirma,
  también con «Público en general»; «Público en general» de solo lectura para el vendedor; el pedido de una cotización no se
  borra; la instalación queda en 1.1.0; borrar y confirmar a la vez no deja un pedido confirmado borrado).
- Modificadas a propósito: los clientes de `SalesTests` reciben un límite del gerente (el comportamiento nuevo es «vacío = 0»
  para el vendedor) y el final de `H2_CA3` prueba que el **gerente** confirma sin límite; `McpTests › F04` vence el código antes
  de barrer (D-11: mientras el código está vigente el cliente no se barre; después, sí); `Hardening034Tests` declara un reporte
  para que la exportación de reportes, que ahora deja rastro, se ejerza en su recorrido.
- `docs/sdd/rutas.md`: las dos exportaciones de reportes pasan a efecto «bitácora».

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | compras, inventario, valuación contable, ventas y canario (iteración) | 71 en verde (antes: 1 falla por el texto nuevo del error de crédito y la limpieza del canario) | 1 min 20 s |
| 2 | Completa | 468 en verde | 13 min 54 s |
| 3 | `--veces=3 --lento` sobre los mismos cinco archivos | 3 vueltas de 71 en verde | 6 min 18 s |

Archivos modificados: `canario.spec.ts` (partida detrás de un campo de partidas oculto, prueba `H1 (045)`, limpieza que archiva
primero el campo de partidas) y `ventas.spec.ts` (el cliente queda con un límite amplio; texto del error). Sin capturas nuevas
ni cambiadas: no hay pantallas nuevas.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff sin confirmar, las preguntas de la tabla STRIDE y el inventario de rutas.
Encontró:

| ID | Severidad | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|
| F-01 | Media | `DeleteAsync` leía el documento sin bloquearlo: una confirmación entre la lectura y el borrado dejaba borrar un pedido confirmado (contado para siempre en los pedidos abiertos) | El borrado toma `UPDLOCK, HOLDLOCK` sobre el documento antes de leerlo | `SalesTests › Regresion_045_F01_borrar_y_confirmar_a_la_vez_no_borra_un_pedido_confirmado` (con una retención determinista de `Embarque`: falla con `63bd34a`) |
| F-02 | Baja | `deletableWhen` se evaluaba con un cargador sin alcance: un bit por intento sobre datos fuera de alcance | Se evalúa con el alcance de quien elimina (`viewer`); un campo propio oculto queda como riesgo | [R-045-8](../../riesgos.md#045) |
| F-03 | Baja | La descripción del plan no nombraba `deletableWhen` | «se elimina solo si …» en el paso | revisión exploratoria (vista previa) |
| F-04 | Baja | `data.unique`/`data.export` bajo la raíz: nombre de un campo oculto o de un reporte de partidas | Aceptado | [R-045-4](../../riesgos.md#045) |
| F-05 | Baja | El barrido del registro sigue con una ventana de milisegundos; una falla grave dejaba rota la conexión de los topes | El barrido va en su propia conexión; la ventana queda en R-040-6 | `Mcp2Tests › H045_H6_CA2…` |
| F-06 | Baja | El tope de 100 se puede saturar con lo de una persona | Aceptado | [R-045-5](../../riesgos.md#045) |
| F-07 | Baja | Solo dos tipos de excepción consumían la clave; una cancelación interna terminaba la renovación | Toda excepción tras el éxito consume la clave; solo la cancelación propia termina la renovación | `Mcp2Tests › H045_H5_CA1…`, `› H045_H5_CA2…` |
| F-08 | Baja | Lista en archivo y texto en variable de entorno: gana la lista | Aceptado | [R-045-6](../../riesgos.md#045) |
| F-09 | Baja | Ventas 1.1.0 reemplaza completas dos políticas de seguridad | Aceptado | [R-045-7](../../riesgos.md#045) |

Revisó sin hallazgo: `SeesLines` en todos los caminos de reportes (lista, corrida, exportación, tableros del modelo, tarjetas
personales, exploración, fuentes y campos, saltos por `Parent`), que búsqueda, OData, vistas y registros ya rechazan partidas;
un solo camino de borrado; el rastro nunca lleva el valor; las reglas de crédito (nulos, `COALESCE`, confirmaciones
serializadas por `LockAutomationTargetsAsync`, `Cotizacion` sin forma de vaciarse); instalación nueva = actualizada
(`FullChain`); acotado del *lease* y barrido de claves.

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`), como administrador, en oscuro y 1280 px.
Un catálogo con `deletableWhen` por plan: la vista previa dice «se elimina solo si ISBLANK(Referencia)»; eliminar una nota con
referencia deja el aviso del modelo (`role="alert"`, con la referencia del error), el registro sigue y el foco vuelve a
«Eliminar». Las pantallas de reportes, exploración y Ventas no cambian de forma (lo cubren las E2E: canario y `ventas.spec.ts`).

### Hallazgos
Ninguno.

### Contra el código anterior
- Integración (`it:regresion -- 63bd34a`): de las 17 nuevas, 16 fallan por la razón esperada. `H045_H2_CA2` pasaba en la primera
  versión porque cambiar el RFC ya daba 403 (sale del alcance de escritura): ahora cambia `Notas` y falla con `63bd34a`.
  `H045_H5_CA2` pasaba porque la restricción inyectada nombraba solo `Id` (SQL Server no la revisa si el UPDATE no toca esa
  columna): ahora nombra `PendingUntil`/`LeaseUntil` y falla con el bucle anterior. `Regresion_045_F01` no se reproducía con
  peticiones a la vez: ahora retiene `Embarque` para detener el borrado entre la lectura y la escritura, y falla con `63bd34a`.
  `H045_H2_CA3` falla con `63bd34a` porque el paquete era 1.0.0 (es la comprobación de la versión).
- E2E (`e2e:regresion -- 63bd34a e2e/canario.spec.ts`): fallan `H1 (045)` (el lector ve `CnDocPartida` en sus fuentes) y
  `H2 CA2` de rutas que no son GET (explorar la partida le devolvía el canario). Las demás pasan.

### Lista de comprobación
- [x] Camino feliz de cada historia (por API en integración; borrar con condición, por la UI)
- [x] Mensajes en español claro (el del crédito dice qué hacer: «… o fija el límite del cliente»)
- [x] Seguridad: cada camino alterno del STRIDE con un usuario sin permiso, por API (`Hardening045Tests`, canario)
- [x] Dos peticiones a la vez sobre la operación nueva (borrar y confirmar: F-01)
- [x] Consola sin errores ni violaciones de CSP (guardia de la suite en las 468)
- [x] Alcance congelado: O-03 y el editor visual de `deletableWhen` quedaron en el roadmap

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#045): R-045-1 … R-045-8. Actualizados: R-040-3, R-040-6, R-043-6 (cerrado) y R-044-1.
