# Spec 021 — Endurecimiento 2 (superusuario, cambios que se pisan, trabajos dobles, tareas, reversión, OData)

**Estado:** Implementado · **Depende de:** 001, 012, 015–020 · **Origen:** [roadmap](../../roadmap.md#021--endurecimiento-2)

## Problema
Una auditoría de solo lectura (2026-10-24) revisó las specs 014–020 contra el código. Ninguno de los hallazgos es una
capacidad nueva: son reglas que el motor ya promete (mínimo privilegio, Plan de Cambios con vista previa, bandeja con
*lease*, tareas en pausa hasta que el superusuario las active, reversión con la misma compuerta) y que no se cumplen en
todos los caminos.

1. **Escalamiento a superusuario (desde la 001).** Quien solo tiene *administrar usuarios* puede asignarse (o asignar a
   otro) un rol superusuario, o un rol con capacidades que él no tiene (modelo, auditoría). Con aprobación de seguridad
   (015), el aprobador de ese cambio tampoco necesita ser superusuario.
2. **Cambios de modelo que se pisan.** El diseñador (019) manda `setLayout`/`setWorkflow` completos. Si otra persona
   aplicó un cambio al mismo catálogo entre que se abrió el editor (o se revisó la vista previa) y se aplica, el cambio
   ajeno se pierde sin aviso.
3. **Trabajos asíncronos que se ejecutan dos veces.** La bandeja de la 016 confirma la transacción del trabajo y marca el
   éxito después, en otra conexión y sin comprobar que el *lease* siga siendo suyo; un *lease* vencido se retoma sin
   límite de intentos. Si el proceso cae entre los dos pasos, el trabajo se repite.
4. **Tareas programadas no atadas a su contenido.** El estado «activa» se guarda por nombre: un plan puede cambiar el rol,
   la consulta o las acciones de una tarea activa (o quitarla y volver a declararla) y sigue corriendo sin que el
   superusuario lo revise (contradice H2 CA2 de la 016).
5. **Reversión que reabre seguridad.** Revertir un paquete (017) restaura seguridad, automatizaciones, webhooks y tareas
   de la instantánea con un aviso genérico: quien aprueba no ve que se quita o cambia seguridad.
6. **Contabilidad y costeo (020).** Cancelar desde «Aplicada» una póliza automática la quita de los saldos pero no toca
   el inventario: contabilidad e inventario quedan descuadrados. Hay además diferencias de valuación en casos límite.
7. **OData sin límites de costo.** `$expand` de partidas trae todas las partidas de hasta 500 documentos por navegación,
   y `$skip` no tiene tope.
8. **Menores.** Una solicitud de seguridad queda atorada en `applying` si el proceso cae; el beneficiario de un cambio
   puede aprobarlo; emitir un token OAuth no queda en la auditoría; un plan puede quitar un estado del proceso en el que
   hay registros; el editor del catálogo no avisa al salir con cambios sin aplicar.

## Historias

**H1 — Nadie otorga lo que no tiene.** Como *responsable de seguridad*, quiero que solo un superusuario reparta
superusuario, y que nadie reparta capacidades administrativas que no tiene.
- CA1: Agregar o quitar a un usuario (alta o cambio) un rol **superusuario** exige ser superusuario; si no, 403 «Solo un
  administrador del sistema puede otorgar o quitar el rol …». Vale también para uno mismo.
- CA2: Agregar o quitar un rol con *administrar modelo* o *ver auditoría* exige tener esa capacidad (quien administra
  usuarios ya tiene la suya).
- CA3: Crear o editar un rol con capacidades que quien edita no tiene, o editar el rol superusuario, exige tenerlas (o
  ser superusuario); quitar capacidades de un rol también.
- CA4: Con aprobación de seguridad, la misma regla vale para quien aprueba: la solicitud muestra `canApprove = false` y
  aprobar responde 403 si el aprobador no tiene lo que el cambio otorga o quita; la solicitud sigue pendiente.
- CA5: El beneficiario no aprueba su propio cambio: un cambio a mi usuario, a un rol que tengo (directo o heredado) o a un
  equipo al que me agrega lo aprueba otra persona.

**H2 — Cambios de modelo sin pisarse.** Como *modelador*, quiero que aplicar falle si el catálogo cambió desde que lo
abrí o revisé, en lugar de borrar el cambio de otra persona.
- CA1: Cada catálogo del modelador trae su versión (`version`, huella de su definición). El editor (formulario y
  proceso incluidos) manda la versión con la que abrió el catálogo; la vista previa y aplicar rechazan con «el catálogo
  cambió desde que lo abriste» si ya no coincide.
- CA2: La vista previa devuelve `baseHash`, la huella de los catálogos y tableros que el plan toca tal como estaban al
  revisar. Aplicar la exige (400 sin ella) y la recalcula bajo el candado del modelo: si cambió, 409 «el modelo cambió
  desde la vista previa».
- CA3: Una propuesta del asistente incluye esa huella en la suya (`reviewHash`): un cambio a los catálogos que toca entre
  la revisión y la aplicación da 409, aunque el plan compile igual.

**H3 — Un trabajo se ejecuta una vez.** Como *responsable de operación*, quiero que un trabajo asíncrono no repita sus
efectos si el proceso cae o si otra instancia lo retoma.
- CA1: El éxito se marca dentro de la transacción del trabajo: los efectos y el estado `succeeded` se guardan juntos, o
  ninguno.
- CA2: Cada reclamo lleva un dueño (`LeaseToken`). Terminar, reprogramar un reintento o reportar avance exige ser el
  dueño actual; si otra instancia lo retomó, la transacción del trabajo se revierte.
- CA3: El *lease* se renueva mientras el trabajo corre (asíncrono o tarea), y en una tarea cada registro comprueba el
  dueño en su propia transacción.
- CA4: Un trabajo cuyo *lease* venció ya con todos sus intentos usados no se retoma: queda `failed` con «se interrumpió
  N veces sin terminar».

**H4 — Tareas atadas a lo que se activó.** Como *superusuario*, quiero que una tarea activa deje de correr si cambia lo
que hace o si se quita.
- CA1: Activar guarda la huella del contenido de la tarea (calendario, rol, consulta, lote y acciones).
- CA2: Un plan (modelador, módulo, paquete o reversión) que cambia el contenido de una tarea activa, la quita o archiva
  su catálogo la pausa en la misma transacción, con auditoría (`automation.schedule`, `pause`, motivo). Al volver a
  declararla llega en pausa.
- CA3: Una ocurrencia ya encolada de una tarea que cambió se cancela al ejecutarse («la tarea cambió desde que se
  activó»), salvo «Ejecutar ahora» del superusuario.
- CA4: Las tareas activas antes de esta spec (sin huella) adoptan la de su contenido actual la primera vez.

**H5 — La reversión dice qué seguridad cambia.** Como *aprobador*, quiero ver y confirmar lo que una reversión cambia de
seguridad.
- CA1: El diff de una reversión trae `securityChanges`: los avisos de seguridad del asistente (011) para sus
  operaciones de seguridad por campo y registro, proceso con roles, automatizaciones, webhooks y tareas.
- CA2: Con `securityChanges`, aprobar y aplicar exigen `confirmSecurity: true` (409 si no), como `confirmArchive`; la
  pantalla de *Entornos* lo muestra y pide la casilla.

**H6 — Póliza automática sin descuadre.** Como *contador*, quiero que una póliza generada por una recepción no se cancele
a mano una vez aplicada.
- CA1: Contabilidad 1.1.0 (por `upgrades`): «Cancelar póliza» desde *Aplicada* solo para pólizas manuales; una
  automática aplicada responde «Una póliza automática aplicada no se cancela: la generó su documento de origen. Registra
  una póliza manual de ajuste.»

**H7 — OData con límites.** Como *responsable de operación*, quiero que una consulta OData no traiga cantidades
ilimitadas.
- CA1: `$skip` mayor que 100 000 responde 400 («filtra por Id para avanzar»).
- CA2: `$expand` de partidas trae como máximo `OData:MaxExpandedLines` (5 000) partidas por página; si se pasa, 400
  «pide menos documentos con $top».

**H8 — Menores.**
- CA1: Una solicitud de seguridad en `applying` por más de 10 minutos vuelve a poderse aprobar o rechazar.
- CA2: Emitir un token OAuth queda en la auditoría (`integration.oauthtoken`: cliente, alcances y vigencia; nunca el token).
- CA3: Un plan que quita un estado del proceso con registros en él se rechaza en la vista previa y al aplicar: «N
  registros están en el estado X».
- CA4: El editor del catálogo avisa al cerrar o recargar la pestaña con cambios sin aplicar, y «Cancelar» con cambios
  pide confirmación.

## Decisiones (CLARIFY)
Sesión sin interlocutor; se registran para revisión del dueño.
- **Hallazgo 1, alcance:** la regla es «no otorgar ni quitar lo que no tienes», no solo superusuario. Quitar también,
  porque quitar el rol de modelador a otro administrador es tan administrativo como darlo. Se compara la capacidad
  efectiva del actor (`UserContext`) contra las capacidades de los roles agregados o quitados. *Ver auditoría* entra en
  la regla: la bitácora muestra datos de todos los catálogos legibles.
- **Hallazgo 1, aprobación:** la regla vive en la operación (`CreateUserCore`, `UpdateUserCore`, `SaveRole`), que corre
  primero al solicitar (con quien solicita) y otra vez al aprobar (con quien aprueba). `canApprove` la anticipa para la
  pantalla.
- **Hallazgo 2:** dos huellas porque hay dos ventanas: abrir→revisar (`version` por catálogo, la manda el cliente) y
  revisar→aplicar (`baseHash`, la calcula el servidor). La huella cubre solo lo que el plan toca, para no rechazar por
  cambios en otros catálogos. Entornos ya compara su diff contra el destino al aplicar (`DiffHash`, 009); no cambia.
- **Hallazgo 3:** marcar el éxito dentro de la transacción del trabajo da idempotencia sin tabla de deduplicación. Un
  trabajo que excede el *lease* sin renovarlo pierde su transacción (se revierte); por eso se renueva cada minuto.
- **Hallazgo 4:** se pausa en la transacción del plan (no en el despachador) para que no exista ninguna ventana en que
  corra el contenido nuevo. El calendario es contenido: cambiarlo también pausa la tarea (en la 016 solo se reagendaba),
  porque correr a otra hora o con otra frecuencia cambia lo que procesa. El despachador repite la comprobación por si el
  cambio llegó por otra vía.
- **Hallazgo 5:** la confirmación se exige en reversiones, que es donde la auditoría lo encontró. En un paquete normal el
  diff ya compara contra el entorno de origen revisado; extenderlo se deja para cuando un paquete lo necesite.
- **Hallazgo 6:** se impide (no se reemplaza por póliza de reversa): crear la reversa exige rutas de tres niveles y una
  póliza en sentido contrario por producto, pendientes de la 020.

## Diferidos (con razón)
- **Costeo (hallazgo 6, resto):** traspaso con bases distintas cuando las capas no cubren, entrada sin costo en una llave
  sin capas, `Costo` nulo omitido por `SUM` en la recepción, fecha del movimiento distinta de la de la póliza y cambio
  FIFO→Promedio que sigue en FIFO. Son decisiones contables (qué costo de respaldo usar, si bloquear recepciones sin tipo
  de cambio, si fundir capas al cambiar de método) que requieren al dueño; se agrupan en una spec de costeo 2.
- **Límite de `/oauth/token` detrás de un proxy:** el límite por IP usa `RemoteIpAddress`; detrás de un proxy hay que
  configurar `ForwardedHeaders` con los proxies conocidos de cada despliegue. Se documenta en el roadmap; configurarlo a
  ciegas permitiría falsificar la IP con `X-Forwarded-For`.
- **Bloqueo de navegación dentro de la SPA:** la app usa `BrowserRouter` (sin `useBlocker`); se avisa al cerrar o recargar
  y al cancelar, no al usar el menú lateral. Migrar a un *data router* es una spec de interfaz.
