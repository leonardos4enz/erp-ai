# Spec 044 — MCP 2: copia aislada y propuestas por MCP

**Estado:** Implementado · **Depende de:** 033/036 (copia aislada, `entregar`, propuesta con huella), 034 (capacidad publicada,
límites del despachador en la copia), 038/040 (servidor MCP, conexiones, registro dinámico) · **Origen:** roadmap, punto 14
(«044 — MCP 2», antes 040 y 043): lo que la 038 dejó fuera en su CLARIFY 2. Decisión del dueño (2026-11-01): va después de
Ventas, y la auditoría 8 (040–044) después de ella.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | `McpServer.CallAsync` arma el `ToolContext` con `SandboxAsync = _ => throw new SandboxUnavailable(...)`; `McpTools.All` solo tiene herramientas del entorno real; `McpScopes.All` es `[modelo, datos]` y `Parse` rechaza `aislado` (`invalid_scope`). | Un Claude conectado por MCP solo lee. No puede probar un plan, crear registros de prueba ni ejecutar transiciones: para eso hay que abrir Studio y pagar el modelo del ERP, aunque la persona ya tenga su propio Claude. |
| 2 | La copia aislada vive en `meta.StudioSession` (`SandboxDb`, `SandboxToken`), se crea en el turno del agente (`StudioAgent.CreateSandboxAsync`) y se suelta al terminar o por inactividad (`StudioService.ReleaseSandboxesAsync`); su nombre es `<prefijo>_S<sesión>_SBX`. | MCP no tiene turnos ni fin de conversación: no hay quién cree, renueve, suelte ni barra una copia de una conexión. |
| 3 | `entregar` es una herramienta de servidor sin `Run`: solo la interpreta `StudioAgent.DeliverAsync`, que deja la propuesta en la sesión del agente. | Lo que diseñe un Claude externo no llega a la persona como propuesta revisable: tendría que copiarlo al modelador a mano. |
| 4 | Todas las herramientas MCP declaran `readOnlyHint: true` e `idempotentHint: true`; `/mcp` es `ReadsOnly()` en el inventario. | Un cliente que reintenta una llamada (red caída, *timeout*, *code mode* que repite) no tiene cómo hacerlo sin duplicar el efecto en cuanto haya una herramienta que escriba. |
| 5 | La pantalla de Studio sin clave de Anthropic solo muestra «no está configurado» (`StudioPage`, `!st.configured`). | Una propuesta que no viene del agente de Studio no se podría revisar en un entorno sin clave. |

## Principio
Capacidad **genérica del motor**, sin código por herramienta ni por módulo:
- **La copia aislada tiene dos dueños posibles, con el mismo motor.** `SandboxFactory` crea, abre y borra copias por nombre;
  Studio las liga a una conversación y el MCP a una **conexión** (`meta.McpSandbox`): *lease* por llamada con renovación,
  inactividad, barrido de huérfanas y muerte con la conexión. Las herramientas `aislado_*` son las mismas de Studio, por el
  mismo `StudioToolRunner` (capacidad de modelador, límites de la persona en la copia).
- **Lo que escribe un cliente externo es una propuesta o una prueba, nunca el entorno real** (constitución II.8): las
  escrituras por MCP van a la copia aislada de la conexión, y `entregar` deja un Plan de Cambios que la persona revisa y aplica
  en Studio con la huella de siempre (012/021).
- **Toda escritura por MCP lleva clave de idempotencia** (lección 012-5/021-2: «revisar → aplicar» con huella; aquí,
  «llamar → reintentar» con clave): la misma clave y la misma entrada devuelven el mismo resultado sin repetir el efecto.

## Historias

**H1 — Consentir la copia y las propuestas.** Como *persona modeladora*, quiero autorizar a mi Claude a probar en una copia
aislada y a proponerme cambios, por separado y apagados por omisión, para dar solo lo que quiero.
- CA1: los metadatos (RFC 9728/8414) anuncian los alcances `modelo`, `datos`, `aislado` y `proponer`; `/oauth/authorize`
  acepta los cuatro y sigue rechazando uno desconocido con `invalid_scope`.
- CA2: la pantalla **Conectar** muestra `aislado` («Probar en una copia aislada») y `proponer` («Proponerte cambios al
  modelo») apagados por omisión; deshabilitados con el motivo si el cliente no los pidió, si la persona no es modeladora o
  (solo `aislado`) si el servidor no tiene copia aislada (`Studio:Sandbox`).
- CA3: el servidor solo concede lo disponible: una persona que no modela, o un servidor sin copia aislada, no recibe
  `aislado`/`proponer` aunque la petición los traiga marcados.

**H2 — Una copia aislada por conexión.** Como *persona*, quiero que mi Claude pruebe en una copia propia de su conexión que se
prepara sola y desaparece cuando ya no se usa, para no dejar bases olvidadas.
- CA1: con `aislado`, `tools/list` da `aislado_aplicar_plan`, `aislado_crear`, `aislado_actualizar`, `aislado_transicion`,
  `aislado_leer`, `aislado_listar`, `aislado_procesar_asincronas` y `aislado_descartar`. La primera que usa la copia la
  prepara (el modelo del entorno, sin datos reales) y las siguientes de la misma conexión la reutilizan; nada toca la base
  real.
- CA2: diez primeras llamadas a la vez en la misma conexión dejan **una** copia (las que esperan reciben un error con
  `retryAfterSeconds` o la copia ya lista).
- CA3: la copia se borra si no se usa en `Mcp:SandboxIdleMinutes`; al desconectar la conexión (de inmediato); si la conexión
  muere (revocada, reuso, vencida) o la persona cambia su sello; con `aislado_descartar`; y una base huérfana (sin fila) o una
  preparación que quedó a medias se barre. Una llamada en curso renueva su *lease* y el barrido no le borra la copia.
- CA4: tope de copias vivas por persona (`Mcp:SandboxesPerUser`) y en el entorno (`Mcp:MaxSandboxes`), con un error que dice
  qué hacer.

**H3 — Escribir por MCP sin duplicar.** Como *persona que usa un cliente en code mode*, quiero reintentar una escritura con la
misma clave sin repetir su efecto, para que un corte de red no me deje registros duplicados.
- CA1: cada herramienta MCP que escribe y no es idempotente (`aislado_aplicar_plan`, `aislado_crear`, `aislado_actualizar`,
  `aislado_transicion`, `aislado_procesar_asincronas`, `entregar`) exige `idempotencyKey` (8 a 100 caracteres
  `[A-Za-z0-9._:-]`) en su `inputSchema` y la anota con `readOnlyHint: false` e `idempotentHint: false`; sin ella, error de la
  herramienta.
- CA2: la misma clave con la misma entrada en la misma conexión devuelve el mismo resultado sin volver a ejecutar; con otra
  entrada, error 409; mientras la primera corre, error 409 con `retryAfterSeconds`. Diez llamadas a la vez con la misma clave
  producen un solo efecto. La clave es de la conexión (otra conexión la usa sin chocar) y vive `Mcp:IdempotencyHours`.
- CA3: un error que no ejecutó nada (tope, copia ocupada o no disponible) no consume la clave; un error de la ruta
  (validación, permiso) sí, y se repite igual.

**H4 — Entregar una propuesta por MCP.** Como *persona modeladora*, quiero que mi Claude me entregue su Plan de Cambios como
propuesta, para revisarlo y aplicarlo yo.
- CA1: con `proponer`, `entregar` (resumen, plan opcional, pruebas, fallas, riesgos) compila el plan contra el modelo real: si
  no compila, error de la herramienta con los errores y nada se guarda; si compila (o no trae plan), crea una conversación de
  Studio de la persona con la propuesta y responde `{ proposalId, status, url }`.
- CA2: `entregar` es exactamente una vez por clave: el efecto y la marca de la clave van en la misma transacción (diez
  llamadas a la vez con la misma clave dejan una propuesta).
- CA3: tope de `Mcp:ProposalsPerDay` propuestas por persona en 24 horas; no cuentan en los topes de Studio (que son de costo
  del modelo del ERP). La bitácora registra `mcp.proposal`.

**H5 — Revisar en Studio.** Como *persona*, quiero ver la propuesta de mi Claude en Studio, con su resumen, sus pruebas y su
vista previa, y aplicarla o descartarla, para decidir con la misma revisión que una de Studio.
- CA1: la propuesta aparece en las conversaciones de Studio marcada «MCP» con el nombre del cliente; al abrirla se ve de dónde
  llegó, el resumen, el informe y la tarjeta de propuesta (vista previa, avisos, aplicar con huella, descartar).
- CA2: aplicar exige la huella de lo revisado y la compuerta de cambio directo, como cualquier propuesta; otra persona no la ve
  (404).
- CA3: no se le pueden mandar mensajes (no hay agente de Studio detrás): el compositor dice que se sigue en el cliente, y la
  API lo rechaza con el mismo motivo.
- CA4: sin clave de Anthropic, Studio sigue mostrando las conversaciones y las propuestas (solo la bienvenida dice que el
  agente no está configurado).

**H6 — Ver y descartar la copia de una conexión.** Como *persona*, quiero ver en **Conexiones de Claude** si una conexión
tiene una copia aislada viva y descartarla, para liberar el servidor cuando termine.
- CA1: cada conexión muestra sus alcances (también `aislado` y `proponer`) y, si tiene copia, desde cuándo y su último uso.
- CA2: «Descartar copia» (con confirmación) la borra; la conexión sigue viva y la siguiente herramienta aislada prepara otra.
  Una persona no descarta la copia de otra (404).

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (6: no) · más de ~2 500 líneas de producto
   estimadas (~1 600: no) · seguridad o concurrencia junto con UI nueva (**sí**: casillas en Conectar, estado en Conexiones,
   propuesta en Studio) · más de una pantalla principal (no: ninguna pantalla nueva; se tocan tres existentes). → **Un
   criterio: no se divide.** Lo que la habría hecho crecer (escrituras en el entorno real y la bitácora de lectura) queda
   fuera (decisiones 6 y 7).
2. **Ciclo de vida de la copia por conexión** (`meta.McpSandbox`, una fila por conexión): se crea con la primera herramienta
   aislada bajo un bloqueo por conexión (`sp_getapplock`); si otra llamada la está preparando, la que espera recibe un error
   con `retryAfterSeconds` (no se encolan peticiones HTTP de minutos). El nombre de la base usa el id de la fila
   (`<prefijo>_SM<id>_SBX`): una copia nueva nunca reusa el nombre de una que se está borrando (la carrera de la 036 F-03 no
   existe aquí). Cada llamada toma un *lease* (`Mcp:SandboxLeaseSeconds`, renovado cada tercio) y actualiza el último uso. Un
   trabajo en segundo plano (`McpSandboxSweeper`, cada `Mcp:SweepSeconds`) borra las inactivas, las de conexiones muertas o
   con el sello cambiado, las preparaciones vencidas y las bases huérfanas; desconectar desde la pantalla la borra en el acto.
3. **Dos alcances nuevos, solo para modeladores** (`aislado`, `proponer`), además de los de la 038; las herramientas exigen
   *modelador* como en Studio (034: la copia corre como su administrador). `proponer` es aparte de `modelo` para que una
   conexión vieja no gane la capacidad de dejar propuestas sin un consentimiento nuevo.
4. **`entregar` crea una conversación de Studio con origen MCP** (columnas `Origin`, `McpGrantId`, `McpClient` en
   `meta.StudioSession`): aplicar, descartar, huella, compuerta de cambio directo y bitácora son los de Studio, sin una vía
   nueva al modelo. No se le mandan mensajes: no hay agente de Studio detrás y la conversación sigue en el cliente.
5. **Idempotencia por conexión** (`meta.McpWrite`, única por conexión y clave, con la huella SHA-256 de la herramienta y la
   entrada canónica). En `entregar` la marca y el efecto van en una transacción (exactamente una vez). En la copia aislada el
   efecto es en otra base: la clave se marca «en curso» con un *lease* (`Mcp:WriteLeaseSeconds`) y un intento propio, se
   ejecuta y se guarda el resultado; si el proceso cae entre el efecto y la marca, al vencer el *lease* la clave se puede
   reintentar y el efecto puede repetirse **una vez en la copia** (sin datos reales; R-044-1). Al borrarse la copia se borran
   sus claves.
6. **Escrituras en el entorno real por MCP: fuera.** La constitución II.8 dice que la IA solo propone; escribir registros reales
   con los permisos de la persona desde un cliente externo exige un ADR y una spec propia (roadmap).
7. **Bitácora del primer `leer_registros` por conexión y catálogo: fuera.** El dueño no la pidió (R-040-3 sigue aceptado).
8. **Topes** (`Mcp:*`): `SandboxesPerUser` 2, `MaxSandboxes` 10, `SandboxIdleMinutes` 30, `SandboxLeaseSeconds` 120,
   `ProposalsPerDay` 20, `IdempotencyHours` 24, `WriteLeaseSeconds` 300, `SweepSeconds` 30. Las copias de Studio cuentan aparte
   (R-044-2).

## Fuera de alcance (anotado en el roadmap)
- Escrituras en el entorno real por MCP (requiere ADR por la constitución II.8).
- Bitácora del primer `leer_registros` por conexión y catálogo (R-040-3), si el dueño la pide.
- Seguir por Studio (con su agente) una conversación que empezó por MCP; ver los pasos que el cliente hizo en la copia.
- Un tope común de copias aisladas entre Studio y MCP (R-044-2).
