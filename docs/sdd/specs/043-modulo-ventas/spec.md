# Spec 043 — Módulo de Ventas

**Estado:** Implementado · **Depende de:** 003 (Compras: productos, monedas, condiciones de pago, almacenes), 005
(automatizaciones y acumulados), 006 (seguridad por campo y registro), 010 (empresa y moneda), 016 (tareas programadas), 020
y 022 (costeo, `capture`, libro de partida doble), 023 (`merge`, avisos, rutas de tres niveles), 024 (`setApp`), 027 (PDF),
039 (reportes y medidas calculadas), 042 (tableros personales) · **Origen:** roadmap punto 13 (antes 042, pendiente desde la
020: «póliza de salidas (costo de ventas)»); decisión del dueño (2026-11-01): la 043 va antes de MCP 2 (044) y de la
auditoría 8.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `modules/` tiene Compras, Inventario, Contabilidad y Costeo; no hay clientes ni documentos de venta. `MovimientoInventario` admite `Tipo = 'Salida'`, pero solo se captura a mano. | La mercancía entra por recepción con su póliza, pero sale sin documento, sin costo de ventas y sin ingreso ni cuenta por cobrar. |
| 2 | La 020 dejó pendiente la «póliza de salidas (costo de ventas)»; Costeo solo genera la póliza de entrada de la recepción. | El costo de lo vendido no llega a contabilidad; la cuenta 5100 del catálogo base nunca se mueve. |
| 3 | `LineTemplate` (spec 020) agrega **una** partida fija por plantilla al documento que crea una acción `create`; no puede repetirse por cada partida del documento de origen (`RecordService.Automations.cs`, caso `Create`). | Un documento no se puede «convertir» en otro con sus partidas (cotización → pedido), y una póliza no puede llevar una partida por cada renglón del origen (un cobro que aplica varias facturas de almacenes distintos). |
| 4 | El selector de módulos (024) deja el orden 15 libre entre Compras (10) e Inventario (20). | — |

## Principio
Ventas es un **paquete de módulo con metadatos** (`modules/ventas.json`), como Compras, Inventario, Contabilidad y Costeo,
sin código por módulo. Lo único que falta es una capacidad **genérica del motor**: en una acción `create`, una plantilla de
partida con `from` se repite por cada partida de ese campo del registro que dispara la acción, evaluada en el ámbito de
esa partida (con su documento como padre). La usan Ventas (cotización → pedido; póliza de cobro con una partida por
factura) y cualquier módulo futuro; el modelador la ofrece sin JSON (026).

El resto se ejerce con lo que ya existe: `merge` para extender Producto y Almacén y agregar automatizaciones a catálogos
de otros módulos, reglas de aviso con `onWarning` (límite de crédito), rutas de tres saltos (la cuenta de clientes del
almacén de la factura que aplica un cobro), `create` + `capture` para la salida al costo real (como el traspaso de la 022),
`setLedger` para que toda póliza cuadre y caiga en periodo abierto, acumulados que se recalculan desde la fuente, `setApp`,
reportes de la 039 y tableros del modelo y personales.

## Historias

**H1 — Partidas que se copian del origen (motor).** Como *modelador*, quiero que una acción `create` agregue al documento
nuevo una partida por cada partida del documento que la dispara, para convertir un documento en otro sin capturarlo de
nuevo.
- CA1: una plantilla de `createLines` con `from: "<campo de partidas del origen>"` se repite por cada partida vigente del
  origen, en su orden; `when` y cada `set` se evalúan en el ámbito de esa partida (su documento se lee por su campo padre,
  también a dos y tres saltos). Sin `from`, la plantilla agrega una partida como en la 020.
- CA2: el compilador rechaza un `from` que no es un campo de partidas del ámbito, uno en una acción que ya corre por
  partida (`lines`), y expresiones que nombran campos que no existen en la partida de origen; el mensaje dice cuál.
- CA3: el modelador ofrece «Repetir por cada partida de» en la plantilla, sin JSON; el explorador del modelo cuenta los
  campos que usa la plantilla (en la partida de origen) para «¿quién usa este campo?».
- CA4: un paquete o plan que no usa `from` se serializa igual que antes (la huella de un modelo instalado no cambia).

**H2 — Clientes, precios y crédito.** Como *vendedor*, quiero un padrón de clientes con su límite de crédito y saldo, y
productos con precio de venta, para cotizar y vender sin calcular a mano.
- CA1: Ventas agrega Clientes (por empresa) con RFC, razón social, condición de pago, moneda, límite de crédito, vendedor
  asignado y saldo por cobrar calculado por el sistema; el RFC y el correo se validan como en Proveedor.
- CA2: Producto gana precio de venta y su moneda (con `merge` sobre sus reglas: el precio no es negativo); las partidas de
  venta sugieren el precio convertido a la moneda del documento a su fecha, y sin tipo de cambio no sugieren nada.
- CA3: un pedido cuyo total en moneda base más el saldo del cliente supera su límite muestra el aviso en vivo; al
  confirmarlo, quien tiene «Gerente de ventas» lo confirma aceptando el aviso y cualquier otro rol recibe el error con el
  motivo. Un cliente sin límite no avisa.
- CA4: el saldo del cliente es la suma de los saldos de sus facturas emitidas, siempre recalculado desde las facturas (10
  facturas y cobros a la vez lo dejan cuadrado).

**H3 — De la cotización al pedido.** Como *vendedor*, quiero cotizar y convertir la cotización aceptada en pedido, para
no capturar dos veces.
- CA1: la cotización lleva cliente, almacén, moneda y tipo de cambio, vigencia y partidas con precio, descuento e IVA;
  se envía, se acepta, se rechaza o vence (tarea programada «Vencer cotizaciones», en pausa al instalar); se descarga en PDF.
- CA2: aceptar una cotización crea su pedido en borrador con las mismas partidas, cantidades y precios (H1), y la
  cotización guarda el folio del pedido; aceptarla dos veces a la vez crea un solo pedido.
- CA3: un descuento mayor a 15 % solo lo captura el gerente de ventas (mensaje claro para el vendedor).

**H4 — Surtido con costo de ventas.** Como *surtidor*, quiero registrar la salida de almacén de un pedido, para que la
existencia baje, el pedido avance y el costo de ventas llegue a contabilidad.
- CA1: el embarque se liga al pedido y a sus partidas (sugiere lo pendiente); confirmarlo crea una salida de inventario
  por partida, guarda el costo real de cada una (promedio o FIFO, `capture`) y mueve el pedido a «Surtido parcial» o
  «Surtido».
- CA2: confirmar genera la póliza de costo de ventas aplicada (cargo a costo de ventas, abono a inventario, ambos del
  almacén) por el costo total; sin las cuentas en el almacén no se confirma, con el mensaje de qué configurar; sin
  existencia suficiente tampoco.
- CA3: nadie surte más de lo pedido, tampoco con 10 embarques del mismo pedido confirmados a la vez.
- CA4: el surtidor no ve costos; el gerente de ventas, el contador y el supervisor de inventario sí.

**H5 — Factura y cobranza.** Como *responsable de facturación y cobranza*, quiero facturar lo surtido y aplicar los
cobros, para que el saldo del cliente y la contabilidad cuadren.
- CA1: la factura se liga al pedido y a sus partidas (sugiere lo surtido por facturar), calcula vencimiento por la
  condición de pago, subtotal, descuento, IVA y total (y en moneda base); se descarga en PDF. No se factura más de lo
  surtido.
- CA2: emitirla genera la póliza de ventas aplicada (cargo a clientes por el total, abono a ventas por el subtotal neto y
  a IVA trasladado, en moneda base y cuadrada) y suma al saldo del cliente; el pedido surtido y facturado por completo se
  cierra solo.
- CA3: el cobro (cuenta de banco, moneda y tipo de cambio) aplica importes a una o varias facturas del cliente en su
  moneda; aplicarlo baja el saldo de cada factura (que pasa a «Pagada parcial» o «Pagada»), el del cliente, y genera la
  póliza de ingreso con un abono por factura a la cuenta de clientes de **su** almacén (H1, ruta de tres saltos).
- CA4: nadie cobra más del saldo de una factura, tampoco con 10 cobros a la vez.

**H6 — Módulo, seguridad, reportes y tableros.** Como *administrador*, quiero instalar Ventas como un módulo más, con
roles y seguridad listos, reportes y un tablero, para operarlo sin modelar.
- CA1: Ventas requiere Inventario, Contabilidad y Costeo; se instala desde Configuración › Módulos con su vista previa y
  aparece en el selector de módulos entre Compras e Inventario (orden 15).
- CA2: roles nuevos (Vendedor, Gerente de ventas, Facturación y cobranza, Surtidor, servicio «Vencimiento de
  cotizaciones»); no cambia los permisos de roles que ya existen. El vendedor ve solo sus clientes, cotizaciones, pedidos
  y facturas; el gerente, todos; nadie fuera de la empresa los ve.
- CA3: reportes de ventas por mes, por cliente, productos más vendidos con margen (medida calculada de la 039, solo para
  quien ve costos), cartera por cliente y cartera vencida; tablero «Ventas»; un vendedor arma su tablero personal con
  ellos (042) y ve solo lo suyo.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (6: no) · más de ~2 500 líneas de
   producto (≈1 300: el paquete JSON ≈ 900 líneas, motor ≈ 150, modelador ≈ 60: no) · seguridad o concurrencia junto con
   UI nueva (concurrencia sí, pero sin pantalla nueva: las pantallas son las genéricas del motor y el modelador gana un
   campo: no) · más de una pantalla principal (ninguna nueva: no). → **No se divide.** MCP 2 sigue en la 044.
2. **Clientes por empresa** (Proveedor y Producto se comparten): el saldo de un cliente suma facturas en la moneda base de
   **una** empresa; con clientes compartidos mezclaría monedas base. Un cliente de dos empresas se da de alta en cada una.
3. **Cuentas contables en el almacén**, como Costeo (cuenta de inventario y de entradas): costo de ventas, ventas,
   clientes e IVA trasladado. El almacén ya es por empresa y funciona como sucursal; una configuración aparte sería otro
   catálogo de una sola fila. El cobro lleva su cuenta de banco.
4. **Sin cancelar lo emitido.** Un embarque confirmado y una factura emitida no se cancelan (ya movieron inventario,
   saldos y pólizas automáticas, que la 021 no deja cancelar a mano). Devoluciones, notas de crédito y la reversa de
   pólizas son specs siguientes (roadmap 17). Se cancelan en borrador.
5. **Se factura lo surtido.** Una partida de factura no excede lo surtido menos lo ya facturado de su partida del pedido
   (sin anticipos). Así el pedido se cierra solo con la última factura.
6. **Moneda del cobro = moneda de la factura.** El cobro aplica en la moneda de las facturas (regla), con su tipo de
   cambio; la póliza va en moneda base al tipo de cambio de cada documento. La diferencia cambiaria entre factura y cobro
   queda fuera (roadmap 15, pólizas en moneda extranjera): el saldo del cliente en moneda base usa el tipo de cambio de
   la factura (R-043-2).
7. **Límite de crédito:** aviso (`severity: warning`, `onWarning: confirm`) para el gerente y error para los demás roles
   al confirmar, con el mismo cálculo (saldo del cliente + total del pedido en moneda base). Los pedidos confirmados sin
   facturar no cuentan en el saldo (R-043-1).
8. **Liga cotización → pedido por folio.** El pedido creado guarda el folio de la cotización y la cotización el del pedido
   (`capture`), como texto: el lenguaje de expresiones no expone el id del registro y agregarlo es otra capacidad
   (pendiente en el roadmap si se pide).
9. **Roles nuevos sin tocar los existentes.** El instalador de módulos (008) fusiona los permisos de un rol que ya existe
   sin decirlo en la vista previa; Ventas no da permisos a Almacenista ni a Contador: crea Surtidor y da lectura de
   costos por campo a los roles que ya los veían (Supervisor de inventario, Contador). Que la vista previa liste los
   permisos que un paquete agrega a roles existentes va al roadmap.
10. **IVA trasladado al emitir** (no por cobrar): base devengada; el IVA efectivamente cobrado queda para una spec fiscal.
11. **Requiere Costeo** (y por él Inventario y Contabilidad): sin costo no hay costo de ventas.

## Fuera de alcance (anotado en el roadmap)
- Devoluciones de venta, notas de crédito, anticipos y cancelación de lo emitido (con la reversa de pólizas, punto 17).
- Diferencias cambiarias entre factura y cobro (con pólizas en moneda extranjera, punto 15).
- Listas de precios por cliente, comisiones, existencia comprometida por pedidos, CFDI.
- Que la vista previa de un módulo diga qué permisos agrega a roles existentes (decisión 9).
- Id del registro en expresiones (decisión 8).
