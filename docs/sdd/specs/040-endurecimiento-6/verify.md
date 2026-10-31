# Verify 040 — Endurecimiento 6 (auditoría 7)

## Métricas
- inicio: 2026-10-31 12:30
- cierre: 2026-10-31 13:50
- unitarias: 986 (+21: 710 de Erp.Tests, +6; 205 de integración, +15; Vitest 71, sin cambio)
- e2e: 424 (+2: `canario.spec.ts › H4 CA3 (040)`, `mcp.spec.ts › H1 CA3 (040)`)
- corridas completas: 1 (dotnet test 1 min 46 s + E2E 10 min 10 s; la E2E limpia, en integración una prueba de la 035 ajustada a la regla nueva de F-08, ver §1; `dotnet test` repetido limpio tras el ajuste, 1 min 46 s); la segunda es CI
- corridas parciales: 9 (iteración de `Hardening040Tests`, de las pruebas H040 de Studio y de `McpTests` ×4; `canario.spec.ts` y `mcp.spec.ts` ×1 cada uno; `dotnet test` completo antes de la revisión adversarial; `--veces=3` sobre `canario.spec.ts` y `mcp.spec.ts`; `dotnet test` completo final)
- contra el código anterior: 4 (`it:regresion` 99276fe con las 13 pruebas nuevas de integración: 13 fallan; `e2e:regresion` 99276fe `mcp.spec.ts`: falla solo la nueva; mutaciones de F-01, F-03, F-06 y F-08)
- hallazgos: 0/5/4 (revisión adversarial F-01…F-08 y revisión exploratoria F-09)
- categorías: concurrencia=2, seguridad=3, motor-api=3, UI/UX=1
- revisión adversarial: 8 hallazgos (F-01…F-08)
- riesgos: R-040-1, R-040-2, R-040-3, R-040-4, R-040-5, R-040-6, R-040-7

## 0. Apertura
- `git rev-list --count origin/main..main` = 0 en `99276fe`.
- CI de `main`: la corrida de `35c1554` (cierre de la 039) en `success`; la de `99276fe` (solo docs, el resultado de la
  auditoría 7 en el roadmap) estaba en curso al abrir.
- Cada hallazgo de la auditoría 7 (D-01…D-12, O-01…O-03) se reconfirmó en el código antes de escribir la spec.
- El servidor de desarrollo `erp-api` (5080) no estaba corriendo; la base `ErpAi` y la contraseña de `admin` no se tocaron.
  Ninguna prueba llamó a la API de Anthropic (proveedor simulado y servidor de mensajes en *loopback*).
- Renumeración: 041 reportes y tableros personales, 042 Ventas, 043 MCP 2 (roadmap, índice, ADR-0009, specs 038 y 039).
- CLARIFY: 5 historias, ~350 líneas de producto, sin UI nueva: **no se divide**. `ForwardedHeaders` entra (cierra R-021-5 y
  R-018-4). D-11, O-01 y O-03 se aceptan como riesgos. R-016-3 y R-029-4 se le plantean al dueño (no son de esta spec).

## 1. Pruebas unitarias e integración
`dotnet test`: **710 unitarias** (704 + 6 de `Hardening6Tests`: clave de dirección por /64 y prefijo configurable, proxies
explícitos y un salto por omisión, `RenewSeconds ≤ LeaseSeconds / 3`, purga de un historial ilegible, `MaxJoins` y su conteo
como lo reutiliza el motor) y **205 de integración** (190 + 15):
- `Hardening040Tests` (9): 1 000 clientes revocados no bloquean el registro; el barrido borra clientes sin conexión viva con
  sus tokens y códigos; tope por persona en la pantalla y en el canje; 10 canjes a la vez con tope 2 dan 2; una /64 y el límite
  `oauth` se agrupan; solo un proxy listado cambia dirección y esquema; F-01 (canjes y renovaciones simultáneos); F-03.
- `StudioTests` (4): una sesión que no se puede vencer no atora la cola (y se cierra de respaldo, F-08); al apagar, el turno
  se cierra interrumpido y purga; con `datos`, otra empresa no sigue la conversación (409 y `canSend`); detener un seguimiento
  conserva el resumen.
- `StudioProviderTests` (1): una falla del *callback* (pool agotado) sale tal cual; la del proveedor sigue siendo del
  proveedor.
- `Reports039Tests` (1): agrupar por una referencia con el campo para mostrar oculto da una fila «(sin acceso)» en pantalla y
  exportación (el listado ya lo decía).
- Ajustadas a la regla nueva (no son regresiones): `McpTests › H1_CA3…` fija el tope con los clientes **vivos**;
  `StudioTests › F03_la_purga_va_en_la_transaccion_del_cierre` (035) esperaba que una sesión cuya bitácora falla quedara
  «running» para siempre: con F-08 se cierra de respaldo, y la prueba conserva lo que protegía (cierre y purga en una sola
  transacción: nunca «interrumpida» con lo leído). `StudioTests › H3_CA3_las_bases_aisladas_huerfanas_se_barren` falló tres
  veces seguidas en aislamiento con la falla de E/S de canalizaciones con nombre que documenta `ErpHost`; ahora corre dentro de
  `Task.Run` como las demás de la clase (pasa).

Vitest: 71 sin cambio.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | `canario.spec.ts` (iteración) | 11 en verde | 11 s |
| 2 | `mcp.spec.ts` (iteración, con la prueba de F-09) | 13 en verde | 19 s |
| 3 | Completa | 424 en verde | 10 min 10 s |
| 4 | `--veces=3` (con `--lento`) sobre `canario.spec.ts` y `mcp.spec.ts` | 3 vueltas en verde (23 cada una) | 1 min 37 s |

Archivos modificados: `canario.spec.ts` (reportes `PorSecreto2`, `PorSecretoRef2` y `PorPuenteDoble` a dos saltos en el
recorrido de `/api/reports/*`, su exportación y dos tarjetas del tablero, más un control con el administrador) y
`mcp.spec.ts` (tope de conexiones en Conectar). Sin pantallas nuevas ni capturas de referencia cambiadas (las de Conectar
siguen iguales: el aviso nuevo solo aparece con el tope).

El canario a dos saltos **no falla con el código anterior** en las rutas de reportes, exportación y tablero: el alcance por
salto ya estaba bien (039); es una guardia nueva (D-12), no la regresión de un bug. Sí cubre D-10: `PorPuenteDoble` agrupa por
un `CnPuente` cuyo campo para mostrar es una fórmula que cruza un catálogo restringido, y el lector recibe «(sin acceso)».

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió: el diff (`git diff 99276fe` más los archivos nuevos), la tabla STRIDE del plan
(sin sus conclusiones) y `rutas.md`. Revisó: registro, barrido, conteos y canje del MCP; `ClientAddress` y `ForwardedHeaders`;
vencimiento, apagado, purga y origen de las fallas de Studio; empresa de la conversación; `JoinCount` contra `ReportService`;
el canario.

| ID | Severidad | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|
| F-01 | Media | Contar las conexiones vivas con `UPDLOCK, HOLDLOCK` leía `McpToken` en orden contrario a la renovación (token → conexión): interbloqueo entre un canje y una renovación de la misma persona | Los canjes de una persona se forman con `sp_getapplock`; el conteo va sin pistas de bloqueo | `Hardening040Tests › F01_canjes_y_renovaciones_simultaneos_de_la_misma_persona_no_se_interbloquean` — **no se reprodujo** con la mutación (el interbloqueo depende del tiempo): queda como guardia de invariante |
| F-02 | Media (sospechado) | El barrido iba dentro de la transacción serializable de los topes y sin índice por cliente: bloqueaba o interbloqueaba canjes y renovaciones | Barrido en su propia transacción corta; `IX_McpGrant_Client` | sin prueba propia (es de bloqueo y de plan de ejecución); la suite de MCP y la de `Hardening040Tests` siguen verdes |
| F-03 | Media | Conectar y desconectar en bucle dejaba filas sin tope: las conexiones muertas no contaban en ningún tope y esperaban un día | El tope por persona cuenta también las desconectadas en las últimas 24 h | `Hardening040Tests › F03_las_conexiones_desconectadas_hoy_cuentan_en_el_tope_por_persona` (falla con la mutación) |
| F-04 | Baja | Un cliente barrido deja a la persona con «cliente no registrado»; un barrido entre «Permitir» y el canje deja el intento en error | No se barre un cliente con un código sin usar ni vencer, y autorizar cuenta como actividad; el resto, [R-040-6](../../riesgos.md#040) | cubierto por `H1_CA2…` (al escribirla apareció que un código ya canjeado contaba como «en curso»: corregido) |
| F-05 | Media (sospechado) | Agrupar por /64 junta a muchos clientes legítimos detrás del mismo /64 (renovaciones de Claude.ai) | `Security:Ipv6PrefixLength` (48–128) | `Hardening040Tests › H1_CA4_el_limite_oauth_agrupa_la_red_64` (con /128 cada dirección cuenta aparte); `Hardening6Tests › H1_CA4…` |
| F-06 | Baja-Media | Con una red CIDR listada y saltos sin tope, un cliente de esa red elegía su dirección con su propio `X-Forwarded-For` (rodeaba el límite de inicio de sesión) | Un salto por omisión (`Security:ForwardedLimit`) | `Hardening040Tests › H1_CA5…` con la cadena «inventada, 10.42.0.99» (falla con la mutación `ForwardLimit = null`) |
| F-07 | Baja | Un reporte guardado antes de la 040 con 17 o 18 uniones hace fallar cualquier plan; `RunAsync` no aplica el tope | Aceptado: [R-040-7](../../riesgos.md#040) | `Hardening6Tests › H4_CA1…` |
| F-08 | Baja | Una sesión que falla siempre al vencer deja a su dueña «en curso» para siempre (y un error cada 2 s) | Cierre de respaldo sin bitácora y sin leer el historial (purga en la misma transacción) | `StudioTests › H040_H2_CA1…` (falla con la mutación) |

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`) con `Mcp__MaxConnectionsPerUser=1`.
Admin con una conexión hecha por API (registro, consentimiento y canje); luego Conectar con un segundo cliente en el navegador
integrado, escritorio en claro. Studio no se recorrió en el navegador: mandar un mensaje exige la clave de Anthropic (503 antes
de la regla de empresa) y no se llama a la API real; lo cubre la integración. Los reportes con «(sin acceso)» los cubren la
integración y el canario.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-09 | Media | UI/UX | Con el tope de conexiones, Conectar decía «La petición de conexión no es válida» (era válida) y «Ya tienes 1 conexiones» | Título «Llegaste al máximo de conexiones de Claude», mensaje que dice qué cuenta y qué hacer, y enlace «Ver mis conexiones» | `mcp.spec.ts › H1 CA3 (040)…` (falla con `99276fe`: allí no hay tope y se ve el consentimiento) |

### Contra el código anterior
- `node tests/Erp.IntegrationTests/regresion.mjs 99276fe "FullyQualifiedName~H040|FullyQualifiedName~Hardening040Tests"`:
  **13 fallan, 0 pasan** (H1_CA3 falla con 503: con los 1 000 clientes revocados de H1_CA1 el código anterior ya no registra;
  también es D-01).
- `npm --prefix web run e2e:regresion -- 99276fe e2e/mcp.spec.ts`: falla solo `H1 CA3 (040)`.
- Mutaciones de las correcciones de la revisión adversarial (F-01, F-03, F-06, F-08), restauradas después: fallan F-03, F-06 y
  F-08; F-01 no se reprodujo (ver §3).

### Lista de comprobación
**Flujo**
- [x] El camino feliz de cada historia se completa (integración; Conectar en el navegador)
- [x] Los mensajes de error dicen qué pasó y cómo resolverlo (F-09)
- [x] Doble clic y dos pestañas: canjes simultáneos (integración, `Concurrently`)
- [ ] Datos límite en pantalla: sin formularios nuevos

**Diseño**
- [x] Conectar con el aviso en claro a 1280 px; las capturas claro, oscuro y móvil de Conectar siguen iguales
- [x] Solo iconos Lucide; el aviso usa `Alert`
- [x] Estado de error con diseño (aviso y enlace)

**Accesibilidad**
- [x] El foco va al título «No se puede conectar»; el enlace es alcanzable con teclado (`mcp.spec.ts`)
- [ ] Diálogos: sin diálogos nuevos

**Seguridad**
- [x] Cada camino alterno del STRIDE probado por API (integración)
- [x] La consola sin errores (guardias de la suite)
- [x] Dos peticiones a la vez sobre las operaciones nuevas (canjes, canje y renovación)

**Cierre**
- [x] Hermanos de un componente genérico: la partición por dirección se aplicó a todos los límites (`login`, `oauth`, global y
      políticas por persona sin persona); la bitácora conserva la dirección completa
- [x] Alcance congelado: nada pedido durante VERIFY

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#040): R-040-1 (muchas /64 llenan los pendientes una hora), R-040-2 (D-11),
R-040-3 (O-01), R-040-4 (O-03), R-040-5 (apagado con plazo de 5 s), R-040-6 (cliente barrido), R-040-7 (reportes viejos con
más de 16 uniones). Cerrados: R-018-4 y R-021-5. Ampliados: R-036-4, R-036-5, R-038-1 y R-038-5.

**Para el dueño:** R-016-3 y R-029-4 (tareas programadas que se saltan o reprocesan registros) llevan tres auditorías marcados;
la auditoría 7 pide que los decida o los pase a «Aceptado» con fecha.

## 6. CI
La corrida 41234884587 de `9ae139e` (integración y E2E completa en `windows-2025`, la segunda corrida completa) terminó en
`success`.
