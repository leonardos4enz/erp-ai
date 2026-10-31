# Tasks 040 — Endurecimiento 6

## 0. Apertura
- [x] `git rev-list --count origin/main..main` = 0 (`99276fe`).
- [x] Última corrida de CI en `main`: `35c1554` en verde (la de `99276fe`, solo docs, en curso al abrir).
- [x] Hallazgos de la auditoría 7 reconfirmados en el código (D-01 a D-12, O-01 a O-03).
- [x] Roadmap e índice renumerados (041 reportes personales, 042 Ventas, 043 MCP 2) y referencias en ADR-0009 y las specs 038 y 039.

## 1. H1 — Registro del MCP (D-01, D-02, R-021-5, R-018-4)
- [x] `ClientAddress.Key` (IPv4, /64, mapeada, nula) en todos los límites por dirección y en `CreatedIp`.
- [x] Barrido de clientes sin conexión viva (con tokens, códigos y conexiones); `MaxClients` cuenta vivos.
- [x] `Mcp:MaxConnectionsPerUser`: aviso en la pantalla (409) y regla en el canje bajo bloqueo.
- [x] `Security:ForwardedProxies` → `UseForwardedHeaders` (For y Proto, proxies listados).
- [x] `Hardening040Tests` y unitarias.
- [x] Revisión adversarial F-01 (candado por persona), F-02 (barrido aparte, índice), F-03 (desconectadas hoy cuentan), F-04 (código sin usar, actividad al autorizar), F-05 (`Ipv6PrefixLength`), F-06 (`ForwardedLimit`); revisión exploratoria F-09 (aviso de Conectar).

## 2. H2 — Cola de Studio (D-03, D-04, D-05, D-06)
- [x] `ExpireAsync` sesión por sesión; despachador con `try` separados; purga tolerante.
- [x] `RenewSeconds ≤ LeaseSeconds / 3`.
- [x] Cierre `interrupted` al apagar.
- [x] Revisión adversarial F-08: cierre de respaldo sin bitácora.
- [x] `StudioCallbackFailed` en el adaptador.

## 3. H3 — Conversación (D-07, D-08)
- [x] 409 por empresa con `datos`; resumen conservado.

## 4. H4 — Reportes (D-09, D-10, D-12)
- [x] `MaxJoins` en `ValidateReports`; «(sin acceso)» con el campo para mostrar oculto; canario a dos saltos.

## 5. H5 — Riesgos
- [x] R-040-*, R-038-1/5, R-036-4/5, R-021-5, R-018-4; nota en el STRIDE 039 (D-11); limpieza de la fila 1 de `riesgos.md`.

## 6. VERIFY
- [x] Contra `99276fe`: las pruebas nuevas fallan (`it:regresion`).
- [x] Corrida completa; `--veces=3` del canario.
- [x] Revisión adversarial (subagente sin contexto).
- [x] `verify.md`, riesgos, roadmap, índice.
- [x] Commit y push; CI.
