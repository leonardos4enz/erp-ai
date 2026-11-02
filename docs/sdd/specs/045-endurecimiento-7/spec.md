# Spec 045 — Endurecimiento 7: las partidas se ven solo con su campo, el crédito de Ventas tiene tope y las claves del MCP no se sueltan tras el efecto

**Estado:** Implementado · **Depende de:** 006, 007, 014, 029, 038, 039, 040, 041, 042, 043, 044 · **Origen:** la **auditoría
externa 8** (2026-11-01, solo lectura al cerrar la 044, commit `63bd34a`, specs 040–044): 1 defecto alto (D-01), 1 medio
(D-02), 9 bajos (D-03 a D-11) y 3 observaciones (O-01 a O-03). El dueño pidió corregir lo grave y lo bajo (2026-11-01).

## Problema
Cada hallazgo se reconfirmó en el código de `d7cc96e` (igual a `63bd34a` fuera de `docs/`) antes de escribir esta spec.

| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | D-01 (alta) · `PersonalReports.Catalog`, `Sources`, `Fields` y `ReportService.Available`/`CanReadPath` solo piden **leer el documento** de una partida; el resto del motor (`FieldAccess.Strip`, `AuditFilter`, OData, `DerivedAccess`) además exige **ver el campo de partidas** (`lines`) | Con `Nomina` (partidas) visible solo para RH, quien lee `Empleado` ve `EmpleadoNomina` en `/api/my-reports/sources` y explora la suma de `Monto` por empleado (y la exporta, la guarda, la comparte, la pone en un tablero). Igual un reporte del modelo sobre la partida y un salto por referencia a una partida |
| 2 | D-02 (media) · Ventas: las reglas `LimiteCredito` y `LimiteCreditoGerente` de `Pedido` dicen `ISBLANK(Cliente.LimiteCredito) OR …`; el vendedor crea clientes pero no puede fijar su límite (solo el gerente lo edita; el motor ignora el campo al crear) | Todo cliente que da de alta un vendedor queda **sin límite** y el vendedor confirma a crédito pedidos de cualquier monto, sin aviso ni gerente. Igual «Público en general» (sembrado sin límite) |
| 3 | D-03 · el permiso `{"roles": ["Vendedor"], "where": "RFC = 'XAXX010101000'"}` de `Cliente` no lleva `access: read` | Todo vendedor **edita** «Público en general» (razón social, correo, condición, moneda); si cambia el RFC, ningún vendedor vuelve a verlo |
| 4 | D-04 · `RecordService.DeleteAsync` solo protege estados no editables y referencias de tipo `reference`; `Pedido.Cotizacion` y `Cotizacion.Pedido` son texto | El vendedor borra el pedido en borrador que creó una cotización aceptada: la cotización queda «Aceptada» (final) con el folio de un pedido que no existe y sin forma de volver a generarlo |
| 5 | D-05 · el índice único es por empresa (`DdlGenerator`) y el choque responde «Ya existe un registro con ese valor de RFC.» (`ExecuteMappingUniqueAsync`), también en la vista previa de la importación | Un vendedor con alcance «propios» sabe si un RFC ya es cliente de otro vendedor: uno por alta y hasta 200 por vista previa de importación, **sin rastro** |
| 6 | D-06 · `GET /api/reports/{e}/{r}/export` y `POST /api/my-reports/explore/export` no escriben bitácora; la exportación de listados sí (`data.export`) | Agrupar por folio con 6 medidas baja 1 000 filas por archivo, 20 por minuto, sin rastro |
| 7 | D-07 · `GET /api/my-reports/shared` (y `GET /api/my-dashboards`) devuelven todo lo compartido con los roles de quien pide, sin tope; los reportes corren `Check` uno por uno | Con un rol amplio y muchas personas compartiendo (50 reportes cada una), cada carga de la pantalla valida miles de definiciones |
| 8 | D-08 · `McpServer.RunAsync`: tras el efecto, un resultado de más de `Mcp:MaxResultBytes` (413) o una salida que `Structure` no entiende (500) **sueltan la clave** | `aislado_crear` con un texto de ~300 KB crea el registro y responde 413; el reintento con la misma clave crea otro (contra H3 de la 044) |
| 9 | D-09 · `McpSandboxLease.RenewAsync` y `McpWrites.Keeper.LoopAsync`: el `catch` está fuera del `while` | Una sola falla pasajera de la base termina la renovación: la clave «en curso» vence y un reintento corre a la vez que la llamada original; la copia queda a merced del barrido por inactividad |
| 10 | D-10 · `McpOptions.From` acota `IdempotencyHours` y `WriteLeaseSeconds` por separado y `SweepAsync` borra `meta.McpWrite` por `CreatedAt` sin mirar `Status` | Con `IdempotencyHours=1` y `WriteLeaseSeconds>3600`, o una escritura que dura más que `IdempotencyHours`, el barrido borra una clave en curso y el reintento la ejecuta otra vez |
| 11 | D-11 · el barrido de `/oauth/register` toma como inútil un cliente de más de 1 h sin conexiones aunque tenga un **código vigente**, y su falla no está protegida | Si la persona pulsa «Permitir» con un cliente así, el barrido siguiente borra su código (el canje falla) y, si el `INSERT` del código cae entre los `DELETE`, el registro anónimo responde 500 por la llave foránea |
| 12 | O-02 · `ClientAddress.ForwardedOptions` lee `Security:ForwardedProxies` como lista; un valor de texto (variable de entorno sin `__0`) se ignora sin aviso | Detrás de un proxy configurado así, todos comparten los límites por dirección (R-021-5 vuelve) |

## Principio
Ninguna capacidad de negocio nueva salvo una genérica pequeña (condición para eliminar). Cuatro reglas del motor: **la regla de
un camino vale en todos** (ver una partida exige ver su campo de partidas también en reportes, exploración, tableros y saltos;
exportar deja rastro en todos los caminos); **lo que se cuenta contra un tope es acotado** (lo compartido que se lista);
**una clave de idempotencia solo se suelta si no hubo efecto** y su *lease* sobrevive a una falla pasajera; y **un trabajo de
mantenimiento no rompe la petición que lo dispara** ni borra lo que está en uso (códigos vigentes, claves en curso). Ventas
corrige su crédito y su cliente compartido con **metadatos** (`upgrades` 1.1.0 del paquete, sin editar el plan base).

## Historias

**H1 — Las partidas se ven solo si se ve su campo (D-01).** Como *dueño de datos*, quiero que ocultar el campo de partidas
de un documento las oculte en todos los caminos, para que un reporte o una exploración no revele lo que el registro esconde.
- CA1: con el campo `lines` oculto para el rol, la partida no aparece en `GET /api/my-reports/sources` y
  `GET /api/my-reports/sources/{partida}`, `explore`, `explore/export` y guardar un reporte sobre ella responden como un
  catálogo inexistente (404, «El catálogo no existe.»).
- CA2: un reporte del modelo sobre la partida no se lista ni corre para ese rol (`/api/reports`, su exportación, tableros
  del modelo y tarjetas de tableros personales); el dueño lo sigue viendo.
- CA3: un salto por referencia que llega a una partida cuyo campo de partidas no ve solo admite su campo para mostrar (lo que
  ofrece el buscador), como un catálogo que no lee; tampoco se baja por él en la lista de campos.
- CA4: el canario recorre una partida detrás de un campo de partidas oculto (exploración, reporte del modelo, rutas del
  inventario): el administrador ve el canario y el lector no.

**H2 — Ventas 1.1.0: el crédito tiene tope y «Público en general» no se edita (D-02, D-03).** Como *gerente de ventas*,
quiero que un cliente sin límite capturado no dé crédito ilimitado a los vendedores y que el cliente compartido solo lo cambie
yo.
- CA1: para quien no es gerente de ventas, un cliente **sin límite capturado tiene límite 0**: el pedido avisa al guardar y
  no se confirma («… Solo el gerente de ventas confirma un pedido sobre el límite o fija el límite del cliente.»). El gerente
  confirma sin aviso un cliente sin límite (R-043-1, sin cambio).
- CA2: un vendedor ve «Público en general» y lo usa en sus documentos, pero no lo edita (403); el gerente sí.
- CA3: el cambio llega como `upgrades` 1.1.0 de `modules/ventas.json` (instalación nueva y actualización de una instalada).

**H3 — Integridad de los documentos (D-04, D-05).** Como *dueño*, quiero que un documento que otro nombra no se borre y que
probar valores únicos deje rastro.
- CA1: `security.deletableWhen` (expresión del documento) y `security.deleteMessage`: si la condición no se cumple, eliminar
  responde 409 con el mensaje; vale para todos los roles. Se valida al compilar el plan; una partida no la admite.
- CA2: Ventas 1.1.0 declara en `Pedido` `deletableWhen: ISBLANK(Cotizacion)`: el pedido creado desde una cotización no se
  borra (se cancela); uno capturado directo sí.
- CA3: un choque de valor único que se le responde a una persona (alta, cambio o importación, también en vista previa) deja
  en la bitácora `data.unique` bajo el catálogo (campo y número de filas; nunca el valor).

**H4 — Exportar deja rastro y lo compartido tiene tope (D-06, D-07).** Como *auditor*, quiero ver cada exportación de un
reporte; como *dueño*, que listar lo compartido no crezca sin límite.
- CA1: exportar un reporte del modelo o una exploración escribe `data.export` bajo el catálogo raíz (reporte, formato y
  filas; nunca la definición ni los valores).
- CA2: `GET /api/my-reports/shared` y `GET /api/my-dashboards` devuelven a lo más 100 de lo compartido con quien pide (los
  más recientes primero) y validan solo esos.

**H5 — Las claves y la copia del MCP aguantan (D-08, D-09, D-10).** Como *persona que usa Claude*, quiero que reintentar
una escritura con la misma clave nunca la repita cuando ya se hizo.
- CA1: tras el efecto, un resultado de más de `Mcp:MaxResultBytes` o una salida que no se entiende **consumen la clave**: el
  reintento recibe el mismo error («La escritura se hizo, pero su resultado …») sin volver a ejecutar.
- CA2: una falla pasajera al renovar no termina la renovación de la clave ni de la copia: la siguiente vuelta renueva.
- CA3: `WriteLeaseSeconds` nunca pasa de la mitad de `IdempotencyHours`, y el barrido solo borra claves terminadas o con el
  *lease* vencido.

**H6 — El registro del MCP y los proxies (D-11, O-02).** Como *persona que conecta su Claude*, quiero que el barrido del
registro no me deje a medias ni responda 500.
- CA1: el barrido no toma como inútil un cliente con un código sin usar ni vencer (tampoco el que nunca canjeó).
- CA2: una falla del barrido se registra en el log y el registro sigue (201).
- CA3: `Security:ForwardedProxies` escrito como texto (`10.0.0.5, 10.0.0.0/24`) se lee como lista.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios: más de 6 historias (6, no) · más de ~2 500 líneas de producto (unas 400, no) · seguridad o
   concurrencia junto con UI nueva (no hay UI nueva) · más de una pantalla principal (ninguna). → **No se divide.**
2. **D-01, una sola regla:** `FieldAccess.SeesLines(entidad)` = lee la raíz y, si es partida, ve **algún** campo `lines`
   activo del documento que apunta a ella (el modelo admite uno por partida). La usan `Catalog`, `Sources`, `Fields`,
   `Available` y cada salto de `CanReadPath`. Oculto responde igual que inexistente (lección de la 041).
3. **D-02, «vacío = 0» para quien no es gerente** (no un valor por omisión de 0): cubre los clientes que ya existen sin límite
   y «Público en general» sin tocar datos. Consecuencia buscada: un vendedor no confirma a crédito para «Público en general»
   hasta que el gerente le fije un límite. El aviso del borrador usa la misma regla (el vendedor lo ve antes de confirmar).
4. **D-04, capacidad genérica `deletableWhen`** (como `when`/`whenMessage` de una transición): el paquete solo crea roles y no
   puede quitarle `canDelete` al vendedor en una actualización, y un permiso por registro haría lo mismo con editar. Se
   declara en `security` porque es una regla de autorización del registro. El editor visual de seguridad no la muestra
   todavía (el editor JSON y los paquetes sí): [R-045-2](../../riesgos.md#045).
5. **D-05, rastro en lugar de ocultar:** el valor único es único por empresa a propósito (un RFC no se repite aunque sea de
   otro vendedor), así que la respuesta «ya existe» es inevitable; se registra quién prueba. Residuo: [R-045-1](../../riesgos.md#045).
6. **D-07, tope de 100** (no paginación): la pantalla muestra una lista corta; con más de 100 se ven los 100 más recientes.
7. **D-08:** se consume la clave solo cuando la herramienta **sí terminó** (salida sin error). Un error de la ruta (409, 429,
   500) se revirtió en su transacción: soltar la clave sigue siendo lo correcto.
8. **O-01** (huella SHA-256 sin sal de la definición): **no se toma**; revela a un auditor, por fuerza bruta, solo la elección
   del dueño en un filtro de poca entropía, que el auditor no puede correr sin leer el catálogo. Se registra: [R-045-3](../../riesgos.md#045).
9. **O-02 se toma** (CA3 de H6): es una línea y evita un error de configuración silencioso. **O-03** (posible interbloqueo
   entre el registro serializable y las renovaciones) **no se toma**: queda en el roadmap como prueba de investigación.
10. **R-040-3** (MCP como camino de exportación sin bitácora): sigue aceptado; con D-06 los reportes ya dejan rastro y el MCP
    es el único camino de lectura masiva sin `data.export`.

## Fuera de alcance (anotado en el roadmap)
- O-03: prueba de integración con registros y renovaciones del MCP simultáneos.
- `deletableWhen` en el editor visual de seguridad (R-045-2).
- La bitácora del primer `leer_registros` por conexión y catálogo (R-040-3).
