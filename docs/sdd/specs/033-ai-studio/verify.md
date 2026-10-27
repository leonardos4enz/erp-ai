# Verify 033 — ERP AI Studio: herramientas, bucle agéntico y entorno aislado

## Métricas
- inicio: 2026-10-27 11:41
- cierre: 2026-10-27 12:37
- unitarias: 740 (+35: 643 de Erp.Tests, +7 en StudioCoreTests; 97 de integración, +28 en StudioTests, StudioModulesTests y StudioProviderTests) y Vitest 36 (sin cambios)
- e2e: 374 (+0: la spec no tiene pantalla; la 034 la agrega)
- corridas completas: 2 (7 min 23 s antes de la revisión adversarial; 7 min 09 s la final)
- corridas parciales: 12 (StudioTests ×4 iterando; StudioModulesTests; StudioProviderTests ×6 hasta tener el servidor simulado; las nuevas de integración y StudioCoreTests ×3 con el código final)
- contra el código anterior: 1 (reversión local de las 7 correcciones de la revisión adversarial: las 7 pruebas fallan)
- hallazgos: 0/2/9
- categorías: seguridad=5, motor-api=4, pruebas=2
- revisión adversarial: 11 hallazgos (0 altos, 1 medio, 10 bajos)
- riesgos: R-033-1, R-033-2, R-033-3, R-033-4, R-033-5, R-033-6, R-033-7, R-033-8, R-033-9, R-033-10, R-033-11

## 0. Apertura
`git rev-list --count origin/main..main` = 0 y la última corrida de CI en `main` (`611a4f1`, cierre de la 032) en verde
(`curl …/actions/runs?branch=main&per_page=2`: `completed`/`success`, también `b8ac1db`). CLARIFY dividió la spec (dos
criterios: ~3 300 líneas con la pantalla y seguridad/concurrencia junto con UI nueva): la 033 es el motor (1 893 líneas de
producto nuevas en `src/Erp.Api/Studio`, la ruta y el inventario vivo) y la **034** la pantalla. Renumeración del roadmap:
035 servidor MCP, 036 Ventas, 037 CFDI 4.0; auditoría 6 al cerrar la 033 (trabajo agéntico y delegación de identidad) y 7
al cerrar la 035 (punto de entrada OAuth).

## 1. Pruebas unitarias e integración
`dotnet test`: **740 correctas** (643 + 97). Nuevas:
- `tests/Erp.Tests/StudioCoreTests.cs` (7): cada herramienta del entorno real usa una ruta del inventario que no escribe;
  la capacidad de cada herramienta es la de su ruta; ninguna toca rutas de seguridad, usuarios, claves, paquetes ni aplicar;
  esquemas y nombres válidos; nombre de la base aislada (sufijo, huella, inyección, longitud); el delimitador de datos no
  confiables no se puede cerrar desde un dato; estimación y reserva de costo.
- `tests/Erp.IntegrationTests/StudioTests.cs` (25, SQL real, proveedor de LLM simulado por guion, ninguna llamada a
  Anthropic): H1 (catálogo solo para modeladores; el mismo 403 que la API; identidad recargada antes de cada paso y cada
  herramienta; alcances; el despachador niega una escritura real pedida a mano; errores sin detalles), H2 (flujo completo
  investigar → compilar → corregir → entregar → corregir la entrega; respuesta sin plan; tope de pasos sin la llamada de
  más; **10 sesiones a la vez → una aceptada**; costo del día reservado antes de llamar; cancelar con base aislada viva;
  **lease perdido sin escrituras**; sesión sin trabajador → interrumpida y su base borrada), H3 (plan, registros, regla que
  rechaza, transición con `set`, automatizaciones; base real sin cambios en modelo, datos y auditoría; barrido que no toca
  bases de otra base real), H4 (aplicar con huella: sin huella 400, huella vieja 409, una sola vez; otra persona 404;
  entorno con aprobación 409), H5 (canario por **todas** las herramientas con control positivo del superusuario; texto
  hostil escapado) y las 5 de la revisión adversarial.
- `StudioModulesTests` (1): la copia aislada con Compras, Inventario, Contabilidad y Costeo (22 catálogos) en **0.5 s**; ahí
  corren las reglas de `Proveedor`.
- `StudioProviderTests` (2): el adaptador real (`AnthropicStudioModel`, SDK oficial) contra un servidor HTTP mínimo en
  loopback que responde como la API: esquema de herramientas, instrucciones con `cache_control`, el razonamiento devuelto
  con su firma, `tool_result` con el mismo id y delimitado, costo con lectura de caché; un 401 da «Anthropic rechazó la
  clave» sin el texto del proveedor. (`WebApplicationFactory` intercepta cualquier `WebApplication` del proceso de pruebas:
  el servidor simulado es un `TcpListener`.)

`ACTUALIZAR_RUTAS=1 dotnet test --filter RouteInventory`: [`rutas.md`](../../rutas.md) con las 10 rutas nuevas (149); todas
declaran capacidad, `POST /api/studio/sessions` es costosa con su límite propio y `apply` tiene compuerta y huella.
`npm --prefix web test`: 36 correctas (sin cambios).

## 2. E2E
| # | Qué | Resultado | Duración |
|---|---|---|---|
| 1 | Completa (antes de la revisión adversarial) | 374 en verde | 7 min 23 s |
| 2 | Completa final (con las correcciones) | **374 en verde** | 7 min 09 s |

La spec no tiene pantalla ni archivos E2E nuevos (las pruebas de API sin página van en integración, CLAUDE.md); el canario y
`capacidades.spec.ts` recorren las rutas nuevas del inventario (GET de modelador: 403 al Lector). Lo nuevo con `--veces=3`
son las clases de integración: `dotnet test --filter Studio` tres veces con el código final, 28/28 cada vez (29 s, 31 s,
28 s), y `StudioCoreTests` 7/7. La segunda corrida completa es la de CI tras el push.

## 3. Revisión adversarial
Subagente sin el contexto de la sesión. Recibió el diff (`git diff` con los archivos nuevos) y la tabla STRIDE de
`plan.md`, sin sus conclusiones. Revisó: despacho de rutas y capacidades, alcances, parámetros que controla el modelo,
servicios compartidos entre la app y la copia, nombre y borrado de bases, lease, cancelar, reservas, aplicar, fugas y
costo. Sin defecto: el despachador niega en el entorno real toda ruta que escribe y exige la capacidad; rutas fijas con
entrada validada y escapada; `RecordFilter`/`FieldAccess`/empresa por la misma ruta; identidad recargada; propiedad por
usuario; aplicar con `UPDLOCK`, huella y compuerta; reclamo con `READPAST`; escrituras con lease; nombre de la copia y
barrido. Encontró F-01…F-11 (abajo).

## 4. Revisión exploratoria
No hay pantalla (CLARIFY 1: la 034). No se usó el servidor de desarrollo: sin la contraseña de `admin` (del dueño) ni una
clave de Anthropic de prueba, y la spec prohíbe llamar a la API real. La exploración se hizo por la API real en memoria con
el adaptador real contra el proveedor simulado (`StudioProviderTests`) y con sesiones completas en integración; el recorrido
humano de la pantalla queda para la 034.

### Hallazgos
| ID | Severidad | Categoría | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|---|
| F-01 | Media | seguridad | La copia aislada heredaba la configuración real: clave de firma de paquetes (un «admin» de la copia firmaría paquetes válidos para producción si una herramienta futura o el MCP llegaran a `/api/environments`), la clave de Anthropic y la carpeta de llaves de Data Protection | La copia anula `Environments:SigningKey`, `Assistant:*` y `DataProtection:KeysPath`, y usa `EphemeralDataProtectionProvider` y sus propias `AssistantOptions` | `StudioTests › F01_la_copia_aislada_no_hereda_secretos_del_entorno` (falla con la reversión) |
| F-02 | Media | seguridad | La reserva no era el peor caso: 3 caracteres por token (un texto CJK o de símbolos da ~1 por carácter), sin la tarifa de escritura de caché, y el tope de tokens se miraba después de la llamada | Entrada estimada = tokens medidos de la llamada anterior + un token por carácter nuevo, a la tarifa de escritura de caché, con la salida completa; el tope de tokens cuenta la siguiente llamada | `StudioCoreTests › El_costo_se_estima_antes_de_llamar` (falla con la reversión); R-033-7 |
| F-03 | Baja | concurrencia | Una falla pasajera de la base al renovar el lease mataba la renovación y, en el `finally`, `await renew` relanzaba: no se borraba la copia y una entrega ya pagada se perdía (interrumpida) | La renovación atrapa y reintenta; la copia se borra antes de esperar la renovación | `StudioTests › F03_una_falla_pasajera_al_renovar_no_pierde_la_sesion` (restricción CHECK temporal; falla con la reversión) |
| F-04 | Baja | motor-api | `tested: ["texto"]` en la entrega lanzaba una excepción y la sesión terminaba fallida al final | Una prueba en texto se guarda como descripción con `ok = false` | `StudioTests › F04_una_prueba_en_texto_no_tumba_la_entrega` (falla con la reversión) |
| F-05 | Baja | seguridad | Los errores de `entregar` (mensajes del compilador que citan etiquetas del modelo, escritas por otras personas) volvían al modelo sin delimitar | También van en `<datos_no_confiables herramienta="entregar">` | `StudioTests › H2_CA1_una_sesion_investiga_compila_corrige_y_entrega` (falla con la reversión) |
| F-06 | Baja | motor-api | Un nombre de herramienta de más de 60 caracteres (lo escribe el modelo) truncaba la columna y tumbaba la sesión; `Studio:MaxPromptChars` admitía 20 000 con una columna de 4 000 | Nombre recortado y, en el delimitador, solo nombres del catálogo («desconocida»); tope del pedido 4 000 | `StudioTests › F06_un_nombre_de_herramienta_largo_no_tumba_la_sesion` (falla con la reversión) |
| F-07 | Baja | seguridad | `compilar_plan` devuelve, para un plan que quita estados, el conteo de registros por estado de toda la tabla aunque la sesión no tenga `datos` | Riesgo (es lo que el Modelador ya muestra a la persona; un conteo, no valores) | R-033-9 |
| F-08 | Baja | seguridad | Los pasos guardan registros leídos sin caducidad y ningún auditor ve los pasos de otra persona | Riesgo (caducidad en la 034) | R-033-4 |
| F-09 | Baja | motor-api | Una base previa con el nombre de la copia (huérfana sin barrer, id repetido tras restaurar) se reutilizaba con datos de otra sesión | La copia borra una base con ese nombre antes de crearla | `StudioTests › F09_una_base_previa_con_el_mismo_nombre_no_se_reutiliza` (falla con la reversión) |
| F-10 | Baja | motor-api | Cada sondeo de una propuesta recompila el plan y sin `after` devuelve todos los pasos | Riesgo (la pantalla pide con `after`) | R-033-10 |
| F-11 | Baja | pruebas | El informe «probado» es afirmación del modelo; la creación de la copia no recibe el token de cancelación; carrera del barrido entre leer las sesiones y las bases | El barrido lee las sesiones vivas después de las bases; el resto, riesgo | R-033-11 |

Además, en la implementación (no de la revisión): el `HttpContext` en proceso no traía `IHttpRequestBodyDetectionFeature` y
las rutas mínimas ignoraban el cuerpo (`compilar_plan` y la copia del modelo respondían «formato no válido»); lo detectaron
H2 CA1 y H3 en la primera corrida.

### Contra el código anterior
Las correcciones de la revisión no tenían un commit intermedio: se revirtieron en el árbol con el script inverso de las
ediciones y se corrieron sus pruebas: **fallan las 7** (`El_costo_se_estima_antes_de_llamar`, F01, F03, F04, F06, F09 y
H2 CA1 por F-05). Restauradas, todo en verde. Las historias son capacidades nuevas (no hay código anterior que probar).

### Lista de comprobación
- [x] Cada ruta nueva declara capacidad (`RouteInventoryTests`), efecto, compuerta (`apply`) y límite (`sessions`).
- [x] Ninguna herramienta escribe en el entorno real ni usa rutas de seguridad (unitarias) y el despachador lo niega aunque
      se le pida (integración).
- [x] Canario de la 029 por todas las herramientas, con control positivo.
- [x] Concurrencia y fallas parciales en integración (`Concurrently`, lease ajeno, sesión sin trabajador, falla al renovar).
- [x] La suite y CI no llaman a Anthropic (proveedor simulado por DI y por loopback).
- [x] Sin pantalla nueva (034): sin capturas que revisar.

## 5. Riesgos residuales
Registrados en [riesgos.md](../../riesgos.md#033): R-033-1 (permiso para crear bases; tiempo medido hasta 22 catálogos),
R-033-2 (suplantar al proveedor), R-033-3 (la copia sin tipos de cambio ni empresas), **R-033-4** (pasos con datos sin
caducidad, medio), R-033-5 (rutas en proceso sin limitadores de ASP.NET), R-033-6 (la copia como administrador),
R-033-7 (precios configurados), R-033-8 (sesión interrumpida no se retoma), R-033-9 (conteo por estado en la vista previa),
R-033-10 (sondeo costoso), R-033-11 (informe declarado por el modelo).

## 6. CI
El push a `main` dispara la segunda corrida completa (integración y E2E en `windows-2025`); su resultado se registra en un commit aparte («CI en verde»), como en la 030–032.
