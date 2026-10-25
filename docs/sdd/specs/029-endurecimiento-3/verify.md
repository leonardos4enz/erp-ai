# Verify 029 — Endurecimiento 3: capacidades declaradas y canario de fugas

## Métricas
- inicio: 2026-10-25 10:39
- cierre: 2026-10-25 11:59
- unitarias: 628 (+21)
- e2e: 400 (+15)
- corridas completas: 4 (8 min 28 s apertura, segunda de la 028; 7 min 04 s con 4 fallas por el cambio intencional; 8 min 00 s en verde; 7 min 45 s final tras F-01)
- corridas parciales: 9 (iteración de capacidades, canario y la cadena Compras → Inventario → valuación → PDF: 7; --veces=3 sobre los 9 archivos nuevos o modificados con sus dependencias, 3 × 122 en verde, 5 min 05 s; --veces=3 sobre capacidades y compras tras F-01, 1 min 41 s)
- contra el código anterior: 2 (`e2e:regresion -- 5de6fa0 e2e/capacidades.spec.ts e2e/canario.spec.ts`, antes y después de la revisión adversarial)
- hallazgos: 0/3/4
- categorías: seguridad=3, concurrencia=2, UI/UX=1, motor-api=1
- revisión adversarial: 5 hallazgos (3 medios, 2 bajos) y una observación (F-02 a F-07)
- riesgos: R-029-1, R-029-2, R-029-3, R-029-4, R-029-5, R-029-6, R-029-7, R-029-8, R-029-9, R-029-10, R-029-11, R-029-12, R-029-13, R-029-14

## 1. Pruebas unitarias
`dotnet test`: **628 correctas** (607 de antes + 21 nuevas, < 1 s).
- `RouteInventoryTests` (8): construye las rutas reales sin base (`ErpApp.AddErp` + `MapErpRoutes`) y exige una capacidad por
  ruta, lista blanca anónima, efecto declarado en todo lo que no es GET, compuerta para modelo y seguridad (y nunca con
  «sesión» o «registros»), límite en lo costoso, huella en todo cambio de modelo y en cada «aplicar» con «vista previa», y que
  [`docs/sdd/rutas.md`](../../rutas.md) coincida. **Mutación:** quitar `.Requires(...)` de `GET /api/users/lookup` hace fallar
  `Cada_ruta_declara_exactamente_una_capacidad` («GET /api/users/lookup (0)») y la foto versionada.
- `Hardening3Tests` (13): fórmula que lee un oculto (y a través de partidas y de otras fórmulas: el *Costo* de la recepción);
  `Strip` en partidas sin reglas propias; orden por una fórmula oculta; fórmula que recorre un catálogo restringido, de
  compartido a por empresa y con `UNITCOST` (F-04); modelo sin anotar; cada tipo de cambio de seguridad con regla y uno
  desconocido sin aprobar; `unit.move` bajo la unidad propia; semáforo del PDF por tiempo y por cancelación; capacidades.
- Contra `5de6fa0` las unitarias nuevas **no compilan** (no existen `Capability`, `DerivedAccess`, `PrintLimits.RenderAsync`
  ni `ErpApp`): su regresión se demuestra con la mutación de arriba y con los E2E, que sí corren contra el código anterior.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa al abrir (segunda corrida de la 028) | 385 en verde | 8 min 28 s |
| 2 | Completa con lo implementado | 366 en verde, **4 fallan** + 19 sin correr: el almacenista ya no ve el costo de la recepción (2), las imágenes y la vista previa del PDF exigen modelador (1 + 1 al seguir), y con `PolizaConCuentas` en aviso el almacenista ya no recibe un aviso que lee el costo (R-029-11). Cambios intencionales: las pruebas leen el costo como administrador o confirma el gerente | 7 min 04 s |
| 3 | Completa tras la revisión adversarial (F-02 a F-04) | 399 en verde | 8 min 00 s |
| 4 | `--veces=3`: `capacidades`, `canario`, `compras`, `inventario`, `seguridad`, `valuacion-contable`, `valuacion-politicas`, `valuacion-rutas-avisos`, `visual-pdf` | 3 × 122 en verde | 5 min 05 s |
| 5 | Completa final, tras F-01 | 400 en verde | 7 min 45 s |
| 6 | `--veces=3`: `capacidades` (modificado por F-01) y `compras` (instala por la UI que cambió) | 3 × 30 en verde | 1 min 41 s |

Archivos nuevos: `capacidades.spec.ts`, `canario.spec.ts`. Modificados: `seguridad.spec.ts` (+1), `visual-pdf.spec.ts`,
`valuacion-contable.spec.ts`, `valuacion-politicas.spec.ts`, `valuacion-rutas-avisos.spec.ts` (efecto intencional del cambio),
`regresion.mjs` (copia `docs/sdd/rutas.md`), `playwright.config.ts` (`BulkPerMinute`, `SearchesPerMinute`). Sin pantallas
nuevas ni capturas de referencia cambiadas: la única UI tocada es el diálogo de módulos (mismo aspecto).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió: el diff contra `5de6fa0`, la tabla STRIDE del plan y `rutas.md`. Revisó:
cada `Require*` quitado contra el código anterior, `CapabilityHandler` y el orden de middlewares, todos los caminos de
lectura frente a `DerivedAccess` y al cargador con alcance, `WhyNotApprover`, la huella de módulos, el semáforo y los límites.
Encontró F-02 a F-07 (abajo). Sin hallazgo en: capacidades (todas iguales o más fuertes; superusuario implica las demás;
claves y OAuth llenan el usuario y no tienen capacidades administrativas), orden de middlewares, avisos con `block`, cargador
con alcance (cada salto, sin caché entre usuarios), `FieldAccess` en todos los caminos, los 11 tipos de cambio y megapíxeles.

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`), navegador integrado, como
administrador. Diálogo de módulos: la vista previa de Compras trae la huella; con otro cambio al modelo a la mitad (plan por
API), «Instalar módulo» muestra «El modelo o el módulo cambiaron desde la vista previa: vuelve a revisarla» dentro de la
vista y no instala; al reabrir, instala (conjunto de cambios #12). Consola: solo el 401 previo al inicio de sesión y el 409
provocado. El resto de la spec no tiene pantallas: se probó por API (capacidades, canario) y queda en las E2E.

### Revisión campo por campo: Inventario y Costeo (H4 CA2)
Ocultos de Compras 1.3.0 (solo *Comprador*, *Gerente de compras*, *Director*): en la orden `Subtotal`, `Descuento`, `IVA`,
`Total`, `TotalMXN`; en su partida `PrecioUnitario`, `DescuentoPct`, `IvaPct`, `Importe`, `DescuentoImporte`, `IvaImporte`,
`Total`, `CostoMXN`.

| Catálogo › fórmula | Lee | Antes (almacenista) | Con la 029 |
|---|---|---|---|
| RecepcionPartida › `CostoUnitario` | `PartidaOrden.PrecioUnitario`, `DescuentoPct`, `CostoMXN` (ocultos) | visible: revelaba el precio | **oculta** (`seguridad › Spec 029 H4 CA2`) |
| RecepcionPartida › `Costo` | `CostoUnitario` | visible | **oculta** (transitiva) |
| Recepcion › `CostoTotal` | `SUM(Partidas.Costo)` | visible | **oculta** (por partidas) |
| OrdenCompra › `MonedaClave` | `Moneda.Clave` | visible | visible |
| Existencia › `StockMinimo`, `BajoMinimo`, `Clave` | `Producto.StockMinimo`, `Almacen.Clave` | visible | visible (sin alcance restringido en los módulos) |
| Existencia › `CostoPromedio`, `CosteoIncompleto` | `Valor`, `Cantidad`, `CantidadCosteada` (guardados por automatización) | visible | visible: **el valor lo copió una automatización desde el costo de la recepción** → [R-029-6](../../riesgos.md#029) |
| Traspaso › `TotalPiezas`; Poliza › `TotalCargos`, `TotalAbonos`, `Diferencia` | cantidades y partidas propias | visible | visible |

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Baja | UI/UX | Con la vista previa ya vieja, «Instalar módulo» daba 409 y había que cerrar y reabrir el diálogo para revisarla otra vez | El diálogo vuelve a pedir la vista previa tras un 409 | `capacidades › H6 CA2 (UI)` (con `5de6fa0` instala sin 409) |
| F-02 | Media | concurrencia | (Adversarial F-A) La huella del módulo se comparaba sin el candado del modelo: un plan aplicado entre comparar y aplicar pasaba | `PlanService.LockModelAsync` antes de comparar (como los paquetes) | sin prueba: necesita dos conexiones y una pausa → [R-029-12](../../riesgos.md#029), 030 |
| F-03 | Media | seguridad | (Adversarial F-B) El campo para mostrar es una fórmula (`Existencia.Clave`): `__label`, buscador, agrupación de reportes e importación lo leían aunque `DerivedAccess` la ocultara | `FieldAccess.DisplayHidden`: «(sin acceso)» en etiquetas, el número en el buscador, sin etiqueta en reportes, sin buscar por él al importar | `canario › H3 CA2` y `› H2 CA2` (el puente se muestra con una fórmula que llega al secreto) |
| F-04 | Media | seguridad | (Adversarial F-C) `UNITCOST('Catálogo', …)` nombra su catálogo de saldo como texto: ni `DerivedAccess` ni el cargador con alcance lo veían | Cuenta como salto en `DerivedAccess`; en vivo, sin capas para quien tiene restringido ese catálogo | `Hardening3Tests › UNITCOST_de_un_catalogo_restringido_cuenta_como_recorrido` (no compila contra `5de6fa0`) |
| F-05 | Baja | concurrencia | (Adversarial, junto a F-A) La huella no incluía los roles: un rol con el mismo nombre creado después recibía los permisos del módulo | Los nombres de los roles entran en la huella | sin prueba propia (revisión de código; ver R-029-12) |
| F-06 | Baja | motor-api | (Adversarial F-D) Un modelo sin anotar no oculta fórmulas | Aceptado: todos los caminos usan el modelo anotado | [R-029-13](../../riesgos.md#029) |
| F-07 | Baja | seguridad | (Adversarial F-E) Un render colgado retiene su lugar sin plazo | Aceptado | [R-029-14](../../riesgos.md#029) |

### Contra el código anterior
`npm --prefix web run e2e:regresion -- 5de6fa0 e2e/capacidades.spec.ts e2e/canario.spec.ts` (copia también `rutas.md`):
**11 fallan, 3 pasan (las dos «preparar» y el 401 sin sesión)**. Motivos con `5de6fa0`:
- `canario › H2 CA2 (GET)`: el canario sale en 20 rutas: `/api/audit`, `/api/dashboards/CnTablero`, el listado, el registro y
  su PDF, la exportación CSV (con y sin columnas), los reportes `PorDato` y `PorCopia` (y su exportación) y OData (con y sin
  `$expand`): las fórmulas `Dos`, `Tres`, `OtraEmpresa` y `Copia`.
- `canario › H2 CA2 (POST)`: `/compute` (nuevo y existente), crear y editar devuelven las fórmulas con el canario.
- `canario › H3 CA1`: la sección «SinSecreto» se ocultaba para el lector (el secreto a 2 saltos se leía).
- `canario › H3 CA2`: `Dos` venía con el canario. `› H3 CA3`: el PDF imprimía `{{ Dos }}`, `{{ Tres }}`, `{{ Copia }}`.
- `canario › H4 CA3`: el 400 listaba «el campo oculto tiene valor».
- `capacidades › H1 CA4`: aprobar un cambio de seguridad, `/api/files/{id}` y la vista previa del PDF respondían 404 en vez de
  403. `› H1 CA5`: la imagen se servía a cualquier sesión (404 en lugar de 403 para ids inexistentes). `› H5 CA3`: ningún
  429. `› H6 CA2`: no había `baseHash`. `› H6 CA2 (UI)`: instalaba sin 409.
- Pasan con el código anterior (lo esperado): las «preparar» y el 401 sin sesión.

### Lista de comprobación
**Flujo**
- [x] El camino feliz de cada historia se completa (instalar un módulo por la UI; el resto es API y queda en E2E)
- [x] Los mensajes de error dicen qué pasó y cómo resolverlo («vuelve a revisarla», «incumple una regla del catálogo», 12 MP)
- [x] Doble clic, Atrás, recargar: el diálogo de módulos deshabilita el botón mientras aplica (sin cambios)
- [ ] Datos límite: sin UI nueva de captura (no aplica)

**Diseño**
- [x] Sin pantallas nuevas; el diálogo de módulos conserva su aspecto (sin capturas cambiadas)
- [x] Solo iconos Lucide
- [x] Estados de error con diseño (alerta dentro del diálogo)

**Accesibilidad**
- [x] El error del diálogo es una alerta visible dentro de la vista (no fuera de ella)
- [x] Diálogo con foco atrapado y Esc (sin cambios)

**Seguridad**
- [x] Un usuario sin permiso no llega por URL directa: `capacidades › H1 CA4` recorre cada ruta (403) y sin sesión (401)
- [x] La consola no muestra errores ni violaciones de CSP (solo los 401/409 provocados)
- [x] Cada camino alterno de la tabla STRIDE probado con un usuario sin permiso por API: el canario recorre todas las GET
- [x] Dos peticiones a la vez: la huella de módulos se compara con el modelo bloqueado (F-02; prueba de carrera en la 030)

**Cierre**
- [x] Sin pantallas nuevas que recorrer con teclado; la spec no agrega metadatos al modelador
- [x] Hallazgos de un componente genérico, sus hermanos: F-03 se buscó en todos los usos de `EffectiveDisplayField`
      (`SelectClause`, buscador, `FormatValues`, reportes, importación; la impresión ya lo revisaba)
- [x] Alcance congelado: nada nuevo pedido durante VERIFY

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#029): R-029-1 a R-029-14. Además, la 029 cerró R-006-1 y R-023-5, dejó prueba a
R-023-3 y R-024-3 y anotó el estado de R-008-4, R-016-3, R-021-5 y R-021-6 (auditoría 4).
