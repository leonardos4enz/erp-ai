# Plan 020 — Diseño técnico

## 1. Libro contable (`setLedger`) — `meta.Entity.LedgerJson`
```ts
type LedgerDef = {
  lines: string;            // campo de partidas del documento
  debit: string;            // campo numérico de la partida (cargo)
  credit: string;           // campo numérico de la partida (abono)
  date?: string;            // campo fecha del encabezado (obligatorio si hay period)
  states?: string[];        // estados contabilizados; omitido = todos (o catálogo sin proceso)
  period?: {
    entity: string;         // catálogo de periodos (raíz, distinto del libro)
    start: string;          // campo fecha de inicio
    end: string;            // campo fecha de fin (inclusiva)
    open: string;           // condición sobre el periodo, p. ej. "Estado = 'Abierto'"
  };
};
Operation += { op: 'setLedger'; entity: string; ledger: LedgerDef | null }   // null lo quita
EntityDef += { ledger?: LedgerDef }
```
### Validación (`LedgerRules`, pura; pasada final de **todos** los libros, como seguridad)
Catálogo raíz; `lines` es un campo de partidas vigente; `debit`/`credit` numéricos de la partida y distintos;
`date` es fecha del encabezado (obligatorio con `period`); `states` existen en el proceso (sin proceso no se permiten);
`period.entity` existe, es raíz y no es el libro; `start`/`end` son fechas; `open` se analiza en el ámbito del periodo.
Un libro compartido no puede usar periodos por empresa.

### Ejecución (`RecordService.Ledger.cs`)
`CheckLedgerAsync(cn, tx, e, model, draft, company, before?)` después de `Validate` en: alta (`InsertDraftAsync`, también
importación, módulos y automatizaciones), edición (`UpdateAsync`), transición (`ApplyTransitionAsync`, de personas o del
motor), escritura del motor (`SystemUpdateAsync`) y baja (`DeleteAsync`, solo «antes»).
- `posted(s) = states is null || states ∋ s`.
- **Cuadre** si `posted(después)`: ≥ 2 partidas; por partida `cargo ≥ 0`, `abono ≥ 0`, exactamente uno > 0; `Σcargo = Σabono`.
  Errores 400 (`ValidationFailed`): `Partidas[i].Cargo` y `registro`: «No cuadra: cargos 1,000.00 y abonos 900.00
  (diferencia 100.00). La partida doble exige que sean iguales.»
- **Periodo** por cada par (estado, fecha) de antes y después con `posted`: `SELECT … FROM app.Periodo WITH (HOLDLOCK)
  WHERE Inicio <= @d AND Fin >= @d AND IsDeleted = 0 [AND CompanyId = @co]`. 0 filas → «No hay un periodo contable que
  incluya el 24/10/2026…»; > 1 → traslape; `open` falso → «El periodo «Octubre 2026» está cerrado: …». Error en el campo
  `date`. `HOLDLOCK` deja el rango con bloqueo compartido hasta el commit: la transición que cierra el periodo (un `UPDATE`
  de esa fila) espera, y la póliza siguiente lo lee cerrado (H1 CA6).
- **Traslape**: al guardar un registro del catálogo de periodos de algún libro, otro vigente de la misma empresa con
  `inicio <= fin' AND fin >= inicio'` → 400 en `start`.

## 2. Capas de costo (acción `cost`) — `meta.CostLayer`
```ts
AutomationAction += {
  // type: 'cost'
  entity: string;               // catálogo de saldo (p. ej. Existencia): define la llave y recibe el valor
  match: { field; source }[];   // llave: campos del saldo = campos del movimiento
  field: string;                // campo numérico del saldo que recibe el valor restante de las capas
  quantity: string;             // campo numérico del movimiento, con signo (+ entra, − sale)
  method: string;               // expresión → 'average' | 'fifo'
  unitCost?: string;            // expresión: costo unitario de una entrada
  fallbackCost?: string;        // expresión: costo de la parte de una salida que no cubren las capas
  costField: string;            // campo del movimiento que recibe el costo total con signo
  unitCostField?: string;       // campo del movimiento que recibe el costo unitario efectivo
}
```
```sql
meta.CostLayer (Id bigint identity PK, Book nvarchar(128), KeyHash char(64), CompanyId int NULL,
  SourceEntity nvarchar(128), SourceId bigint, Quantity decimal(28,8), Value decimal(28,8), CreatedAt datetime2)
INDEX IX_CostLayer_Open (Book, KeyHash, Id) INCLUDE (Quantity, Value) WHERE Quantity > 0
```
`KeyHash` = SHA-256 de `RollupLockKey(saldo, valores)` + empresa (si el saldo es por empresa). Algoritmo puro en
`CostMath.Apply(capas, método, q, unitario, respaldo, escala)`:
- `q > 0`: costo = `round(q × unitario, escala)`; sin unitario, el promedio vigente (Σvalor/Σcantidad) o 0. Negativo → error.
  FIFO: capa nueva. Promedio: las capas abiertas quedan en 0 y se inserta una con la suma.
- `q < 0`: consume en orden de `Id`: `toma = min(falta, Q)`, `v = toma = Q ? V : round(V × toma / Q, escala)`. Lo que no
  cubren las capas: `round(falta × respaldo, escala)` (respaldo nulo = 0); no hay capas negativas.
- Escribe en el movimiento `costField` (± costo) y `unitCostField` (|costo| / |q|, a su escala) con `SystemUpdateAsync`
  (sin disparar más que «al editar»), y en el saldo (`entity` + `match`, misma empresa) `field = Σ valor abierto`.
  Si el saldo no existe, solo se omite ese paso.
- Bloqueo por llave: el mismo `sp_getapplock` que el acumulado por coincidencia (y se toma antes de validar, como en 014).
- Validación: sin `lines`; `entity` raíz; `match` como en `rollup`; `field`/`costField`/`unitCostField` numéricos no
  calculados; `quantity` numérico; `method` obligatorio (una literal debe ser `'average'` o `'fifo'`); expresiones en el
  ámbito del movimiento. Efectos para ciclos: «al editar» del propio catálogo y del saldo.

### `UNITCOST(catálogo, cantidad, llave1, …, llave5)`
Costo unitario de las siguientes `cantidad` unidades que saldrían (mismo orden y redondeo a 2 decimales por capa que
`cost`), en la empresa del contexto; nulo sin capas o con cantidad ≤ 0. El primer argumento debe ser texto literal con un
catálogo existente. Implementado por `RelatedLoader` (`ICostLayers`), como `RATE`.

## 3. `create` con partidas y transición
```ts
AutomationAction += {
  createLines?: { field: string; when?: string; set: { field; value }[] }[];   // type: 'create'
  transition?: string;   // create: transición a ejecutar sobre el registro nuevo (si está en su estado de origen)
}
```
Las partidas se agregan al borrador antes de `InsertDraftAsync` (valida obligatorios, reglas y libro); luego
`SystemTransitionAsync` con «Automática: …». Validación: `field` es partidas del destino; campos asignables; obligatorios
de la partida sin defecto asignados; ≤ 20 plantillas; transición existente. Efectos: `create` y, con transición,
`transition`/`enterState` del destino.

## 4. Módulos
**Inventario 1.5.0** (`upgrades`): `OrdenCompra.MonedaClave` (`Moneda.Clave`); `Recepcion.Moneda` (texto, sistema, por
defecto `OrdenCompra.MonedaClave`); `RecepcionPartida.CostoUnitario` =
`COALESCE(CONVERT(PartidaOrden.PrecioUnitario * (1 - PartidaOrden.DescuentoPct / 100), Recepcion.Moneda, BASECURRENCY(), Recepcion.Fecha), PartidaOrden.CostoMXN)`
redondeado a 4, `Costo = ROUND(Cantidad * CostoUnitario, 2)`, `Recepcion.CostoTotal = SUM(Partidas.Costo)`;
`MovimientoInventario.CostoUnitario` (capturable) y su regla; `EntradaAlmacen` y `MoverMercancia` (merge por nombre) pasan
el costo (el traspaso, con `UNITCOST('Existencia', Cantidad, Traspaso.AlmacenOrigen, Producto)` y la entrada primero).

**Contabilidad 1.0.0**: `CuentaContable` (por empresa; Codigo único, Nombre, Tipo, Naturaleza, Activa, Cargos, Abonos,
Saldo, Descripcion para mostrar), `PeriodoContable` (por empresa; Nombre, Inicio, Fin; *Abierto/Cerrado*, `cerrar`
Contador, `reabrir` Administrador con comentario; tarea `CerrarVencidos` mensual en pausa con rol *Cierre contable*),
`Poliza` + `PolizaPartida` (por empresa; libro con `states: ['Aplicada']` y periodo; *Borrador → Aplicada*, *Cancelada*
con comentario; automatizaciones `ActualizarSaldos` al entrar a Aplicada y a Cancelada: `rollup` por partida de
`Cargos`/`Abonos` con filtro `Poliza.Estado = 'Aplicada'`). Reporte *Balanza de comprobación*. Seed de 9 cuentas.

**Costeo 1.0.0** (requiere inventario y contabilidad): `Producto.MetodoCosteo` (Promedio/FIFO);
`MovimientoInventario.CostoTotal`; `Existencia.Valor` y `CostoPromedio`; `Almacen.CuentaInventario` y `CuentaEntradas`;
automatización `Valuar` (merge en `MovimientoInventario`, al crear, acción `cost`); `PolizaEntrada` (merge en `Recepcion`,
al entrar a Confirmada, con cuentas y costo > 0): `create` de `Poliza` con dos partidas y `transition: 'aplicar'`.

## Contrato de API (cambios)
```ts
EntityDef += { ledger?: LedgerDef }
Operation += { op: 'setLedger'; entity: string; ledger: LedgerDef | null }
AutomationAction += { quantity?, method?, unitCost?, fallbackCost?, costField?, unitCostField?, createLines? }  // type 'cost' nuevo
Expresiones += UNITCOST(catálogo, cantidad, llave…)
```
Sin endpoints nuevos: libro, capas y pólizas automáticas son efectos del servidor sobre la API de datos existente.
Errores: 400 `ValidationFailed` (cuadre, periodo, traslape) o `AutomationFailed` dentro de una automatización.
Modelador: JSON del libro en la sección avanzada; `ModelDiff` y el asistente conocen `setLedger` y `cost`.

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **T** Póliza descuadrada por una vía que no pasa por la UI (API, importación, automatización) | El cuadre está en el motor, en el núcleo común de alta, edición, transición y escritura del motor; no es una regla editable |
| **T** Registrar o cambiar pólizas de un periodo cerrado | Periodo revisado con el estado y la fecha de antes y de después, también al cancelar y eliminar; el cierre es una transición con rol |
| **T** Cerrar un periodo mientras se aplica una póliza (carrera) | Lectura del periodo con `HOLDLOCK` hasta el commit: el `UPDATE` del cierre espera y la siguiente póliza lo ve cerrado |
| **T** Capas cruzadas por concurrencia (dos salidas a la vez del mismo producto) | `sp_getapplock` por llave antes de validar y al valuar; capas leídas con `UPDLOCK`; recálculo del saldo desde las capas |
| **T** Valor de inventario y costos de movimientos que se separan por redondeo | Cada consumo se redondea una vez y se descuenta exacto de la capa; el saldo se recalcula de las capas, no se incrementa |
| **I** Ver costos sin permiso | Las capas no tienen API; costo y valor son campos con la seguridad por campo y registro de la 006. `UNITCOST` lo escriben modeladores y respeta la empresa del contexto |
| **I** Capas de otra empresa | La llave incluye la empresa; `UNITCOST` usa la empresa del contexto |
| **I/T** Inyección por el libro o por `UNITCOST` | Identificadores de metadatos validados y citados; valores como parámetros; `UNITCOST` exige un catálogo existente como literal |
| **E** Escalar privilegios con la póliza automática | La declara un modelador (plan auditado y con aprobación en producción); el evento exige el permiso normal; el motor no omite cuadre, periodo ni la condición de `aplicar` |
| **E** Reabrir un periodo sin autorización | `reabrir` solo para *Administrador* y con comentario en el historial |
| **R** No saber de dónde vino una póliza | Origen «Automática», referencia al folio, historial «Automática: …» y auditoría `automation.create` a nombre de quien confirmó |
| **D** Muchas capas por llave | Índice filtrado de capas abiertas; promedio mantiene una sola capa; FIFO consume en orden y deja de leer al cubrir |
