# Plan 045 — Diseño técnico

## 1. Modelo de datos
Sin tablas nuevas. Cambios:

| Dónde | Cambio |
|---|---|
| `SecurityPolicy.DeletableWhen`, `DeleteMessage` | Nuevas (opcionales): expresión del documento que debe cumplirse para eliminar y el mensaje (≤ 300). En `setSecurity`, paquetes, reversión y Studio (la misma operación). No en partidas |
| `FieldAccess.SeesLines(e, u, model)` | Nueva: lee la raíz y, si `e` es partida, ve algún campo `lines` activo del documento que apunta a `e` |
| `ValidationFailed.Unique` | Nueva propiedad opcional: catálogo y campo del choque de valor único (para la bitácora) |
| `AuditActions.UniqueConflict` = `data.unique` | Nueva acción de catálogo (`AuditFilter.EntityActions`) |
| `McpOptions.WriteLeaseSeconds` | Se acota además a `IdempotencyHours × 1800` (la mitad de la retención) |
| `modules/ventas.json` | `upgrades` 1.1.0 (versión del paquete 1.1.0): reglas de crédito, permiso de «Público en general» con `access: read`, `deletableWhen` del pedido |

## 2. Contrato de la API
**Ninguna ruta nueva** (siguen las de [`rutas.md`](../../rutas.md)). Cambios de comportamiento:

| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `GET /api/my-reports/sources` | registros | igual | Sin las partidas cuyo campo de partidas no ve |
| `GET /api/my-reports/sources/{e}`, `POST /api/my-reports/explore`, `/explore/export`, `POST/PUT /api/my-reports` | registros | partida con su campo oculto | **404** «El catálogo no existe.» (igual que inexistente) |
| `GET /api/reports`, `/api/reports/{e}/{r}`, `/export`, `/api/dashboards/{n}`, `/api/my-dashboards/{id}` | registros | reporte sobre una partida con su campo oculto | No se lista; 404 «El reporte no existe.»; la tarjeta no corre |
| `GET /api/reports/{e}/{r}/export`, `POST /api/my-reports/explore/export` | registros | igual | Igual; además escribe `data.export` bajo el catálogo raíz: `{ report, format, rows, truncated }` |
| `GET /api/my-reports/shared`, `GET /api/my-dashboards` | registros | igual | A lo más 100 de lo compartido (los más recientes); los propios de `GET /api/my-dashboards` siguen completos (≤ 20 por persona) |
| `DELETE /api/data/{e}/{id}` | registros + eliminar | igual | **409** con `deleteMessage` (o «El registro no cumple la condición para eliminarse.») si `deletableWhen` no se cumple |
| `POST /api/data/{e}`, `PUT …/{id}`, `POST …/import` (también `dryRun`) | registros | valor único que ya existe | Igual (400 «Ya existe un registro con ese valor de …»); además `data.unique` en la bitácora: `{ field, rows }` |
| Planes (`setSecurity`) | modelador | `deletableWhen` inválido, en partida o mensaje largo | **400** «… › seguridad: …» |
| `POST /mcp` `tools/call` con clave | token MCP | resultado > `MaxResultBytes` o salida ilegible tras el efecto | El error (413/500) **se guarda con la clave**: «La escritura se hizo, pero su resultado …»; el reintento lo repite sin ejecutar |
| `POST /oauth/register` | anónima | igual | El barrido no toma un cliente con código vigente; si el barrido falla, se registra en el log y el registro sigue |

Ventas 1.1.0 (metadatos):

| Operación | Contenido |
|---|---|
| `setRules` `Pedido` (`merge`) | `LimiteCredito` (aviso): `(ISBLANK(Cliente.LimiteCredito) AND HASROLE('Gerente de ventas')) OR Cliente.Saldo + Cliente.PedidosAbiertos + TotalBase <= COALESCE(Cliente.LimiteCredito, 0)`; `LimiteCreditoGerente` (error, no gerente): `… <= COALESCE(Cliente.LimiteCredito, 0)` con el mensaje nuevo |
| `setSecurity` `Cliente` | La misma política con `access: read` en el permiso de «Público en general» |
| `setSecurity` `Pedido` | La misma política con `deletableWhen: ISBLANK(Cotizacion)` y su mensaje |

## 3. SPA
Sin pantallas nuevas. El 409 de eliminar lo muestra el aviso que ya existe (`role="alert"`). Tipos: `SecurityPolicy` gana
`deletableWhen` y `deleteMessage` (el editor visual las conserva al guardar: copia la política con `...policy`); la ayuda del
editor JSON las nombra.

## 4. Pruebas
- **Integración nueva:** `Hardening045Tests` (H1 CA1–CA3, H3 CA1 y CA3, H4 CA1–CA2). `Mcp2Tests.Hardening045.cs` (H5 CA1–CA3,
  H6 CA1–CA2: `Mcp2Tests` pasa a `partial`). En `SalesTests`: H2 CA1–CA3 y H3 CA2 (los clientes de las pruebas existentes
  reciben un límite del gerente: «vacío = 0» es el comportamiento nuevo).
- **Unitarias nuevas:** `Hardening7Tests` (H5 CA3: acotar `WriteLeaseSeconds`; H6 CA3: `ForwardedProxies` como texto).
- **E2E modificados:** `canario.spec.ts` (H1 CA4) y `ventas.spec.ts` (el cliente de la prueba recibe un límite en lugar de
  quedar sin él), con `--veces=3`.
- **Contra el código anterior:** `npm --prefix web run it:regresion -- 63bd34a "H045|Hardening045"` (con
  `Hardening7Tests` en `tests/Erp.Tests`, corrida aparte) y `e2e:regresion -- 63bd34a e2e/canario.spec.ts`.

## 5. STRIDE
**Las 8 filas son obligatorias.**

| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones o caminos agrega o cambia? ¿Qué capacidad pide cada una? | Ninguna ruta. Cambian `setSecurity` (dos propiedades, modelador), `DELETE /api/data/{e}/{id}` (409 por condición), las rutas de reportes personales y del modelo (partidas), las exportaciones de reportes (bitácora), `/shared` y `/my-dashboards` (tope), `tools/call` con clave y `/oauth/register` (barrido). El paquete Ventas 1.1.0 entra por la vía de módulos (vista previa con huella, modelador) | `Hardening045Tests › H3_CA1_deletableWhen_impide_eliminar_y_se_valida_al_compilar`; `SalesTests › H045_H2_CA3_la_instalacion_queda_en_1_1_0_sin_actualizaciones_pendientes` |
| Caminos alternos | ¿Todos los caminos al mismo dato o efecto aplican la misma regla? | Partidas: la misma `SeesLines` en fuentes, campos, exploración (y su exportación, guardado y lo compartido, que pasan por `Check`), reportes del modelo (lista, corrida, exportación, tableros del modelo y tarjetas personales, que pasan por `Available`) y cada salto (`CanReadPath`); registro, OData, bitácora y fórmulas ya lo exigían. Eliminar: un solo camino (`DeleteAsync`: API, Studio y MCP en la copia llaman la ruta). Valor único: `ExecuteMappingUniqueAsync` es el único traductor (alta, cambio, partidas, importación y automatizaciones). Exportar: listados, reportes y exploración con `data.export`; el MCP sigue sin ella (R-040-3) | `Hardening045Tests › H1_CA1…`, `› H1_CA2…`, `› H1_CA3…`, `› H3_CA3…`, `› H4_CA1…`; `canario.spec.ts › H1 (045)…`; residuo [R-040-3](../../riesgos.md#040) |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | Sí: la corrida de reportes no cambia (alcance y empresa por unión); se agrega la visibilidad del campo de partidas antes de correr. La bitácora nueva no lleva valores (`data.unique`: campo y filas; `data.export`: reporte, formato y filas) y es de catálogo (la ve quien lee el catálogo raíz) | `Hardening045Tests › H1_CA1…`, `› H3_CA3…` (sin el valor), `› H4_CA1…` |
| Delegación | ¿Alguien puede otorgar, aprobar o heredar algo que no tiene? | Un reporte compartido corre con quien lo ve (042): sin ver las partidas, no corre aunque el dueño las vea. `deletableWhen` lo declara solo quien modela y vale también para el superusuario. El vendedor ya no fija crédito por omisión (antes «sin límite» equivalía a un límite infinito que él concedía al crear el cliente) | `Hardening045Tests › H1_CA2…` (tarjeta compartida); `SalesTests › H045_H2_CA1…` |
| Concurrencia | ¿Qué invariante entre filas protege? ¿10 a la vez? | El crédito ya era serializable por cliente (043 F-B); «vacío = 0» no cambia el bloqueo. Claves MCP: una sola ejecución por clave también cuando el resultado no cabe; el *lease* se mantiene ante una falla pasajera, así que un reintento no corre a la vez que la llamada original. El barrido de claves no borra una en curso. Eliminar con `deletableWhen` evalúa sobre el documento leído dentro de la transacción del borrado; revisión adversarial F-01: el borrado **bloquea el documento** (`UPDLOCK, HOLDLOCK`) antes de leerlo, así que una confirmación simultánea espera y no queda un documento confirmado borrado | `Mcp2Tests › H045_H5_CA1…`, `› H045_H5_CA2…`, `› H045_H5_CA3…`; `SalesTests › Regresion_045_F01_borrar_y_confirmar_a_la_vez_no_borra_un_pedido_confirmado`, `› Regresion_FB…` (sin cambio de bloqueo) |
| Fallas parciales | ¿Qué pasa si el proceso cae entre dos pasos? | La bitácora `data.unique` se escribe después del rechazo, en su propia conexión: si falla, se registra en el log y la respuesta al usuario no cambia (es rastro, no control). `data.export` va antes de enviar el archivo: si la bitácora falla, no hay archivo. Una falla del barrido del registro MCP ya no tumba el registro; el barrido siguiente lo reintenta | `Hardening045Tests › H4_CA1…`; `Mcp2Tests › H045_H6_CA2_una_falla_del_barrido_no_tumba_el_registro` |
| Límites | Tamaño, paginación, topes en memoria, tasa por usuario o IP | 100 compartidos por lista; `WriteLeaseSeconds ≤ IdempotencyHours × 1800`; `deleteMessage` ≤ 300. Probar valores únicos sigue al ritmo de los límites de escritura e importación (sin tope propio): [R-045-1](../../riesgos.md#045) | `Hardening045Tests › H4_CA2_lo_compartido_se_lista_con_tope`; `Hardening7Tests › H5_CA3…` |
| Semántica | Nulos, vacíos, monedas, fechas, redondeo | Límite vacío: para el gerente, «sin límite» (043); para los demás, `COALESCE(…, 0)` (lección 022: vacío no es «sin dato» cuando el control lo exige). `deletableWhen` con resultado desconocido (nulo) **no** permite eliminar (`whenNull: false`, como `when` de una transición). Un cliente con límite 0 y un pedido en 0 se confirma | `SalesTests › H045_H2_CA1…`; `Hardening045Tests › H3_CA1…` |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** | — (ningún cambio de identidad) | — | — |
| **T** | El vendedor edita el cliente compartido (y lo deja inaccesible); borra el pedido de una cotización aceptada | `access: read`; `deletableWhen` | `SalesTests › H045_H2_CA2…`, `› H045_H3_CA2…` |
| **R** | Exportar reportes y probar valores únicos sin rastro | `data.export` y `data.unique` | `Hardening045Tests › H4_CA1…`, `› H3_CA3…` |
| **I** | **Alta:** partidas ocultas por su campo, reveladas en exploración y reportes; existencia de un valor único fuera de alcance | `SeesLines` en todos los caminos de reportes; rastro (residuo aceptado) | `Hardening045Tests › H1_CA1…`, `› H1_CA2…`, `› H1_CA3…`; `canario.spec.ts › H1 (045)…`; [R-045-1](../../riesgos.md#045) |
| **D** | Lista de compartidos sin tope; el registro MCP con 500 | Tope de 100; barrido tolerante | `Hardening045Tests › H4_CA2…`; `Mcp2Tests › H045_H6_CA2…` |
| **E** | Crédito ilimitado concedido por quien no puede fijarlo | «Vacío = 0» para quien no es gerente | `SalesTests › H045_H2_CA1…` |
