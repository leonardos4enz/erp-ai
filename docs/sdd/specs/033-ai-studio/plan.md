# Plan 033 — Diseño técnico

## Piezas
```
 POST /api/studio/sessions { prompt, scopes }      (modelador; CSRF; 202)
   │  StudioService: clave (AssistantKeys de la 011), una sesión activa por persona, sesiones y costo del día
   │  (sp_getapplock por usuario) → meta.StudioSession 'queued'
   ▼
 StudioDispatcher (BackgroundService) ── reclama con LeaseToken (UPDATE … OUTPUT, READPAST) → 'running'
   │  vencidas sin dueño → 'interrupted' (no se repiten) · barrido de bases aisladas huérfanas
   ▼
 StudioAgent.RunAsync  ── renueva el lease y lee CancelRequested cada Studio:RenewSeconds
   │  antes de cada llamada: pasos, herramientas, tokens, minutos, costo de la sesión y del día (reserva)
   │  IStudioModel (AnthropicStudioModel: SDK oficial, tool use) ⇄ herramientas
   │  cada paso → meta.StudioStep (INSERT … WHERE el lease sigue siendo mío)
   ▼
 StudioTools (catálogo: nombre, descripción, esquema, alcance, ruta) ── ToolRunner
   │   destino real   → RouteDispatcher: la RUTA de la API, en proceso, con la identidad recargada de la persona
   │                    (UserContextLoader + sello), su empresa y la capacidad de la ruta (029). Solo rutas que no escriben.
   │   destino aislado→ StudioSandbox: base <base>_<huella>_S<id>_SBX, proveedor de servicios propio (mismos servicios,
   │                    Db y EnvironmentOptions propios, sin servicios en segundo plano), las mismas rutas como su administrador
   │   entregar       → compila contra el modelo real (PlanCompiler) + PlanWarnings → informe
   ▼
 'proposed' ── POST /api/studio/sessions/{id}/apply { reviewHash }  (la persona; DirectChange; huella 021)
   └─ PlanService.ApplyInTransactionAsync → 'applied' + studio.apply + model.change
```
El modelo de lenguaje nunca recibe credenciales del ERP ni llama a la API: pide herramientas por nombre y el servidor las
ejecuta con la identidad de la persona. Toda escritura real pasa por `apply`, que solo llama la persona.

## 1. Modelo de datos (Bootstrap, idempotente)
```sql
meta.StudioSession (Id int IDENTITY PK, UserId int FK User, CompanyId int NULL, SecurityStamp uniqueidentifier,
  Prompt nvarchar(4000), Scopes nvarchar(100),
  Status nvarchar(12) /* queued running proposed answered failed cancelled interrupted applied discarded */,
  CancelRequested bit, LeaseToken uniqueidentifier NULL, LeaseUntil datetime2 NULL,
  Steps int, ToolCalls int, InputTokens bigint, OutputTokens bigint, CostMicros bigint, ReservedMicros bigint,
  Model nvarchar(100), ErrorCode nvarchar(30) NULL, Summary nvarchar(max) NULL, PlanJson nvarchar(max) NULL,
  ReportJson nvarchar(max) NULL, SandboxDb nvarchar(128) NULL, CreatedAt, StartedAt NULL, FinishedAt NULL, AppliedAt NULL,
  ChangeSetId int NULL)   INDEX (UserId, CreatedAt), INDEX (Status)
meta.StudioStep (Id bigint IDENTITY PK, SessionId int FK, Seq int, Kind nvarchar(12) /* text tool deliver limit error */,
  Tool nvarchar(60) NULL, InputJson nvarchar(max) NULL, OutputJson nvarchar(max) NULL, Status nvarchar(10) /* ok error denied */,
  Summary nvarchar(400), DurationMs int, CreatedAt)   UNIQUE (SessionId, Seq)
```
Costo en microdólares: `in × Studio:InputUsdPerMTok + out × Studio:OutputUsdPerMTok` (lectura de caché al 10 %, escritura
al 125 %). Sin operaciones nuevas del Plan de Cambios.

## 2. Contrato de la API
| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `GET /api/studio` | modelador | — | `{ configured, model, sandboxAvailable, limits, sessionsToday, costTodayUsd, active?: id }` |
| `GET /api/studio/tools` | modelador | — | `StudioToolInfo[]` = `{ name, description, scope, target: real\|aislado\|servidor, method?, route?, capability, costly, inputSchema }` |
| `GET /api/studio/sessions` | modelador | — | mis 30 más recientes: `{ id, title, status, createdAt, finishedAt?, steps, costUsd }[]` |
| `GET /api/studio/sessions/{id:int}?after=n` | modelador | — | `StudioSessionDto` con los pasos `Seq > n`; 404 si no es mía |
| `POST /api/studio/sessions` | modelador · `Writes(Session)` · costosa (límite propio) | `{ prompt, scopes?: ('datos'\|'aislado')[] }` | 202 `StudioSessionDto` · 400 vacío/largo/alcance desconocido · 409 ya tienes una activa · 429 límite del día · 503 sin clave |
| `POST /api/studio/sessions/{id:int}/cancel` | modelador · `Writes(Session)` | — | `StudioSessionDto` · 409 si ya terminó |
| `POST /api/studio/sessions/{id:int}/discard` | modelador · `Writes(Session)` | — | `StudioSessionDto` · 409 si no es `proposed` |
| `POST /api/studio/sessions/{id:int}/apply` | modelador · `Writes(Model, DirectChange)` · `AppliesReviewed("reviewHash")` | `{ reviewHash }` | `{ changeSetId, session }` · 400 sin huella o ya no compila · 409 modelo cambiado / no vigente / entorno con aprobación |
| `GET /api/explorer/routes` | modelador | `q?` | `{ method, route, capability, effect?, gate?, costly? }[]` (inventario vivo, para la herramienta) |
| `GET /api/explorer/risks` | modelador | `q?` | `{ id, text, severity, decision, state }[]` del registro de riesgos (recurso incrustado) |

```ts
type StudioSessionDto = { id: number; prompt: string; scopes: string[]; status: string; error?: string; model: string;
  steps: number; toolCalls: number; inputTokens: number; outputTokens: number; costUsd: number;
  createdAt: string; startedAt?: string; finishedAt?: string; summary?: string;
  report?: { tested: { description: string; ok: boolean; detail?: string }[]; failures: string[]; risks: string[] };
  plan?: ChangePlan; preview?: PlanPreview; warnings: string[]; reviewHash?: string; canApply: boolean; blockedReason?: string;
  appliedAt?: string; changeSetId?: number; trail: StudioStepDto[] };
type StudioStepDto = { seq: number; kind: string; tool?: string; status: string; summary: string; input?: unknown; output?: unknown; durationMs: number; at: string };
```
Auditoría: `studio.start` (sesión, alcances), `studio.finish` (estado, pasos, tokens, costo; dentro de la transacción del
cierre con el lease), `studio.cancel`, `studio.discard`, `studio.apply` (+ `model.change`).

### Herramientas (catálogo inicial, `Studio/StudioTools.cs`)
| Nombre | Alcance | Destino | Ruta |
|---|---|---|---|
| `listar_catalogos` | modelo | real | `GET /api/model/entities` |
| `ver_catalogo` | modelo | real | `GET /api/explorer/entities/{name}` |
| `relaciones` | modelo | real | `GET /api/explorer/relations` |
| `base_fisica` | modelo | real | `GET /api/explorer/database` |
| `donde_se_usa` | modelo | real | `GET /api/explorer/usages` |
| `automatizaciones` | modelo | real | `GET /api/explorer/automations` |
| `seguridad` | modelo | real | `GET /api/explorer/security` |
| `inventario_rutas` | modelo | real | `GET /api/explorer/routes` |
| `riesgos` | modelo | real | `GET /api/explorer/risks` |
| `funciones_expresion` | modelo | real | `GET /api/model/expressions/functions` |
| `validar_expresion` | modelo | real | `POST /api/model/expressions/check` (`ReadsOnly`) |
| `compilar_plan` | modelo | real | `POST /api/model/plan/preview` (`ReadsOnly`) + `PlanWarnings` |
| `leer_registros` | datos | real | `GET /api/data/{entity}` (20 por página) |
| `leer_registro` | datos | real | `GET /api/data/{entity}/{id:long}` |
| `aislado_aplicar_plan` | aislado | aislado | `POST /api/model/plan/preview` + `POST /api/model/plan/apply` |
| `aislado_crear` / `aislado_actualizar` | aislado | aislado | `POST /api/data/{entity}` / `PUT /api/data/{entity}/{id:long}` |
| `aislado_transicion` | aislado | aislado | `POST /api/data/{entity}/{id:long}/transition` |
| `aislado_leer` / `aislado_listar` | aislado | aislado | `GET /api/data/{entity}/{id:long}` / `GET /api/data/{entity}` |
| `aislado_procesar_asincronas` | aislado | aislado | bandeja de automatizaciones de la copia (`AutomationService.DispatchBatchAsync`) |
| `entregar` | modelo | servidor | — (termina la sesión con la propuesta) |

`RouteDispatcher` arma un `HttpContext` en proceso (método, ruta, valores de ruta, consulta, cuerpo JSON, `RequestServices`
del proveedor del destino, `Items[usuario]`, principal autenticado), exige la `CapabilityMetadata` de la ruta con
`EndpointCapabilities.Has`, rechaza en el destino real cualquier ruta que escriba y ejecuta el `RequestDelegate` del
endpoint. `AppException` → su estado y mensaje (los mismos que la API); `ExprException` → 400 con su mensaje; cualquier
otra → «Ocurrió un error inesperado.» (detalle al log). La salida al modelo va como
`<datos_no_confiables herramienta="…">JSON</datos_no_confiables>` con el codificador por omisión de System.Text.Json
(escapa `<`, `>` y `&`: un texto no puede cerrar el delimitador) y recortada a `Studio:MaxToolResultChars`.

**Lista para la 035 (MCP):** `StudioTools` + `ToolRunner` no dependen del bucle ni de la sesión: reciben la identidad, los
alcances y la entrada. El servidor MCP los llamará con el usuario del token y los alcances consentidos (`modelo`, `datos`,
`aislado`), y `GET /api/studio/tools` ya da el `inputSchema` de cada una en JSON Schema.

## Configuración (`Studio:*`, por entorno)
| Clave | Por omisión | Uso |
|---|---|---|
| `MaxSteps` | 30 | Llamadas al modelo por sesión |
| `MaxToolCalls` | 80 | Herramientas por sesión |
| `MaxTokens` | 1 500 000 | Entrada + salida por sesión |
| `MaxMinutes` | 20 | Duración de la sesión |
| `MaxCostUsd` / `CostPerDayUsd` | 5 / 20 | Costo estimado por sesión y por persona al día |
| `SessionsPerDay` | 10 | Por persona (una activa a la vez) |
| `InputUsdPerMTok` / `OutputUsdPerMTok` | 2 / 10 | Precio del modelo (Sonnet 5.5) |
| `MaxToolResultChars` | 20 000 | Lo que una herramienta devuelve al modelo |
| `Sandbox` | true | Habilita el alcance `aislado` (exige `CREATE DATABASE`) |
| `Workers`, `PollSeconds`, `LeaseSeconds`, `RenewSeconds` | 2, 2, 120, 10 | Trabajador |

## 3. SPA
Sin pantalla en esta spec (CLARIFY 1): la **034** construye Configuración › ERP AI Studio sobre este contrato.

## 4. Pruebas
- **Unitarias** (`tests/Erp.Tests/StudioCoreTests.cs`, nuevo): catálogo contra el inventario de rutas, nombre de la base
  aislada, delimitador de datos no confiables, estimación de costo.
- **Integración** (`tests/Erp.IntegrationTests/StudioTests.cs`, nuevo; SQL real, proveedor de LLM simulado por
  `ConfigureTestServices`: ninguna prueba llama a Anthropic): todas las historias, concurrencia y fallas parciales.
- `docs/sdd/rutas.md` regenerado (`ACTUALIZAR_RUTAS=1`). E2E: sin archivos nuevos (no hay pantalla); el canario de la 029
  recorre solo las rutas nuevas (GET de modelador → 403 al Lector) y el de las herramientas está en integración (H5 CA1).

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas y caminos nuevos | 10 rutas (tabla de arriba), todas con capacidad declarada; un camino nuevo: el agente ejecuta rutas existentes por `RouteDispatcher`. Las herramientas del entorno real solo usan rutas de lectura (GET o `ReadsOnly`) y nunca de seguridad, usuarios, claves, aplicar ni paquetes. | `StudioCoreTests › Las_herramientas_del_entorno_real_usan_rutas_que_no_escriben` · `Ninguna_herramienta_usa_rutas_de_seguridad_ni_de_aplicar` · `RouteInventoryTests` (snapshot) |
| Caminos alternos | ¿Otro camino al mismo dato o efecto? | Leer: la herramienta **es** la ruta (misma capacidad, `RecordFilter`, `FieldAccess`, empresa, `viewer`), no una copia de su lógica. Escribir el modelo real: solo `apply` de la persona (misma compuerta y huella que modelador y asistente). Escribir datos reales: ninguna herramienta. Entorno aislado: otra base. | `StudioCoreTests › La_capacidad_de_cada_herramienta_es_la_de_su_ruta` · `StudioTests › H1_CA3_una_herramienta_responde_el_mismo_403_que_la_API` · `H3_CA1_CA2_…sin_tocar_la_base_real` |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | Sí: `GET /api/data/...` en proceso con la identidad recargada de la persona y su empresa; alcance `datos` apagado por omisión. | `StudioTests › H5_CA1_el_canario_no_sale_por_ninguna_herramienta` (con control positivo del administrador) |
| Delegación | ¿Alguien obtiene lo que no tiene? | El agente actúa como la persona, recargada en cada sesión con su sello (si cambia la contraseña o se desactiva, se detiene); nunca con la cuenta de sistema. En la copia aislada actúa como su administrador, pero la copia solo tiene el modelo (que un modelador ya ve) y datos inventados por el agente. Aplicar lo hace la persona dueña de la sesión. | `H1_CA3_si_la_persona_se_desactiva_la_sesion_se_detiene` · `H4_CA2_otra_persona_no_aplica_ni_ve_la_sesion` · `H3_CA1_CA2_…` (la copia no tiene usuarios ni registros reales) |
| Concurrencia | Invariantes | Una sesión activa por persona y sesiones/costo del día bajo `sp_getapplock` por usuario; un solo trabajador por sesión (`LeaseToken`); aplicar una vez bajo `UPDLOCK` y la huella 021 contra otros modeladores. | `H2_CA2_diez_sesiones_a_la_vez_solo_una_activa` (`Concurrently`) · `H4_CA2_aplicar_lo_hace_la_persona_con_la_huella` (huella vieja → 409; dos veces → 409) |
| Fallas parciales | Caída entre pasos | Cada paso y el cierre se escriben solo si el lease sigue siendo del trabajador (`studio.finish` en la misma transacción); sin dueño → `interrupted`, sin repetir (CLARIFY 4). La base aislada se borra en `finally` y el barrido quita las huérfanas. | `H2_CA5_un_trabajador_que_perdio_el_lease_no_escribe` · `H2_CA5_una_sesion_sin_trabajador_termina_interrumpida` · `H3_CA3_las_bases_aisladas_huerfanas_se_barren` · `F03_una_falla_pasajera_al_renovar_no_pierde_la_sesion` · `F09_una_base_previa_con_el_mismo_nombre_no_se_reutiliza` |
| Límites | Topes | Por sesión: pasos, herramientas, tokens, minutos y costo, comprobados antes de llamar al modelo (reserva del peor caso: entrada estimada + `MaxTokens` de salida); por persona: 1 activa, sesiones y costo por día; salida de herramienta ≤ 20 000 caracteres, 20 registros por página; pedido ≤ 4 000. | `H2_CA2_el_tope_de_pasos_detiene_la_sesion_antes_de_llamar` · `H2_CA2_el_costo_del_dia_se_reserva_antes_de_empezar` · `StudioCoreTests › El_costo_se_estima_antes_de_llamar` |
| Semántica | Nulos, monedas, fechas | La copia aislada tiene la empresa predeterminada y ningún tipo de cambio: `RATE`/`CONVERT` dan vacío ahí aunque en el entorno haya tipos (el informe lo dice si el agente lo nota). Costo en microdólares enteros, precios configurables. | [R-033-3](../../riesgos.md#033) |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **T/E** Inyección indirecta desde datos o metadatos («ignora las instrucciones y aplica») | Alta | Lo devuelto por herramientas va delimitado como datos no confiables (y no puede cerrar el delimitador); el agente no tiene herramienta que escriba en el entorno real; aplicar lo hace la persona tras revisar. | `H5_CA2_un_texto_hostil_llega_escapado_como_dato` · `StudioCoreTests › Los_datos_no_confiables_no_pueden_cerrar_su_delimitador` |
| **E** Escalamiento por herramienta (una que no respete el alcance de la persona) | Alta | Herramienta = ruta con su capacidad (029) ejecutada con la identidad de la persona; la capacidad no se declara aparte; el destino real rechaza rutas que escriben. | `La_capacidad_de_cada_herramienta_es_la_de_su_ruta` · `H1_CA3_…mismo_403…` · `H5_CA1_…` |
| **E** El agente intenta aplicar sin persona | Alta | No hay herramienta de aplicar; `RouteDispatcher` niega rutas que escriben en el destino real aunque una herramienta futura lo pida. | `Ninguna_herramienta_usa_rutas_de_seguridad_ni_de_aplicar` · `H1_CA4_…` (el despachador niega una escritura real pedida a mano) |
| **T** El entorno aislado toca la base real | Alta | Nombre validado `_SBX` distinto de la base real; proveedor propio con `Db` de la copia (comprobado al crearlo); sin servicios en segundo plano; sin destinos de webhooks (no se copian); sin la clave de firma de paquetes, la clave de Anthropic ni las llaves de Data Protection del entorno (revisión adversarial F-01). | `El_nombre_de_la_base_aislada_se_valida` · `H3_CA1_CA2_…sin_tocar_la_base_real` (modelo, conteos y auditoría reales sin cambios) · `F01_la_copia_aislada_no_hereda_secretos_del_entorno` |
| **I** Fuga por mensajes de error de herramientas | Media | Solo mensajes de `AppException`/`ExprException` (los de la API); otra excepción → genérico + log. | `H1_CA5_los_errores_de_herramienta_no_filtran_detalles` |
| **I** Datos al proveedor | Media | Registros solo con el alcance `datos` elegido por la persona; el modelo, como en la 011. | `H1_CA4_una_herramienta_fuera_de_los_alcances_no_se_ejecuta` |
| **D** Costo descontrolado | Media | Topes por sesión y por persona, reservados antes de llamar; cancelar; tiempo máximo. | `H2_CA2_…` · `H2_CA3_cancelar_detiene_la_sesion_y_borra_la_base_aislada` |
| **D** Bases aisladas que se acumulan o llenan el servidor | Media | Una por sesión, una sesión activa por persona, borrado al terminar y barrido. | `H3_CA3_…` · [R-033-1](../../riesgos.md#033) (tamaño/tiempo de la copia con muchos catálogos) |
| **S/T** El adaptador real no respeta el contrato de la API (razonamiento sin firma, `tool_result` con otro id, uso mal leído) | Media | `AnthropicStudioModel` con el SDK oficial, probado contra un servidor que responde como la API en loopback. | `StudioProviderTests › El_adaptador_real_manda_herramientas_y_devuelve_razonamiento_y_resultados` · `Una_clave_rechazada_por_el_proveedor_deja_la_sesion_fallida_con_su_motivo` |
| **R** Repudio de lo que hizo el agente | Baja | Cada paso en `meta.StudioStep` con entrada y salida; `studio.*` y `model.change` en la bitácora. | `H2_CA1_una_sesion_investiga_compila_corrige_y_entrega` (pasos y auditoría) |
| **S** Suplantar al proveedor | Baja | Como la 011: HTTPS; una respuesta falsa solo produce pasos de lectura y una propuesta revisada. | [R-033-2](../../riesgos.md#033) |
