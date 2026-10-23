# Spec 006 — Seguridad por campo y por registro

**Estado:** Implementado · **Depende de:** 002, 004, 005 · **Origen:** constitución II.2 (deuda) y [roadmap](../../roadmap.md#006--seguridad-por-campo-y-por-registro-prioridad-1-deuda-de-la-constitución)

## Problema
1. La constitución (II.2) promete permisos «a nivel entidad y campo», pero solo existen permisos por
   catálogo: quien lee una orden de compra ve todos sus importes, y quien la edita puede capturar cualquier campo.
2. No hay campos editables solo en ciertos estados. «Motivo de rechazo» se puede capturar en *Borrador*
   aunque solo tiene sentido al rechazar (hallazgo F-25 de la spec 004).
3. No hay seguridad por registro: un solicitante ve las requisiciones de toda la empresa.
4. Al administrador se le ofrece «Nuevo» en *Existencias* aunque solo las crea el sistema (F-04 de la 005).
5. Todo debe aplicarse **en el servidor**: ocultar en pantalla no protege nada si la API devuelve el dato.

## Historias

**H1 — Seguridad por campo.** Como *modelador*, quiero declarar por rol qué campos se ocultan, cuáles son
de solo lectura y en qué estados se pueden capturar.
- CA1: Un campo puede limitarse a ciertos roles para **verse** (`read`) y para **capturarse** (`edit`).
  Quien no lo ve no lo recibe en ninguna respuesta: listado, registro, partidas, `/compute`, transiciones
  ni respuestas de guardado. Tampoco puede buscar ni ordenar por él.
- CA2: Un campo de solo lectura para el usuario conserva su valor aunque la petición traiga otro (la API lo
  ignora, igual que los campos que asigna el sistema).
- CA3: Un campo puede ser editable **solo en ciertos estados** del proceso (`editableIn`); con `[]` solo se
  captura en las transiciones que lo piden. Aplica también a los campos de partidas (estado del documento).
- CA4: El formulario y los listados no muestran los campos ocultos y bloquean los de solo lectura con una
  explicación («Solo lectura para tu rol», «No se edita en este estado»).
- CA5: El plan se valida en la vista previa: campos, roles y estados existentes; el campo para mostrar no se
  puede ocultar; un campo obligatorio sin valor por defecto debe poder capturarse al crear.

**H2 — Seguridad por registro.** Como *modelador*, quiero decidir qué registros ve y modifica cada rol.
- CA1: Alcance por rol: **todos**, **de mi unidad** (el dueño pertenece a mi unidad) o **propios**. El dueño es
  quien lo creó o un campo de tipo usuario declarado (`owner`, p. ej. *Solicitante*).
- CA2: Reglas declarativas con el lenguaje de expresiones (`where`), p. ej. `Solicitante = CURRENTUSER()` o
  `Departamento = CURRENTUNIT()`, combinables con el alcance.
- CA3: Cada permiso es de **lectura** o de **escritura**. Leer da acceso a ver y a ejecutar transiciones (que
  tienen sus propios roles); escribir, además, a editar y eliminar.
- CA4: Se aplica en el servidor a listados (con totales y conteos por estado), lectura, buscadores, validación
  de referencias, `/compute`, transiciones, historial, edición y eliminación. Un registro fuera de alcance
  responde **404** (no revela que existe); uno visible pero sin escritura, **403**.
- CA5: No se puede crear ni dejar un registro fuera del propio alcance de escritura (p. ej. cambiar el
  departamento a otro): la operación se rechaza y no queda nada.
- CA6: Cada usuario tiene una **unidad** (área o sucursal) que el administrador asigna en Seguridad.

**H3 — Catálogos que administra el sistema.** Como *administrador*, no quiero que se me ofrezca capturar a mano
lo que solo crea el motor.
- CA1: Un catálogo marcado `systemManaged` no acepta altas, ediciones ni bajas por API, ni del administrador;
  las automatizaciones sí escriben en él. La UI no ofrece «Nuevo» ni «Eliminar». Resuelve F-04.

**H4 — Compras 1.3.0 e Inventario 1.1.0.** Como *usuario de Compras*, quiero que el módulo use estas capacidades.
- CA1: «Motivo de rechazo» (requisición y orden) y «Motivo de cancelación» (orden) solo se capturan en su
  transición. Resuelve F-25.
- CA2: El solicitante ve y edita solo sus requisiciones; compradores, gerentes y directores ven todas.
- CA3: El almacenista no ve los importes de la orden de compra (precios, descuentos, IVA y totales).
- CA4: *Existencias* pasa a ser administrado por el sistema (Inventario 1.1.0).
- CA5: Llegan como actualizaciones versionadas, sin reinstalar ni perder datos.

**H5 — Modelador.** Como *modelador*, quiero declarar la seguridad del catálogo como JSON validado, igual que
reglas, procesos y automatizaciones.

## Decisiones (CLARIFY)
Sesión sin interlocutor disponible: se tomaron estas decisiones y se registran para revisión.
- **Lista blanca y unión de roles.** `read`/`edit` listan quién puede; omitido = todos los que tienen el permiso
  del catálogo. Un usuario con varios roles obtiene lo más permisivo de ellos (como los permisos por catálogo).
- **El superusuario** ignora las restricciones por rol (`read`, `edit`, `records`), pero no las de proceso
  (`editableIn`, estados no editables) ni `systemManaged`, que son lógica de negocio, no privilegio.
- **Campos de solo lectura se ignoran en silencio** al guardar, como los que asigna el sistema (spec 004): la UI
  envía el formulario completo y un error rompería guardados legítimos.
- **Transiciones y campos.** Las transiciones capturan sus `requiredFields` aunque el campo no sea editable en el
  formulario (así se resuelve F-25); un campo **oculto** para el usuario no se puede capturar en una transición.
- **Transiciones y registros.** Basta con ver el registro: un autorizador no necesita permiso de escritura sobre
  la requisición ajena; la transición ya exige sus roles y condiciones.
- **Las automatizaciones actúan con autoridad del motor** (spec 005): no revisan seguridad por campo ni por
  registro en el destino. La respuesta al usuario sí se filtra.
- **`where` se traduce a SQL parametrizado** con un subconjunto del lenguaje (campos del propio catálogo,
  comparaciones, `IN`, `AND/OR/NOT`, `ISBLANK`, `CURRENTUSER()`, `CURRENTUNIT()`, `HASROLE()`, `ISCREATOR()`,
  `TODAY()`). Así el listado pagina y cuenta en la base, y lectura y escritura usan exactamente el mismo filtro.
- **Unidad = texto** en el usuario (con sugerencias de las existentes). Un catálogo de unidades con jerarquía
  queda para la spec 010 (multi-empresa).
- **Reportes** aún no existen (spec 007): deberán usar los mismos filtros de este motor.
- La **auditoría** la ve solo quien tiene ese permiso y muestra los valores completos (privilegio explícito).
