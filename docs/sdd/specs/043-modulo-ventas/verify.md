# Verify 043 — Módulo de Ventas

## Métricas
- inicio: 2026-11-01 10:40
- cierre: 2026-11-01 12:50
- unitarias: 1077 (+29: 752 de Erp.Tests, +9; 252 de integración, +20; Vitest 73, +0)
- e2e: 463 (+9: `ventas.spec.ts`)
- corridas completas: 2 (E2E 15 min 0 s con una falla —los catálogos de prueba `Cliente`, `Cotizacion` y `Pedido` de otros archivos impedían instalar Ventas—; final 463 en verde, 12 min 47 s); dotnet test 1 min 50 s; la siguiente es CI
- corridas parciales: 12 (iteración de `ventas.spec.ts` con sus dependencias 8; integración de `SalesTests` 4); `--veces=3` 2
- contra el código anterior: 2 (integración: las 7 regresiones de la revisión adversarial con el paquete anterior; las 2 de la exploratoria con el paquete anterior)
- hallazgos: 0/5/5 (adversarial F-A…F-H; exploratoria F-01, F-02)
- categorías: seguridad=4, motor-api=3, UI/UX=2, pruebas=1
- revisión adversarial: 8 hallazgos
- riesgos: R-043-1, R-043-2, R-043-3, R-043-4, R-043-5, R-043-6

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0. La última corrida de CI de `main` con código (`641d333`) estaba en **success**; la
de `53b355d` (solo roadmap) estaba en curso.

## 1. Pruebas unitarias e integración
`dotnet test`: **752 unitarias** (743 + 9 en `tests/Erp.Tests/SalesModuleTests.cs`) y **252 de integración** (232 + 20 en
`tests/Erp.IntegrationTests/SalesTests.cs`), en verde (1 min 50 s). Vitest: 73.
- Unitarias: Ventas compila sobre Compras, Inventario, Contabilidad y Costeo (todo por empresa, reglas del producto con
  `merge`), no compila sin Costeo, no da permisos a roles de otros módulos; `from` mal declarado (no es partida, con `lines`,
  campo del encabezado en la partida, `when`), `from` con rutas por el padre, y sin `from` la plantilla se serializa igual.
- Integración (SQL real): `from` en un modelo propio (orden, `when`, ruta de tres saltos desde la partida); compilador;
  flujo completo cotización → pedido → dos embarques (promedio y FIFO) → factura → dos cobros con sus tres pólizas cuadradas
  al centavo y el saldo del cliente; cobro de dos facturas de almacenes distintos (un abono por cuenta de clientes); crédito
  (aviso en `_warnings`, error del vendedor, 428 y aceptación del gerente; sin límite no avisa); descuento; sin cuentas y sin
  existencia; **10 embarques a la vez** del mismo pedido (dos 200, ocho 400, surtido ≤ pedido, existencia y costo exactos);
  **10 cobros a la vez** de la misma factura (dos 200, cobrado ≤ total, saldo del cliente cuadrado); aceptar la misma
  cotización dos veces a la vez (un pedido); seguridad por registro (vendedor), campo (costos), reporte (margen) y empresa;
  módulo en el orden 15 y la tarea; regresiones F-A, F-B (también tres pedidos a la vez contra el límite), F-C, F-E, F-F,
  F-G, F-H, F-01 y F-02.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa | 454 en verde, 1 falla (`ventas › H6 CA1`: «Ya existe un catálogo llamado 'Cliente'», de `h5-h6-seguridad`; también `Cotizacion` de `seguridad` y `Pedido` de `interfaz`) y 8 sin correr | 15 min 0 s |
| 2 | `--veces=3` sobre `compras`, `h5-h6-seguridad`, `interfaz`, `inventario`, `seguridad`, `valuacion-contable`, `ventas` (lento) | vuelta 1: 4 fallas de renombres incompletos y del rol «Gerente de ventas» que ya crea `seguridad` | 2 min |
| 3 | La misma, corregida | 3 × 95 en verde | 6 min 51 s |
| 4 | Completa final | 463 en verde | 12 min 47 s |

Archivo nuevo: `ventas.spec.ts` (9 pruebas: una por criterio con UI, más preparar; corre después de `valuacion-contable`).
Modificados: `h5-h6-seguridad.spec.ts` (`Cliente` → `UiCliente`), `seguridad.spec.ts` (`Cotizacion` → `SegCotizacion`) e
`interfaz.spec.ts` (`Pedido` → `IfzPedido`): sus catálogos de prueba ocupaban los nombres de Ventas en la base compartida; se
conservan las etiquetas. Sin capturas nuevas: Ventas no agrega pantallas (usa las genéricas) y ninguna referencia cambió.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff (`git diff` y los archivos nuevos) y la tabla STRIDE del plan como
lista de amenazas, sin sus conclusiones. Revisó el motor (`create`/`from`, `SystemUpdate`, `SystemTransition`, `Rollup`,
`LockAutomationTargets`, `CheckLedger`, tareas), el paquete completo contra los otros módulos, caminos alternos, alcance,
delegación, concurrencia, fallas parciales y pólizas. Verificó sin hallazgo: bloqueo del destino antes de validar en
embarque, factura y cobro; reglas de respaldo; sin ciclo de bloqueo; doble aceptación por `rowVersion`; transiciones `system`
rechazadas a personas; `editable:false` en API, importación y partidas; pólizas que cuadran por construcción; `from`
validado y huella sin cambios.

| ID | Severidad | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|
| F-A | Media | El tope del 15 % se esquivaba bajando el precio unitario | La regla vale sobre el precio neto contra el precio de lista (`PrecioLista`, calculado en la partida) | IT `Regresion_FA_bajar_el_precio_cuenta_como_descuento…` |
| F-B | Media | El crédito no contaba los pedidos confirmados sin facturar: N pedidos de 900 pasaban un límite de 1 000 | `Cliente.PedidosAbiertos` (acumulado de lo por facturar con IVA) en el cálculo; el acumulado bloquea al cliente al confirmar | IT `Regresion_FB_los_pedidos_abiertos_cuentan…` (uno tras otro, cancelar y tres a la vez) |
| F-C | Media | El tipo de cambio era libre (`TotalBase` casi en cero en USD) | Solo lo edita el gerente de ventas; por omisión, el registrado | IT `Regresion_FC_el_tipo_de_cambio_solo_lo_cambia_el_gerente` |
| F-D | Media | `Producto.CostoUltimo` (Compras) lo leen vendedor, surtidor y facturación | — (cambia Compras; decisión del dueño) | [R-043-5](../../riesgos.md#043) |
| F-E | Media-baja | Un descuento que capturó el gerente impide al vendedor aceptar la cotización | — (aceptado; el mensaje dice qué hacer) | IT `Regresion_FE_…` · [R-043-4](../../riesgos.md#043) |
| F-F | Baja | Embarque o cobro de 500 partidas: la póliza de 501 rebasaba el tope sin explicarlo | Hasta 499 partidas, con el motivo | IT `Regresion_FF_un_embarque_de_500_partidas…` |
| F-G | Baja | Una aplicación que redondea a 0 en moneda base dejaba una partida 0/0 y el libro rechazaba el cobro | `when` en las plantillas (y en la de ventas) | IT `Regresion_FG_…` (EUR a 0.4) |
| F-H | Baja | Factura con otro almacén u otro vendedor que el pedido; banco = cualquier cuenta | Reglas `AlmacenDelPedido`, `VendedorDelPedido`, `CuentaDeBanco`, `BancoNoEsClientes` | IT `Regresion_FH_…` |

Sospechas no verificadas (pedido en borrador borrable con la cotización aceptada; ordenar por un campo oculto): [R-043-6](../../riesgos.md#043).

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`), con los cinco módulos, seis usuarios de
prueba (vendedor, segundo vendedor, gerente, surtidor, facturación, contador), cuentas en el almacén, periodo y tres productos
con existencia, preparados por API en esa base. Navegador integrado: cotización como vendedor (cliente genérico, moneda
extranjera sin tipo de cambio), pedido sobre el límite con su aviso en móvil (375 px) y oscuro, embarque confirmado como
surtidor (sin costos), tablero «Ventas» como gerente (margen 60.5 % correcto), consola sin errores (solo los 401 del cambio de
sesión y el 400 provocado).

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Baja | UI/UX | «Público en general» llegaba sin moneda: cada documento la pedía a mano | Sembrado en MXN | IT `Regresion_exploratoria_F01_F02_…` |
| F-02 | Media | UI/UX | Con moneda extranjera sin tipo de cambio registrado, el vendedor veía «Tipo de cambio es obligatorio» en un campo que no puede capturar | Regla `TipoCambioRegistrado` con dónde registrarlo; el campo deja de ser obligatorio por esquema | IT `Regresion_exploratoria_F01_F02_…` |

### Contra el código anterior
Las regresiones se corrieron con el `modules/ventas.json` anterior a cada corrección (copia en el scratchpad de la sesión):
las 7 de la revisión adversarial y la de la exploratoria fallaron; con el paquete corregido, las 20 de `SalesTests` pasan. La
E2E no se corre contra el commit anterior: Ventas no existía.

### Lista de comprobación
**Flujo**
- [x] El camino feliz de cada historia se completa sin ayuda (E2E por la UI: cotizar, aceptar, confirmar, surtir, facturar, cobrar)
- [x] Los mensajes de error dicen qué pasó y cómo resolverlo (cuentas del almacén, existencia, crédito, descuento, tipo de cambio: F-02)
- [x] Dos peticiones a la vez: aceptar, surtir, cobrar y confirmar contra el límite (integración)
- [x] Datos límite: razón social de 90 caracteres con ñ, 500 partidas, moneda con tipo < 1, sin tipo de cambio

**Diseño**
- [x] Claro y oscuro, escritorio y móvil (375 px): aviso de crédito y formularios sin cortes; el tipo de cambio solo fuera de la moneda base
- [x] Solo iconos Lucide (los del paquete)
- [x] Estados vacíos del tablero («Sin datos todavía»)

**Accesibilidad**
- [x] axe sin violaciones serias en cotización, embarque, factura, cobro, pedido con aviso, modelador y tablero (E2E)
- [x] Diálogos de transición y de avisos con teclado (los de la 023, sin cambios)

**Seguridad**
- [x] El vendedor no ve lo de otro vendedor (404) ni costos; el surtidor no ve costos; otra empresa no ve nada
- [x] Caminos alternos del STRIDE por API directa (transiciones `system`, campos calculados, precio, tipo de cambio)
- [x] Consola sin errores ni violaciones de CSP

**Cierre**
- [x] Pasada con teclado por el modelador («Repetir por cada partida de» es un `select` con etiqueta)
- [x] Hermanos: el choque de nombres de catálogos de prueba se buscó en todos los archivos E2E (tres renombrados)
- [x] Alcance congelado: lo que quedó fuera va al roadmap (punto 13)

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#043): R-043-1 … R-043-6.
