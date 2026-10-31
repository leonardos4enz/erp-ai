# Spec 040 — Endurecimiento 6: el registro del MCP no se agota, la cola de Studio no se atora y los reportes cuadran en todos los caminos

**Estado:** Implementado · **Depende de:** 018, 021, 029, 030, 033, 034, 035, 036, 038, 039 · **Origen:** la **auditoría externa 7**
(2026-10-31, solo lectura al cerrar la 039, commit `35c1554`, specs 034–039): 1 defecto medio (D-01: el registro dinámico
del MCP se agota y bloquea conexiones nuevas), 11 bajos (D-02 a D-12) y 3 observaciones (O-01 a O-03), sin fugas ni
escaladas. El dueño aprobó ponerla **antes de los reportes personales** (2026-10-31): los reportes y tableros personales
pasan a la 041, Ventas a la 042 y MCP 2 a la 043. D-01 no debía llegar a MCP 2, que agrega más uso del registro dinámico.

## Problema
Cada hallazgo se reconfirmó en el código de `99276fe` antes de escribir esta spec.

| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | D-01 (media) · `McpOAuthService.RegisterAsync`: el barrido borra solo clientes **sin ninguna** `McpGrant` y `MaxClients` cuenta **todos** los clientes; no hay `DELETE meta.McpGrant` en el código | (a) Sin cuenta: `PendingClientsPerAddress` (20) cuenta por la dirección completa; una IPv6 por equipo y 2^64 por /64: con 50 direcciones se llenan los 1 000 de `MaxClients` en un minuto y todos reciben 503 durante una hora. (b) Con cuenta: registrar, autorizar y canjear 1 000 veces llena `MaxClients` **para siempre** (una conexión revocada o vencida nunca se barre). En uso normal pasa lo mismo, despacio, con cada reconexión de Claude que se vuelve a registrar |
| 2 | D-02 · el límite `oauth`, el de inicio de sesión y `PendingClientsPerAddress` usan `Connection.RemoteIpAddress`; no hay `UseForwardedHeaders` (R-021-5, R-018-4) | Detrás de un proxy todas las peticiones tienen la misma dirección: 20 registros sin canjear o 30 renovaciones por minuto bloquean a todos. Las URL de OData y de los metadatos del MCP salen con el esquema del proxy (`http`) |
| 3 | D-03 · `StudioService.ExpireAsync` vence **todas** las sesiones con *lease* vencido en una transacción, y el despachador llama a `ClaimAsync` después, en el mismo `try` | Si la purga o la bitácora fallan con una sesión, se revierte el lote en cada vuelta: ninguna pasa a `interrupted` y no se reclama ningún turno nuevo para nadie |
| 4 | D-04 · `StudioOptions.From` acota `LeaseSeconds` (5–3600) y `RenewSeconds` (1–600) por separado | Con `LeaseSeconds=5` y `RenewSeconds=10`, cada turno pierde su *lease*, queda interrumpido y se cobra la reserva |
| 5 | D-05 · `StudioAgent.RunAsync`: al apagar, el turno sale sin cerrar ni purgar | Si el servidor no vuelve a arrancar, lo que leyeron las herramientas de `datos` queda en `StudioStep.OutputJson` y `StudioMessage`, contra la purga de R-033-4 |
| 6 | D-06 · `AnthropicStudioModel.NextAsync` solo vuelve a lanzar `SqlException` y `StudioLeaseLost`; lo demás es «falla del proveedor» (R-036-4) | Una falla del estado en vivo que no es `SqlException` (p. ej. `InvalidOperationException` con el *pool* agotado) pone la reserva en 0: el tope de costo queda por debajo de lo real |
| 7 | D-07 · `StudioService.SendAsync` copia la empresa actual a la conversación; ninguna lectura filtra por empresa | Con `datos`, el texto que el modelo escribió citando registros de la empresa A vuelve al proveedor en un turno de la empresa B (R-036-5) |
| 8 | D-08 · `StudioAgent.FinishAsync` conserva el resumen solo si el turno contesta | Detener un turno de seguimiento borra el resumen de la propuesta vigente (la propuesta y su informe sí se conservan) |
| 9 | D-09 · `ReportService.Query.Join` no tiene tope; el plan 039 (§5, Límites) dice «≤ 16» | 2 agrupaciones y 6 medidas a dos saltos suman hasta 18 uniones, cada una con su subconsulta de `RecordFilter`, multiplicadas por tarjeta en un tablero |
| 10 | D-10 · agrupar por una referencia cuyo campo para mostrar está oculto (`FieldAccess.DisplayHidden`) da `#id`; el listado da «(sin acceso)» | Incoherencia entre caminos (la lección «la regla de un camino vale en todos») |
| 11 | D-11 · filas, total y comparación de un reporte son 3 SELECT en un lote, en READ COMMITTED | Con escrituras simultáneas el total puede no ser la suma de las filas; la fila «Fallas parciales» del STRIDE 039 daba a entender una sola lectura |
| 12 | D-12 · el canario no tiene reportes a dos saltos | Una regresión de alcance en un reporte, su exportación o una tarjeta a dos saltos no la detectaría la guardia que recorre todos los caminos |
| 13 | O-01, O-02, O-03 | MCP como camino alterno a la exportación (sin la bitácora ni el tope `bulk`); retorno *loopback* marcado como conocido; el token de acceso anterior vale hasta que vence tras renovar |

## Principio
Ninguna capacidad de negocio nueva. Tres reglas del motor: **lo que se cuenta contra un tope es lo vivo** (un cliente MCP
sin conexión viva no ocupa lugar, y el reparto por dirección es por red, no por equipo); **un trabajo asíncrono aísla cada
unidad de trabajo** (una sesión que falla no detiene a las demás, y lo que se cierra se cierra también al apagar); y **la
regla de un camino vale en todos** (la etiqueta de un campo oculto, el tope de uniones prometido y el canario que recorre
reportes, exportación y tablero a dos saltos).

## Historias

**H1 — El registro del MCP no se agota (D-01, D-02, R-021-5, R-018-4).** Como *persona que conecta su Claude*, quiero poder
registrarlo siempre que no haya un abuso en curso desde mi red, para que nadie con o sin cuenta deje el servidor sin
conexiones nuevas.
- CA1: `Mcp:MaxClients` cuenta solo clientes **vivos**: con una conexión viva (no revocada y con *refresh* sin usar ni
  vencer) o pendientes (sin conexiones, registrados hace menos de una hora). 1 000 clientes con su conexión revocada no
  impiden registrar uno nuevo.
- CA2: el barrido borra los clientes sin conexión viva (los nunca autorizados tras una hora; los que tuvieron conexiones, un
  día después de la última actividad) con sus conexiones, tokens y códigos.
- CA3: una persona tiene a lo más `Mcp:MaxConnectionsPerUser` (20) conexiones con clientes distintos, vivas o desconectadas
  en las últimas 24 horas (revisión adversarial F-03): la pantalla Conectar lo avisa antes de consentir (409, «Llegaste al
  máximo…», con un enlace a Conexiones de Claude) y el canje lo vuelve a exigir con los canjes de la persona en fila (10 canjes
  a la vez con tope 2: 2). Volver a conectar el mismo cliente no cuenta (reemplaza la anterior).
- CA4: `PendingClientsPerAddress` y los límites por dirección (`oauth`, inicio de sesión y el global de anónimos) agrupan
  una IPv6 por su /64 (`Security:Ipv6PrefixLength`, revisión adversarial F-05) y una IPv4 mapeada en IPv6 como IPv4.
- CA5: con `Security:ForwardedProxies` (IP o redes CIDR), la dirección y el esquema de una petición que llega de un proxy
  conocido salen de `X-Forwarded-For` y `X-Forwarded-Proto` (un salto por omisión, `Security:ForwardedLimit`; revisión
  adversarial F-06); los de un proxy no listado se ignoran. Sin la opción, nada cambia.

**H2 — La cola de Studio no se atora (D-03, D-04, D-05, D-06).** Como *dueño*, quiero que una sesión que falla al vencer, una
configuración incoherente, un apagado o una falla de la base no dejen sesiones atoradas, datos sin purgar ni costos sin
cobrar.
- CA1: cada sesión vencida se cierra en su propia transacción; si una falla, las demás pasan a `interrupted` y el despachador
  sigue reclamando turnos. Una salida de herramienta que no se puede leer se purga igual (se reemplaza por un aviso).
- CA2: `RenewSeconds` nunca pasa de un tercio de `LeaseSeconds`.
- CA3: al apagar el servidor, el turno en curso se cierra como `interrupted` y purga lo leído con `datos`, como al vencer.
- CA4: una falla que viene del ERP mientras llega la respuesta del proveedor (estado en vivo, base) no se trata como falla
  del proveedor: la llamada se cobra con su reserva.

**H3 — Una conversación de Studio con datos no cambia de empresa, y detenerla no borra la propuesta (D-07, D-08).** Como
*persona que usa Studio con `datos`*, quiero que los registros de una empresa no viajen en un turno de otra, y que detener un
turno no me quite el resumen de la propuesta que tengo.
- CA1: con el alcance `datos`, mandar un mensaje desde otra empresa da 409 con el motivo (vuelve a esa empresa o empieza una
  conversación nueva). Sin `datos` (solo metadatos), la conversación sigue y toma la empresa actual, como hoy.
- CA2: detener (o un turno que falla sin mensaje propio) con una propuesta vigente conserva su resumen.

**H4 — Los reportes cuadran en todos los caminos (D-09, D-10, D-12).** Como *auditor*, quiero que el tope de uniones
prometido exista, que un campo oculto se vea igual en un reporte que en el listado y que el canario recorra los reportes a
dos saltos.
- CA1: un reporte con más de 16 uniones distintas (`InterfaceRules.MaxJoins`) no compila, con un mensaje que dice cuántas
  lleva; contar reutiliza la misma unión como el motor (`Ref` y `Ref.Campo` comparten la de `Ref`).
- CA2: agrupar por una referencia cuyo campo para mostrar está oculto para quien ve da una sola fila «(sin acceso)», como
  cuando el registro referido no se ve (039 F-B).
- CA3: el canario agrega reportes que agrupan por `Puente.Secreto.Dato` y `Puente2.Puente.Secreto` y miden
  `Puente.Secreto.Dato`, una tarjeta del tablero con uno de ellos, y los recorre en `/api/reports/*`, su exportación y
  `/api/dashboards/*`: el canario no aparece para el lector y sí para el administrador.

**H5 — Decisiones en riesgos.md (D-11, O-01–O-03).** Como *auditor*, quiero que lo que no se corrige quede decidido, con su
alcance, y que los riesgos que esta spec cierra o amplía digan cómo quedaron.
- CA1: D-11, O-01 y O-03 quedan como riesgos de la 040 con su decisión; R-038-1 menciona el retorno *loopback* (O-02);
  R-038-5, R-036-4 y R-036-5 dicen lo que la 040 corrigió; R-021-5 y R-018-4 se cierran con su prueba.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (**5**, no) · más de ~2 500 líneas de
   producto (~350 de producto y ~600 de pruebas, no) · seguridad o concurrencia junto con UI nueva (**no hay UI nueva**) ·
   más de una pantalla principal (ninguna). → **No se divide.**
2. **Qué es un cliente vivo (D-01).** Uno con una conexión viva (sin revocar y con un *refresh* sin usar ni vencer, el mismo
   criterio de «Conexiones de Claude») o uno pendiente (sin conexiones, registrado hace menos de una hora). Solo esos
   cuentan contra `MaxClients`. Un cliente que tuvo conexiones y ya no tiene ninguna viva se barre **un día** después de su
   última actividad (última autorización o última revocación), no al instante: así una persona que desconectó hoy y vuelve a
   conectar con el mismo cliente no se encuentra «cliente no registrado». El barrido sigue en el registro (dentro de su
   transacción serializable), como en la 038.
3. **Tope por persona (D-01 b).** `Mcp:MaxConnectionsPerUser`, 20 por omisión (1–1 000), cuenta conexiones vivas con
   **otros** clientes. Se rechaza en el canje (la regla que manda, bajo `UPDLOCK, HOLDLOCK` sobre las conexiones de la
   persona) con `invalid_grant` y en la pantalla antes de consentir (409, para que la persona sepa qué hacer). Se descartó
   revocar la más vieja: una conexión que la persona usa desaparecería sin aviso.
4. **/64 en IPv6 (D-01 a).** La clave de partición de una IPv6 es su prefijo /64 (`2001:db8:1:2::/64`), la de una IPv4 (o
   IPv4 mapeada) la dirección. Se aplica a todo límite por dirección (`login`, `oauth`, el global y las políticas por persona
   cuando no hay persona) y a `PendingClientsPerAddress` (`McpClient.CreatedIp` guarda la clave). La bitácora sigue guardando
   la dirección completa. Quien controla muchas /64 (un /48) todavía puede llenar los pendientes durante una hora: el
   registro anónimo es así por diseño (RFC 7591) y queda como riesgo (R-040-1).
5. **`ForwardedHeaders` (D-02, R-021-5, R-018-4): entra.** Lista explícita `Security:ForwardedProxies` (IP o CIDR); solo
   `X-Forwarded-For` y `X-Forwarded-Proto`. `X-Forwarded-Host` no: el `Host` lo conserva un proxy bien configurado y aceptarlo
   pediría también lista de hosts; para el emisor del MCP sigue mandando `Mcp:PublicUrl`. Sin la opción, el comportamiento es
   el de hoy (un despliegue sin proxy no confía en ninguna cabecera).
6. **Vencer sesión por sesión (D-03).** Cada sesión vencida en su transacción con su `try/catch`; `ClaimAsync` corre aunque
   `ExpireAsync` falle. Una salida de herramienta cuyo JSON no se puede leer se reemplaza por un aviso al purgar (el historial
   conserva su lugar; el siguiente turno puede fallar con el proveedor, pero ningún dato queda). Una sesión cuya purga falla
   por otra causa (la base) se reintenta en la vuelta siguiente sin detener a las demás.
7. **Apagado (D-05).** Se cierra el turno como `interrupted` con un plazo corto (5 s); la copia aislada se conserva como en
   cualquier fin de turno (la suelta el barrido por inactividad). Se descartó vencer las `running` al arrancar: si el
   servidor no vuelve, no ayuda; si vuelve, el vencimiento por *lease* ya lo hace.
8. **Origen de la falla (D-06).** Se marca desde el *callback*: lo que lanza el *callback* del texto en vivo (código del ERP)
   sale tal cual y se cobra la reserva; solo lo que lanza el SDK del proveedor es «falla del proveedor». Se descartó ampliar
   la lista de tipos (`DbException`, `InvalidOperationException`): el SDK también puede lanzar `InvalidOperationException`.
9. **Empresa de la conversación (D-07).** Con `datos`, 409 si la empresa actual no es la de la conversación; sin `datos`, sigue
   como hoy (solo metadatos y conteos, que no dependen de la empresa más que por los catálogos que se ven). Se descartó
   empezar una conversación nueva en automático: la persona no lo pidió.
10. **«(sin acceso)» (D-10).** Una sola fila, como la 039 F-B, y no una por id con la misma etiqueta (dos categorías iguales
    en una gráfica confunden y el conteo por id oculto es justo lo que F-B quitó).
11. **`MaxJoins` (D-09).** 16, el número del plan 039. Se cuentan rutas distintas: cada salto (`Ref`, `Ref.Ref2`), la unión al
    catálogo de una agrupación por referencia y la de una por usuario. Lo valida `InterfaceRules.ValidateReports` (paquetes,
    reversión, Studio y planes usan el mismo `PlanCompiler`).
12. **D-11: se acepta como riesgo** (R-040-2) en lugar de `SNAPSHOT`: exigiría `ALLOW_SNAPSHOT_ISOLATION` en la base del
    cliente y cada SELECT ya lleva el mismo alcance (no es de seguridad). La fila del STRIDE 039 se corrige con una nota.
13. **O-01: se acepta como riesgo** (R-040-3): son los registros que la persona ve, la API con la cookie ya paginaba, cada
    llamada pasa por la cubeta MCP, el global por persona y el despachador. La bitácora por conexión y catálogo queda para la
    043 (MCP 2), que ya toca las herramientas. **O-03** se registra como aceptado (R-040-4: es OAuth; revocar los mata todos).
14. **R-016-3 y R-029-4** (tareas que se saltan o reprocesan registros, tres auditorías marcados): no son de esta spec; se le
    plantean al dueño al cerrar, como pidió la auditoría.

## Fuera de alcance (anotado en el roadmap y en riesgos)
- Bitácora del primer `leer_registros` por conexión y catálogo (O-01): 043.
- Vista del superusuario de todas las conexiones MCP (R-038-4): sigue igual.
- Contadores compartidos entre instancias (R-034-2, R-036-1): siguen igual.
