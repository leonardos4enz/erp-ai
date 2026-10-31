# ADR-0009 — Servidor MCP por persona: OAuth con PKCE, token solo para `/mcp` y catálogo de Studio de solo lectura

**Estado:** Aceptado · **Fecha:** 2026-10-29 · **Spec:** [038](../sdd/specs/038-servidor-mcp/spec.md)

## Contexto
El dueño quiere que cada persona conecte su Claude (Claude.ai, Claude Desktop, Claude Code) al ERP con exactamente sus
permisos. MCP define el transporte (*Streamable HTTP*, JSON-RPC 2.0) y delega la autorización en OAuth 2.1: el cliente
descubre el servidor de autorización por los metadatos del recurso (RFC 9728), se registra solo (RFC 7591) y pide un código
con PKCE. El ERP ya tiene un servidor de tokens (018) para cuentas de servicio (`client_credentials`) y un catálogo de
herramientas que ejecuta rutas con la identidad de la persona (033, ADR-0008). La constitución exige sesión por cookie sin
tokens en el navegador (II.3, ADR-0002), mínimo privilegio (II.2) y que la IA no actúe con privilegios propios (II.8).

## Decisión
1. **El token MCP no es una credencial de la API.** Es un token opaco (`erpmt_`, SHA-256 en la base) ligado a una
   *conexión* (persona + cliente + alcances + sello de seguridad) y al recurso `/mcp` (RFC 8707). `BearerScope` lo niega en
   cualquier otra ruta y el selector de esquema manda `/mcp` siempre a su manejador (la cookie no cuenta ahí: sin CSRF). La
   persona se recarga en cada petición con su sello: cambiar contraseña, roles o desactivarla mata la conexión.
2. **Las herramientas son las de Studio del entorno real** (`StudioTools` + `StudioToolRunner` + `RouteDispatcher`), con la
   persona del token y los alcances de la conexión (`modelo`, `datos`). El despachador recibe la credencial nula (la persona):
   el límite del token está en los alcances (runner) y en que el destino real solo admite rutas que no escriben. La copia
   aislada y `entregar` no se exponen (su ciclo de vida es el de una conversación de Studio; va a la 043, MCP 2; numerada 040 al escribir este ADR y recorrida en la 039 y la 040).
3. **Clientes públicos con registro dinámico abierto y consentimiento de la persona.** Sin secretos de cliente (PKCE S256
   obligatorio, código de un uso y 2 min), retorno exacto a uno registrado (https o *loopback*), *refresh token* rotado con
   detección de reuso. El consentimiento es una pantalla de la SPA con la sesión de la persona (cookie + CSRF); muestra el
   sitio de retorno y avisa si no es conocido. Se descartó exigir que el superusuario dé de alta cada cliente: cada Claude
   se registra con su propia URL y la persona sería quien pide el alta.
4. **Sin sesión MCP ni flujo del servidor**: cada `POST /mcp` es un mensaje y su respuesta JSON. Sin estado por conexión.
5. **El *code mode* es del cliente.** El servidor publica `inputSchema` estrictos, `outputSchema` y `structuredContent`,
   paginación explícita y una cubeta de fichas por persona con `retryAfterSeconds`; nunca una herramienta que ejecute código
   en el ERP (II.8).

## Consecuencias
- (+) Un camino nuevo al ERP sin una regla de autorización nueva: la herramienta es la ruta; el canario de la 029 se recorre
  también por MCP.
- (+) La persona ve y revoca sus conexiones; el administrador corta todas las de alguien cambiando su sello.
- (−) El registro abierto permite que un sitio se presente como «Claude»: la defensa es el consentimiento (R-038-1).
- (−) Dos renovaciones simultáneas del mismo *refresh token* revocan la conexión (detección de reuso estricta): el cliente
  vuelve a conectar (R-038-3).
- Punto de entrada nuevo: auditoría externa 7 al cerrar la 038.
