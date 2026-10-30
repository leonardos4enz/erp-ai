# Tareas 038 — Servidor MCP por persona

- [x] T1 Bootstrap: `meta.McpClient`, `meta.McpCode`, `meta.McpGrant`, `meta.McpToken` (idempotente).
- [x] T2 `Mcp/McpOptions.cs` y `Mcp/McpOAuth.cs`: formatos, PKCE S256, validación de URL de retorno, metadatos RFC 9728/8414,
      registro RFC 7591 con tope y barrido.
- [x] T3 Autorizar: `GET /oauth/authorize` (302 a la SPA), `GET|POST /api/mcp/authorize` (sesión, CSRF), código de un uso.
- [x] T4 Tokens: `authorization_code` y `refresh_token` en `POST /oauth/token` (rotación con detección de reuso, en una
      transacción), `POST /oauth/revoke`.
- [x] T5 `McpTokenHandler` (esquema `Mcp`), selector de esquema (`/mcp` y `Bearer erpmt_`), `BearerScope` para el token MCP.
- [x] T6 `Mcp/McpServer.cs`: JSON-RPC (`initialize`, `ping`, `tools/list`, `tools/call`), `Origin`, versión, tamaño, lotes,
      405 en GET/DELETE.
- [x] T7 `Mcp/McpTools.cs`: catálogo MCP (solo entorno real), esquemas de entrada endurecidos (`items`,
      `additionalProperties`), `outputSchema`, anotaciones, salida estructurada, `pageSize`/`nextPage`, cubeta por persona,
      tope de tamaño.
- [x] T8 Conexiones: `GET /api/mcp/connections`, `DELETE /api/mcp/connections/{id}`; auditoría `mcp.*` clasificada.
- [x] T9 SPA: `/conectar` (consentimiento) y `/conexiones` (lista, copiar URL, desconectar), enlace en el menú de usuario.
- [x] T10 Pruebas: `McpCoreTests` (unitarias), `McpTests` (integración), `mcp.spec.ts` (E2E), `rutas.md` regenerado.
- [x] T11 VERIFY: revisión adversarial, exploratoria, suites, `verify.md`, `riesgos.md`, roadmap e índice, ADR.
