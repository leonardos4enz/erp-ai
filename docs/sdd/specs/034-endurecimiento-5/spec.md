# Spec 034 — Endurecimiento 5: nadie actúa sobre quien tiene más, y el catálogo de Studio con los permisos exactos

**Estado:** Implementado · **Depende de:** 009, 015, 018, 021, 029, 030, 033 · **Origen:** la **auditoría externa 6**
(2026-10-27, solo lectura al cerrar la 033, commit `ad0534b`): una escalada de privilegios alta y no relacionada con Studio
(A-01), huecos del catálogo de herramientas que deben cerrarse antes del servidor MCP (A-02 a A-04, R-033-5), una fuga menor
en la bitácora (A-07, R-032-6), dos fallas de contabilidad de Studio (A-05, A-06) y riesgos marcados para auditoría sin
prueba. El dueño quiere que el MCP (ahora 038) opere con los permisos actuales de cada persona y nunca más. Va antes de la
pantalla de Studio (ahora 035). Suma la estabilidad de CI que la 033 dejó pendiente (R-030-5).

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | A-01 (alta) · `UpdateUserCore` (`AdminEndpoints.cs`) solo revisa privilegios cuando cambian los roles: `RequireGrantableRolesAsync` sale si `changed.Length == 0` | Quien tiene la capacidad «Usuarios» sin ser superusuario hace `PUT /api/admin/users/{id}` sobre el admin con los mismos `roleIds` y un `password` nuevo: 200, y entra como él. Igual desactiva a otro administrador o le cambia unidad o empresas. Viola la lección 021-1. La aprobación de cambios de seguridad (015) reaplica la misma función con quien aprueba: el mismo hueco |
| 2 | R-009-1 (alta) · `EnvironmentOptions.Create` acepta `RequireApproval` sin `SigningKey` | Un entorno que exige aprobación arranca sin clave y acepta paquetes sin firmar (`PackageSigner.Verify` solo corre con clave) |
| 3 | A-02 · `StudioToolRunner.SandboxAsync`, `SandboxApplyAsync`, `SandboxDrainAsync` (y las de actualizar y transición) corren como el admin de la copia (`sb.Admin`) | No comprueban la capacidad de `c.User`: hoy lo protege que el bucle exige *modelador* al recargar la identidad; un MCP que reutilice el runner con otra identidad tendría la copia con privilegios de administrador |
| 4 | A-03 · `RouteDispatcher.SendAsync` arma la petición con el esquema `erp.studio` y sin alcances | Un token OAuth `data.read` o una clave de API que llegue por el MCP pasaría a rutas que `UseBearerScope` le niega por HTTP |
| 5 | A-04 · `RouteDispatcher.Writes()` decide por el verbo | `GET /api/data/{e}/{id}/pdf` y `GET /api/data/{e}/export` escriben en la bitácora (`data.print`, `data.export`) y pasan como lectura en el entorno real |
| 6 | R-033-5 · las rutas despachadas no pasan por los limitadores de ASP.NET | Un cliente MCP podría llamar rutas costosas (exportar, imprimir, buscar) sin el tope por usuario |
| 7 | A-07 / R-032-6 · `AuditFilter.Build` deja ver todo lo que no esté en `RecordActions` ni `EntityActions`; `data.print`, `automation.schedule` y `automation.retry` no están | Cualquier auditor ve esos eventos (catálogo, registro, nombre de la tarea) aunque no lea el catálogo ni el registro. `jerarquia.spec.ts › H5 CA1` compara los `recordId` de la bitácora (que en `model.change` son ids de ChangeSet) con ids de registro: choca cuando corre después de `h5-h6-seguridad` |
| 8 | A-05 · `FinishAsync` y `ExpireAsync` (Studio) ponen `ReservedMicros = 0` | Al cancelar, vencer o interrumpir una sesión a mitad de una llamada al modelo, se pierde su costo (el proveedor sí lo cobra) y el tope del día queda por debajo de lo gastado |
| 9 | A-06 · `SandboxAsync = run.Sandbox ??= await CreateSandboxAsync(...)` | Si crear la copia falla, cada herramienta aislada vuelve a intentarlo (base nueva, modelo copiado) |
| 10 | Riesgos | R-015-3, R-021-4 y R-011-2 tienen prueba y siguen abiertos; R-011-1 no dice que Studio con `datos` envía valores; R-016-3 y R-008-4 sin prueba |
| 11 | CI (R-030-5) | Las pruebas intermitentes de la 033 eran carreras de la prueba (leer el último elemento de un receptor, `getByText().last()`, escribir antes de que llegue la respuesta). En CI no se distingue una falla reproducible de una intermitente |

## Principio
Ninguna capacidad de negocio nueva. Tres reglas del motor, aplicadas en todos los caminos: **nadie actúa sobre quien tiene
más que él** (contraseña, activación, unidad y empresas: la regla de 021-1 de los roles, ahora sobre la persona); **una
herramienta corre con exactamente la identidad, la credencial y los límites de quien la pide** (capacidad publicada,
alcances de la credencial, efecto declarado, frecuencia); y **la bitácora deniega por omisión** lo que no esté clasificado.

## Historias

**H1 — Nadie actúa sobre quien tiene más (A-01).** Como *dueño*, quiero que quien administra usuarios no pueda tomar la
cuenta del superusuario ni de nadie con más capacidades, para que «Usuarios» no sea una puerta a «Superusuario».
- CA1: cambiar la contraseña, desactivar, reactivar, cambiar la unidad o las empresas de un usuario con alguna capacidad
  administrativa que el actor no tiene da **403** con la razón (sin cambiar nada ni crear solicitud). Con superusuario,
  solo actúa otro superusuario. Revisión adversarial F-01: cambiarle los roles también (aunque el rol no dé capacidades,
  renueva su sello y cierra sus sesiones). Prueba: `Un_gestor_de_usuarios_no_cambia_la_contrasena_ni_desactiva_a_quien_tiene_mas_capacidades`.
- CA2: la aprobación (015) aplica la misma regla con quien aprueba; quien solicita ya la pasó al pedir.
- CA3: quien tiene las mismas capacidades (o más) sigue pudiendo; cambiar solo el nombre visible no cuenta.

**H2 — Un entorno con aprobación exige su clave de firma (R-009-1).** Como *dueño*, quiero que un entorno que exige
aprobación no arranque sin clave, para que no acepte paquetes sin firmar.
- CA1: `Environments:RequireApproval=true` sin `Environments:SigningKey` detiene el arranque con un mensaje que dice qué
  falta y por qué.

**H3 — El catálogo de Studio con los permisos exactos (A-02, A-03, A-04, R-033-5).** Como *dueño*, quiero que una
herramienta haga exactamente lo que la persona y su credencial pueden por la API, para que el MCP no amplíe nada.
- CA1: el runner exige la capacidad publicada (`GET /api/studio/tools › capability`) para cada destino: sin *modelador*, una
  herramienta de la copia aislada da `denied` aunque la sesión tenga el alcance `aislado`.
- CA2: el despachador recibe la credencial (esquema y alcances OAuth, o clave de API) y le aplica la misma función que
  `UseBearerScope`: un token `data.read` hacia una ruta que exige otro alcance da 403 con el mismo mensaje.
- CA3: un GET que escribe en la bitácora lo declara (efecto «bitácora») y `Writes` lo respeta: en el entorno real no se
  despacha. Una prueba de inventario recorre todas las GET y falla si una deja rastro sin declararlo (o al revés).
- CA4: las rutas despachadas pasan por los mismos topes por persona que por HTTP (global y la política de la ruta); al
  rebasarlos, 429.

**H4 — La bitácora deniega lo que no está clasificado (A-07, R-032-6).** Como *auditor con alcance limitado*, quiero no ver
eventos de registros o catálogos fuera de mi alcance, para que la bitácora no sea otro camino al dato.
- CA1: `data.print` se filtra por registro; `automation.schedule` y `automation.retry`, por catálogo.
- CA2: toda acción de auditoría está clasificada (de registro, de referencia a un registro, de catálogo o fuera de los
  datos); una acción sin clasificar no se le muestra a quien no es superusuario, y una guardia falla si alguna falta.
- CA3: `jerarquia.spec.ts › H5 CA1` compara solo eventos de datos.

**H5 — Studio cobra lo reservado y recuerda la falla de la copia (A-05, A-06).** Como *dueño*, quiero que el tope de costo
cuente lo que de verdad se gastó, y que una copia que no se pudo crear no se intente en cada herramienta.
- CA1: al cancelar, vencer o interrumpir, lo reservado de la llamada en curso se suma al costo.
- CA2: si preparar la copia falla, las siguientes herramientas aisladas de la sesión responden esa falla sin volver a intentar.

**H6 — Riesgos con su prueba.** Como *auditor*, quiero que un riesgo con prueba se cierre y uno mal descrito se corrija.
- CA1: R-015-3, R-021-4 y R-011-2 se cierran con su prueba existente; R-011-1 se reescribe para Studio (con `datos`, se
  envían valores a Anthropic); R-016-3 y R-008-4 reciben prueba si cabe.

**H7 — CI que distingue una falla real de una carrera de la prueba (R-030-5).** Como *quien cierra una spec*, quiero que las
pruebas nuevas se ejecuten con latencia y que CI diga si una falla se repite.
- CA1: `--lento` agrega latencia aleatoria a `/api/**` en cada página; con `--veces` > 1 es obligatorio.
- CA2: `helpers.ts` ofrece `expectToast(page, texto, acción)`, `tras(respuesta, acción)` y `receptor()` con
  `marca()`/`siguiente()` sobre `expect.poll`.
- CA3: una guardia (Vitest) prohíbe `.at(-1)` sobre receptores y `getByText(...).last()` sin una razón escrita.
- CA4: en CI, si la suite falla, se repite solo el archivo que falló (con los que declara que necesita) sobre una base
  nueva; si pasa, la anotación dice «intermitente» para R-030-5. El trabajo sigue en rojo (es detección, no reintento).

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (**7**, sí) · más de ~2 500 líneas de
   producto (~450 de producto y ~350 de infraestructura de pruebas, no) · seguridad o concurrencia junto con UI nueva (no
   hay UI) · más de una pantalla principal (ninguna). → **Un criterio: no se divide.** La estabilidad de CI entra aquí: son
   herramientas de la suite, no de producto, y no mueven la numeración.
2. **Qué cuenta como «actuar sobre» (A-01).** Contraseña, activación (desactivar o reactivar), unidad y empresas: lo que
   permite entrar como la persona, negarle el acceso o cambiar qué registros ve. El nombre visible no (no da ni quita
   acceso). Los roles los cubría 021-1 rol por rol; la revisión adversarial (F-01) mostró que un rol sin capacidades
   también actúa (renueva el sello), así que cambiar roles cuenta. Las capacidades del destino son las de sus roles
   actuales (los roles administrativos no se heredan, 015).
3. **Caminos que llegan a un usuario.** API (`PUT /api/admin/users/{id}`) y aprobación (015: reaplica `UpdateUserCore` con
   quien aprueba). No hay importación de usuarios; los paquetes (009) y los módulos no traen usuarios ni contraseñas; los
   equipos (015) no tienen roles ni sesión y sus miembros no ganan capacidades; claves de API y clientes OAuth son solo del
   superusuario. Se documenta en el STRIDE.
4. **Efecto de un GET.** Nuevo efecto **«bitácora»** (`Effect.AuditTrail`): no cambia datos, pero deja rastro. Es el único
   efecto que puede declarar un GET. `Writes` = efecto declarado distinto de «ninguno», o sin declarar y no GET.
5. **Límites despachados.** Los mismos topes por persona (`Security:RequestsPerMinute` y la política de la ruta:
   `print`, `search`, `bulk`, `aggregate`), con contadores propios del despachador (no comparte la ventana con el navegador:
   en el peor caso la persona usa el doble, R-034-2). La partición es la persona, también en la copia aislada (no el
   administrador de la copia).
6. **Bitácora por omisión.** Se descartó solo agregar las tres acciones: una cuarta volvería a escapar. El filtro muestra a
   quien no es superusuario solo las acciones clasificadas; `data.print` es de **referencia** (filtra por registro, pero su
   `after` no son valores del registro y no se recorta).
7. **CI: reintentar como detección.** Se descartó reintentar para ponerlo en verde: una intermitente que pasa al segundo
   intento seguiría escondida. El reintento solo clasifica la falla en la anotación.

## Fuera de alcance (anotado en el roadmap y en riesgos)
- HMAC con clave compartida entre los entornos de la cadena (la otra mitad de R-009-1): R-034-1.
- Contadores compartidos entre navegador y despachador: R-034-2.
- La caducidad de los pasos de Studio (R-033-4), `after` y el informe del agente (R-033-10, R-033-11): pantalla, 035.
