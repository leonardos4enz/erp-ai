# Spec 013 — Formulario en solo lectura fuera del alcance de escritura

**Estado:** Implementado · **Depende de:** 006, 012 · **Origen:** hallazgo F-05 de la [verificación 012](../012-endurecimiento/verify.md)

## Problema
La seguridad por registro (006) distingue el alcance de **lectura** del de **escritura**: un permiso con
`access: 'read'` deja ver un registro sin poder modificarlo. El servidor ya lo cumple (403 «Puedes consultar este
registro, pero tu rol no puede modificarlo» al guardar o eliminar) y desde la 012 las transiciones sin roles se muestran
deshabilitadas con ese motivo. Pero el **formulario** solo conoce los permisos del rol sobre el catálogo: muestra los
campos editables, «Guardar cambios» y «Eliminar», y la persona se entera del rechazo después de capturar.

No hay fuga ni escalamiento (el servidor rechaza), pero la pantalla promete algo que no se puede hacer.

## Historia

**H1 — El formulario dice lo que se puede hacer con el registro.** Como *vendedor* que consulta una oportunidad pública
de otro vendedor, quiero ver el formulario en solo lectura y saber por qué, para no capturar cambios que se van a perder.
- CA1: El registro que devuelve la API (detalle, alta, edición y transición) trae `_access` con `update`, `delete` y
  `reason`: lo que el usuario puede hacer **con ese registro**, combinando el permiso del rol sobre el catálogo, el
  alcance de escritura por registro (006/010) y que el catálogo no lo administre el sistema. `reason` explica la
  negativa cuando la causa es el alcance de escritura.
- CA2: Si `_access.update` es falso, el formulario muestra los campos y las partidas deshabilitados, sin «Guardar
  cambios», y un aviso «Solo lectura» con el motivo. Si `_access.delete` es falso, no aparece «Eliminar».
- CA3: Quien sí puede modificar el registro (el dueño, en el mismo catálogo) sigue viendo el formulario editable con
  «Guardar cambios» y «Eliminar».
- CA4: Al cambiar el registro de alcance (p. ej. tras una transición) el formulario se ajusta con la respuesta, sin
  recargar.
- CA5: El servidor sigue rechazando con 403 aunque un cliente ignore `_access` (no es la defensa, solo la explicación).

## Decisiones (CLARIFY)
- **Genérico del motor:** `_access` existe en todo registro, tenga o no proceso; no depende de `_workflow`.
- **Combinado y no solo el alcance:** el cliente no tiene que repetir reglas; las que ya sabía (rol, administrado por el
  sistema, estado no editable) siguen igual y sus avisos actuales no cambian.
- **El estado del proceso no entra en `_access`:** `_workflow.editable` ya lo informa y tiene su propio aviso.
- **Webhooks:** la carga ya descarta las claves que empiezan con `_`; `_access` no sale del ERP.
- **Listado:** no se calcula por fila (costo de una consulta por registro); el listado no ofrece editar ni eliminar en
  línea, así que basta con el formulario.
