# Verify 038 — Servidor MCP por persona

## Métricas
- inicio: 2026-10-29 22:55
- cierre: 2026-10-30 08:40 (la sesión estuvo detenida de noche; trabajo efectivo ≈ 3 h 30 min)
- unitarias: 917 (+65: 681 de Erp.Tests, +22; 178 de integración, +43; Vitest 58, sin cambios)
- e2e: 413 (+12)
- corridas completas: 2 (1: dotnet test 1 min 45 s + E2E 8 min 50 s con 2 fallas, corregidas; 2: dotnet test 1 min 43 s + E2E 9 min 39 s, limpia)
- corridas parciales: 5 (--veces=3 sobre mcp.spec.ts y canario.spec.ts; McpTests ×3 en integración; iteración de mcp.spec.ts, canario + oauth-odata + mcp tras las fallas)
- contra el código anterior: 9 (F-01 y F-04…F-10 en integración contra el código sin corregir; F-03 en E2E con la redirección automática repuesta; F-02 visto en la instancia de revisión)
- hallazgos: 0/4/9
- categorías: seguridad=5, motor-api=3, pruebas=3, UI/UX=1, concurrencia=1
- revisión adversarial: 12 hallazgos
- riesgos: R-038-1, R-038-2, R-038-3, R-038-4, R-038-5, R-038-6, R-038-7

## 0. Apertura
- `git rev-list --count origin/main..main` = 0.
- CI de `main` en verde: la corrida 40975503317 de `7cf0217` («Spec 037: CI en rojo corregido») terminó con
  `conclusion: success` (anotado aquí por la regla de apertura: la 037 cerró antes de ver su CI) y la 41005971373 de `6302079`
  (solo docs: se retiró la 040 CFDI) también, `success`.
- El servidor de desarrollo `erp-api` (5080) no estaba escuchando al abrir ni durante la sesión (sin proceso `Erp.Api` en el
  puerto): no hubo que apagarlo. La única instancia que se detuvo fue la de la revisión exploratoria (5950), arrancada en
  esta sesión. La base `ErpAi` y la contraseña de `admin` no se tocaron.

## 1. Pruebas unitarias e integración
`dotnet test`: **680 unitarias** (659 + 21 de `McpCoreTests`: PKCE con el vector del RFC 7636, URL de retorno, sitios
conocidos y punycode, formatos, alcances, `BearerScope` y selector de esquema del token MCP, catálogo MCP solo de lectura,
esquemas de entrada y salida) y **178 de integración** (135 + 43 de `McpTests`). `McpTests` cubre como un cliente real:
metadatos, registro (errores RFC 7591, tope total y por dirección, barrido), consentimiento (pantalla, errores sin redirigir
y que vuelven al cliente, CSRF), canje (cliente, retorno, verificador, recurso, vencido, gastado al fallar), reuso de código y
de *refresh*, **10 canjes y 10 renovaciones simultáneos** (exactamente uno), canjes simultáneos de la misma persona y cliente
(una conexión), **falla inyectada al emitir** (restricción CHECK en `meta.McpToken`: el código sigue sin usar), sello
(contraseña y desactivación), token fuera de `/mcp` (403), cookie/clave/token de servicio en `/mcp` (401), `tools/list` según
persona y alcances, mismo 403 que la API, **canario** (con control positivo del administrador), JSON-RPC (versiones, lotes,
tamaño, JSON y UTF-8 inválidos, GET/DELETE 405, `Origin`), `outputSchema` de las 14 herramientas validado con un validador
mínimo, paginación con `nextPage`, **bucle de 200 llamadas** y **40 en paralelo** contra la cubeta, límite global, resultado
grande, conexiones, RFC 7009, bitácora sin tokens y `Mcp:Enabled=false`.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa | 399 en verde, 2 fallas (F-11, F-12), 12 sin correr (dependían de ellas) | 8 min 50 s |
| 2 | `canario` + `oauth-odata` + `mcp` tras corregir | 31 en verde | 27 s |
| 3 | Completa (más `dotnet test`: 680 + 178) | 413 en verde | 9 min 39 s |
| 4 | `--veces=3 --lento` sobre `mcp.spec.ts` y `canario.spec.ts` | 21 × 3 en verde | 1 min 18 s |

Archivo nuevo `web/e2e/mcp.spec.ts` (12): un cliente MCP simulado se registra con retorno de *loopback* servido por la prueba,
el navegador pasa por inicio de sesión → **Conectar** → vuelve con el código, y la prueba canjea y llama a `/mcp`; Datos con
aviso; Cancelar; sitio desconocido; retorno no registrado sin redirigir; error del cliente con «Volver a…»; **Conexiones de
Claude** (menú, copiar URL, desconectar con confirmación, miga de pan). `canario.spec.ts` recorre ahora `POST`/`DELETE /mcp`
con un token MCP del lector (el inventario lo exigió). `playwright.config.ts` sube `Security__TokenRequestsPerMinute` para la
suite. Capturas nuevas, revisadas una por una: `conectar-{light,dark,movil}` (se corrigió el anillo de foco del título) y
`conexiones-{light,dark,movil}` (se corrigió la tarjeta sin relleno y la tabla que en móvil dejaba «Desconectar» fuera: ahora
filas que se acomodan). Vitest 58/58 (`pantallas.test.ts` declara las dos rutas nuevas con sus capturas).

`McpTests` tres veces seguidas: 43 × 3 en verde (29 s). La E2E de la corrida 1 se cortó una vez a medias al reiniciarse la
sesión de trabajo (no cuenta: se repitió completa).

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió: el diff sin confirmar y solo la sección STRIDE del plan. Revisó el token
fuera de `/mcp` y otras credenciales en `/mcp`, el despacho de herramientas (capacidad, `RecordFilter`, `FieldAccess`,
empresa, `datos`), el sello en todos los caminos, PKCE, canje y rotación concurrentes, fallas parciales, retorno, CSRF del
consentimiento, límites, errores, SQL parametrizado y bitácora. Sin hallazgos altos. Encontró 12: F-03…F-10 (corregidos, abajo)
y cuatro que quedan como riesgo: límite `oauth` por dirección compartido con clientes en la nube y ventana global compartida
con el navegador (R-038-5), respuesta perdida tras el commit = reuso (R-038-6), oráculo de conteo de `compilar_plan`
preexistente (R-038-7), y que los `grant_types` registrados no restringen la emisión de *refresh* (informativo: el cliente
pidió el canje; no amplía acceso).

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (5950, base `ErpAi_Revision_E2E`). Persona de prueba sin capacidades
administrativas creada por API. Recorrido: `/oauth/authorize` sin sesión → inicio de sesión → vuelve a Conectar (sitio
`claude.ai` conocido, sin aviso), teclado (Tab a Datos, Espacio, aviso de registros, Tab a Cancelar), menú de usuario →
Conexiones de Claude a 375 px (sin desborde, estado vacío), oscuro. No se pulsó Permitir ni Cancelar hacia `claude.ai`
(habría navegado fuera del equipo); ese camino lo prueba la E2E con un retorno de *loopback*. Registro de clientes por `curl`.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | motor-api | Un cuerpo con UTF-8 inválido en `/oauth/register` (y en `/mcp`) daba 500: `JsonNode.Parse` no valida el texto hasta leer la cadena (lo encontró `curl` con un «ó» en Latin-1). | `StrictJson.Parse`: UTF-8 estricto antes de interpretar → 400 / `-32700`. | `McpTests › F01_un_cuerpo_con_UTF8_invalido_es_400_y_no_500` (500 con el código anterior) |
| F-02 | Baja | UI/UX | La miga de pan decía «Inicio» en Conexiones de Claude. | `useBreadcrumb` conoce `/conexiones`. | `mcp.spec.ts › H6 CA1 CA2…` (visto con la SPA anterior en la instancia de revisión) |
| F-03 | Media | seguridad | (Adversarial F-01) Un error que vuelve al cliente (sin PKCE, alcance desconocido…) se redirigía **solo**: con un cliente registrado por cualquiera, el ERP era un redirector abierto. | El servidor da el error y el sitio; la pantalla lo explica y la persona elige «Volver a *sitio*». | `mcp.spec.ts › H2 CA2: un error del cliente (alcance desconocido) se explica y la persona decide volver` (falla con la redirección automática repuesta) |
| F-04 | Media | seguridad | (Adversarial F-02) Consentir sin canjear marcaba al cliente como autorizado y el barrido nunca lo quitaba; nada limitaba los pendientes por dirección: el tope total se llenaba y bloqueaba registros legítimos. | `LastAuthorizedAt` al canjear; barrido de 1 h de los que nunca canjearon (con sus códigos); `Mcp:PendingClientsPerAddress` (429). | `McpTests › F04_un_cliente_que_nunca_canjeo_se_barre_y_cada_direccion_tiene_tope_de_pendientes` |
| F-05 | Baja | seguridad | (Adversarial F-04) Registro leía el cuerpo entero antes de medirlo (sin `Content-Length`); revocar aceptaba multipart y formularios sin límites. | Lectura por trozos; revocar con los `FormOptions` del servidor de tokens y sin multipart. | `McpTests › F05_revocar_rechaza_multipart` |
| F-06 | Baja | concurrencia | (Adversarial F-05) Dos códigos de la misma persona y cliente canjeados a la vez podían dejar dos conexiones vivas (`UPDLOCK` no bloquea un rango vacío). | `UPDLOCK, HOLDLOCK`. | `McpTests › F06_canjes_simultaneos_de_la_misma_persona_y_cliente_dejan_una_conexion` (no se reprodujo con 6 canjes contra el código anterior: queda como invariante) |
| F-07 | Baja | seguridad | (Adversarial F-06) Los metadatos no llevaban `no-store`; sin `Mcp:PublicUrl` el origen sale de `Host`. | `no-store` en `/.well-known/*`; el plan pide fijar `PublicUrl` en producción. | `McpTests › F07_los_metadatos_no_se_guardan_en_cache` |
| F-08 | Baja | motor-api | (Adversarial F-07) Un reuso sobrescribía cuándo se usó el código o el *refresh* por primera vez. | `COALESCE(UsedAt, …)`. | `McpTests › F08_el_reuso_conserva_el_primer_uso` |
| F-09 | Baja | seguridad | (Adversarial F-09) El nombre del cliente admitía caracteres de formato (bidi, ancho cero) y el sitio se mostraba en Unicode (homógrafos). | Se rechazan los de formato; el sitio se muestra y se compara en punycode. | `McpTests › F09_el_nombre_del_cliente_no_admite_caracteres_de_formato` · `McpCoreTests › Un_sitio_conocido_…` |
| F-10 | Baja | motor-api | (Adversarial F-11) `POST /mcp/` llega a la misma ruta pero `BearerScope` negaba el token. | Se normaliza la barra final. | `McpTests › F10_mcp_con_barra_final_acepta_el_token` |
| F-11 | Media | pruebas | La E2E completa: el canario de la 031 exige recorrer toda ruta de lectura nueva que el lector pueda llamar, y `/mcp` no estaba. | El canario obtiene un token MCP del lector (registro, consentimiento, PKCE) y recorre sus herramientas de registros; sin fuga. | `canario.spec.ts › H2 CA2…` (falló en la corrida 1) |
| F-13 | Baja | pruebas | CI (corrida 41073327990, `74e0225`): fallaron las unitarias en Ubuntu (en Windows pasaban). En Unix `Uri.TryCreate("/relativa", Absolute)` da `file:///relativa`: la URL se rechazaba igual, pero con otro mensaje, y la prueba esperaba «absoluta». Diagnóstico por razonamiento: sin sesión de GitHub no se pudo leer el log, y Docker no estaba levantado. | `RedirectUris.Problem` exige `esquema://` en todos los sistemas. | `McpCoreTests › La_URL_de_retorno_se_valida` (`file:///…` agregado) |
| F-12 | Baja | pruebas | La E2E completa: `mcp.spec.ts` gastaba el límite `oauth` por dirección y `oauth-odata.spec.ts` recibía 429. | `Security__TokenRequestsPerMinute` en la suite (como los demás límites); el riesgo real queda en R-038-5. | `oauth-odata.spec.ts › H2 CA1 · CA2 · CA3…` (falló en la corrida 1) |

### Contra el código anterior
Las pruebas de F-01, F-04, F-05, F-07, F-08, F-09 y F-10 se escribieron antes de corregir y fallaron con el código de ese
momento por la razón esperada (500, cliente no barrido, 200 con multipart, sin `Cache-Control`, `UsedAt` distinto, 201 con
U+202E, 403). F-03 se comprobó reponiendo la redirección automática en `ConnectPage.tsx`: la prueba falla (no hay pantalla
de error, el navegador se va) y pasa con la corrección. F-06 no se reprodujo (carrera de ventana corta). `e2e:regresion`
contra `6302079` no aplica: ese commit no tiene MCP.

### Lista de comprobación
- [x] Cada CA con su escenario (integración para API y concurrencia; E2E para las dos pantallas).
- [x] Concurrencia y fallas parciales en integración.
- [x] axe sin violaciones serias en Conectar (con y sin Datos, sitio desconocido, errores) y Conexiones.
- [x] Claro, oscuro y 390 px; sin desborde horizontal.
- [x] Foco: el título de Conectar al cargar; Cancelar en el diálogo de desconectar; guardias de la suite activas.
- [x] Capturas nuevas revisadas una por una.
- [x] `rutas.md` regenerado (12 rutas nuevas) y la lista blanca anónima ampliada en `RouteInventoryTests`.
- [x] Ninguna prueba llama a Anthropic ni sale del equipo (retornos de *loopback*; `claude.ai` solo como texto).

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#038): R-038-1 (medio: registro abierto, defensa en el consentimiento),
R-038-2…7 (bajos). **Auditoría 7** pendiente en sesión aparte (roadmap).

## 6. *Code mode* (duda del dueño)
Depende del cliente: es el agente quien decide escribir código que llama herramientas en lugar de llamarlas una por una. El
servidor se lo pone fácil y lo deja probado: esquemas de entrada estrictos (`additionalProperties: false`, `items`,
`minimum`), `outputSchema` + `structuredContent` (cada herramienta validada contra su esquema), anotaciones de solo lectura e
idempotencia, `pageSize`/`nextPage`, errores de herramienta con `status` y `retryAfterSeconds`, y topes pensados para ráfagas
(cubeta por persona más los del despachador): el bucle de 200 y las 40 en paralelo reciben exactamente lo que permiten, nunca
algo fuera de sus permisos. No hay herramienta que ejecute código en el ERP (constitución II.8). Las escrituras por MCP (con
clave de idempotencia) quedan para la 040.
