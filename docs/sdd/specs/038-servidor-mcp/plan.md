# Plan 038 — Diseño técnico

## Piezas
```
 Claude (cliente MCP)                                ERP
 ──────────────────                                  ───
 POST /mcp (sin token) ───────────────────────────▶  401  WWW-Authenticate: Bearer resource_metadata=".../.well-known/oauth-protected-resource/mcp"
 GET  /.well-known/oauth-protected-resource[/mcp] ─▶ { resource, authorization_servers, scopes_supported }
 GET  /.well-known/oauth-authorization-server ────▶ { issuer, authorization_endpoint, token_endpoint, registration_endpoint, revocation_endpoint, … }
 POST /oauth/register (RFC 7591) ─────────────────▶ meta.McpClient (público, sin secreto)
 navegador: GET /oauth/authorize?… ───────────────▶ 302 /conectar?…  (SPA; sin sesión → /login → vuelve)
     SPA: GET  /api/mcp/authorize?…   (sesión)      valida cliente y retorno; da lo que muestra la pantalla
     SPA: POST /api/mcp/authorize     (sesión+CSRF) meta.McpCode (2 min, un uso, reto S256) → { redirect: retorno?code&state&iss }
 POST /oauth/token grant_type=authorization_code ─▶ código + verificador → meta.McpGrant (conexión) + access (1 h) + refresh (30 d)
 POST /oauth/token grant_type=refresh_token ──────▶ rota el refresh; reuso → revoca la conexión
 POST /mcp  Authorization: Bearer erpmt_… ────────▶ McpTokenHandler: token vigente, conexión sin revocar, persona con el mismo sello
     initialize · ping · tools/list · tools/call ── McpServer → StudioToolRunner (alcances de la conexión, persona, sin copia aislada)
                                                      └─ RouteDispatcher (ruta de lectura, capacidad, RecordFilter, FieldAccess, límites 034)
 POST /oauth/revoke (RFC 7009) ───────────────────▶ revoca la conexión del token
 SPA «Conexiones de Claude»: GET /api/mcp/connections · DELETE /api/mcp/connections/{id}
```
El token MCP no es una credencial de la API: solo `/mcp` lo acepta (`BearerScope`), y `/mcp` solo acepta ese token (el
selector de esquema manda `/mcp` al manejador MCP aunque traiga cookie: sin CSRF posible).

## 1. Modelo de datos (Bootstrap, idempotente)
```sql
meta.McpClient (Id int IDENTITY PK, ClientId varchar(40) UNIQUE, Name nvarchar(100), RedirectUris nvarchar(max) /* JSON */,
  CreatedAt datetime2, CreatedIp varchar(45) NULL, LastAuthorizedAt datetime2 NULL)
meta.McpCode (Id bigint IDENTITY PK, CodeHash binary(32) UNIQUE, ClientId int FK McpClient, UserId int FK User,
  SecurityStamp uniqueidentifier, Scopes nvarchar(60), RedirectUri nvarchar(500), Challenge varchar(128), Resource nvarchar(500),
  ExpiresAt datetime2, UsedAt datetime2 NULL, GrantId int NULL)
meta.McpGrant (Id int IDENTITY PK, ClientId int FK McpClient, UserId int FK User, SecurityStamp uniqueidentifier,
  Scopes nvarchar(60), Resource nvarchar(500), CreatedAt datetime2, LastUsedAt datetime2 NULL, RevokedAt datetime2 NULL,
  RevokedReason nvarchar(20) NULL /* person replaced reuse client stamp */)   INDEX (UserId, RevokedAt)
meta.McpToken (Id bigint IDENTITY PK, GrantId int FK McpGrant, Kind char(1) /* a r */, TokenHash binary(32) UNIQUE,
  ExpiresAt datetime2, UsedAt datetime2 NULL /* refresh rotado */, CreatedAt datetime2)   INDEX (GrantId)
```
Formatos (256 bits aleatorios, en la base solo su SHA-256): cliente `erpmcp_` + 16 hex, código `erpmc_`, acceso `erpmt_`,
renovación `erpmr_` (+ 43 base64url). Sin operaciones nuevas del Plan de Cambios.

## 2. Contrato de la API
| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `GET /.well-known/oauth-protected-resource` y `…/mcp` | anónima | — | RFC 9728: `{ resource, authorization_servers: [issuer], scopes_supported: ["modelo","datos"], bearer_methods_supported: ["header"], resource_name }` |
| `GET /.well-known/oauth-authorization-server` | anónima | — | RFC 8414: `{ issuer, authorization_endpoint, token_endpoint, registration_endpoint, revocation_endpoint, response_types_supported: ["code"], grant_types_supported: ["authorization_code","refresh_token"], code_challenge_methods_supported: ["S256"], token_endpoint_auth_methods_supported: ["none"], revocation_endpoint_auth_methods_supported: ["none"], scopes_supported, authorization_response_iss_parameter_supported: true }` |
| `POST /oauth/register` | anónima · `Writes(Security, NoGrant)` · límite `oauth` (por dirección) | JSON RFC 7591 `{ client_name, redirect_uris, grant_types?, response_types?, token_endpoint_auth_method? }` (≤ 8 KB) | 201 `{ client_id, client_id_issued_at, client_name, redirect_uris, grant_types, response_types, token_endpoint_auth_method: "none" }` · 400 `{ error: invalid_redirect_uri \| invalid_client_metadata, error_description }` · 429 |
| `GET /oauth/authorize` | anónima · `ReadsOnly` | consulta OAuth | 302 a `/conectar?<la misma consulta>` |
| `GET /api/mcp/authorize` | sesión | consulta OAuth | `{ client: { name, redirectHost, trusted }, user: { username, displayName }, scopes: [{ name, required, requested }] }` · 400 `{ title }` (no se redirige: cliente o retorno inválidos) · `{ redirect }` si el error va al cliente |
| `POST /api/mcp/authorize` | sesión · `Writes(Security, NoGrant)` | `{ …consulta OAuth, approve: bool, scopes: string[] }` | `{ redirect }` (con `code`, `state`, `iss`; o `error=access_denied`) · 400 igual que el GET |
| `POST /oauth/token` (extiende la 018) | anónima · límite `oauth` | `grant_type=authorization_code` (`code`, `redirect_uri`, `client_id`, `code_verifier`, `resource?`) o `refresh_token` (`refresh_token`, `client_id`, `resource?`) | `{ access_token, token_type: "Bearer", expires_in, refresh_token, scope }` · 400 `invalid_grant` / `invalid_request` / `invalid_target` · 401 `invalid_client` |
| `POST /oauth/revoke` | anónima · `Writes(Security, NoGrant)` · límite `oauth` | formulario `token`, `client_id` | 200 siempre (RFC 7009 §2.2) · 400 sin `token` |
| `POST /mcp` | sesión (solo token MCP) · `ReadsOnly` · límite propio (cubeta por persona) | JSON-RPC 2.0 (un mensaje) | 200 JSON-RPC · 202 (notificación) · 400 lote/JSON inválido/versión · 401 · 403 `Origin` · 413 |
| `GET /mcp`, `DELETE /mcp` | sesión (solo token MCP) | — | 405 (sin flujo del servidor ni sesión MCP) |
| `GET /api/mcp/connections` | sesión | — | `McpConnectionDto[]` vigentes de la persona: `{ id, client, redirectHost, scopes, createdAt, lastUsedAt? }`, más `{ serverUrl }` |
| `DELETE /api/mcp/connections/{id:int}` | sesión · `Writes(Session)` | — | 204 · 404 si no es suya o ya revocada |

Errores del flujo de autorización (RFC 6749 §4.1.2.1): `invalid_request` (falta o sobra un parámetro, `code_challenge_method`
distinto de `S256`, reto mal formado), `unsupported_response_type`, `invalid_scope` (alcance desconocido, p. ej. `aislado`),
`invalid_target` (RFC 8707: `resource` que no es este `/mcp`), `access_denied`. Todos con `state` e `iss` (RFC 9207).

### MCP (JSON-RPC sobre *Streamable HTTP*, respuestas JSON)
- `initialize` → `{ protocolVersion, capabilities: { tools: { listChanged: false } }, serverInfo: { name: "erp-ai", title: "ERP AI", version }, instructions }`.
  Versión: la del cliente si es `2025-03-26`, `2025-06-18` o `2025-11-25`; si no, la más nueva. Cabecera `MCP-Protocol-Version`
  desconocida → 400.
- `tools/list` → `{ tools: [{ name, title, description, inputSchema, outputSchema, annotations: { title, readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false } }] }`
  (sin paginar: son ≤ 14).
- `tools/call { name, arguments }` → `{ content: [{ type: "text", text: <JSON> }], structuredContent, isError: false }`; error de
  la herramienta (permiso, entrada, límite, tamaño) → `{ content: [{ type: "text", text: <JSON del error> }], isError: true }`
  con `{ error, status?, retryAfterSeconds? }`. Herramienta desconocida o no permitida → error JSON-RPC `-32602`.
- Método desconocido `-32601`; JSON inválido `-32700`; lote o mensaje inválido `-32600`.

Salida estructurada (siempre un objeto): un arreglo de la API va en `{ items }`; `leer_registros` →
`{ items, total, page, pageSize, nextPage }`; `compilar_plan` → `{ valid, errors, steps, warnings }`. Cada herramienta declara su
`outputSchema` en `Studio/McpTools.cs` (junto al catálogo; las de Studio no cambian para el modelo de Studio salvo `pageSize`).

### Configuración (`Mcp:*`)
| Clave | Por omisión | Uso |
|---|---|---|
| `Enabled` | true | Con false, `/mcp`, metadatos, registro, autorización y conexiones dan 404 |
| `PublicUrl` | (la de la petición) | Origen público (emisor y recurso `…/mcp`) |
| `AccessTokenMinutes` / `RefreshTokenDays` / `CodeSeconds` | 60 / 30 / 120 | Vidas |
| `Burst` / `CallsPerMinute` | 60 / 120 | Cubeta de fichas por persona para `tools/call` |
| `MaxResultBytes` | 262 144 | Tamaño de un resultado |
| `MaxClients` / `PendingClientsPerAddress` | 1 000 / 20 | Clientes registrados; sin canjear por dirección (los que no canjean en 1 h se barren) |
| `TrustedRedirectHosts` | `claude.ai`, `claude.com` (+ *loopback*) | Sin aviso en el consentimiento |
| `AllowedOrigins` | (ninguno además del propio) | `Origin` aceptado en `/mcp` |

Auditoría: `mcp.connect` (conexión creada al cambiar el código: cliente, alcances), `mcp.revoke` (por la persona, por
RFC 7009, por reconexión), `mcp.reuse` (código o *refresh token* reusados: conexión revocada). Clasificadas en
`AuditFilter.OtherActions` (son de la cuenta de la persona, no de un catálogo).

## 3. SPA
- **Conectar** (`/conectar`, fuera del `Layout`, con sesión: `RequireAuth` guarda la consulta y vuelve tras iniciar sesión).
  Tarjeta como la del inicio de sesión: logo, «Conectar *nombre* con ERP AI», «El acceso volverá a **sitio**» (y aviso
  `role="alert"` si no es conocido), «Entrarás como *persona*», alcances (`modelo` marcado y deshabilitado, `datos` apagado con
  aviso), «Permitir» y «Cancelar». Al permitir o cancelar, `window.location.assign(redirect)`. Error sin redirección: `Alert`
  con el motivo y enlace al inicio. Foco en el título al cargar.
- **Conexiones de Claude** (`/conexiones`, en el `Layout`, desde el menú de usuario): URL del servidor con botón «Copiar»,
  tres pasos para agregarlo en Claude, tabla de conexiones (cliente, sitio, alcances, conectado, último uso) y «Desconectar»
  con diálogo de confirmación (constitución IV.4). Estado vacío. Claro/oscuro, 390 px.

## 4. Pruebas
- **Unitarias** (`tests/Erp.Tests/McpCoreTests.cs`, nuevo): PKCE S256 (vector del RFC 7636 apéndice B), validación de URL de
  retorno, formatos de tokens, selector de esquema y `BearerScope` del token MCP, esquemas de entrada (cada arreglo con
  `items`, `additionalProperties: false`), negociación de versión, el catálogo MCP no contiene herramientas que escriben.
- **Integración** (`tests/Erp.IntegrationTests/McpTests.cs`, nuevo; SQL real): flujo completo como un cliente (registro →
  autorizar → código → token → `initialize` → `tools/list` → `tools/call`), errores de cada paso, reuso de código y de
  *refresh*, concurrencia (10 canjes del mismo código, 10 renovaciones del mismo *refresh*), revocación, sello, permisos y
  canario, *code mode* (200 llamadas en bucle y 40 en paralelo contra la cubeta), `outputSchema` de cada herramienta.
- **E2E** (`web/e2e/mcp.spec.ts`, nuevo): consentimiento (con y sin `datos`, cancelar, sitio desconocido, retorno inválido sin
  redirigir, volver tras iniciar sesión), conexiones (lista, copiar, desconectar con confirmación), a11y y capturas de las dos
  pantallas. `docs/sdd/rutas.md` regenerado.

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas y caminos nuevos | 12 rutas (tabla de arriba): 6 anónimas en la lista blanca (metadatos, registro, `authorize` del servidor, revocar), `/mcp` solo con token MCP, 3 de sesión. Un camino nuevo al catálogo de Studio: `tools/call`, que despacha rutas existentes de lectura. | `RouteInventoryTests` (lista blanca y snapshot) · `McpCoreTests › El_catalogo_MCP_solo_tiene_herramientas_de_lectura_del_entorno_real` · `McpTests › H4_CA1_tools_list_da_solo_lo_que_la_persona_puede` |
| Caminos alternos | ¿Otro camino al mismo dato o efecto? | La herramienta **es** la ruta (misma capacidad, `RecordFilter`, `FieldAccess`, empresa). El token MCP no llega a `/api` ni `/odata` (403) y `/mcp` no acepta cookie ni clave de API ni token de servicio. Escribir: ningún camino (solo herramientas del entorno real; el despachador niega escrituras). | `McpTests › H3_CA4_el_token_MCP_no_sirve_fuera_de_mcp` · `H4_CA4_mcp_no_acepta_cookie_ni_otras_credenciales` · `H4_CA2_una_herramienta_da_el_mismo_403_que_la_API` |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | Sí: `leer_registros`/`leer_registro` son `GET /api/data/...` con la persona recargada; alcance `datos` apagado por omisión en el consentimiento. | `McpTests › H4_CA2_el_canario_no_sale_por_MCP` (con control positivo) · `H4_CA3_sin_datos_no_hay_herramientas_de_registros` |
| Delegación | ¿Alguien otorga u obtiene lo que no tiene? | La persona solo delega su propia identidad y solo los alcances que elige; el cliente nunca recibe más que la persona (recargada en cada petición con su sello: cambiar contraseña, roles o desactivar mata la conexión). Nadie autoriza por otro: el consentimiento usa la sesión de quien entra; el código queda ligado a esa persona, al cliente, al retorno y al reto. Una persona no ve ni revoca conexiones ajenas. | `H3_CA4_si_cambia_el_sello_el_token_y_la_renovacion_mueren` · `H6_CA2_otra_persona_no_ve_ni_revoca_mi_conexion` · `H3_CA1_el_codigo_exige_cliente_retorno_y_verificador` |
| Concurrencia | Invariantes | Un código se canjea una vez (`UPDATE … WHERE UsedAt IS NULL … OUTPUT`); un *refresh* se rota una vez (igual) y su reuso revoca la conexión; volver a autorizar revoca la anterior en la misma transacción. Cubeta por persona sin carreras (limitador de .NET). | `McpTests › H3_CA2_diez_canjes_del_mismo_codigo_dan_un_token` · `H3_CA3_diez_renovaciones_del_mismo_refresh_dan_una` · `F06_canjes_simultaneos_de_la_misma_persona_y_cliente_dejan_una_conexion` (`UPDLOCK, HOLDLOCK`) · `H5_CA3_cuarenta_llamadas_en_paralelo_respetan_la_cubeta` |
| Fallas parciales | Caída entre pasos | Canje y emisión en una transacción (marca del código, conexión, tokens y bitácora): si cae, el código sigue sin usar y vence a los 2 min; no hay tokens sin conexión. Rotar el *refresh* igual. Revocar en una sentencia. Nada asíncrono. | `McpTests › Una_falla_al_emitir_no_consume_el_codigo` (falla inyectada) |
| Límites | Tamaño, frecuencia | Cuerpo de `/mcp` ≤ 512 KB y un mensaje (sin lotes); registro ≤ 8 KB, ≤ 5 URL de retorno de ≤ 500; nombre ≤ 100; `state` ≤ 500; tope de clientes y barrido de no autorizados; anónimas con el límite `oauth` por dirección; `tools/call` con cubeta por persona + límites del despachador; `pageSize` ≤ 100; resultado ≤ `Mcp:MaxResultBytes`. | `H1_CA3_el_registro_rechaza_lo_invalido_y_tiene_tope` · `F04_un_cliente_que_nunca_canjeo_se_barre_y_cada_direccion_tiene_tope_de_pendientes` · `F05_revocar_rechaza_multipart` · `F01_un_cuerpo_con_UTF8_invalido_es_400_y_no_500` · `H4_CA4_mcp_rechaza_lotes_cuerpos_grandes_y_versiones` · `H5_CA3_un_bucle_de_200_llamadas_recibe_lo_que_permiten_los_topes` · `H5_CA4_un_resultado_grande_es_un_error_que_pide_paginar` |
| Semántica | Nulos, fechas, paginación | Fechas de conexiones en UTC (como Studio); `nextPage` nulo en la última página; `retryAfterSeconds` entero ≥ 1; los valores de registros salen como los da la API (nulos incluidos). | `H5_CA2_leer_registros_pagina_con_nextPage` · `H5_CA1_cada_salida_cumple_su_outputSchema` |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** Cliente impostor: se registra como «Claude» y devuelve el acceso a otro sitio (phishing de consentimiento) | Alta | La pantalla muestra el **sitio** de retorno destacado y un aviso si no está en `Mcp:TrustedRedirectHosts`; el nombre se marca como no verificado; `datos` apagado por omisión; la conexión se ve y se revoca. | `mcp.spec.ts › H2 CA1: un sitio de retorno desconocido se avisa` · `F09_el_nombre_del_cliente_no_admite_caracteres_de_formato` · `McpCoreTests › Un_sitio_conocido_es_el_de_la_lista_o_el_propio_equipo` (punycode) · [R-038-1](../../riesgos.md#038) (la persona puede aceptar igual) |
| **S/T** Robo o inyección del código (retorno abierto, interceptación) | Alta | Retorno exacto a uno registrado (nunca se redirige a otro, ni con error); PKCE S256 obligatorio; código de un uso, 2 min, ligado a cliente, retorno, persona y recurso; `iss` en la respuesta (RFC 9207). | `H2_CA2_un_retorno_no_registrado_no_redirige` · `mcp.spec.ts › H2 CA2: un error del cliente (alcance desconocido) se explica y la persona decide volver` (F-03: la pantalla no redirige sola) · `H3_CA1_…` · `H3_CA2_…` · `McpCoreTests › El_reto_S256_coincide_con_el_RFC_7636` |
| **E** El token se usa como credencial de la API (más allá de `/mcp`) | Alta | `BearerScope`: un token MCP solo pasa en `/mcp`; el selector manda `/mcp` al manejador MCP (la cookie no cuenta ahí). | `H3_CA4_el_token_MCP_no_sirve_fuera_de_mcp` · `McpCoreTests › BearerScope_niega_el_token_MCP_fuera_de_mcp` |
| **E** Una herramienta hace más que la persona (escribe, copia aislada como su administrador) | Alta | Solo herramientas del entorno real (el despachador niega escrituras); sin `SandboxAsync` (lanza «no disponible»); capacidad publicada exigida por el runner (034). | `McpCoreTests › El_catalogo_MCP_solo_tiene_herramientas_de_lectura_del_entorno_real` · `H4_CA1_…` · `H4_CA2_…` |
| **T** CSRF / clic forzado en el consentimiento | Media | `POST /api/mcp/authorize` con cookie `SameSite=Strict` y cabecera anti-CSRF; `frame-ancestors 'none'`; la decisión la manda la SPA. | `H2_CA3_permitir_sin_cabecera_anti_CSRF_da_403` |
| **T** DNS *rebinding* contra `/mcp` | Media | `Origin` distinto del propio y de `Mcp:AllowedOrigins` → 403. | `H4_CA4_un_origin_ajeno_da_403` · `F07_los_metadatos_no_se_guardan_en_cache` (con `Mcp:PublicUrl` vacío, el origen sale de `Host`: fijarla en producción) |
| **I** Fuga por errores | Media | Errores OAuth con su código de RFC y texto fijo; JSON-RPC sin trazas; herramientas con los mensajes de la API. | `H4_CA4_…` · `H1_CA3_…` |
| **R** Repudio de una conexión | Baja | `mcp.connect`, `mcp.revoke`, `mcp.reuse` a nombre de la persona; `LastUsedAt` por conexión. | `H6_CA4_la_bitacora_registra_conectar_revocar_y_reuso` |
| **D** Registro masivo de clientes / ráfagas | Media | Límite por dirección, tope de clientes, barrido de no autorizados; cubeta por persona y despachador. | `H1_CA3_…` · `H5_CA3_…` |
| **I** Un *refresh token* robado | Media | Rotación con detección de reuso (revoca la conexión), 30 días, ligado al cliente; la persona desconecta. | `H3_CA3_…` · [R-038-3](../../riesgos.md#038) |
