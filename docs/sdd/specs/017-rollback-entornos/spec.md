# Spec 017 — Rollback de entornos

**Estado:** Implementado · **Depende de:** 009, 012, 014 · **Origen:** [roadmap](../../roadmap.md#017--rollback-de-entornos)

## Problema
1. Un paquete aplicado (009) no se puede revertir. Si en producción resulta que la versión nueva estorba, volver atrás
   exige que alguien arme a mano otro paquete «al revés» en desarrollo, lo exporte y lo promueva: lento justo cuando más
   prisa hay, y con riesgo de olvidar algo.
2. Ese paquete «al revés» tampoco podría expresarlo todo: un campo o un catálogo archivado **no se reactiva** con un
   paquete (la 009 lo marca como conflicto) ni desde el modelador («Restaurar campos archivados no está disponible»).
   Los datos siguen en la base, pero no hay forma de volver a verlos.
3. Nadie guarda cómo estaba el modelo antes de aplicar: después de aplicar no se puede saber con certeza qué había.
4. Revertir es un cambio al modelo como cualquier otro: si se salta la vista previa, la aprobación de otro administrador
   o la auditoría, se vuelve un atajo para cambiar producción sin cuatro ojos (012).

## Historias

**H1 — Instantánea antes de aplicar.** Como *administrador*, quiero que al aplicar un paquete quede guardado cómo estaba
el modelo, para poder volver a ese estado.
- CA1: Al aplicar un paquete se guardan, en la misma transacción, dos instantáneas de lo que el paquete puede tocar (sus
  catálogos con sus partidas y sus tableros): **antes** y **después** de aplicarlo, cada una con su huella SHA-256.
- CA2: Las instantáneas son solo metadatos (definiciones de catálogos y tableros): nunca registros, usuarios ni secretos.
  No se editan desde la aplicación.
- CA3: Si la aplicación falla, no queda instantánea (todo o nada).

**H2 — Revertir a la versión anterior.** Como *administrador*, quiero revertir el último paquete aplicado y ver antes
exactamente qué va a cambiar.
- CA1: En un paquete aplicado veo «Revertir a la versión anterior» (o «Revertir la instalación» si no había versión).
  Solo se ofrece en el **último** paquete aplicado de ese identificador que sigue instalado y que tiene instantánea.
- CA2: Pedir la reversión crea una **solicitud de reversión** pendiente, con su propio diff: es el **diff inverso** entre
  el modelo actual y la instantánea *antes*, generado por el mismo `ModelDiff` y compilado por el mismo `PlanCompiler`
  que un paquete. Errores en la vista previa, nunca a mitad de la aplicación.
- CA3: Lo que el paquete creó se **archiva** (catálogos, partidas, campos y tableros): nunca se borra nada y los datos se
  conservan. Lo que el paquete cambió vuelve a su definición anterior; lo que el paquete archivó se **reactiva**.
- CA4: Lo que el plan no puede expresar es **conflicto** y bloquea (p. ej. quitar un proceso que el paquete agregó a un
  catálogo existente, o volver a compartido un catálogo que el paquete hizo por empresa).
- CA5: Si el entorno cambió después de aplicar el paquete (un cambio del modelador sobre esos catálogos), el diff lo
  **advierte**: revertir también deshace ese cambio. Los campos que se agregaron después en este entorno y que la
  reversión archivaría piden confirmación explícita, como en la 014.
- CA6: Al aplicar la reversión, la versión registrada del paquete vuelve a la anterior (o el paquete deja de estar
  instalado), el paquete revertido queda como **Revertido**, y la solicitud como **Aplicada**. Si algo falla, el entorno
  queda como estaba.
- CA7: Los roles y permisos que el paquete creó **no** se quitan (pueden tener usuarios); el diff lo advierte. Revertir
  nunca crea roles ni otorga permisos.

**H3 — Aprobación y auditoría de la reversión.** Como *responsable de producción*, quiero que una reversión pase por la
misma compuerta que un paquete.
- CA1: En un entorno con aprobación obligatoria, la reversión la aprueba un administrador **distinto** de quien la pidió;
  la aprobación queda ligada a la huella del diff y aplicar exige el mismo diff (si el entorno cambió, se revisa de nuevo).
- CA2: Pedir, aprobar, rechazar y aplicar una reversión quedan en la auditoría inmutable (`environment.revertRequest`,
  `environment.approve`, `environment.reject`, `environment.revert`) con el paquete, las versiones, las huellas del diff
  y de la instantánea y quién aprobó. El plan aplicado queda además como conjunto de cambios (`model.change`).
- CA3: Si la instantánea guardada no coincide con su huella, la reversión no se ofrece ni se aplica.
- CA4: Solo el superusuario pide, aprueba y aplica reversiones (como todo *Entornos*).

**H4 — Reactivar campos y catálogos archivados.** Como *modelador*, quiero reactivar un campo o un catálogo archivado y
recuperar sus datos.
- CA1: El Plan de Cambios tiene dos operaciones nuevas: `restoreEntity` y `restoreField`. Reactivan lo archivado con los
  datos que la columna o la tabla conservaron; nunca crean ni borran columnas.
- CA2: Las valida el compilador: el catálogo o el campo debe existir y estar archivado; un campo reactivado que apunta a
  un catálogo archivado, una partida cuyo documento sigue archivado o un documento cuyas partidas siguen archivadas son
  errores de la vista previa. Una fórmula reactivada se recalcula en los registros existentes.
- CA3: En el modelador, un campo archivado se reactiva desde el editor del catálogo y un catálogo archivado desde la lista,
  con la vista previa de siempre. El asistente (011) conoce las dos operaciones.
- CA4: Un paquete que trae activo un campo o catálogo archivado en el destino ya no es conflicto: el diff lo reactiva
  (salvo que su estructura sea distinta, que sigue siendo conflicto).

## Decisiones (CLARIFY)
Sesión sin interlocutor disponible: se tomaron estas decisiones y se registran para revisión.
- **Qué se fotografía:** lo que el paquete puede tocar, no la base entera: los catálogos que crea el plan del paquete
  (sus partidas incluidas) y sus tableros, con su definición completa (campos activos y archivados, proceso, reglas,
  seguridad, interfaz, automatizaciones, webhooks y tareas), antes y después de aplicar. Un paquete de la 009 no puede
  cambiar nada fuera de ese alcance salvo roles y permisos, que no se revierten (abajo).
- **Revertir = diff contra la instantánea *antes***, con el mismo `ModelDiff.Compute` que un paquete, más dos reglas de la
  reversión: dentro del alcance, lo que no existía antes se archiva (catálogos, en orden de dependencias: primero el
  campo de partidas del documento, luego la partida, luego el documento; y tableros con `archiveDashboard`). La
  instantánea *después* sirve para distinguir los cambios posteriores (advertencia) y los campos agregados después en
  este entorno (confirmación aparte, regla de la 014).
- **Solo el último paquete instalado** de ese identificador, y una reversión a la vez por paquete. Revertir varias
  versiones es encadenar reversiones (cada una devuelve la versión anterior). Una reversión no se revierte: para volver a
  la versión nueva se sube de nuevo el paquete (su versión ya es mayor que la registrada).
- **Versión registrada:** al revertir vuelve a la que había antes de aplicar (`PreviousVersion`); si no había, el paquete
  deja de figurar como instalado. El paquete revertido no se puede aplicar de nuevo desde su fila: se sube otra vez.
- **Roles y permisos se conservan.** Pueden tener usuarios asignados y quitarlos no es «nunca borrar datos». La reversión
  no los toca: sobre catálogos archivados no dan acceso a nada. Se advierte para que se limpien en *Seguridad*.
- **La solicitud de reversión es una fila de `meta.PackageImport`** con tipo `revert`: aparece en *Paquetes recibidos*, se
  revisa en el mismo diálogo y usa los mismos endpoints de aprobar, rechazar y aplicar. Así no hay un segundo camino
  con reglas propias.
- **Paquetes aplicados antes de esta spec** no tienen instantánea: no se pueden revertir (se explica en el diálogo).
- **Reactivar en el modelador:** con `restoreField` y `restoreEntity` desde las mismas pantallas, con vista previa. En un
  entorno con aprobación obligatoria el modelador sigue sin aplicar directo (012): se reactiva con un paquete.
- **Fuera de alcance:** revertir módulos del servidor (`compras`, `inventario`: siguen su camino de `upgrades`), revertir
  datos (registros creados o cambiados después del paquete se quedan como están), reversiones parciales (elegir qué
  parte deshacer) y quitar roles creados por un paquete.
