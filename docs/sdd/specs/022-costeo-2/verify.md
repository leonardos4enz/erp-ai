# Verificación 022 — Costeo 2: políticas de valuación configurables

**Fecha:** 2026-10-24 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 485/485 dos veces (+18 en `CostingPolicyTests.cs`; `AccountingTests` ajustada a Inventario 1.6.0 y a la automatización nueva de la recepción) |
| E2E | 299/299 en dos corridas completas limpias seguidas (+10 en `valuacion-politicas.spec.ts`). La primera corrida falló en `valuacion-contable` porque la vista previa de Costeo ahora muestra «Valuar» en `[base]` y en `[v1.1.0]` (la prueba esperaba una sola línea): se ajustó para pedir las dos. `valuacion-contable` `H5 CA3` cambia al defecto nuevo (bloquear sin cuentas) |
| Contra el código anterior | Las pruebas nuevas se corrieron contra `HEAD` (28166b0) en un *worktree* aparte con `E2E_INDEPENDENT=1`: las 11 fallaron por la razón esperada (abajo) |
| Visual | Sin cambios en las referencias: los campos nuevos no aparecen en las pantallas capturadas |
| Accesibilidad | axe sin violaciones serias en el diálogo de confirmación con el error de H3, y en la vista «Pólizas pendientes»; pasada solo con teclado (abajo) |
| Cierre completo (después del primer cierre) | E2E de H3 CA2 y segunda revisión exploratoria (teclado y modelador): F-02, F-03 y F-04 corregidos con regresión. Unitarias 497/497 y E2E 301/301 en dos corridas seguidas; las regresiones de F-03 y F-04 fallan con el código anterior |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 traspaso con capas que no cubren | `valuacion-politicas` `H6 CA2 · H1 CA1 · CA2` (3 con capas + 1 al respaldo: entrada = −salida al centavo; el destino sube exactamente eso) |
| H1 CA2 traspaso sin capas | mismo escenario, segundo traspaso |
| H1 CA3 dos partidas FIFO | `H1 CA3` (salidas −80 y −98, entradas 80 y 98; `CostoOrigen` en cada partida) |
| H1 CA4 `capture` / `totalCost` | `Un_costo_total_exacto…`, `Una_captura_mal_declarada…`, `Capturar_cuenta_como_edicion…` |
| H2 CA1 opción del motor | `Entrada_sin_costo_y_sin_capas_sigue_la_politica` (las tres), `Una_politica_de_valuacion_mal_declarada…` |
| H2 CA2 defecto `fallback` | `H2 CA2` (ajuste sin costo → 4 × 12.50) |
| H2 CA3 alternativa `require` | `H2 CA1 · CA3` (sin respaldo se rechaza con el defecto; con `require`, también con respaldo; restaurado al terminar) |
| H3 CA1 sin tipo de cambio | `H3 CA1 · CA3` (borrador sí; confirmar desde la UI muestra el mensaje; API 400 en `Fecha`) |
| H3 CA2 ninguna partida sin costo | `H3 CA2` (empresa propia con base USD, orden en euros sin tipo EUR→USD: costo de la partida vacío; sin la regla `TipoCambioDeLaRecepcion`, `CostoCalculado` rechaza con 400 en `Partidas[0].CostoUnitario`; la empresa se desactiva y el gerente recupera sus empresas al terminar). Agregada después del cierre (F-04) |
| H3 CA3 alternativa | `H3 CA1 · CA3` (sin la regla: costo 90 = tipo de la orden; póliza y movimiento 180) |
| H4 CA1 fecha | `H4 CA1` (recepción con otra fecha del mes: movimiento y póliza en esa fecha); traspaso: `H1 CA1` |
| H5 CA1 opción del motor | `Promedio_con_varias_capas_las_funde_o_las_conserva…` (`merge` y `keep`) |
| H5 CA2 defecto `merge` | `H5 CA2` (FIFO 10@10 + 10@20 → Promedio; salida de 5 = −75) |
| H6 CA1 · CA2 promedio honesto | `H6 CA2 · H1 CA1 · CA2` (existencia previa: sin promedio y «Costeo incompleto»; con 3 costeadas, promedio 17.37) y `Cantidad_costeada…` |
| H7 CA1 bloquear | `H7 CA1 · CA3` (API 400 en `Almacen`; diálogo de la UI) y `valuacion-contable` `H5 CA3` |
| H7 CA2 pendiente | `H7 CA2` (sin la regla: confirmada, `PolizaPendiente`, sin póliza, en la vista) |
| H7 CA3 costo cero | `H7 CA1 · CA3` |
| H7 CA4 `setRules` con `merge` | `SetRules_con_merge…`; las reglas de Compras se conservan (`Inventario_1_6_y_Costeo_1_1…`) |
| H8 actualización | `Inventario_1_6_y_Costeo_1_1_llegan_como_actualizacion…` (en cualquier orden, igual a una instalación nueva) y `valuacion-politicas` `H8` |

## Contra el código anterior
| Prueba | Falla con `HEAD` |
|---|---|
| `valuacion-contable` H5 CA3 (sin cuentas) | *Expected: 400, Received: 200* (se confirmaba sin póliza) |
| H8 versiones | `installedVersion` 1.5.0 |
| H6 CA2 · H1 CA1 · CA2 | `CostoPromedio` 0 en lugar de vacío y sin `CosteoIncompleto` (la prueba se detiene ahí; el traspaso lo cubre H1 CA3) |
| H1 CA3 dos partidas FIFO | entradas `[80, 80]` en lugar de `[80, 98]`: el traspaso creaba 18.00 de valor menos del que salió |
| H2 CA2 entrada sin costo | `CostoTotal` 0 en lugar de 50 |
| H2 CA1 · CA3 sin respaldo | *Expected: 400, Received: 201* (entraba a cero) |
| H3 CA1 · CA3 sin tipo de cambio | el diálogo no muestra error: la recepción se confirmaba con el tipo de la orden |
| H4 CA1 fecha | `Fecha` del movimiento 2026-10-24 (hoy) en lugar de la de la recepción |
| H5 CA2 FIFO → Promedio | `CostoTotal` −50 en lugar de −75 |
| H7 CA1 · CA3 sin cuentas | *Expected: 400, Received: 200* |
| H7 CA2 pendiente | no existe la regla `PolizaConCuentas` |

## Revisión exploratoria
Servidor aparte (:5096) sobre la base desechable `ErpAi_E2E` después de la corrida verde (nunca `ErpAi`), con los usuarios de
prueba de la suite.
- *Supervisor de inventario*, 1280 px oscuro: existencia del rodamiento en el almacén de refacciones: «Valor» 243.11,
  «Cantidad costeada» 5, «Costo promedio» 48.622 (de lo costeado, no entre las 15 piezas) y «Costeo incompleto: Sí». Se
  lee sin ayuda; la otra existencia del rodamiento, sin capas, no muestra promedio.
- Traspaso aplicado: la partida muestra «Costo de la salida» (80.00) junto a la cantidad. En un traspaso en borrador la
  columna aparece vacía y deshabilitada, con etiqueta accesible («Costo de la salida, partida 1»), como los demás campos
  que asigna el sistema.
- 390 px claro: el formulario de la existencia apila los campos nuevos sin cortes ni desbordes.
- Consola: sin errores de la app ni de CSP. Un 500 apareció por una petición escrita a mano (ver F-01).
- El rechazo al confirmar sin tipo de cambio y sin cuentas se revisó en el diálogo de la UI dentro de la suite
  (`H3 CA1 · CA3`, `H7 CA1 · CA3`), con axe sin violaciones serias.

**Segunda pasada (después del cierre, a pedido del dueño): teclado y modelador**, en el mismo servidor aparte y la misma
base desechable.
- *Modelador de pruebas* (`e2e-ia-modelador`), solo teclado: «Editar» de *Movimientos de inventario* con Enter; el JSON de
  automatizaciones muestra `uncostedEntry`, `methodChange`, `totalCost` y `quantityField` de `Valuar`. Cambiar
  `uncostedEntry` a `"cero"` y «Revisar cambios»: el plan es inválido con «uncostedEntry 'cero' no válido (average,
  fallback, require)» y «Aplicar cambios» deshabilitado; el foco queda atrapado en el diálogo y Esc lo cierra y devuelve el
  foco a «Revisar cambios». Se descartó sin aplicar. La ayuda del campo no mencionaba `cost` ni las opciones (F-02).
- *Almacenista*, solo teclado: Tab hasta «Confirmar recepción» en una recepción de un almacén sin cuentas, Enter; el
  diálogo abre con el foco en «Cancelar» (seguro por defecto); Tab y Enter en «Confirmar recepción». El mensaje aparecía,
  pero el foco caía al `body` y el error no se anunciaba (F-03). Corregido: el foco vuelve al botón, el motivo está en
  `role="alert"` y Esc cierra y regresa el foco.
- Al escribir la E2E de H3 CA2 apareció F-04.

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Baja | `GET /api/data/{catálogo}?sort=Id` responde 500: `ResolveSort` arma `ORDER BY t.[Id] DESC, t.[Id] DESC` (columna repetida). Lo mismo en OData con `$orderby=Id` o una propiedad repetida (`$orderby=Folio,Folio desc`). La SPA no envía esos órdenes; existe desde las specs 001 y 018 | Resuelto después del cierre, a pedido del dueño: ordenar por `Id` no agrega el desempate y en OData gana el primer criterio de cada columna (el desempate sigue al último criterio conservado). Regresiones: `SortTests.cs` (8 de 12 fallaban con el código anterior, ya con `ResolveSort` y `ODataOrderClause` expuestos sin cambiar su lógica), `api-security` «ordena por Id en los dos sentidos» (listado y exportación) y `oauth-odata` H4 CA1 · CA2. Unitarias 497/497 y E2E 300/300 dos veces |
| F-02 | Baja | La ayuda de «Automatizaciones (JSON)» en el modelador listaba solo `update, transition, create o rollup`: faltaban `cost` (desde la 020), `createLines`, `capture` y las políticas | Corregido en `EntityEditor.tsx` (texto de ayuda) |
| F-03 | Media | Diálogo de confirmación de una acción del proceso: si el servidor la rechaza (p. ej. `PolizaConCuentas`), el botón se deshabilita mientras espera y el foco caía al `body`; el motivo se pintaba sin `role="alert"`. Una persona con teclado quedaba fuera del diálogo y un lector de pantalla no anunciaba por qué | Corregido en el genérico: `useFocusTrap` (`Dialog.tsx`) devuelve el foco al último elemento del diálogo cuando se rehabilita, y `Workflow.tsx` pone el motivo en `role="alert"`. Regresión en `valuacion-politicas` `H7 CA1 · CA3` (falla con la UI anterior: no hay `alert`) |
| F-04 | Alta | En expresiones un vacío cuenta como 0 en la aritmética y `ROUND(vacío)` da 0: sin tipo de cambio ni respaldo, `RecepcionPartida.CostoUnitario` era **0**, no vacío. La regla `CostoCalculado` nunca se disparaba y la recepción se valuaba a cero (lo que la 020 describía como «`Costo` nulo» era en realidad un cero silencioso) | Corregido en **Inventario 1.6.1** (actualización nueva; la 1.6.0 ya estaba publicada): la fórmula deja el costo vacío si no hay fuente. No se cambió la semántica de `ROUND` en el motor (afectaría a todas las fórmulas). Regresión `H3 CA2` (con Inventario 1.6.0: *Expected null, Received 0*) y unitaria de módulos |

## Riesgos residuales
- En expresiones, un vacío vale 0 en la aritmética y en `ROUND`/`ABS`/`MIN`/`MAX` (F-04): una fórmula que deba distinguir
  «sin dato» de cero tiene que protegerse con `IF(ISBLANK(...), NULL, ...)`. Cambiarlo en el motor sería otra spec.
- Las recepciones en borrador creadas antes de Inventario 1.5.0 no tienen `Moneda`: la regla del tipo de cambio usa la de
  la orden (`COALESCE`), pero la fórmula del costo de la partida no (rutas de tres niveles): con base MXN toma el costo de
  la orden; con otra base, `CostoCalculado` la detiene con mensaje.
- Si el dueño de un catálogo reemplaza todas sus reglas sin `merge`, quita las políticas que otro módulo agregó (igual que
  con `setAutomations`).
- Las existencias anteriores a Costeo 1.1.0 no tienen cantidad costeada hasta su siguiente movimiento: se marcan
  «Costeo incompleto» y sin promedio.
- Con la regla `PolizaConCuentas` quitada, la póliza pendiente se captura a mano; generarla después queda pendiente.
- El rol *Contador* no lee recepciones (la vista «Pólizas pendientes» la ve quien sí las lee); es dato del entorno (ADR-0004):
  el dueño lo asigna en *Seguridad › Roles* de cada entorno.

## Estado del entorno de desarrollo (`ErpAi`, 2026-10-24)
Lo hizo el dueño desde la UI (la sesión solo lo verificó, en modo lectura):
- Inventario actualizado a 1.6.1; Contabilidad 1.1.0 y Costeo 1.1.0 instalados.
- Rol *Contador* con lectura de *Recepción* (además de cuentas, periodos y pólizas del módulo).

Pendiente **por decisión del dueño** (nadie usa todavía la contabilidad en este entorno): no hay periodo contable abierto y
el almacén ALM-01 no tiene *Cuenta de inventario* ni *Cuenta de entradas por facturar*. Mientras sigan así, confirmar una
recepción con costo en ALM-01 se rechaza (regla `PolizaConCuentas`; sin la regla, la póliza fallaría por falta de periodo).
Para empezar a usarla: crear el periodo del mes en *Periodos contables* y asignar al almacén las cuentas 1200 y 2150 del
catálogo inicial.
