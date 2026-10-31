# Plan 040 — Diseño técnico

## 1. Modelo de datos
Sin tablas ni operaciones nuevas del Plan de Cambios. Cambios:

| Dónde | Cambio |
|---|---|
| `meta.McpClient.CreatedIp` | Guarda la **clave de dirección** (`ClientAddress.Key`: la IPv4, o el /64 de la IPv6, `2001:db8:1:2::/64`), no la dirección completa. Las filas viejas conservan la dirección completa: cuentan como su propia clave hasta que el barrido las borra (una hora) |
| `Mcp:MaxConnectionsPerUser` | Nueva opción (20, 1–1 000) |
| `Security:Ipv6PrefixLength`, `Security:ForwardedLimit` | Revisión adversarial F-05 y F-06: el prefijo de agrupación de una IPv6 (64, 48–128) y los saltos de `X-Forwarded-For` (1, 1–10) |
| `IX_McpGrant_Client` | Revisión adversarial F-02: índice de conexiones por cliente (barrido y topes del registro) |
| `Security:ForwardedProxies` | Nueva opción: lista de IP o redes CIDR de proxies de confianza (vacía por omisión) |
| `Studio:RenewSeconds` | Se acota a `Math.Min(renew, Math.Max(1, lease / 3))` |
| `InterfaceRules.MaxJoins` | 16 uniones distintas por reporte |

## 2. Contrato de la API
**Ninguna ruta nueva** (siguen las 164 de [`rutas.md`](../../rutas.md)). Cambios de comportamiento:

| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `POST /oauth/register` | anónima | igual | El barrido borra también clientes cuyas conexiones están todas muertas desde hace un día (con sus `McpToken`, `McpCode` y `McpGrant`). **429** si la clave de dirección (/64) ya tiene `PendingClientsPerAddress` pendientes; **503** solo si los clientes **vivos** llegan a `MaxClients` |
| `GET /api/mcp/authorize` | sesión | igual | **409** «Llegaste al máximo de N conexiones de Claude (cuentan también las que desconectaste en las últimas 24 horas)…» si la persona tiene `MaxConnectionsPerUser` conexiones con otros clientes, vivas o revocadas en las últimas 24 h (revisión adversarial F-03) |
| `POST /api/mcp/authorize` | sesión + CSRF | igual | ídem (409, antes de emitir el código) |
| `POST /oauth/token` (`authorization_code`) | anónima | igual | **400** `invalid_grant` con el mismo motivo si al canjear (bajo bloqueo) la persona ya tiene el tope; el código queda gastado |
| `POST /api/studio/sessions/{id}/messages` | modelador | igual | **409** «Esta conversación lee registros de otra empresa: vuelve a esa empresa o empieza una conversación nueva.» si la sesión tiene `datos` y la empresa actual no es la suya |
| Planes (`setReports`, paquetes, reversión, Studio) | modelador | reporte con > 16 uniones | **400** «reporte 'X': usa N uniones a otros catálogos; el máximo es 16.» |
| `GET /api/reports/{e}/{r}`, `/export`, tableros | registros | igual | agrupar por una referencia con el campo para mostrar oculto: una fila «(sin acceso)» |
| Toda ruta con límite por dirección | — | — | La partición de una IPv6 es su /64. Con `Security:ForwardedProxies`, la dirección y el esquema de un proxy listado salen de `X-Forwarded-For`/`X-Forwarded-Proto` |

Studio (trabajador, sin ruta):

| Paso | Regla |
|---|---|
| Despachador | `ExpireAsync` y `ClaimAsync` en `try` separados: una falla al vencer no impide reclamar |
| `ExpireAsync` | Lista las sesiones vencidas y cierra cada una en su transacción (`UPDATE … WHERE Id = @id AND Status = 'running' AND LeaseUntil < ahora`, purga, bitácora); una que falla se registra en el log y se reintenta en la siguiente vuelta |
| `PurgeDataAsync` | Un `ContentJson` que no se puede leer se reemplaza por el mensaje «(contenido retirado: no se pudo leer)» del mismo lado |
| Apagado | `RunAsync` cierra con `interrupted`/`interrupted` (purga y bitácora) con un plazo de 5 s |
| `AnthropicStudioModel` | El *callback* del texto en vivo envuelve sus excepciones (`StudioCallbackFailed`); se desenvuelven y se relanzan tal cual |
| `FinishAsync` | `Summary` se conserva si no hay plan nuevo, la propuesta está vigente y el turno contestó **o no trae resumen propio** |

## 3. SPA
Sin cambios: el 409 de Conectar y el de Studio los muestran los avisos que ya existen (`role="alert"`).

## 4. Pruebas
- **Integración nueva:** `Hardening040Tests` (MCP: H1 CA1–CA5, con un `IStartupFilter` de prueba que fija la dirección
  remota desde una cabecera de la prueba, como lo haría la conexión TCP). `StudioTests.Hardening040.cs` (H2 CA1, CA3; H3 CA1,
  CA2). En `StudioProviderTests`: H2 CA4 con el adaptador real. En `Reports039Tests`: H4 CA2.
- **Unitarias nuevas:** `Hardening6Tests` (clave de dirección, `RenewSeconds`, purga de contenido ilegible, `MaxJoins` y
  su conteo, `ForwardedProxies` mal escritos detienen el arranque).
- **E2E modificado:** `canario.spec.ts` (H4 CA3), con `--veces=3`.
- **Contra el código anterior:** `npm --prefix web run it:regresion -- 99276fe "Hardening040Tests|H040"` y `e2e:regresion` del
  canario (las rutas a dos saltos no fallan con el código anterior: el alcance ya estaba bien; la prueba es de guardia, se
  dice en `verify.md`).

## 5. STRIDE
**Las 8 filas son obligatorias.**

| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones o caminos agrega o cambia? ¿Qué capacidad pide cada una? | Ninguna ruta nueva. Cambian `POST /oauth/register` (barrido y topes), `GET/POST /api/mcp/authorize` y el canje (tope por persona), `POST /api/studio/sessions/{id}/messages` (409 por empresa), `setReports` (tope de uniones) y la partición de los límites por dirección. Nueva cabecera de confianza: `X-Forwarded-For/Proto`, solo de proxies listados. | `Hardening040Tests › H1_CA1_mil_clientes_con_la_conexion_revocada_no_impiden_registrar`, `› H1_CA3_una_persona_no_pasa_del_tope_de_conexiones`, `› H1_CA5_solo_un_proxy_conocido_cambia_la_direccion_y_el_esquema`; `StudioTests › H040_H3_CA1_con_datos_otra_empresa_no_sigue_la_conversacion`; `Hardening6Tests › H4_CA1_un_reporte_con_mas_de_16_uniones_no_compila` |
| Caminos alternos | ¿Todos los caminos al mismo dato o efecto aplican la misma regla? | Registrar un cliente: solo `/oauth/register`. Una conexión nueva: solo el canje (la pantalla avisa, el canje manda). La dirección de una petición: una sola función (`ClientAddress.Key`) para `login`, `oauth`, global, políticas por persona sin persona y pendientes del MCP. Un reporte: pantalla, CSV/Excel y tablero llaman al mismo `RunAsync` (la etiqueta «(sin acceso)» vale en los tres); `setReports`, paquetes, reversión y Studio compilan con el mismo `PlanCompiler` (el tope de uniones vale en todos). La empresa de la conversación: solo `SendAsync` encola turnos de seguimiento. | `Hardening040Tests › H1_CA4_una_red_64_no_pasa_del_tope_de_pendientes`, `› H1_CA4_el_limite_oauth_agrupa_la_red_64`; `Reports039Tests › H040_H4_CA2_campo_para_mostrar_oculto_da_sin_acceso` (pantalla y exportación); `canario.spec.ts › recorre todas las rutas GET…` (reportes, exportación y tablero a dos saltos) |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | Los reportes no cambian de alcance (cada salto con `RecordFilter.ForRecordOrParent`); `FieldAccess.DisplayHidden` ahora agrupa como «sin acceso». Studio con `datos` no mezcla empresas en una conversación. El canario cubre dos saltos en reportes, exportación y tablero. | `Reports039Tests › H040_H4_CA2…`; `canario.spec.ts`; `StudioTests › H040_H3_CA1…` |
| Delegación | ¿Alguien puede otorgar, aprobar o heredar algo que no tiene? | Sin cambios de delegación: la conexión MCP sigue sin dar más que la persona. El tope por persona no se puede rodear con otra persona (cuenta por `UserId` de la conexión). `ForwardedProxies` no concede nada: solo cambia de dónde sale la dirección, y solo si la conexión TCP viene de un proxy listado. | `Hardening040Tests › H1_CA5…` (un proxy no listado no cambia la dirección) |
| Concurrencia | ¿Qué invariante protege? ¿10 a la vez? | Conexiones por persona ≤ tope: los canjes de una persona se forman con `sp_getapplock` (`erp-mcp-person:{id}`) y cuentan sin pistas de bloqueo (revisión adversarial F-01: con `UPDLOCK, HOLDLOCK` el conteo leía tokens en orden contrario al de la renovación). El barrido va en su propia transacción corta, antes de la serializable de los topes (F-02). Clientes vivos ≤ `MaxClients` y pendientes por clave ≤ tope: la transacción serializable del registro (como en la 038). Vencer: cada `UPDATE` exige `Status = 'running' AND LeaseUntil < ahora` (dos instancias no vencen dos veces la misma; un trabajador que renovó a tiempo no se vence). | `Hardening040Tests › H1_CA3_diez_canjes_a_la_vez_con_tope_dos_dan_dos`, `› F01_canjes_y_renovaciones_simultaneos_de_la_misma_persona_no_se_interbloquean`; `McpTests › F06_canjes_simultaneos_de_la_misma_persona_y_cliente_dejan_una_conexion` (sigue verde) |
| Fallas parciales | ¿Qué pasa si el proceso cae entre dos pasos? | Una sesión que no se puede vencer ya no detiene a las demás ni el reclamo. Al apagar, el turno se cierra y purga (si el plazo no alcanza, el vencimiento por *lease* lo hace al volver; si el servidor no vuelve y el plazo no alcanzó, queda el residuo de R-040-5). Una falla del ERP durante el flujo del proveedor se cobra. El barrido del registro borra en una transacción (tokens, códigos, conexiones y cliente) o nada. Filas, total y comparación de un reporte no son una sola lectura (D-11). | `StudioTests › H040_H2_CA1_una_sesion_que_no_se_puede_vencer_no_detiene_a_las_demas`, `› H040_H2_CA3_al_apagar_el_turno_se_cierra_interrumpido_y_purga_lo_leido`; `StudioProviderTests › H040_H2_CA4_una_falla_del_callback_no_es_del_proveedor`; `Hardening040Tests › H1_CA2_el_barrido_borra_clientes_sin_conexion_viva_con_sus_tokens`; D-11: [R-040-2](../../riesgos.md#040) |
| Límites | Tamaño, paginación, topes en memoria, tasa por usuario o IP | `MaxClients` (vivos), `PendingClientsPerAddress` (por /64), `MaxConnectionsPerUser` (20), límites por dirección por /64, `MaxJoins` (16), `RenewSeconds ≤ LeaseSeconds / 3`. Quien controla muchas /64 llena los pendientes una hora: [R-040-1](../../riesgos.md#040). MCP como camino de exportación: [R-040-3](../../riesgos.md#040). | `Hardening040Tests › H1_CA4…`; `Hardening6Tests › H1_CA4_la_clave_de_una_IPv6_es_su_red_64`, `› H2_CA2_RenewSeconds_no_pasa_de_un_tercio_del_lease`, `› H4_CA1…` |
| Semántica | Nulos, vacíos, fechas, redondeo | Sin dirección (pruebas, socket Unix) la clave es nula y cuenta como una sola, como hoy. Una IPv4 mapeada en IPv6 es su IPv4. Un reporte sin agrupaciones ni rutas tiene 0 uniones. «(sin acceso)» y «(sin valor)» siguen separados (una referencia vacía es «sin valor»). | `Hardening6Tests › H1_CA4…` (mapeada y nula); `Reports039Tests › H040_H4_CA2…` (la vacía sigue en «sin valor») |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** | Un cliente se hace pasar por un proxy para elegir su dirección (`X-Forwarded-For`) | Solo proxies listados; sin la opción no se lee | `Hardening040Tests › H1_CA5…` |
| **T** | Un registro manipulado (JSON ilegible) atora la cola de Studio | Purga tolerante y vencer sesión por sesión | `StudioTests › H040_H2_CA1…`; `Hardening6Tests › H2_CA1_una_salida_ilegible_se_purga_con_un_aviso` |
| **R** | Una falla del ERP se cobra como falla del proveedor (costo sin registrar) | Origen marcado desde el *callback* | `StudioProviderTests › H040_H2_CA4…` |
| **I** | Registros de la empresa A viajan en un turno de la B; datos sin purgar tras un apagado; «#id» donde el listado dice «(sin acceso)» | 409 por empresa con `datos`; cierre al apagar; etiqueta igual | `StudioTests › H040_H3_CA1…`, `› H040_H2_CA3…`; `Reports039Tests › H040_H4_CA2…` |
| **D** | Agotar el registro MCP (IPv6 o con cuenta); un reporte con 18 uniones por tarjeta | Vivos, barrido, tope por persona, /64; `MaxJoins` | `Hardening040Tests › H1_CA1…`, `› H1_CA3…`, `› H1_CA4…`; `Hardening6Tests › H4_CA1…`; residuo: [R-040-1](../../riesgos.md#040) |
| **E** | — (ningún cambio da privilegios) | — | — |
