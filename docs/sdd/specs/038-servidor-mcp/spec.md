# Spec 038 — Servidor MCP por persona

**Estado:** Implementado · **Depende de:** 018 (servidor de tokens), 021 (huella, *lease*), 029/031 (capacidad por ruta,
canario), 030 (integración), 033 (catálogo de herramientas y `RouteDispatcher`), 034 (credencial, efecto y límites en el
despachador) · **Origen:** roadmap «038 — Servidor MCP por persona» (antes 035 y 036): cada persona conecta **su** Claude al
ERP con exactamente sus permisos. Duda del dueño (2026-10-29): «¿funcionará MCP *code mode*, o eso depende del agente?».

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `StudioTools.All` + `StudioToolRunner` no dependen del bucle (033 CLARIFY 7) y `GET /api/studio/tools` publica el `inputSchema`; nadie los expone fuera de Studio. | Para que Claude (Claude.ai, Claude Code, Claude Desktop) consulte el ERP hay que copiar a mano lo que muestra el explorador, o crear un cliente OAuth de servicio (018), que es de un rol y no de la persona. |
| 2 | El servidor de tokens (`OAuthTokenService.IssueAsync`) solo acepta `client_credentials`; los clientes son cuentas de servicio que da de alta el superusuario (`OAuthClientService`). | No hay *authorization code* + PKCE: una persona no puede autorizar a su Claude a actuar como ella, ni elegir qué le deja ver, ni revocarlo. |
| 3 | `ApiKeyHandler.IsBearer` atrapa cualquier `Bearer …` que no sea `erpat_`; `BearerScope.Check` solo conoce claves de API y tokens de servicio. | Un token nuevo caería en el manejador de claves de API (401), o, si se agregara sin cuidado, podría llegar a cualquier ruta de `/api`. |
| 4 | El esquema de entrada de las herramientas declara `array` sin `items` y objetos sin `additionalProperties`; no hay esquema de salida; `leer_registros` pagina de 20 en 20 sin decir si hay más. | Un cliente en *code mode* (el modelo escribe código que llama a las herramientas) no puede generar tipos fiables ni saber cuándo dejar de paginar. |
| 5 | Los límites del despachador (034) son por minuto (`Security:RequestsPerMinute`, 600) y por política de ruta. | Un bucle de código que llama herramientas sin pausa consumiría la ventana de la persona entera (también la del navegador, R-034-2) antes de recibir un aviso claro de cuánto esperar. |

## Principio
Capacidad **genérica del motor**, sin código por herramienta ni por módulo:
- **El servidor MCP es otro cliente del mismo catálogo.** `tools/list` y `tools/call` son `StudioTools` + `StudioToolRunner`
  ejecutados con la persona del token (recargada en cada petición con su sello), sus alcances consentidos y su empresa: la
  herramienta **es** la ruta (capacidad, `RecordFilter`, `FieldAccess`, empresa). Lo que la persona no puede en el ERP, por
  MCP tampoco.
- **La persona autoriza, el ERP no confía en el cliente.** OAuth 2.1: *authorization code* con PKCE (S256) obligatorio,
  clientes públicos registrados dinámicamente (RFC 7591), consentimiento por alcances (`modelo` siempre, `datos` opcional),
  token ligado al recurso `/mcp` (RFC 8707) y revocación por la persona.
- **Solo lectura.** Por MCP no se escribe nada en el entorno real (constitución II.8): el catálogo expuesto son las
  herramientas del entorno real, que el despachador limita a rutas que no escriben. No hay herramienta «ejecuta este
  código» en el ERP: el *code mode* es del cliente; el servidor se lo pone fácil con esquemas precisos y topes para ráfagas.

## Historias

**H1 — Conectar Claude al ERP.** Como *persona con cuenta*, quiero agregar el ERP como conector MCP en mi Claude con solo su
URL, para no configurar secretos a mano.
- CA1: `POST /mcp` sin token responde 401 con `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource/mcp"`.
- CA2: los metadatos del recurso (RFC 9728) y del servidor de autorización (RFC 8414) dicen el emisor, los *endpoints*, los
  alcances `modelo` y `datos`, `S256` como único método PKCE, los *grants* `authorization_code` y `refresh_token` y que los
  clientes son públicos (`none`).
- CA3: `POST /oauth/register` (RFC 7591, anónimo, limitado por dirección) registra un cliente público con su nombre y sus
  URL de retorno (https, o `http` de *loopback*); rechaza secretos, *grants* ajenos, fragmentos, credenciales en la URL y
  esquemas no web, con el error de §3.2.2.

**H2 — Consentimiento por alcances.** Como *persona*, quiero ver quién pide acceso, a dónde lo va a mandar y qué podrá
hacer, y elegir si comparte registros, para no entregar más de lo que quiero.
- CA1: `/oauth/authorize` lleva a la pantalla **Conectar** (con sesión; sin ella, primero el inicio de sesión y luego
  vuelve). Muestra el nombre del cliente como dato no verificado, el **sitio** al que volverá el acceso (destacado, con
  aviso si no es uno conocido), quién entra y los alcances: `modelo` (siempre) y `datos` (apagado por omisión, con aviso de
  que los registros viajarán a ese cliente).
- CA2: una petición inválida antes de validar el retorno (cliente desconocido, URL de retorno que no es exactamente una
  registrada) muestra el error **sin redirigir**; una inválida después (sin PKCE S256, alcance desconocido, `resource` que
  no es este servidor) se explica en la pantalla y, si la persona elige «Volver a *sitio*», vuelve al cliente con `error`,
  `state` e `iss` (revisión adversarial F-03: nunca se redirige sola).
- CA3: «Permitir» devuelve al cliente un código de un solo uso (2 min) con `state` e `iss`; «Cancelar» devuelve
  `access_denied`. Pedir `datos` y no marcarlo da un acceso sin `datos`.

**H3 — Tokens.** Como *cliente MCP*, quiero cambiar el código por tokens y renovarlos sin volver a pedir permiso, y como
*dueño*, quiero que un código o un token robado sirva lo menos posible.
- CA1: `POST /oauth/token` con `grant_type=authorization_code` exige el `client_id`, el `redirect_uri` exacto y el
  `code_verifier` que corresponde al reto (PKCE S256); da `access_token` (1 h), `refresh_token` (30 días) y el `scope`.
- CA2: un código usado dos veces falla y revoca la conexión que creó; uno vencido o de otro cliente falla con `invalid_grant`.
- CA3: `grant_type=refresh_token` rota el *refresh token*; reusar uno ya rotado revoca la conexión (detección de reuso).
- CA4: el token solo sirve en `/mcp`: en cualquier ruta de `/api` u `/odata`, 403. Si la persona se desactiva o cambia su
  contraseña o sus roles (cambia su sello), el token y su renovación dejan de servir.

**H4 — Herramientas con exactamente los permisos de la persona.** Como *persona*, quiero que mi Claude vea y haga solo lo
que yo puedo; como *dueño*, quiero que el MCP no sea un camino nuevo al dato.
- CA1: `initialize`, `ping`, `tools/list` y `tools/call` (JSON-RPC 2.0, *Streamable HTTP*, respuesta JSON). `tools/list` da
  solo las herramientas del entorno real cuyo alcance consintió la persona y cuya capacidad tiene; las de la copia aislada y
  `entregar` no se exponen.
- CA2: una herramienta da el mismo 403 que la API (mismo mensaje) a quien no tiene el permiso; el canario de la 029 no sale
  por ninguna herramienta MCP a quien no lo ve (con control positivo del administrador).
- CA3: sin el alcance `datos`, `leer_registros` y `leer_registro` no aparecen ni se ejecutan (error de herramienta).
- CA4: el servidor valida `Origin` (DNS *rebinding*), rechaza lotes JSON-RPC, cuerpos de más de 512 KB y versiones de
  protocolo que no conoce; los errores no filtran detalles (constitución II.6).

**H5 — Pensado para *code mode*.** Como *persona que usa un cliente en code mode*, quiero esquemas y resultados tipables,
paginación explícita y topes que avisen cuánto esperar, para que el código generado funcione sin adivinar.
- CA1: cada herramienta publica `inputSchema` con `additionalProperties: false`, `items` en los arreglos y mínimos en los
  enteros, un `outputSchema` y anotaciones (`readOnlyHint`, `idempotentHint`, `openWorldHint: false`); el
  `structuredContent` de cada respuesta cumple su `outputSchema` (probado herramienta por herramienta).
- CA2: `leer_registros` acepta `page` y `pageSize` (1…100) y responde `items`, `total`, `page`, `pageSize` y `nextPage`
  (nulo en la última).
- CA3: tope por persona para ráfagas (cubeta de fichas: `Mcp:Burst` llamadas seguidas y `Mcp:CallsPerMinute` de recarga)
  además de los del despachador; al agotarse, error de herramienta con `retryAfterSeconds`. Un cliente que llama 200
  herramientas en bucle (y 40 en paralelo) recibe exactamente lo que permiten los topes, y nunca una herramienta fuera de
  sus permisos.
- CA4: un resultado de más de `Mcp:MaxResultBytes` no se recorta a medias: es un error que pide filtrar o paginar.

**H6 — Ver y revocar mis conexiones.** Como *persona*, quiero ver qué clientes tienen acceso a mi cuenta y desconectarlos,
para retirar un permiso que ya no quiero.
- CA1: la pantalla **Conexiones de Claude** (menú de usuario) da la URL del servidor MCP para copiar y la lista de mis
  conexiones vigentes: cliente, sitio de retorno, alcances, cuándo se conectó y su último uso.
- CA2: «Desconectar» (con confirmación) revoca la conexión: su token deja de servir en la siguiente llamada. Una persona no
  ve ni revoca conexiones de otra (404).
- CA3: `POST /oauth/revoke` (RFC 7009) revoca la conexión del token que se le da, si es del cliente que lo pide; siempre 200.
- CA4: la bitácora registra `mcp.connect` (al cambiar el código), `mcp.revoke` y `mcp.reuse` (código o *refresh token*
  reusados), a nombre de la persona y sin tokens.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (6: no) · más de ~2 500 líneas de producto
   estimadas (~1 900: no) · seguridad o concurrencia junto con UI nueva (**sí**: consentimiento y conexiones) · más de una
   pantalla principal (no: **Conectar** es un paso intermedio sin navegación, como el inicio de sesión; la principal es
   **Conexiones de Claude**). → **Un criterio: no se divide.** Lo que la habría hecho crecer (copia aislada y propuestas por
   MCP) queda fuera (decisión 2).
2. **Copia aislada y `entregar` por MCP: fuera.** La copia aislada vive con la conversación de Studio (`meta.StudioSession`:
   *lease*, marca, barrido, una copia viva por persona) y MCP no tiene un fin de conversación: exponerla exige un ciclo de
   vida propio (otro trabajo asíncrono, otra base por cliente) y escrituras con idempotencia. Va a una spec propia,
   **040 — MCP 2: copia aislada y propuestas** (después de Ventas). Mientras, el alcance `aislado` no se ofrece por MCP y
   ninguna herramienta MCP escribe: todas son de lectura e idempotentes (la regla de idempotencia o huella en escrituras
   queda escrita para la 040).
3. **Alcances del token MCP = alcances de Studio** (`modelo`, `datos`), no los de la 018 (`odata.read`, `data.*`): el token
   solo sirve en `/mcp` y la herramienta aplica la regla de la ruta con la persona. Por eso el despachador recibe la
   credencial nula (la persona) y el límite del token está en el runner (alcances) y en el destino real (solo lectura).
4. **Registro dinámico abierto, con defensa en el consentimiento.** Claude registra su cliente con RFC 7591; cerrarlo a una
   lista obligaría al superusuario a dar de alta cada Claude. El riesgo (un cliente que se llama «Claude» y devuelve el acceso
   a otro sitio) se mitiga mostrando el **sitio** de retorno y un aviso si no está en `Mcp:TrustedRedirectHosts`
   (`claude.ai`, `claude.com`, *loopback*): R-038-1. Los clientes que nunca canjearon un código se borran a la hora;
   tope de pendientes por dirección (`Mcp:PendingClientsPerAddress`) y total (`Mcp:MaxClients`) (revisión adversarial F-04).
5. **Una conexión = un consentimiento.** Volver a autorizar el mismo cliente revoca la conexión anterior de esa persona con
   ese cliente. La conexión guarda el sello de la persona: si cambia (contraseña, roles, desactivación), la conexión muere y
   hay que volver a conectar. No hay vida máxima absoluta: 30 días sin renovar la terminan (R-038-3).
6. **Sin sesión MCP (`Mcp-Session-Id`) ni flujo SSE del servidor.** Todas las respuestas son JSON; `GET /mcp` da 405. El
   protocolo lo permite y evita estado por conexión. Versiones: `2025-03-26`, `2025-06-18` y `2025-11-25`.
7. **Topes:** cubeta de fichas por persona (`Mcp:Burst` 60, `Mcp:CallsPerMinute` 120) + los del despachador (034). Sin cupo
   diario (las herramientas no cuestan tokens al ERP: el costo del modelo es del cliente).
8. **Habilitado por omisión** (`Mcp:Enabled`, true); con false, todo lo de MCP responde 404. URL pública por configuración
   (`Mcp:PublicUrl`) o, si falta, la de la petición (detrás de un proxy sin `ForwardedHeaders`, R-021-5, conviene fijarla).

## Fuera de alcance (anotado en el roadmap y en riesgos)
- Copia aislada, `entregar` y escrituras por MCP con idempotencia: **040 — MCP 2**.
- *Client ID Metadata Documents* (registro por URL, borrador de MCP 2025-11-25) y CORS para clientes de navegador (R-038-2).
- Que el superusuario vea y revoque las conexiones de todos (hoy: desactivar o cambiar el sello de la persona; R-038-4).
- Notificaciones del servidor (`listChanged`), recursos y *prompts* de MCP.
