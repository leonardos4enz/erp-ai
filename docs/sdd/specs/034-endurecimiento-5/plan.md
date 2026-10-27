# Plan 034 — Diseño técnico

## 1. Modelo de datos
Sin tablas, columnas ni operaciones nuevas del Plan de Cambios. Cambios de metadatos de las rutas:

| Metadato | Cambio |
|---|---|
| `Effect.AuditTrail` («bitácora») | Nuevo efecto: la ruta no cambia datos pero deja rastro. Lo declaran `GET /api/data/{entity}/{id:long}/pdf` y `GET /api/data/{entity}/export`; es el único efecto que admite un GET |
| `AuditActions` | Constantes para las acciones que se escribían como texto (`automation.update`, `automation.create`, `module.install`, `module.upgrade`) |
| `AuditFilter` | Cuatro listas: `RecordActions` (valores de un registro), `ReferenceActions` (`data.print`: un registro, sin valores), `EntityActions` (+ `automation.schedule`, `automation.retry`) y `OtherActions` (sesiones, modelo, seguridad, entornos, Studio…) |

## 2. Contrato de la API
**Ninguna ruta nueva** (siguen 149, [`rutas.md`](../../rutas.md); cambia la columna «Efecto» de dos GET). Cambios de
comportamiento:

| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `PUT /api/admin/users/{id}` | usuarios | igual | **403** «No puedes cambiar la contraseña, la activación, la unidad, las empresas ni los roles de «X»: tiene una capacidad que no tienes (…)» si el destino tiene alguna capacidad administrativa que el actor no tiene y cambia alguno de esos campos (los roles, desde la revisión adversarial F-01). Antes de la solicitud de aprobación |
| `POST /api/admin/security-changes/{id}/approve` (`user.update`) | usuarios o superusuario | `{ hash, comment }` | ídem con quien aprueba |
| `GET /api/data/{e}/{id}/pdf`, `GET /api/data/{e}/export` | registros | igual | sin cambio para el navegador; efecto «bitácora» en el inventario |
| `GET /api/audit` | auditoría | igual | quien no es superusuario solo ve acciones clasificadas; `data.print` por registro; `automation.schedule`/`retry` por catálogo |
| Arranque | — | `Environments:RequireApproval=true` sin `Environments:SigningKey` | no arranca: `InvalidOperationException` con el motivo |

Despachador (`RouteDispatcher.SendAsync`, interno, lo usan Studio y el MCP):

| Paso | Regla | Respuesta |
|---|---|---|
| 1 | Ruta del inventario | excepción si no existe (error del catálogo) |
| 2 | `Writes(endpoint, method)` y `allowWrites = false` | 403 «En el entorno real el agente solo lee…» |
| 3 | Capacidad de la ruta para la persona | 403 genérico |
| 4 | `BearerScope.Check(credential, ruta, método)` (la misma función que `UseBearerScope`) | 403 con el título de la API («El token no tiene el alcance data.write.») |
| 5 | Límite global por persona y el de la política de la ruta (`EnableRateLimiting`) | 429 «Demasiadas solicitudes…» |

Runner (`StudioToolRunner.RunAsync`): antes de correr, la capacidad publicada de la herramienta (`StudioTool.Capability`:
la de su ruta en el destino real, *modelador* en la copia y en el servidor) para `c.User`; sin ella, `denied`. Las
herramientas aisladas despachan como el administrador de la copia, con la credencial y el límite de `c.User`.

## 3. SPA
Sin cambios. La suite E2E cambia (H7): `run.mjs --lento`, primitivas en `helpers.ts`, guardia en Vitest,
`jerarquia.spec.ts › H5 CA1` y las capturas de receptores de `asistente.spec.ts`.

## 4. Pruebas
- **Integración nueva:** `Hardening034Tests` (A-01 con API y con aprobación, R-009-1 al arrancar, A-02, A-03, A-04 con el
  recorrido de las GET, R-033-5, A-07 con un auditor de alcance limitado). En `StudioTests`: A-05 (cancelar y vencer) y
  A-06 (copia que falla). Contra el código anterior: `npm --prefix web run it:regresion -- ad0534b "Hardening034Tests|A0"`.
- **Unitarias nuevas:** `Hardening5Tests` (clasificación de la bitácora por reflexión, ninguna escritura de bitácora con
  texto suelto, `Writes` con el efecto declarado, `BearerScope`), `EnvironmentTests` (sin clave no arranca);
  `RouteInventoryTests`: un GET solo declara «bitácora».
- **Vitest nueva:** `e2eGuardia.test.ts` (receptores y `.last()` sin razón).
- **E2E modificados** (con `--veces=3`, que ahora implica `--lento`): `jerarquia.spec.ts` (con `h5-h6-seguridad.spec.ts`
  antes, el orden en que fallaba), `asistente.spec.ts`.

## 5. STRIDE
**Las 8 filas son obligatorias.**

| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones o caminos agrega o cambia? ¿Qué capacidad pide cada una? | Ninguna ruta nueva. Cambian `PUT /api/admin/users/{id}` y la aprobación de `user.update` (403 nuevo), el efecto de dos GET, el filtro de `/api/audit` y el arranque. El despachador (camino de Studio y del MCP) agrega los pasos 4 y 5. | `Hardening034Tests › Un_gestor_de_usuarios_no_cambia_la_contrasena_ni_desactiva_a_quien_tiene_mas_capacidades`; `RouteInventoryTests › Lo_que_no_es_GET_declara_su_efecto` (un GET solo «bitácora»); `Hardening034Tests › H2_CA1_un_entorno_con_aprobacion_sin_clave_no_arranca` |
| Caminos alternos | ¿Todos los caminos al mismo dato o efecto aplican la misma regla? | A un usuario se llega por API y por la aprobación (reaplica `UpdateUserCore` con quien aprueba); no hay importación de usuarios; paquetes y módulos no traen usuarios; equipos sin roles ni sesión; claves y clientes OAuth solo del superusuario (decisión 3). A una ruta se llega por HTTP (middleware) o por el despachador (misma función `BearerScope.Check`, mismo límite, misma capacidad). A la bitácora, solo por `/api/audit` (la exportación de la bitácora no existe). | `Hardening034Tests › H1_CA2_la_aprobacion_aplica_la_misma_regla_con_quien_aprueba`, `› H3_CA2_el_despachador_aplica_los_alcances_de_la_credencial`, `› H3_CA4_las_rutas_despachadas_tienen_su_limite`; `Hardening5Tests › BearerScope_niega_lo_mismo_que_el_middleware` |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | La bitácora: `data.print` pasa por `RecordFilter.For` del catálogo (como `update`); `automation.*` por el permiso de lectura del catálogo; lo no clasificado no se muestra. El despachador no cambia: la ruta aplica `RecordFilter`/`FieldAccess`/empresa. | `Hardening034Tests › H4_CA1_la_bitacora_no_muestra_impresiones_ni_tareas_fuera_del_alcance`; `Hardening5Tests › Toda_accion_de_auditoria_esta_clasificada`; `jerarquia.spec.ts › H5 CA1` |
| Delegación | ¿Alguien puede otorgar, aprobar o heredar algo que no tiene? ¿El beneficiario puede aprobarse? | A-01: ni al solicitar ni al aprobar se actúa sobre quien tiene más (las capacidades del destino contra las del actor, con `AdminCapabilities.MissingFor`). A-02: la herramienta aislada no hereda las capacidades del administrador de la copia: exige la capacidad publicada de la persona. A-03: el MCP no hereda más que la credencial. | `Hardening034Tests › Un_gestor…`, `› H1_CA2…`, `› H3_CA1_sin_modelador_una_herramienta_aislada_se_niega`, `› H3_CA2…` |
| Concurrencia | ¿Qué invariante protege? ¿10 a la vez? ¿«revisar → aplicar» sin huella? | A-01 lee el destino dentro de la transacción de `UpdateUserCore` (la misma que ya bloquea por `approving.Verify`); un cambio de roles del destino entre la solicitud y la aprobación lo detecta la huella de la solicitud (`UserAccessSnapshot`, 015). Límites: el `FixedWindowRateLimiter` es seguro entre hilos. A-05: la suma de lo reservado va en el mismo `UPDATE` que cierra con el lease (`FinishAsync`) o que vence (`ExpireAsync`). | `Hardening034Tests › H3_CA4…` (10 llamadas a la vez con tope 3: exactamente 3 pasan); `StudioTests › A05_cancelar_cobra_lo_reservado_de_la_llamada_en_curso`, `› A05_una_sesion_vencida_cobra_lo_reservado` |
| Fallas parciales | ¿Qué pasa si el proceso cae entre dos pasos? | Vencer cobra lo reservado (un trabajador que murió a mitad de la llamada). Una copia que falla deja su nombre en la sesión (barrido de la 033) y la falla queda en la corrida: las demás herramientas la responden sin repetir. | `StudioTests › A05_una_sesion_vencida_cobra_lo_reservado`, `› A06_una_copia_aislada_que_falla_no_se_vuelve_a_crear` |
| Límites | Tamaño, tasa, memoria | Rutas despachadas: global `Security:RequestsPerMinute` (600) y la política de la ruta por persona; la partición es la persona también en la copia. Contadores aparte del navegador: [R-034-2](../../riesgos.md#034). | `Hardening034Tests › H3_CA4_las_rutas_despachadas_tienen_su_limite` |
| Semántica | Nulos, vacíos | Un usuario sin roles no tiene capacidades (cualquier gestor actúa sobre él). Un token sin alcances no llega a ninguna ruta que pida uno. Una acción de bitácora sin catálogo (`login`) es «fuera de los datos». | `Hardening034Tests › Un_gestor…` (control: sobre un usuario sin capacidades sí); `Hardening5Tests › BearerScope…` |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** | Entrar como el superusuario tras cambiarle la contraseña | A-01 | `Hardening034Tests › Un_gestor…` |
| **T** | Paquete sin firmar en un entorno con aprobación | Sin clave no arranca | `Hardening034Tests › H2_CA1…`; HMAC compartido: [R-034-1](../../riesgos.md#034) |
| **R** | Se pierde el costo de una llamada cancelada | Se cobra lo reservado | `StudioTests › A05_…` |
| **I** | La bitácora dice qué se imprimió o qué tarea corrió en un catálogo que no se lee | Denegar lo no clasificado | `Hardening034Tests › H4_CA1…`; `Hardening5Tests › Toda_accion…` |
| **D** | Desactivar a otro administrador; llamar rutas costosas sin tope por el MCP | A-01; límites despachados | `Hardening034Tests › Un_gestor…`, `› H3_CA4…` |
| **E** | El MCP con la copia como administrador o con más que la credencial | Capacidad publicada; `BearerScope` | `Hardening034Tests › H3_CA1…`, `› H3_CA2…` |
