# Spec 016 — Automatizaciones asíncronas y programadas

**Estado:** Implementado · **Depende de:** 005, 008, 010, 015 · **Origen:** [roadmap](../../roadmap.md#016--automatizaciones-asíncronas-y-programadas)

## Problema
1. Las automatizaciones de la 005 corren dentro de la transacción del evento. Lo que no tiene por qué detener a quien
   guarda (recalcular un acumulado pesado, avisar a otro catálogo, crear registros de seguimiento) hace más lenta la
   operación y, si falla, revierte el cambio de la persona.
2. Nada ocurre si nadie guarda: no hay vencimientos («las órdenes enviadas cuya entrega ya pasó»), recordatorios ni
   cierres diarios. Hoy alguien tiene que acordarse.
3. Una tarea que corre sola no tiene a una persona detrás: necesita una identidad con permisos acotados (la 015 dejó
   listo el rol de servicio con herencia; un equipo no tiene roles y no sirve como identidad).
4. Sin bitácora ni reintentos, una falla en segundo plano es invisible; sin límite de lotes, una consulta mal hecha
   procesa la base completa.

## Historias

**H1 — Acciones asíncronas.** Como *modelador*, quiero marcar una automatización como asíncrona para que se ejecute
después, sin hacer esperar ni fallar a quien guarda.
- CA1: Una automatización (005) declara `mode: "async"`. Su condición `when` se evalúa en el evento; si se cumple, el
  evento deja un **trabajo en la bandeja** dentro de su misma transacción: si el cambio se revierte, no queda trabajo.
- CA2: Un servicio en segundo plano ejecuta el trabajo en su propia transacción, con los datos **actuales** del registro
  y a nombre de **quien disparó el evento** (con sus roles y su empresa de ese momento). Las acciones hacen lo mismo que
  en la 005: todo o nada por trabajo, fórmulas, reglas, historial «Automática: …», auditoría y webhooks.
- CA3: Si el trabajo falla, quien guardó no se entera ni pierde su cambio: el trabajo queda en la bitácora. Los errores
  **transitorios** (bloqueo u otra operación sobre el mismo registro, tiempo agotado) se reintentan con espera
  creciente; los del **negocio** (una regla, una condición de transición) no, porque repetirlos da lo mismo.
- CA4: Si el registro ya se eliminó, la automatización ya no existe o quien la disparó ya no está activo, el trabajo se
  cancela con el motivo (no se ejecuta con una identidad que ya no debería actuar).
- CA5: Un trabajo asíncrono que provoca otro asíncrono cuenta una generación; más de **5** generaciones fallan con un
  mensaje que pide revisar el ciclo (además de la validación de ciclos de la 005, que ya incluye a las asíncronas).

**H2 — Tareas programadas.** Como *modelador*, quiero declarar «cada día a las 7:00, a los registros que cumplan X, hazles
Y», y como *administrador*, decidir si corre en este entorno.
- CA1: Una tarea se declara como metadato del catálogo (`setSchedules`): nombre, etiqueta, calendario (cada N minutos,
  cada hora en un minuto, diaria a una hora, semanal en ciertos días o mensual en un día), **consulta** `where` (lenguaje
  de la 002, traducible a SQL), **rol de servicio**, acciones (las de la 005, también por partida; sin `target`, sobre el
  propio registro) y tamaño de lote. La
  vista previa del plan la valida (calendario, condición traducible, rol existente, acciones). Que el rol pueda leer el
  catálogo es dato del entorno: se comprueba al ejecutar (H3 CA3).
- CA2: Una tarea nueva llega **pausada**. Un superusuario la activa o pausa por entorno, y puede **ejecutarla ahora**.
  La hora es la local del servidor (la misma de `TODAY()`).
- CA3: En cada ocurrencia se seleccionan los registros vigentes que cumplen `where` y están **dentro del alcance de
  lectura del rol** (006, y por empresa en un catálogo por empresa, 010). Cada registro se procesa en su propia
  transacción, volviendo a comprobar la condición con el registro bloqueado: una falla no revierte a los demás y dos
  ejecuciones a la vez no procesan dos veces el mismo registro.
- CA4: **Límite de lote**: máximo `batchSize` registros por ocurrencia (100 por defecto, máximo 1000), en orden de alta.
  Si quedaban más, la bitácora lo dice y siguen en la siguiente ocurrencia.
- CA5: Las ocurrencias perdidas (servidor detenido) **no se acumulan**: al volver corre una vez y agenda la siguiente.
  Dos instancias del servidor no corren la misma ocurrencia.
- CA6: `TODAY()` y `ADDDAYS(TODAY(), n)` se pueden usar en la consulta (vencimientos y recordatorios), también en las
  reglas de acceso por registro.

**H3 — Identidad de servicio.** Como *responsable de seguridad*, quiero que una tarea actúe con permisos acotados y que
se sepa qué tarea hizo cada cambio.
- CA1: La tarea consulta y actúa con su **rol de servicio** (con lo que hereda, 015), nunca como superusuario: `HASROLE()`
  y los permisos por registro se evalúan con ese rol. Aunque el rol tuviera capacidades administrativas, no aplican.
- CA2: Lo que crea o modifica queda a nombre de la cuenta de sistema **«Tareas programadas»** (no inicia sesión, no
  tiene roles, no aparece en *Usuarios* ni en buscadores), y la auditoría nombra la tarea (`tarea:Catálogo.Nombre`).
- CA3: Si el rol pierde el permiso de lectura del catálogo, la ocurrencia falla con ese motivo y no toca nada.

**H4 — Bitácora y reintentos.** Como *administrador*, quiero ver qué corrió, qué falló y por qué, y reintentarlo.
- CA1: *Configuración › Automatizaciones › Tareas programadas*: catálogo, calendario en palabras, rol, estado (activa o pausada), próxima y
  última ejecución con su resultado; *Activar*, *Pausar* y *Ejecutar ahora*.
- CA2: *Configuración › Automatizaciones › Bitácora*: cada trabajo asíncrono y cada ocurrencia con tipo, catálogo,
  nombre, registro, a nombre de quién, estado (pendiente, en curso, correcta, con errores, fallida, cancelada), intentos,
  registros procesados, correctos y fallidos, inicio y fin, y el error. El detalle muestra los errores por registro.
- CA3: Un trabajo asíncrono fallido o cancelado se puede **reintentar** (vuelve a empezar sus intentos).
- CA4: Activar, pausar, ejecutar y reintentar quedan en la auditoría (`automation.schedule`, `automation.retry`). La
  bitácora se purga a los 30 días. Solo el superusuario ve la opción y la ruta.

**H5 — Modelador, paquetes y asistente.**
- CA1: El modelador edita «Tareas programadas (JSON)» en la sección avanzada, validado en la vista previa, y
  `mode: "async"` en las automatizaciones.
- CA2: Las tareas viajan en los paquetes de entorno (009) como el resto del catálogo; si estaba activa o pausada, no
  (es decisión de cada entorno). El asistente IA conoce `mode` y `setSchedules`.

## Decisiones (CLARIFY)
Sesión sin interlocutor; se registran para revisión del dueño.
- **Asíncrona = misma automatización, otro momento.** `mode` es un atributo de la automatización de la 005, no una
  capacidad nueva: mismas acciones, mismas validaciones, mismo grafo de ciclos. Lo único que cambia es cuándo y en qué
  transacción corren las acciones.
- **Bandeja propia, igual que la de webhooks.** `meta.AutomationRun` se escribe en la transacción del evento y un
  servicio en segundo plano la reclama con *lease* (`READPAST, UPDLOCK`), como `meta.WebhookDelivery` (008). Entrega
  «al menos una vez»: si el proceso muere a mitad de un trabajo, su transacción se revierte y el trabajo se repite.
- **El trabajo asíncrono corre a nombre de quien disparó el evento**, recargado al ejecutarse (roles y estado actuales).
  Así la auditoría dice quién lo causó, `CURRENTUSER()` sigue significando lo mismo que en la versión síncrona, y a una
  persona dada de baja no se le siguen ejecutando acciones. Como en la 005, las acciones actúan con autoridad del motor
  (no se revisan permisos del destino), pero sí reglas y condiciones.
- **`when` se evalúa en el evento** (es la condición del disparo); las condiciones de cada acción, al ejecutar, con datos
  frescos. Una asíncrona no ve el «antes» del evento: lee el registro como está.
- **Reintentar solo lo transitorio**, como la 014 con los 4xx de webhooks: interbloqueo, bloqueo vencido, tiempo
  agotado y el 409 «otra operación está actualizando…». Hasta 5 intentos (inmediato, 30 s, 2 min, 10 min, 1 h).
- **Las tareas son metadato; si corren, del entorno.** El *qué* (calendario, consulta, rol, acciones) viaja en paquetes
  y se aprueba como cualquier cambio de modelo; *activarla* es del superusuario de cada entorno (como el destino de un
  webhook). Un paquete que trae una tarea no la echa a andar en producción: alguien la activa.
- **Identidad de una tarea:** rol de servicio + cuenta de sistema única «Tareas programadas» (`sistema.tareas`, cuenta de
  servicio sin roles). El rol da los permisos y el alcance; la cuenta da un dueño/creador real (llave a `meta.[User]`)
  sin inventar una cuenta por tarea, que sería dato del entorno ligado a metadatos. La auditoría lleva el nombre de la
  tarea. Un equipo no puede ser la identidad (no tiene roles).
- **La consulta es SQL o nada**: `where` usa el traductor de los acumulados (014: campos propios y de un nivel, `=`,
  `<>`, `IN`, comparaciones, `ISBLANK`, `TODAY()`, y ahora `ADDDAYS(TODAY(), n)`). Lo no traducible se rechaza en la
  vista previa: evaluar en memoria obligaría a leer la base completa.
- **Sin `target`, el propio registro.** Las acciones de la 005 siempre apuntaban a un registro relacionado; una tarea casi
  siempre cambia el registro que seleccionó («vencer el pedido»). `update` y `transition` sin `target` (ni `lines`) actúan
  sobre el registro del ámbito; vale también en las automatizaciones, y el grafo de ciclos lo cuenta como arista a sí mismo.
- **Idempotencia por la consulta.** La tarea no recuerda qué registros ya procesó: su `where` debe dejar de cumplirse
  cuando la acción termina (cambia el estado o un campo). Así un registro con error se vuelve a intentar en la siguiente
  ocurrencia, y no hay una segunda fuente de verdad.
- **Catálogo por empresa:** la ocurrencia recorre las empresas activas, cada una con su contexto (moneda base, folios,
  registros que crea). En un catálogo compartido, la empresa del contexto es la predeterminada.
- **Hora local del servidor**, la misma que usa `TODAY()`: una zona configurable aparte haría que «hoy» y «a las 7:00»
  no coincidieran.
- **Página propia, solo superusuario:** *Configuración › Automatizaciones* (en la primera versión eran pestañas de
  *Integraciones*; el dueño pidió separarlas: una tarea programada no integra nada con otro sistema). La bitácora muestra datos de cualquier
  catálogo sin filtrar por la 006.
- **Los módulos no cambian.** Compras e Inventario no necesitan hoy una tarea; la 019 (cierres) será el primer uso en un
  módulo. Las pruebas declaran sus propios catálogos.

## Fuera de alcance
Tareas sin registros (crear un registro «cierre del día» sin consulta), asignar a un equipo por nombre en una acción,
zonas horarias por tarea, expresiones cron, prioridades y colas separadas, cancelar un trabajo pendiente a mano, y que
una ocurrencia continúe sola con el siguiente lote antes de la próxima ocurrencia.
