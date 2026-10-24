# Spec 019 — Editores visuales: diseñador del formulario y diagrama del proceso

**Estado:** Implementado · **Depende de:** 002, 007, 011, 012 · **Origen:** [roadmap](../../roadmap.md#019--editores-visuales-layouts-y-modelador)

## Problema
1. El layout del formulario (007) se escribe como JSON en una caja de texto: para mover un campo de sección hay que
   cortar y pegar texto, cuidar comas y comillas, y recordar reglas que solo dice la vista previa (las pestañas van
   juntas, si una sección tiene pestaña todas deben tenerla, un campo aparece una sola vez).
2. No se ve el resultado hasta aplicar el plan y abrir un registro: no hay vista previa del formulario mientras se diseña.
3. El proceso (002) también es JSON: con ocho estados y doce transiciones (la orden de compra) no se ve de un vistazo qué
   estado lleva a cuál, qué transiciones salen de un estado ni si quedó un estado al que no se llega.
4. El asistente (011) es hoy la única forma «visual» de construir. Quien no quiere escribir JSON ni pedírselo a la IA no
   tiene otra vía.
5. Lo que se agregue no puede abrir un segundo camino para cambiar el modelo: debe producir el mismo Plan de Cambios, con
   la misma vista previa, validación, aprobación (012) y auditoría.

## Historias

**H1 — Diseñador del formulario.** Como *modelador*, quiero organizar el formulario arrastrando campos y secciones y ver
cómo queda mientras lo hago.
- CA1: El editor de un catálogo tiene la pestaña **Formulario** con: los campos sin colocar, las secciones con sus campos
  en el orden y las columnas del formulario, y las pestañas. Si el catálogo no tiene layout, se crea con un clic (una
  sección con todos los campos en su orden); también se puede quitar.
- CA2: Con el ratón se **arrastra y suelta**: un campo dentro de su sección, a otra sección o de vuelta a «Sin colocar»;
  una sección antes de otra o a otra pestaña.
- CA3: Todo se puede hacer **solo con teclado**: un campo se toma con Espacio, se mueve con las flechas (cruza de una
  sección a la siguiente), se suelta con Espacio y se cancela con Esc, y cada paso se anuncia en una región en vivo; las
  secciones y pestañas se mueven con botones; las opciones de un campo (sección, ancho completo, condición, quitar) están
  en un diálogo.
- CA4: Se editan todas las propiedades del layout de la 007: etiqueta, identificador, descripción, columnas (1 o 2) y
  condición (`visibleWhen`) de cada sección; ancho completo y condición de cada campo; pestañas (organizar o no en
  pestañas, agregar, renombrar).
- CA5: **Vista previa en vivo** del formulario tal como lo verá el usuario: pestañas, secciones, columnas, campos de ancho
  completo, «Otros datos» con lo que no se colocó (en un documento, las fórmulas no colocadas en «Totales») y una marca
  en lo condicional. Se actualiza con cada cambio, sin guardar nada.
- CA6: El diseñador produce el mismo `setLayout`: «Revisar cambios» abre la vista previa del plan validada por el
  servidor. Sin cambios reales no se ofrece revisar (mover algo y regresarlo no es un cambio). Por construcción respeta
  las reglas de la 007 (pestañas juntas, todas o ninguna con pestaña, un campo una vez).
- CA7: El JSON sigue disponible: se alterna entre **Diseñador** y **JSON** sobre el mismo borrador. Con JSON inválido el
  diseñador no se ofrece y dice por qué.
- CA8: Un campo que el layout menciona y ya no está activo en el borrador (se archivó o se quitó en la pestaña Catálogo)
  aparece marcado con su problema y se puede quitar con un clic.

**H2 — Diagrama del proceso.** Como *modelador*, quiero ver y editar el proceso como un diagrama de estados.
- CA1: La pestaña **Proceso** dibuja los estados (con su color, el inicial señalado y los no editables con candado) y las
  transiciones como flechas con su etiqueta, ordenados solos de izquierda a derecha a partir del estado inicial. Las
  transiciones de solo automatización (005) se distinguen.
- CA2: Se agregan y eliminan estados y transiciones, y al seleccionar uno se editan sus propiedades en un panel: estado
  (etiqueta, identificador, color, editable, inicial) y transición (etiqueta, identificador, desde, hacia, roles,
  comentario obligatorio, campos obligatorios, condición y su mensaje, asignaciones, icono, variante, solo automatización).
  Con el ratón, **arrastrar de un estado a otro** crea la transición entre ellos.
- CA3: Con teclado y lector de pantalla: cada estado y cada transición es enfocable y tiene nombre accesible (p. ej.
  «Estado Borrador, inicial, editable, 2 transiciones salen»); Enter lo selecciona; desde el panel de un estado se agrega
  una transición hacia otro; eliminar pide confirmación y dice qué transiciones se quitan con el estado.
- CA4: Un catálogo sin proceso ofrece «Crear proceso» con un estado inicial. Los identificadores de estados y
  transiciones ya guardados no se cambian (los usan registros, historial y automatizaciones); eliminar un estado guardado
  advierte que los registros que estén en él se quedan con ese valor.
- CA5: Produce el mismo `setWorkflow`, con la misma vista previa y validación; el JSON alterno de H1 CA7 también aplica.

**H3 — Mismo camino, seguridad y accesibilidad.** Como *responsable del modelo*, quiero que los editores visuales sean
otra forma de escribir el mismo plan, sin atajos.
- CA1: Los diseñadores no aplican nada por su cuenta: todo pasa por «Revisar cambios», la vista previa del servidor y
  «Aplicar cambios», y queda en la auditoría como cualquier plan. En un entorno con aprobación obligatoria (012) el
  modelador sigue sin aplicar directo.
- CA2: Sin endpoints nuevos: solo quien accede al modelador ve los diseñadores; la URL directa a una pestaña sin permiso
  lleva a la pantalla de acceso denegado.
- CA3: Accesibilidad AA (axe sin violaciones serias) en las dos pestañas, en claro y oscuro; a 390 px la página no se
  desborda (el diagrama se desplaza dentro de su recuadro, que se puede recorrer con teclado).
- CA4: En una partida, las pestañas explican que el formulario y el proceso se definen en su documento.

## Decisiones (CLARIFY)
Sesión sin interlocutor disponible: se tomaron estas decisiones y se registran para revisión.
- **Sin capacidad nueva en el motor ni metadato nuevo.** Los diseñadores editan el mismo borrador que el JSON (el texto
  de `Layout` y `Workflow`) y `buildOperations` sigue siendo el único que arma el plan. El servidor no cambia.
- **Posiciones del diagrama calculadas, no guardadas.** Guardar coordenadas sería un metadato nuevo que viaja en paquetes
  y entra al diff de la 009 sin valor de negocio. El diagrama se ordena por capas desde el estado inicial (distancia por
  transiciones), y en cada capa por el orden de declaración. Mismo proceso, mismo dibujo.
- **Dónde viven:** el editor del catálogo pasa a tener pestañas **Catálogo** (lo de hoy), **Formulario** y **Proceso**;
  el JSON del layout y del proceso se mueve de la sección avanzada a su pestaña (modo JSON). La pestaña va en la URL
  (`?edit=X&tab=formulario`) y el borrador se conserva al cambiar de pestaña. Las pestañas de diseño usan el ancho
  completo de la página.
- **Arrastrar y soltar con la API nativa de HTML** (sin bibliotecas nuevas) y, para teclado, el patrón «tomar, mover,
  soltar» con región en vivo. Mover con flechas cruza secciones: es el mismo orden en que se lee el formulario.
- **Pestañas como propiedad de las secciones** (como en la 007), no como contenedor nuevo: mover una sección por encima
  de la primera de su pestaña la pasa a la pestaña anterior. Así el diseñador nunca produce pestañas separadas.
- **Vista previa con la misma función que el formulario real** (`formSections`), con controles simulados (no editables,
  sin consultas al servidor): muestra estructura, no datos. Lo condicional se muestra marcado, no se evalúa (no hay
  registro con qué evaluarlo).
- **Identificadores** de secciones, estados y transiciones nuevos se derivan de la etiqueta (como los campos) y se pueden
  editar hasta guardar; los ya guardados de estados y transiciones quedan fijos (registros, historial, automatizaciones y
  webhooks los nombran). Los de secciones sí se pueden cambiar (solo el layout los usa).
- **Eliminar un estado** quita también las transiciones que llegan a él y lo saca del «desde» de las demás (las que se
  quedan sin origen se quitan); el diálogo lo enumera. No se puede eliminar el estado inicial ni el único estado.
- **El proceso no se quita desde el diseñador**: el motor no tiene operación para quitar un proceso (002); como hoy, el
  JSON vacío no genera operación.
- **Comparación sin importar el orden de las propiedades:** el diseñador puede reescribir un objeto con sus propiedades en
  otro orden; la comparación con lo guardado ordena las claves, así no aparece «Revisar cambios» sin cambios.
- **Fuera de alcance:** diseñador de vistas, reportes y tableros (siguen como JSON validado), layout de partidas (la 007
  no lo admite), editar en el modelador un plan propuesto por el asistente (pendiente de la 011), deshacer/rehacer dentro
  del diseñador (Cancelar descarta el borrador completo) y mover estados en el diagrama a mano.
