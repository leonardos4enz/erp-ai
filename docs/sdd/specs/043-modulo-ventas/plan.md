# Plan 043 — Diseño técnico

## 1. Modelo de datos
Sin tablas ni columnas nuevas en `Bootstrap`: todo es metadato.

**Motor (H1).** `LineTemplate.From` (`string?`, se omite al serializar si es nulo, así un modelo sin `from` conserva su
huella): campo de partidas del registro que dispara la acción. En `RecordService.Automations` (caso `create`), una
plantilla con `from` recorre las partidas vigentes del origen (`lineScopes[from]`, en su orden de `Id`) y evalúa `when` y
cada `set` en el `DocScope` de esa partida, cuyo padre es el encabezado en memoria (rutas por el campo padre, hasta tres
saltos, 023). `AutomationRules.FromScope` resuelve la partida de origen; la validación rechaza un `from` que no es un campo
de partidas vigente del ámbito y uno en una acción con `lines`, y analiza `when`/`set` en la partida de origen. `Usages`
(explorador) cuenta los campos en ese ámbito; `ModelExplorer` dice «con las partidas del origen». El tope de partidas por
documento (`RecordService.MaxLines` = 500) vale para el documento creado.

**Paquete `modules/ventas.json` 1.0.0** (`requires`: inventario, contabilidad, costeo):
- Producto: `PrecioVenta`, `MonedaPrecio`; reglas con `merge` (`PrecioVentaNoNegativo`, `MonedaPrecioClave`); vista
  «Con precio de venta» con `merge`. Almacén: `CuentaCostoVentas`, `CuentaVentas`, `CuentaClientes`, `CuentaIvaTrasladado`.
- Catálogos por empresa: `Cliente` (saldo calculado), `Cotizacion`/`CotizacionPartida`, `Pedido`/`PedidoPartida`
  (surtido, facturado, por surtir, por facturar, venta surtida y costo surtido en moneda base), `Embarque`/`EmbarquePartida`
  (costo capturado), `Factura`/`FacturaPartida` (importes en moneda base, cobrado, saldo), `Cobro`/`CobroAplicacion`.
- Procesos: cotización (Borrador → Enviada → Aceptada | Rechazada | Vencida; Cancelada), pedido (Borrador → Confirmado →
  Surtido parcial → Surtido → Surtido y facturado; Cancelado; las de surtido y facturado son `system`), embarque, factura
  (Borrador → Emitida → Pagada parcial → Pagada; Cancelada) y cobro (Borrador → Aplicado; Cancelado).
- Automatizaciones:

| Origen · evento | Acciones |
|---|---|
| Cotización · entra a Aceptada | `create` Pedido con `createLines.from = Partidas`; `capture` del folio del pedido |
| Embarque · entra a Confirmado | 1) `create` por partida de `MovimientoInventario` «Salida» con `capture` del costo real (`-CostoTotal`, como el traspaso de la 022); 2) `rollup` de lo surtido y de su costo en la partida del pedido y `transition` del pedido; 3) póliza de costo de ventas aplicada: un cargo por partida (`from`) y el abono a inventario |
| Factura · entra a Emitida | `rollup` de lo facturado y `transition` del pedido a «Surtido y facturado»; póliza de ventas aplicada; `rollup` del saldo del cliente |
| Factura · al editar (lo cobrado) | `rollup` del saldo del cliente (suma de `SaldoBase` de sus facturas emitidas) |
| Cobro · entra a Aplicado | `rollup` de lo cobrado en cada factura y su `transition` (pagada parcial o pagada); póliza de ingreso: cargo al banco y un abono por factura a `Factura.Almacen.CuentaClientes` (`from`, tres saltos) |

- Reglas que el motor vuelve a evaluar en las transiciones y escrituras automáticas llevan `when` por estado (lección de
  esta spec: una regla de partida sin `when` se vuelve a validar al pagar la factura o al surtir, con quien dispara la
  automatización): `DescuentoAutorizado` y el error de crédito solo en borrador o al confirmar; `NoExcederPorFacturar` en
  borrador o al emitir.
- Límite de crédito: `LimiteCredito` (`severity: warning`, en borrador y al confirmar) y `LimiteCreditoGerente` (error al
  confirmar sin el rol); `confirmar` con `onWarning: confirm`.
- Seguridad: `Cliente`, `Cotizacion`, `Pedido`, `Factura` con dueño `Vendedor` (el vendedor, los suyos; gerente,
  facturación, surtidor y el rol de la tarea, todos); costos (`EmbarquePartida.Costo`, `Embarque.CostoTotal`,
  `PedidoPartida.CostoSurtido`) solo para Gerente de ventas, Contador y Supervisor de inventario; el límite de crédito y el
  vendedor asignado los edita el gerente.
- Interfaz: secciones con el tipo de cambio visible solo fuera de la moneda base (cotización, pedido, factura, cobro);
  vistas «Mis clientes», «Con saldo por cobrar», «Por surtir», «Por facturar», «Por cobrar»; reportes «Ventas por mes»,
  «Ventas por cliente», «Venta neta facturada», «Cartera por cliente», «Cartera vencida», «Productos más vendidos»,
  «Margen por producto» (con `roles` de costos: un reporte con un campo oculto no existe para quien no lo ve, 039),
  «Pedidos por surtir», «Cotizaciones por estado»; tablero «Ventas»; PDF de cotización y de factura (emitida); tarea
  «Vencer cotizaciones» (en pausa, rol de servicio); `setApp` Ventas, orden 15.
- Roles nuevos: Vendedor, Gerente de ventas, Facturación y cobranza, Surtidor, Vencimiento de cotizaciones. Datos iniciales:
  cuenta 2160 «IVA trasladado» y el cliente «Público en general».

## 2. Contrato de la API
Sin rutas nuevas ni cambios de contrato: Ventas usa las rutas genéricas de datos, transiciones (`acceptWarnings`, 428),
reportes, tableros, impresión y módulos. `docs/sdd/rutas.md` no cambia (`RouteInventoryTests`).

| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `POST /api/admin/modules/ventas/preview` · `/install` | superusuario | `{ baseHash }` | `200`; `409` si falta Inventario, Contabilidad o Costeo |
| `POST /api/model/plan/preview` · `/apply` | modelo | `createLines[].from` | `400` con el mensaje de H1 CA2 |

## 3. SPA
- `ActionsEditor` › `CreateLines`: «Partida N: repetir por cada partida de» (`Select` con «No: una sola partida» y los campos
  de partidas del registro que dispara la acción, solo si la acción no corre por partida); con `from`, las expresiones de la
  plantilla se ayudan con el ámbito de la partida de origen. `types.ts`: `from?` en la plantilla.
- Sin pantallas nuevas: Ventas usa las genéricas (listado, formulario con partidas, avisos, diálogos de transición, PDF,
  reportes, tableros). Sin rutas nuevas en `App.tsx` (`pantallas.test.ts` no cambia).

## 4. Pruebas
- Unitarias `tests/Erp.Tests/SalesModuleTests.cs` (nuevo): Ventas compila sobre los cuatro módulos y no sin Costeo; no da
  permisos a roles de otros módulos; `from` mal declarado (no es partida, con `lines`, ámbito, `when`); `from` con rutas por
  el padre; sin `from` la plantilla se serializa igual.
- Integración `tests/Erp.IntegrationTests/SalesTests.cs` (nuevo): `from` en un modelo propio (orden, `when`, tres saltos);
  compilador; flujo completo con pólizas y saldos; cobro de dos almacenes; crédito (aviso, error, 428 y aceptación);
  descuento; sin cuentas y sin existencia; 10 embarques y 10 cobros a la vez; aceptar dos veces a la vez; seguridad por
  registro, campo, reporte y empresa; módulo y tarea.
- E2E `web/e2e/ventas.spec.ts` (nuevo): un escenario por criterio con UI; corre después de `compras`, `inventario` y
  `valuacion-contable` (con `--veces=3` se corren juntos).
- Sin captura de referencia nueva: no hay pantalla nueva (las de Ventas son las genéricas, ya cubiertas por `visual*.spec.ts`).

## 5. STRIDE
**Las 8 filas son obligatorias.**

| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas, operaciones, acciones, caminos de módulo | Sin rutas nuevas. Operación de plan: `createLines[].from` (validada en el compilador, igual para modelador, paquetes, entornos, reversión, asistente y Studio, que pasan por `PlanCompiler`). Módulo nuevo instalable solo por superusuario con huella (029). | unit `SalesModuleTests › H1_CA2_un_from_mal_declarado_se_rechaza`; IT `SalesTests › H1_CA2_el_compilador_rechaza_from…`; IT `H6_CA1_el_modulo_va_entre_compras_e_inventario…` |
| Caminos alternos | ¿Otros caminos al mismo efecto? | Surtir, facturar y cobrar solo por la transición de su documento (las de pedido y factura son `system`: nadie las ejecuta a mano). La API, la importación y OData crean documentos en borrador con las mismas reglas; las existencias (`systemManaged`) y las pólizas automáticas (no se cancelan, 021) no se tocan a mano. Un movimiento de inventario «Salida» manual sigue siendo de Inventario y no marca surtido. | IT `H4_CA3_diez_embarques…`; IT `H5_CA4_diez_cobros…`; IT `H3_H4_H5_cotizacion_pedido…` (estados por automatización) |
| Lectura de datos | `RecordFilter`, `FieldAccess`, empresa | Todo por empresa; el vendedor ve lo suyo (dueño `Vendedor`), también en reportes de partidas (por el documento padre, 041); costos por campo; el margen es un reporte con `roles` y, además, no existe para quien no ve el costo (039). Las automatizaciones corren con autoridad del motor (005) y no devuelven datos. | IT `H6_CA2_cada_vendedor_ve_lo_suyo_y_los_costos_solo_quien_los_ve`; E2E `ventas.spec.ts › H6 CA3`; E2E `H4 CA1 · CA2 · CA4` |
| Delegación | ¿Otorgar o heredar? | El paquete solo crea roles nuevos (no fusiona permisos en roles existentes, decisión 9). El aviso de crédito lo acepta el gerente; para los demás es error. Un precio neto menor al 85 % del de lista (descuento o precio bajo, F-A) y el tipo de cambio (F-C) solo los captura el gerente. La factura es del almacén y del vendedor de su pedido y el banco es una cuenta de activo (F-H). La cotización la acepta su vendedor (alcance propio). | unit `H6_CA2_Ventas_no_da_permisos_a_roles_de_otros_modulos`; IT `H2_CA3_sobre_el_limite…`; IT `H3_CA3_un_descuento_mayor_a_15…`; IT `Regresion_FA_…`, `Regresion_FC_…`, `Regresion_FH_…`; la vista previa que calla permisos a roles existentes: [R-043-3](../../riesgos.md#043); el descuento del gerente lo acepta el gerente: [R-043-4](../../riesgos.md#043) |
| Concurrencia | Invariantes | (a) Surtido ≤ pedido: el motor bloquea el pedido (destino de `rollup`) antes de validar `NoExcederPendiente` (012), con `SurtidoNoExcede` de respaldo. (b) Cobrado ≤ total: bloqueo de la factura antes de `NoExcederSaldo`, con `CobradoNoExcede`. (c) Saldo del cliente: se recalcula desde las facturas bajo el bloqueo del cliente. (d) Una cotización se acepta una vez (`rowVersion`). (e) Folios: secuencia del motor. (f) Crédito: los pedidos confirmados por facturar se acumulan en el cliente (F-B) y ese acumulado bloquea al cliente antes de validar la confirmación: dos pedidos a la vez no pasan juntos el límite. | IT `H4_CA3_diez_embarques_del_mismo_pedido_a_la_vez_no_surten_de_mas`; IT `H5_CA4_diez_cobros…_y_el_saldo_cuadra`; IT `H3_CA2_aceptar_la_misma_cotizacion_dos_veces…`; IT `Regresion_FB_los_pedidos_abiertos_cuentan_en_el_credito_tambien_uno_tras_otro_y_a_la_vez`; [R-043-1](../../riesgos.md#043) |
| Fallas parciales | ¿Cae entre dos pasos? | Salida, costo, acumulados, transiciones y pólizas corren en la transacción de la transición (005/020): una falla (sin existencia, sin cuentas, periodo cerrado) revierte todo. Sin trabajo asíncrono (la tarea de vencer es la de la 016). | IT `H4_CA2_sin_cuentas_en_el_almacen_o_sin_existencia_no_se_confirma_el_embarque` (nada queda surtido ni movido) |
| Límites | Tamaño, topes | `from` copia a lo más las partidas del origen (≤ 500, `MaxLines`, que también rechaza un documento creado con más); el embarque y el cobro admiten hasta 499 partidas porque su póliza lleva una fija más (F-F); profundidad de automatizaciones 5 (cobro → factura → cliente: 3; embarque → movimiento → existencia/capas y póliza → saldos: 3); `rollup` en SQL sin tope (014). Reportes con los topes de la 039. | IT `H5_CA3_un_cobro_de_dos_facturas…` (cadena completa); la de la 005 para la profundidad |
| Semántica | Nulos, monedas, fechas, redondeo | Montos en moneda base con `ROUND(…, 2)` por documento; IVA base = total base − subtotal base (la póliza cuadra siempre); una partida de póliza que redondea a cero no se agrega (F-G). Sin tipo de cambio registrado, un mensaje que dice dónde registrarlo (exploratoria F-02). Precio sugerido con `CONVERT` a la fecha del documento y vacío sin tipo de cambio. Saldo del cliente al tipo de cambio de la factura; la diferencia cambiaria del cobro queda fuera. «Cartera vencida» compara con `TODAY()` al correr (no es una fórmula guardada). | IT `H3_H4_H5_cotizacion_pedido…` (pólizas cuadradas al centavo); IT `Regresion_FG_…`; IT `Regresion_exploratoria_F01_F02_…`; [R-043-2](../../riesgos.md#043) |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **E** — el vendedor confirma sobre el límite o aplica un descuento grande | Media | Error por rol en la transición; `HASROLE` en la regla de partida en borrador | IT `H2_CA3_…`; IT `H3_CA3_…`; E2E `H2 CA3 (UI)` |
| **I** — el surtidor o el vendedor ven costos y márgenes | Media | Seguridad por campo; margen con `roles` | IT `H6_CA2_…`; E2E `H4 CA1 · CA2 · CA4`; E2E `H6 CA3`; el costo último de Compras sigue visible: [R-043-5](../../riesgos.md#043) |
| **I** — un vendedor ve clientes y documentos de otro, o de otra empresa | Media | Dueño `Vendedor` y empresa | IT `H6_CA2_…` |
| **T** — surtir o cobrar de más con peticiones simultáneas | Alta | Bloqueo del destino antes de validar y regla de respaldo | IT `H4_CA3_…`; IT `H5_CA4_…` |
| **T** — una póliza que no cuadra o en periodo cerrado | Media | `setLedger` (020) en la misma transacción | IT `H3_H4_H5_…`; las de la 020 |
| **R** — negar una venta o un cobro | Baja | Bitácora de cada transición y escritura automática (con su origen) | la de la 005 |
| **D** — una cotización con 500 partidas que se acepta | Baja | `MaxLines` y una sola transacción | sin prueba propia (la del tope de la 012) |
