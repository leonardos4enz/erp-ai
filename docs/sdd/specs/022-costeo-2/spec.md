# Spec 022 — Costeo 2: políticas de valuación configurables

**Estado:** Implementado · **Depende de:** 005, 010, 020, 021 · **Origen:** pendientes de la [spec 021](../021-endurecimiento-2/spec.md) y
decisión del dueño (2026-10-24)

## Problema
La spec 021 difirió siete casos de valuación como «decisiones contables». El dueño señaló que **son reglas de negocio**
(constitución I.1): cada empresa debe poder elegir cómo valuar sin programar, con valores por defecto razonables. Hoy unas
están fijas en el código del motor (`CostMath`) y otras en expresiones de los módulos sin una opción declarada; una es un
error que ninguna política justifica.

Verificación en el código (2026-10-24, `HEAD` = 28166b0):

| # | Caso | Dónde | Lo que pasa hoy | Clase |
|---|---|---|---|---|
| 1 | Traspaso con capas que no cubren la cantidad | `inventario.json` 1.5.0 `MoverMercancia`; `CostMath.PeekUnitCost` / `Apply` | La entrada al destino se crea **antes** que la salida y se valúa con `UNITCOST` (promedio de lo **cubierto**, redondeado a 4); la salida valúa lo no cubierto al respaldo y redondea cada capa a 2. Sin capas, `UNITCOST` es nulo y la entrada vale el promedio del destino o 0. Dos partidas FIFO del mismo producto leen las mismas capas. El traspaso crea o destruye valor | **Invariante** (error) |
| 2 | Entrada sin costo en una llave sin capas | `CostMath.Apply` (`unitCost ?? promedio ?? 0`) | Entra a costo 0 sin aviso; el respaldo (costo último) solo se usa en salidas | Configurable |
| 3 | Recepción sin tipo de cambio | `inventario.json` 1.5.0 `RecepcionPartida.CostoUnitario`; `costeo.json` `PolizaEntrada` | Sin tipo de cambio a la fecha, el costo cae **en silencio** al de la orden (`CostoMXN`, en pesos aunque la moneda base sea otra). Si tampoco hay ese, `Costo` es nulo: `SUM` lo cuenta como 0 en la póliza mientras el movimiento se valúa al promedio | Configurable |
| 4 | Fecha del movimiento de la recepción | `inventario.json` `EntradaAlmacen` | El movimiento toma `TODAY()` (su valor por defecto); la póliza y `CONVERT` usan `Recepcion.Fecha` | Configurable |
| 5 | Cambio de método FIFO → Promedio | `CostMath.Apply` | Con varias capas abiertas, una salida «promedio» las consume en orden (FIFO) hasta la siguiente entrada | Configurable |
| 6 | Costo promedio con existencias sin capas | `costeo.json` `Existencia.CostoPromedio = Valor / Cantidad` | Divide el valor de lo costeado entre **toda** la existencia (incluida la anterior al costeo): un promedio menor al real, sin aviso | Configurable |
| 7 | Póliza de entrada sin cuentas o en cero | `costeo.json` `PolizaEntrada.when` | Sin cuentas en el almacén la recepción se confirma y se valúa, pero no deja póliza ni rastro de que falta | Configurable |

## Historias

**H1 — Un traspaso no crea ni destruye valor (invariante).** Como *contador*, quiero que la entrada al destino valga
exactamente lo que valió la salida del origen, cubran o no las capas la cantidad.
- CA1: Con capas que cubren parte de la cantidad (y el resto sale al respaldo), la entrada al destino tiene el mismo costo
  total que la salida, al centavo.
- CA2: Sin capas en el origen, la entrada vale lo mismo que la salida (el respaldo o, sin respaldo, cero).
- CA3: Dos partidas del mismo producto en un traspaso FIFO consumen capas distintas, y cada entrada vale lo que su salida.
- CA4 (motor): una acción `create` puede guardar en el registro que la disparó valores del registro que creó
  (`capture`); una acción `cost` puede valuar una entrada a un costo total exacto (`totalCost`).

**H2 — Costo de una entrada sin costo unitario (configurable).**
- CA1 (motor): la acción `cost` declara qué hacer con una entrada sin costo en una llave sin capas (`uncostedEntry`):
  `average` (cero, como en la 020), `fallback` (el costo de respaldo; sin respaldo, o con respaldo en cero, se rechaza) o
  `require` (se rechaza).
  Con capas, la entrada sin costo sigue al promedio vigente en las tres.
- CA2 (Costeo 1.1.0, defecto `fallback`): un ajuste positivo sin costo de un producto sin capas entra al costo último.
- CA3: con `require`, la misma entrada se rechaza con un mensaje que dice qué capturar.

**H3 — Recepción sin tipo de cambio (configurable).**
- CA1 (Inventario 1.6.0, defecto): confirmar una recepción en moneda extranjera sin tipo de cambio en o antes de su fecha
  se rechaza con un mensaje que dice qué registrar. Mientras es borrador se puede guardar.
- CA2 (invariante): ninguna partida de una recepción confirmada queda sin costo: la póliza y el movimiento valen lo mismo.
- CA3 (alternativa): sin la regla `TipoCambioDeLaRecepcion`, la recepción usa el tipo de cambio de la orden (solo si la
  moneda base es MXN, la moneda en que la orden guarda su costo); en otra moneda base, CA2 la rechaza.

**H4 — Fecha del movimiento de una recepción (configurable).**
- CA1 (Inventario 1.6.0, defecto): la entrada al almacén lleva la fecha de la recepción, la misma de la póliza y del tipo
  de cambio. Una recepción con fecha distinta de hoy deja su movimiento en esa fecha.

**H5 — Cambio de método de costeo (configurable).**
- CA1 (motor): la acción `cost` declara qué hacer cuando el método es promedio y la llave tiene varias capas abiertas
  (`methodChange`): `merge` las funde en una antes de valuar; `keep` (la 020) las consume en orden.
- CA2 (Costeo 1.1.0, defecto `merge`): un producto FIFO con dos capas que pasa a Promedio sale al promedio de ambas.

**H6 — Costo promedio honesto (configurable).**
- CA1 (motor): la acción `cost` puede escribir en el saldo la cantidad que queda en las capas (`quantityField`).
- CA2 (Costeo 1.1.0, defecto): la existencia muestra la *cantidad costeada*, el costo promedio de **lo costeado** y
  «Costeo incompleto» cuando hay existencia sin capas.

**H7 — Póliza de entrada sin cuentas (configurable).**
- CA1 (Costeo 1.1.0, defecto «bloquear»): confirmar una recepción con costo en un almacén sin cuenta de inventario o de
  entradas por facturar se rechaza con un mensaje que nombra las cuentas.
- CA2 (alternativa «registrar pendiente»): sin la regla `PolizaConCuentas`, la recepción se confirma, se valúa y queda
  marcada *Póliza pendiente*, visible en la vista «Pólizas pendientes».
- CA3: una recepción de costo cero no genera póliza (una póliza en cero no cuadra) y no se bloquea ni se marca.
- CA4 (motor): `setRules` acepta `merge` (agrega o reemplaza por nombre), para que un módulo agregue una política a un
  catálogo de otro sin reescribir sus reglas.

**H8 — Llegan como actualización.** Inventario 1.6.0 y Costeo 1.1.0 se instalan sobre 1.5.0 y 1.0.0 sin reinstalar ni
perder datos, y la vista previa los valida.

## Decisiones (CLARIFY)
Los valores por defecto los aceptó el dueño (2026-10-24). **Dónde se cambia cada opción** (Modelador › catálogo ›
Automatizaciones o Reglas; o un plan / paquete):

| # | Opción | Metadato | Defecto (módulo) | Alternativas |
|---|---|---|---|---|
| 1 | — (invariante) | `Traspaso.MoverMercancia` (Costeo 1.1.0): salida primero con `capture: CostoOrigen`, entrada con `CostoTotal = CostoOrigen`; `Valuar.totalCost = CostoTotal` | — | — |
| 2 | Entrada sin costo y sin capas | `MovimientoInventario` › automatización `Valuar` › acción `cost` › `uncostedEntry` | `fallback` | `average` (cero), `require` |
| 3 | Recepción sin tipo de cambio | `Recepcion` › regla `TipoCambioDeLaRecepcion` (Inventario 1.6.0) | regla activa: se bloquea | quitar la regla: tipo de cambio de la orden (`RecepcionPartida.CostoUnitario` ya lo trae como respaldo) |
| 4 | Fecha del movimiento | `Recepcion` › automatización `EntradaAlmacen` › `set` de `Fecha` | `Recepcion.Fecha` | `TODAY()` (y entonces la póliza `PolizaEntrada` debería usar también `TODAY()`) |
| 5 | Cambio FIFO → Promedio | `Valuar` › `methodChange` | `merge` | `keep` |
| 6 | Costo promedio | `Existencia.CostoPromedio` (fórmula) y `CosteoIncompleto`; `Valuar.quantityField` | promedio de lo costeado + aviso | `Valor / Cantidad` (la 020) |
| 7 | Póliza sin cuentas | `Recepcion` › regla `PolizaConCuentas` (Costeo 1.1.0) | regla activa: se bloquea | quitar la regla: se confirma y queda *Póliza pendiente* (automatización `MarcarPolizaPendiente`, ya instalada) |

- **El motor conserva la 020 cuando la opción se omite** (`uncostedEntry: average`, `methodChange: keep`, sin
  `quantityField`): una automatización existente de otra empresa no cambia de comportamiento por actualizar el motor. Los
  valores recomendados los declaran los módulos (Costeo 1.1.0), así cada uno es visible y editable.
- **Una sola palanca por opción.** Donde la alternativa es «no bloquear» (3 y 7), el módulo instala ya lo que hace la
  alternativa (el respaldo en la fórmula, la automatización que marca pendiente) y la regla lo impide: cambiar la política
  es quitar una regla, sin escribir expresiones. Con la regla activa, lo instalado nunca se ejerce.
- **Traspaso (1) no es opción.** Un traspaso entre almacenes de la misma empresa no cambia el valor del inventario: la
  entrada se valúa con el costo **real** de la salida (capturado al crearla), no con un estimado. Se invierte el orden de
  la 020 (salida primero), lo que además elimina el riesgo residual de dos partidas FIFO del mismo producto.
- **Por qué `capture` y `totalCost` en lugar de un «traspaso» en el motor.** Son capacidades genéricas: `capture` sirve a
  cualquier documento que deba guardar lo que produjo su automatización (p. ej. el folio de la póliza en la recepción);
  `totalCost` valúa una entrada a un total exacto sin pasar por un unitario redondeado (4 decimales × cantidad ≠ total).
- **«Avisar» sin bloquear (7)** requiere reglas de advertencia (severidad), que el motor no tiene: queda pendiente. La
  alternativa «registrar pendiente» deja el aviso persistente (marca y vista). Generar después la póliza pendiente queda
  pendiente; mientras, se captura a mano con la referencia de la recepción.
- **Tipo de cambio (3).** `RATE` ya toma el más reciente **en o antes** de la fecha (spec 010): «el último disponible» a la
  fecha ya es el comportamiento; lo que falta es no caer en silencio al de la orden. El respaldo de la orden se limita a
  moneda base MXN porque `CostoMXN` está en pesos (antes se usaba aunque la base fuera otra).
- **Fecha (4).** Recepción, movimiento, tipo de cambio y póliza comparten la fecha de la recepción. Los movimientos del
  traspaso llevan la fecha del traspaso.
- **Promedio honesto (6).** Las existencias anteriores a Costeo 1.1.0 no tienen *cantidad costeada* hasta su siguiente
  movimiento: se marcan «Costeo incompleto» y sin promedio, en lugar de mostrar uno que podría ser falso.
- **`setRules` con `merge` (H7 CA4).** Como `setAutomations` (020). Si después el dueño del catálogo reemplaza todas sus
  reglas sin `merge`, quita las agregadas: mismo riesgo que con automatizaciones, documentado.
- **Inventario 1.6.0 no toca el traspaso** (lo hace Costeo 1.1.0): así el orden en que se apliquen las dos actualizaciones
  no importa.
