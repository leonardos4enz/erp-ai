# Tareas 044 — MCP 2: copia aislada y propuestas por MCP

- [x] T1 Bootstrap: `meta.McpSandbox`, `meta.McpWrite`, columnas `Origin`, `McpGrantId`, `McpClient` de `meta.StudioSession`.
- [x] T2 `SandboxFactory`: crear, abrir y borrar por nombre; nombres por conexión (`_SM<id>_SBX`) y su barrido.
- [x] T3 `McpOptions`/`McpScopes`: alcances `aislado` y `proponer`, topes nuevos; consentimiento con disponibilidad (servidor).
- [x] T4 `Mcp/McpSandboxes.cs`: adquirir (bloqueo por conexión, topes, *lease* con renovación), soltar, barrer; `McpSandboxSweeper`.
- [x] T5 `Mcp/McpWrites.cs`: clave de idempotencia (empezar, repetir, terminar con intento, soltar), entrada canónica.
- [x] T6 `Mcp/McpProposals.cs` y `StudioDelivery` (compartido con `StudioAgent`): `entregar` en una transacción, tope diario.
- [x] T7 `McpTools`/`McpServer`: herramientas nuevas, esquemas con `idempotencyKey`, anotaciones, despacho; `/mcp` `Writes(Session)`.
- [x] T8 Studio: origen MCP en el DTO, `SendBlocked`/`SendAsync`, topes de Studio sin las de MCP.
- [x] T9 Conexiones: copia en el DTO, `DELETE /api/mcp/connections/{id}/sandbox`, soltar la copia al desconectar; bitácora.
- [x] T10 SPA: Conectar (casillas), Conexiones (copia y descartar), Studio (sin clave, origen MCP).
- [x] T11 Pruebas: `McpCoreTests`, `Mcp2Tests`, `mcp2.spec.ts`, `mcp.spec.ts`, `rutas.md`.
- [x] T12 VERIFY: revisión adversarial, exploratoria, suites, `verify.md`, `riesgos.md`, roadmap e índice.
