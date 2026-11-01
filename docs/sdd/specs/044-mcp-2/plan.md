# Plan 044 — Diseño técnico

## Piezas
```
 Claude (cliente MCP, token con «aislado» y/o «proponer»)          ERP
 POST /mcp tools/call aislado_* ──────────▶ McpServer
     ├─ idempotencyKey (si escribe) ──────▶ McpWrites.BeginAsync: meta.McpWrite (pendiente, lease, intento) | repetir | 409
     ├─ McpSandboxes.AcquireAsync(conexión) ─ sp_getapplock por conexión → meta.McpSandbox (creating → ready)
     │                                       └─ SandboxFactory.CreateAsync(<prefijo>_SM<id>_SBX) / OpenAsync (marca)
     ├─ StudioToolRunner (capacidad modelador, límites de la persona) → RouteDispatcher en la copia, como su administrador
     └─ McpWrites.FinishAsync (resultado, si el intento sigue siendo suyo) · lease.DisposeAsync (último uso)
 POST /mcp tools/call entregar ───────────▶ McpProposals: compila contra el modelo real; en UNA transacción: clave + StudioSession
                                            (Origin 'mcp', PlanStatus 'proposed') + paso «deliver» + bitácora mcp.proposal
 POST /mcp tools/call aislado_descartar ──▶ McpSandboxes.ReleaseAsync(conexión)
 McpSandboxSweeper (BackgroundService) ───▶ inactivas · conexiones muertas o sello cambiado · preparaciones vencidas · huérfanas
                                            · claves vencidas
 SPA Conectar: casillas aislado/proponer · Conexiones: copia viva + DELETE /api/mcp/connections/{id}/sandbox
 SPA Studio: propuesta con origen MCP (sin compositor), conversaciones visibles sin clave de Anthropic
```

## 1. Modelo de datos (Bootstrap, idempotente)
```sql
meta.McpSandbox (Id int IDENTITY PK, GrantId int NOT NULL CONSTRAINT UX_McpSandbox_Grant UNIQUE, UserId int NOT NULL,
  DbName nvarchar(128) NOT NULL, Token uniqueidentifier NOT NULL, Status varchar(10) NOT NULL /* creating ready */,
  CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(), LastUsedAt datetime2(3) NOT NULL, LeaseUntil datetime2(3) NOT NULL)
  INDEX (UserId)
meta.McpWrite (Id bigint IDENTITY PK, GrantId int NOT NULL, IdemKey varchar(100) NOT NULL, Tool nvarchar(60) NOT NULL,
  InputHash binary(32) NOT NULL, SandboxId int NULL, Status varchar(8) NOT NULL /* pending done */, Attempt uniqueidentifier NOT NULL,
  PendingUntil datetime2(3) NULL, ResultJson nvarchar(max) NULL, CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME())
  UNIQUE (GrantId, IdemKey)
meta.StudioSession + Origin varchar(8) NULL /* null = Studio, 'mcp' */, McpGrantId int NULL, McpClient nvarchar(100) NULL
```
Sin llaves foráneas a `meta.McpGrant`: el barrido de clientes de la 040 borra conexiones muertas, y el de copias trata como
muerta una fila cuya conexión ya no existe. Sin operaciones nuevas del Plan de Cambios.

## 2. Contrato de la API
| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `POST /mcp` (cambia) | sesión (solo token MCP) · **`Writes(Session)`** (antes `ReadsOnly`) · cubeta por persona | JSON-RPC 2.0 | igual que la 038 |
| `GET /.well-known/oauth-*` (cambia) | anónima | — | `scopes_supported: ["modelo","datos","aislado","proponer"]` |
| `GET /api/mcp/authorize` (cambia) | sesión | consulta OAuth | `scopes: [{ name, required, requested, available, reason? }]` |
| `POST /api/mcp/authorize` (cambia) | sesión · `Writes(Session)` | `{ query, approve, scopes }` | el código lleva lo marcado ∩ lo pedido ∩ lo disponible |
| `GET /api/mcp/connections` (cambia) | sesión | — | cada conexión + `sandbox: { createdAt, lastUsedAt, status } \| null` |
| `DELETE /api/mcp/connections/{id:int}/sandbox` (nueva) | sesión · `Writes(Session)` | — | 204 · 404 si la conexión no es suya, está revocada o no tiene copia |
| `GET /api/studio/sessions[/{id}]` (cambia) | modelador | — | `origin` (`studio`/`mcp`) y `client`; `canSend: false` con motivo en las de MCP |
| `POST /api/studio/sessions/{id}/messages` (cambia) | modelador | — | 409 «Esta propuesta llegó por MCP…» en las de MCP |

### Herramientas MCP nuevas (`tools/call`)
| Herramienta | Alcance | Clave | Anotaciones | `structuredContent` |
|---|---|---|---|---|
| `aislado_aplicar_plan` | `aislado` | sí | `readOnly:false, idempotent:false, destructive:false` | `{ applied, operations }` |
| `aislado_crear` | `aislado` | sí | ídem | el registro creado |
| `aislado_actualizar` | `aislado` | sí | ídem | el registro |
| `aislado_transicion` | `aislado` | sí | ídem | el registro |
| `aislado_procesar_asincronas` | `aislado` | sí | ídem | `{ processed, runs }` |
| `aislado_leer` / `aislado_listar` | `aislado` | no | `readOnly:true, idempotent:true` | el registro / `{ items, total, … }` |
| `aislado_descartar` | `aislado` | no | `readOnly:false, idempotent:true, destructive:true` | `{ discarded }` |
| `entregar` | `proponer` | sí | `readOnly:false, idempotent:false, destructive:false` | `{ proposalId, status, url }` |

Todas exigen *modelador* (capacidad publicada de la copia y del servidor, 034). Errores de herramienta nuevos:
`idempotencyKey` faltante o mal formada (400), clave con otra entrada (409), clave en curso (409 + `retryAfterSeconds`), copia
preparándose (409 + `retryAfterSeconds`), tope de copias (429), tope de propuestas (429), plan que no compila (400 + `errors`),
copia no disponible (503).

### Configuración (`Mcp:*`, nuevas)
`SandboxesPerUser` 2 · `MaxSandboxes` 10 · `SandboxIdleMinutes` 30 · `SandboxLeaseSeconds` 120 (renovación cada tercio) ·
`ProposalsPerDay` 20 · `IdempotencyHours` 24 · `WriteLeaseSeconds` 300 · `SweepSeconds` 30 · `SandboxCreationsPerHour` 6 (revisión adversarial A-02). La copia por MCP exige además
`Studio:Sandbox`.

Auditoría: `mcp.proposal` (propuesta creada: sesión, cliente, operaciones), `mcp.sandbox` (copia preparada o borrada, con el
motivo: `idle`, `revoked`, `discarded`, `person`, `stuck`). Clasificadas en `AuditFilter.OtherActions` como las `mcp.*`.

## 3. SPA
- **Conectar**: dos casillas más, apagadas por omisión; deshabilitadas con el motivo que da el servidor (`reason`). La nota de
  pie deja de decir «Nada se escribe»: «No se escriben registros ni se cambia el modelo por esta conexión: las pruebas van en
  una copia aislada y las propuestas las aplicas tú en ERP AI Studio».
- **Conexiones de Claude**: insignias de los cuatro alcances; si hay copia, una línea «Copia aislada desde … · último uso …» y
  «Descartar copia» con confirmación (`ConfirmDialog`). El subtítulo deja de decir «solo para leer».
- **Studio**: la página carga las conversaciones aunque no haya clave (la bienvenida muestra el estado «no configurado» en
  lugar del compositor); en la barra, la etiqueta «MCP»; en la conversación de origen MCP, en lugar del mensaje de la persona,
  un aviso «Propuesta de *cliente* por MCP» con la fecha; el compositor queda bloqueado con el motivo del servidor.

## 4. Pruebas
- **Unitarias** (`tests/Erp.Tests/McpCoreTests.cs`): el catálogo MCP — las del entorno real solo leen, las que escriben son de
  la copia o `entregar`, piden clave y se anotan como no idempotentes —, alcances nuevos en `Parse`, formato de la clave,
  entrada canónica (el orden de las propiedades no cambia la huella), nombres de copia por conexión (no los barre Studio).
- **Integración** (`tests/Erp.IntegrationTests/Mcp2Tests.cs`, nuevo; SQL real, copias reales en LocalDB): una prueba por CA y
  las de concurrencia y fallas parciales de la tabla de abajo.
- **E2E** (`web/e2e/mcp2.spec.ts`, nuevo): Conectar con las casillas nuevas, Conexiones con la copia y descartarla, Studio con
  la propuesta MCP (aplicar). `mcp.spec.ts` y su captura cambian (casillas nuevas). `docs/sdd/rutas.md` regenerado.

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas y caminos nuevos | 1 ruta nueva (`DELETE /api/mcp/connections/{id}/sandbox`, sesión) y `/mcp` pasa a `Writes(Session)`. 9 herramientas MCP nuevas por el mismo `tools/call`: 7 de la copia (rutas del inventario corridas en la copia), `aislado_descartar` y `entregar`. Dos alcances nuevos en el flujo OAuth. | `RouteInventoryTests` (snapshot) · `McpCoreTests › El_catalogo_MCP_lee_en_el_entorno_real_y_escribe_solo_en_la_copia_o_como_propuesta` · `Mcp2Tests › H2_CA1_con_aislado_tools_list_da_las_de_la_copia_y_sin_el_no` |
| Caminos alternos | ¿Otro camino al mismo efecto? | Al modelo real: solo por aplicar en Studio (huella, compuerta, `PlanService`), igual que una propuesta del agente. A registros reales: ninguno (las herramientas del entorno real siguen con `allowWrites: false`). La copia: la misma regla que en Studio (capacidad de modelador y límites de la persona en el despachador). Enviar mensajes a una propuesta MCP: la API lo niega, no solo la pantalla. | `Mcp2Tests › H5_CA2_aplicar_exige_la_huella_y_otra_persona_no_la_ve` · `› H5_CA3_a_una_propuesta_MCP_no_se_le_mandan_mensajes` · `› H2_CA1_la_copia_no_toca_la_base_real` |
| Lectura de datos | ¿`RecordFilter`, `FieldAccess`, empresa? | La copia no tiene datos reales (como en Studio). `entregar` no lee registros. Las herramientas de lectura del entorno real no cambian. | `Mcp2Tests › H2_CA1_la_copia_no_toca_la_base_real` |
| Delegación | ¿Alguien obtiene lo que no tiene? | `aislado` y `proponer` solo para modeladores y lo que el servidor permite, también si el POST los trae; el runner vuelve a exigir *modelador* en cada llamada. La propuesta es de la persona del token (Studio solo la muestra a ella) y aplicarla exige sus permisos de modelador y la compuerta. Descartar una copia ajena: 404. | `Mcp2Tests › H1_CA3_quien_no_modela_no_recibe_aislado_ni_proponer` · `› H1_CA3_sin_copia_en_el_servidor_no_se_concede_aislado` · `› H6_CA2_otra_persona_no_descarta_mi_copia` · `› H5_CA2_…` |
| Concurrencia | Invariantes | Una copia por conexión (bloqueo por conexión + `UNIQUE(GrantId)`); topes de copias bajo un bloqueo global; una clave = un efecto (`UNIQUE(GrantId, IdemKey)` + `UPDLOCK, HOLDLOCK`); el barrido no borra una copia con *lease* vigente (renovado en llamadas largas); el resultado de un intento solo se guarda si el intento sigue siendo suyo. | `Mcp2Tests › H2_CA2_diez_primeras_llamadas_a_la_vez_dejan_una_copia` · `› H3_CA2_diez_llamadas_con_la_misma_clave_dan_un_efecto` · `› H4_CA2_diez_entregas_con_la_misma_clave_dan_una_propuesta` · `› H2_CA4_los_topes_de_copias_se_cumplen_a_la_vez` · `› H2_CA3_el_barrido_respeta_el_lease_de_una_llamada_en_curso` · `› Regresion_A01_una_escritura_larga_conserva_su_clave_y_termina_aunque_el_cliente_se_vaya` · `› Regresion_A03_claves_que_solo_difieren_en_mayusculas_son_distintas` |
| Fallas parciales | Caída entre pasos | Copia: la fila «creating» se escribe antes de crear la base (el barrido la encuentra); si la preparación falla, se borra la fila y la base; si el proceso cae, la fila vence y se barre, y una base sin fila se borra. Clave en la copia: pendiente con *lease* e intento; al caer, se puede reintentar al vencer (R-044-1). `entregar`: todo en una transacción; si falla la bitácora, no queda propuesta ni clave. | `Mcp2Tests › H2_CA3_una_preparacion_a_medias_y_una_base_huerfana_se_barren` · `› H3_CA2_una_clave_pendiente_vencida_se_puede_reintentar` · `› H4_CA2_una_falla_al_guardar_la_propuesta_no_consume_la_clave` · `› Regresion_A04_una_copia_que_no_responde_no_se_borra` · `› Regresion_A05_si_falla_marcar_la_copia_lista_no_queda_nada` · [R-044-1](../../riesgos.md#044) |
| Límites | Tamaño, frecuencia | Copias por persona y en el entorno; propuestas por día; clave de 8 a 100; plan ≤ 100 operaciones, resumen ≤ 4 000; cuerpo ≤ 512 KB (038); cubeta MCP y límites del despachador en cada ruta de la copia; resultado ≤ `Mcp:MaxResultBytes`; claves que vencen. | `Mcp2Tests › H2_CA4_…` · `› H4_CA3_el_tope_de_propuestas_por_dia` · `› H3_CA1_sin_clave_o_con_una_mal_formada_es_error` · `› Regresion_A02_preparar_copias_tiene_tope_por_hora` (`Mcp:SandboxCreationsPerHour`, revisión adversarial) · [R-044-4](../../riesgos.md#044) |
| Semántica | Nulos, fechas | Fechas en UTC (como Studio); la huella de la entrada no depende del orden de las propiedades ni del espacio en blanco; un `plan` ausente en `entregar` da una respuesta sin propuesta (`status: answered`). | `McpCoreTests › La_huella_de_la_entrada_no_depende_del_orden` · `Mcp2Tests › H4_CA1_entregar_sin_plan_deja_una_respuesta` |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **E** Un cliente escribe en el entorno real (registros o modelo) | Alta | Las escrituras son de la copia (otro proveedor de servicios, otra base, nombre validado y distinto de la real) o una propuesta inerte; aplicar es de la persona en Studio con huella. | `Mcp2Tests › H2_CA1_la_copia_no_toca_la_base_real` · `› H5_CA2_…` · `McpCoreTests › El_catalogo_MCP_lee_en_el_entorno_real_…` |
| **E** Una persona que no modela usa la copia como su administrador | Alta | Alcances solo para modeladores (servidor), capacidad publicada exigida por el runner en cada llamada, sello de la persona en el token. | `Mcp2Tests › H1_CA3_quien_no_modela_…` · `› H2_CA1_si_deja_de_modelar_la_conexion_muere` |
| **T** Reintento que duplica una escritura | Media | Clave por conexión con huella de la entrada; `entregar` exactamente una vez; en la copia, a lo más una repetición tras una caída (R-044-1). | `Mcp2Tests › H3_CA2_…` · `› H4_CA2_…` |
| **D** Agotar el servidor con copias (bases) | Media | Topes por persona y entorno, preparaciones por hora (revisión adversarial A-02), inactividad, barrido de huérfanas y preparaciones vencidas, muerte con la conexión. | `Mcp2Tests › H2_CA3_…` · `› H2_CA4_…` · `› Regresion_A02_…` · [R-044-2](../../riesgos.md#044) |
| **D** Llenar Studio de propuestas | Baja | Tope diario por persona; no cuentan en los topes de costo de Studio. | `Mcp2Tests › H4_CA3_…` |
| **S** Phishing de consentimiento con alcances que escriben | Media | Las casillas nuevas van apagadas y deshabilitadas si el cliente no las pidió; el sitio de retorno sigue destacado (038, R-038-1). | `mcp2.spec.ts › H1 CA2: …` |
| **I** El resultado guardado de una clave filtra datos | Baja | La copia no tiene datos reales; las claves son por conexión (otra conexión no las lee) y vencen. | `Mcp2Tests › H3_CA2_la_clave_es_de_la_conexion` |
| **R** Repudio de una propuesta o de una copia | Baja | `mcp.proposal` y `mcp.sandbox` a nombre de la persona; la propuesta guarda el cliente. | `Mcp2Tests › H4_CA3_…` (bitácora) · `› H2_CA3_…` |
