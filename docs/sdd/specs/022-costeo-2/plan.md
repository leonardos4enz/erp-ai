# Plan 022 — Diseño técnico

## 1. Motor: opciones de la acción `cost` (`CostMath`, `RecordService.Costing.cs`)
```ts
AutomationAction += {                       // type: 'cost'
  uncostedEntry?: 'average' | 'fallback' | 'require';  // entrada sin costo en llave sin capas; omitido = 'average' (020)
  methodChange?: 'merge' | 'keep';                     // promedio con varias capas abiertas; omitido = 'keep' (020)
  totalCost?: string;     // expresión: costo total exacto de una entrada (gana sobre unitCost); nulo = no aplica
  quantityField?: string; // campo numérico del saldo que recibe la cantidad que queda en las capas
}
```
`CostMath.Apply(open, method, q, unitCost, fallback, scale, CostOptions? options)` con
`CostOptions(UncostedEntry = "average", MergeAverage = false, TotalCost = null)`; el resto de la firma no cambia.
- **Entrada (`q > 0`):** `totalCost` no nulo → `costo = round(totalCost, escala)` (negativo → 400). Si no, `unitCost`
  no nulo → `round(q × unitCost)`. Si no, con capas abiertas → promedio vigente (020). Sin capas: `average` → 0;
  `fallback` → `round(q × respaldo)` si el respaldo es mayor que cero (un respaldo en cero no es un costo), si no 400; `require` → 400. Mensaje: «La entrada no
  tiene costo unitario y no hay existencias valuadas de las que tomar el promedio[ ni costo de respaldo]: captura el costo
  unitario.»
- **`merge`:** si el método es promedio y hay más de una capa abierta, antes de valuar (entrada o salida) las abiertas
  quedan en 0 y se agrega una capa nueva con la suma. Una entrada promedio ya fundía; el cambio es para la salida.
- `CostResult.RemainingQuantity = Σ cantidad de las capas`. Con `quantityField`, el saldo recibe valor y cantidad en la
  misma escritura del motor.
- **Validación** (`AutomationRules.ValidateCost`): `uncostedEntry`/`methodChange` en su lista; `totalCost` en el ámbito
  del movimiento; `quantityField` numérico capturable del saldo y distinto de `field`.

## 2. Motor: `capture` en la acción `create`
```ts
AutomationAction += { capture?: { field: string; value: string }[] }   // type: 'create'
```
Después de crear el registro (y de su `transition`, si la hay) se relee el registro nuevo y cada `value` se evalúa **en su
ámbito**; el resultado se asigna a `field` del registro que disparó la acción (el encabezado o, con `lines`, la partida)
con `SystemUpdateAsync` (fórmulas, reglas del registro, libro, auditoría `automation.update` y «al editar»). La acción
siguiente relee el origen (005), así que ya ve lo capturado.
- **Validación:** solo en `create`; 1 a 20 asignaciones; `field` asignable en el ámbito de la acción (no calculado ni
  partidas); `value` se analiza en el ámbito del catálogo creado.
- **Ciclos:** `capture` agrega la arista (catálogo de origen, «al editar»).

## 3. Motor: `setRules` con `merge`
```ts
Operation 'setRules' += { merge?: boolean }   // agrega o reemplaza por nombre; las demás reglas se conservan
```
`PlanCompiler.SetRules`: con `merge`, la lista resultante es la actual sin los nombres del plan más las del plan; el tope
de 50 reglas se revisa sobre el resultado. El diff de entornos sigue emitiendo la lista completa.

## 4. Módulos
**Inventario 1.6.0** (`upgrades`):
- `updateField RecepcionPartida.CostoUnitario`: `ROUND(COALESCE(CONVERT(precio neto, Recepcion.Moneda, BASECURRENCY(),
  Recepcion.Fecha), IF(BASECURRENCY() = 'MXN', PartidaOrden.CostoMXN, NULL)), 4)`.
- `setRules merge Recepcion`: `TipoCambioDeLaRecepcion` (cuando `Estado = 'Confirmada'`, `NOT ISBLANK(RATE(COALESCE(Moneda,
  OrdenCompra.MonedaClave), BASECURRENCY(), Fecha))`, campo `Fecha`).
- `setRules merge RecepcionPartida`: `CostoCalculado` (cuando `Recepcion.Estado = 'Confirmada'`, `NOT ISBLANK(CostoUnitario)`).
- `setAutomations merge Recepcion` `EntradaAlmacen`: igual que 1.5.0 más `Fecha = Recepcion.Fecha`.

**Costeo 1.1.0** (`upgrades`):
- `Existencia.CantidadCosteada` (decimal, no editable); `CostoPromedio = IF(COALESCE(CantidadCosteada, 0) > 0,
  ROUND(Valor / CantidadCosteada, 4), NULL)`; `CosteoIncompleto = Cantidad > COALESCE(CantidadCosteada, 0)`.
- `TraspasoPartida.CostoOrigen` (decimal 2, no editable, oculto en lista).
- `Valuar` (merge): `uncostedEntry: 'fallback'`, `methodChange: 'merge'`, `totalCost: 'CostoTotal'`,
  `quantityField: 'CantidadCosteada'`.
- `MoverMercancia` (merge): salida primero (`Fecha = Traspaso.Fecha`, `capture: [{ CostoOrigen ← -CostoTotal }]`); entrada
  después (`Fecha = Traspaso.Fecha`, `CostoTotal = CostoOrigen`).
- `Recepcion.PolizaPendiente` (booleano, no editable); regla `PolizaConCuentas` (merge; cuando `Estado = 'Confirmada' AND
  CostoTotal > 0`); automatización `MarcarPolizaPendiente` (al entrar a Confirmada, sin cuentas y con costo: `update`
  propio `PolizaPendiente = true`); vista `PolizasPendientes`.

## Contrato de API (cambios)
```ts
AutomationAction += { uncostedEntry?, methodChange?, totalCost?, quantityField?, capture? }
Operation setRules += { merge? }
```
Sin endpoints nuevos. Errores: 400 `ValidationFailed` (reglas de los módulos) o `AutomationFailed` («No se pudo completar
«Valuar el movimiento»: La entrada no tiene costo…»). Asistente y tipos de la SPA conocen las propiedades nuevas.

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **T** Un traspaso que crea o destruye valor (inventario inflado sin compra) | La entrada se valúa con el costo real capturado de la salida (`totalCost` exacto, sin unitario redondeado); salida primero, así dos partidas no leen las mismas capas |
| **T** Fijar el costo de una entrada con `CostoTotal` desde la API | `CostoTotal` no es editable por personas (el servidor ignora el valor que mande el cliente); solo lo asigna una automatización declarada por un modelador |
| **T** Inventario valuado a cero sin aviso | `uncostedEntry: fallback` / `require`; una entrada sin costo resultante se rechaza en lugar de entrar a 0 |
| **T** Póliza y movimiento que no cuadran (costo nulo, tipo de cambio de otra moneda) | Regla invariante `CostoCalculado`; respaldo de la orden solo con base MXN; regla `TipoCambioDeLaRecepcion` por defecto |
| **T** `capture` que escribe donde no debe | Solo campos asignables del registro que disparó la acción (validado en la vista previa); pasa por reglas, libro, auditoría y «al editar» como cualquier escritura del motor |
| **R** No saber por qué una recepción no tiene póliza | Regla que bloquea con mensaje (defecto) o marca *Póliza pendiente* con vista (alternativa); el cambio de política es un plan auditado |
| **E** Cambiar la política para ocultar algo | Cambiar reglas o automatizaciones exige el permiso de modelo, queda en la auditoría y, con aprobación (009), pasa por otro administrador |
| **D** Ciclos por `capture` | `capture` agrega la arista «al editar» del origen a la detección de ciclos y respeta el límite de profundidad (005) |
| **I** Ver costos por `capture` | Lo capturado es un campo más con la seguridad por campo y registro de la 006 |
