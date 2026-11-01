# Verify 044 — MCP 2: copia aislada y propuestas por MCP

## Métricas
- inicio: 2026-11-01 14:31
- cierre: 2026-11-01 15:34
- unitarias: 1106 (+29: 755 de Erp.Tests, +3; 278 de integración, +26; Vitest 73, +0)
- e2e: 467 (+5: `mcp2.spec.ts`)
- corridas completas: 2 (dotnet test 1 min 58 s; E2E 467 en verde, 11 min 27 s); la siguiente es CI
- corridas parciales: 9 (integración de `Mcp2Tests` 5, de `McpTests`/`StudioTests`/`Hardening040` 1; E2E de `mcp.spec.ts` + `mcp2.spec.ts` 3); `--veces=3` 1
- contra el código anterior: 3 (integración: las 5 regresiones de la revisión adversarial con cada corrección desactivada; E2E: F-01 y F-02 con su corrección desactivada, una a la vez)
- hallazgos: 0/3/6 (adversarial A-01…A-05 corregidos, A-06 y A-07 a riesgos; exploratoria F-01, F-02)
- categorías: concurrencia=3, seguridad=1, motor-api=3, UI/UX=2
- revisión adversarial: 7 hallazgos
- riesgos: R-044-1, R-044-2, R-044-3, R-044-4, R-044-5

## 0. Antes de abrir la spec
`git rev-list --count origin/main..main` = 0. La última corrida de CI de `main` (`9f12355`, la 043) estaba en **success**.

## 1. Pruebas unitarias e integración
`dotnet test`: **755 unitarias** (752 + 3 en `tests/Erp.Tests/McpCoreTests.cs`, con dos reescritas) y **278 de integración** (252 + 26 en
`tests/Erp.IntegrationTests/Mcp2Tests.cs`), en verde (1 min 58 s). Vitest: 73.
- Unitarias (`McpCoreTests`): el catálogo MCP lee en el entorno real y escribe solo en la copia o como propuesta, toda escritura
  no idempotente pide `idempotencyKey`; alcances `aislado` y `proponer`; huella de la entrada independiente del orden; nombres
  `_SM<id>_SBX` que el barrido de Studio no reconoce; título de la propuesta sin Markdown (F-02).
- Integración (`Mcp2Tests`, SQL real y copias reales en LocalDB, 26): consentimiento (solo quien modela, sin copia en el
  servidor no se concede `aislado`); herramientas y anotaciones; la copia no toca la base real y se reutiliza; **10 primeras
  llamadas a la vez dejan una copia**; inactividad, descartar, desconectar, cambio de sello y conexión revocada; el barrido
  respeta el *lease* renovado de una llamada larga; preparación a medias y base huérfana; **topes de copias a la vez**
  (persona y entorno) y error que no consume la clave; clave faltante o mal formada; repetir, conflicto, validación que se
  repite, claves que se van con la copia; **10 llamadas con la misma clave dan un efecto**; clave por conexión y pendiente
  vencida; `entregar` (propuesta en Studio, aplicar con huella, otra persona 404, sin mensajes, fuera de los topes de Studio),
  plan que no compila, respuesta sin plan, **10 entregas con la misma clave dan una propuesta**, falla inyectada en la
  bitácora que no consume la clave, tope diario; Conexiones con la copia y descartarla (otra persona 404); regresiones A-01…A-05.
- `McpTests` (038) ajustada: `aislado` ya es un alcance (el desconocido de prueba es `escribir`) y los metadatos anuncian cuatro.

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | `mcp.spec.ts` + `mcp2.spec.ts` (iteración) | 2 fallas: captura de Conectar (casillas nuevas, esperado) y la URL de la propuesta (`/studio` en lugar de `/admin/studio`, corregida) | 27 s |
| 2 | Completa | 467 en verde | 11 min 27 s |
| 3 | `--veces=3` sobre `mcp.spec.ts`, `mcp2.spec.ts` (lento) | 3 vueltas en verde (17 cada una) | 1 min 32 s |

Archivos: `web/e2e/mcp2.spec.ts` (nuevo: Conectar con las casillas nuevas, Conexiones con la copia y descartarla, la propuesta
MCP revisada y aplicada en Studio, con a11y) y `mcp.spec.ts` (el alcance desconocido de prueba y su mensaje). Capturas: las
seis de `mcp.spec.ts` (`conectar-*`, `conexiones-*`) cambiaron por las casillas y los textos nuevos; revisadas una por una
(claro, oscuro y móvil). La propuesta MCP en Studio no tiene captura propia: es la pantalla de chat de la 037 (con captura)
con un aviso en lugar del mensaje de la persona.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff (`git diff HEAD`, 2 807 líneas), la tabla STRIDE sin la columna de
pruebas y `rutas.md`. No encontró caminos al entorno real ni escaladas por delegación (revisó capacidad y alcances en
`tools/list` y en cada llamada, `allowWrites: false` en el entorno real, la base de la copia validada, aplicar en Studio,
propiedad por conexión y persona, bloqueos y orden, SQL sin datos concatenados, nombres de base). Encontró:

| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| A-01 | Media | concurrencia | La clave «en curso» tenía un *lease* fijo (300 s): una llamada más larga dejaba que un reintento con la misma clave ejecutara otra vez; si el cliente se desconectaba, la escritura se cortaba sin marca y se repetía al vencer. | La clave se renueva mientras la llamada corre (`McpWrites.Keep`, con el intento) y una escritura empezada corre sin el token de la petición y siempre se marca. | IT `Regresion_A01_una_escritura_larga_conserva_su_clave_y_termina_aunque_el_cliente_se_vaya` (con un retén sobre la tabla de la copia) |
| A-02 | Media | seguridad | Descartar y volver a preparar en bucle creaba y borraba bases sin tope (los topes contaban copias vivas). | `Mcp:SandboxCreationsPerHour` (6) por persona, contado bajo el bloqueo global. | IT `Regresion_A02_preparar_copias_tiene_tope_por_hora` |
| A-03 | Baja | motor-api | `IdemKey` comparaba sin distinguir mayúsculas. | `COLLATE Latin1_General_BIN2`. | IT `Regresion_A03_claves_que_solo_difieren_en_mayusculas_son_distintas` |
| A-04 | Baja | concurrencia | Un error al abrir la copia (base que no responde) se tomaba como copia perdida y se borraba con sus claves. | `SandboxFactory.OpenAsync(strict)`: el error es 503 y la copia se conserva. | IT `Regresion_A04_una_copia_que_no_responde_no_se_borra` (base fuera de línea, otra instancia) |
| A-05 | Baja | motor-api | Si fallaba marcar la copia como lista, quedaban su proveedor en memoria y su base hasta el barrido (15 min). | Se liberan el proveedor, la base y la fila. | IT `Regresion_A05_si_falla_marcar_la_copia_lista_no_queda_nada` |
| A-06 | Baja | concurrencia | Descartar o desconectar no espera a una llamada en curso sobre la copia. | — | [R-044-3](../../riesgos.md#044) |
| A-07 | Baja | motor-api | `meta.McpWrite` puede crecer mucho en un día por conexión. | — | [R-044-4](../../riesgos.md#044) |

Contra el código anterior: con las cinco correcciones desactivadas, las cinco regresiones fallan (`dotnet test --filter
Mcp2Tests.Regresion`: 5 con error); restauradas, pasan.

## 4. Revisión exploratoria
Instancia: `npm --prefix web run revision` (puerto 5950, base `ErpAi_Revision_E2E`). Roles: modeladora (`rev-mia`, dos
conexiones: una con `aislado` y `proponer`, otra solo con `proponer` y un nombre de cliente muy largo) y lector. Datos con
acentos y ñ. Pantallas: Studio con la propuesta MCP y sin clave de Anthropic, Conexiones de Claude (claro y oscuro, 1280 y
390 px), Conectar. Por API (como el cliente): entregar con planes inválidos (identificador con ñ, tipo desconocido) recibe los
errores del compilador y nada se guarda.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | UI/UX | En la propuesta MCP el informe decía «Lo que el agente informa» y avisaba que «no ejecutó ninguna herramienta en la copia aislada», que no aplica: las pruebas las hizo el cliente y el ERP no las ve. | `ReportView` con el cliente: «Lo que informa *cliente*» y «sus pruebas las hizo por su cuenta y este ERP no las vio», sin comparar con pasos que no tiene. | E2E `mcp2.spec.ts › H5 CA1 CA2 CA3` (falla con `ChatView` sin el cliente) |
| F-02 | Baja | UI/UX | El título de la propuesta en la barra y el encabezado mostraba las marcas de Markdown del resumen («**Vehículos**»). | Título = primera línea del resumen sin marcas (`McpProposals.Title`). | E2E `mcp2.spec.ts › H5 CA1 CA2 CA3` (falla con el título anterior) · unit `El_titulo_de_una_propuesta_es_la_primera_linea_sin_Markdown` |

### Lista de comprobación
**Flujo**
- [x] El camino feliz de cada historia se completa sin ayuda (conectar con las casillas, la copia aparece en Conexiones, la propuesta se abre desde la URL que devuelve `entregar` y se aplica)
- [x] Los mensajes de error dicen qué pasó y cómo resolverlo (plan que no compila, tope de copias, clave en curso, propuesta sin mensajes)
- [x] Doble clic y dos pestañas: idempotencia probada en integración (10 a la vez); descartar dos veces da 404 con aviso
- [x] Datos límite: acentos y ñ en resúmenes y nombres, nombre de cliente muy largo (se ajusta en 390 px)

**Diseño**
- [x] Claro y oscuro, 1280 y 390 px: nada tapado ni desbordado (Conexiones sin desplazamiento horizontal en 390 px)
- [x] Solo iconos Lucide (`FlaskConical`, `Inbox`, `Trash2`)
- [x] Estados: copia «preparándose», sin copia, Studio sin clave con sus conversaciones

**Accesibilidad**
- [x] Teclado: «Descartar copia» abre el diálogo con el foco en Cancelar; Esc lo cierra y el foco vuelve al botón
- [x] axe sin violaciones en Conectar, Conexiones y la propuesta en Studio (`expectAccessible`)

**Seguridad**
- [x] Quien no modela no ve ni recibe `aislado`/`proponer` (pantalla y POST directo); otra persona no descarta la copia ni ve la propuesta (404)
- [x] Consola sin errores de la página (solo el 401 esperado de `/api/auth/me` antes de iniciar sesión)
- [x] Caminos alternos del STRIDE por API directa (integración)
- [x] Concurrencia: primeras llamadas, topes, claves y entregas a la vez (integración)

**Cierre**
- [x] Pasada con teclado por las pantallas tocadas
- [x] Hallazgos de un componente genérico: `ReportView` solo se usa en `ChatView`
- [x] Alcance congelado: nada nuevo pedido durante VERIFY

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#044): R-044-1 (repetición en la copia tras una caída), R-044-2 (topes de copias
separados de Studio), R-044-3 (descartar no espera a una llamada en curso), R-044-4 (crecimiento de `meta.McpWrite`), R-044-5
(nombre del cliente sin verificar en la propuesta). R-040-3 sigue aceptado: el dueño no pidió la bitácora de lectura.
