# Spec 012 — Endurecimiento (auditoría, transiciones, concurrencia, aprobación, asistente, etiquetas)

**Estado:** Implementado · **Depende de:** 005–011 · **Origen:** [roadmap](../../roadmap.md#012--endurecimiento)

## Problema
Una auditoría de solo lectura (2026-10-23) comparó el resultado de las specs 005–011 con Salesforce, Dataverse y Odoo y
verificó en el código seis huecos. Ninguno es una capacidad nueva: son lugares donde una regla que el motor ya declara
(seguridad por registro y campo de la 006, empresa de la 010, aprobación de la 009, límites de la 011) no se aplica en
todos los caminos.

1. **Bitácora sin seguridad.** `GET /api/audit` devuelve el antes/después completo de cualquier registro a quien tiene
   *ver auditoría*, aunque no pueda leer ese catálogo, el registro sea de otra empresa o de otro dueño, o el campo esté
   oculto para su rol (p. ej. precios para el almacenista).
2. **Transiciones sobre registros ajenos.** Una transición sin roles pide «permiso de edición» del catálogo, pero no el
   alcance de escritura por registro: quien solo edita sus propios registros ejecuta acciones (y captura sus campos
   obligatorios) en los de otros.
3. **Sobre-recepción concurrente.** La regla `NoExcederPendiente` de la recepción se evalúa con datos de la orden leídos
   antes de bloquearla; dos confirmaciones simultáneas contra la misma partida reciben el doble de lo pedido. Lo mismo
   con cancelar la orden mientras se confirma una recepción.
4. **Cuatro ojos con atajos.** En un entorno que exige aprobación (009), solo los paquetes la piden: aplicar un plan en
   el modelador, instalar o actualizar un módulo y aplicar una propuesta del asistente cambian el modelo con una sola
   persona.
5. **Límite del asistente evadible y aplicación sin huella.** El límite por hora se cuenta antes de llamar al LLM y la
   solicitud se guarda después: varias solicitudes simultáneas lo rebasan. Aplicar no comprueba que la vista previa que
   la persona revisó sea la que se aplica (el modelo pudo cambiar entre medio).
6. **Etiquetas de referencias sin seguridad.** El nombre que acompaña a una referencia (`Proveedor__label`) se lee del
   registro referido sin el alcance por registro ni la empresa del usuario.

## Historias

**H1 — Bitácora con la seguridad del dato.** Como *responsable de seguridad*, quiero que la auditoría de datos respete
los mismos permisos que la pantalla del registro.
- CA1: Quien no es superusuario solo ve eventos de datos (`create`, `update`, `delete`, `workflow.*`, `automation.*`) de
  catálogos que puede leer y de registros dentro de su alcance de lectura (empresa actual y permisos por registro),
  incluidos los ya eliminados. El total del paginado cuenta solo lo visible.
- CA2: En el antes/después no aparecen los campos que su rol no ve (encabezado y partidas).
- CA3: `data.import`, `data.export` y `view.change` solo se ven si puede leer el catálogo. Los eventos que no son de datos
  (sesiones, modelo, seguridad, entornos, empresas, asistente…) siguen visibles para quien audita.
- CA4: El superusuario ve todo, como antes.

**H2 — Transiciones con el alcance de escritura.** Como *responsable de seguridad*, quiero que una acción sin roles
declarados exija poder modificar ese registro.
- CA1: Una transición **sin roles** exige el permiso de edición del catálogo **y** que el registro esté en el alcance de
  escritura por registro; si no, 403 «Puedes consultar este registro, pero tu rol no puede modificarlo» y la acción
  aparece deshabilitada con ese motivo.
- CA2: Una transición **con roles** sigue como en la 006: basta ver el registro y tener el rol (p. ej. el gerente que
  autoriza la requisición de otro).

**H3 — Concurrencia en automatizaciones.** Como *almacenista*, quiero que dos recepciones simultáneas no reciban más de
lo pedido ni se confirme una recepción contra una orden recién cancelada.
- CA1: Antes de validar una transición, el motor bloquea los documentos que modificarán sus automatizaciones (destinos
  `target` de las acciones de esa transición y de entrar al estado) y relee sus datos: las reglas y condiciones se
  evalúan con datos actuales. Varias confirmaciones simultáneas contra la misma partida: solo pasan las que caben.
- CA2: Compras 1.7.0 agrega a la partida de la orden la regla `CantidadRecibida <= Cantidad` (red de seguridad: si algo
  rebasa lo pedido, la automatización falla y nada se guarda).
- CA3: Cancelar la orden mientras se confirma una recepción: gana la primera; la otra recibe 409 (versión) o 400 (la
  orden ya no está enviada).

**H4 — Cuatro ojos sin atajos.** Como *responsable de cambios*, quiero que en un entorno con aprobación el modelo solo
cambie por paquetes aprobados.
- CA1: Con `Environments:RequireApproval`, aplicar un plan del modelador, instalar o actualizar un módulo y aplicar una
  propuesta del asistente responden 409 con el motivo («llegan como paquete desde *Entornos*»). La vista previa sigue
  disponible.
- CA2: El asistente lo indica: la propuesta no se puede aplicar y dice por qué.
- CA3: `Environments:AllowDirectChanges` (falso por defecto) lo permite de forma explícita; solo para suites de prueba
  que se comportan como producción pero preparan datos con planes.

**H5 — Asistente: límite y huella.** Como *responsable de seguridad*, quiero que el límite por hora sea exacto y que se
aplique lo que se revisó.
- CA1: La solicitud reserva su lugar (fila `pending`) antes de llamar al LLM, con un bloqueo por usuario: N solicitudes
  simultáneas con L lugares libres → exactamente L llegan al proveedor; el resto, 429.
- CA2: Cada propuesta lleva la huella de lo revisado (plan, pasos con SQL y advertencias, compilados contra el modelo
  actual). Aplicar exige esa huella y la recalcula dentro de la transacción: si cambió, 409 «el modelo cambió desde que
  revisaste la propuesta»; sin huella, 400.

**H6 — Etiquetas de referencias.** Como *usuario*, no quiero ver el nombre de un registro que mi rol o mi empresa no ven.
- CA1: En listados, detalle, partidas y cálculo en vivo, la etiqueta de una referencia a un registro fuera de mi alcance
  de lectura (empresa o permisos por registro) es `(sin acceso)`, como en los reportes (007). El id se conserva.
- CA2: El buscador y la importación ya filtran por alcance, empresa y eliminados (se verifica con pruebas). El campo para
  mostrar no puede ocultarse por rol (lo impide el compilador desde la 006).

**H7 — Menores.**
- CA1: Compras 1.7.0 guarda la moneda del *costo último* del producto (compartido entre empresas con distinta moneda base).
- CA2: No se puede cambiar la moneda base de una empresa que ya tiene registros en catálogos por empresa (409).

## Decisiones (CLARIFY)
Sesión sin interlocutor; se registran para revisión del dueño.
- **Transiciones (hallazgo 2):** se exige el filtro de escritura solo a las transiciones **sin roles**, porque su
  autorización *es* «permiso de edición». Las transiciones con roles son autorización de proceso: un gerente aprueba
  documentos que no son suyos y que no puede editar; exigirle escritura rompería el flujo de la 006 (H2 CA2).
- **Concurrencia (hallazgo 3):** se hacen las dos cosas que sugería la auditoría: bloquear antes de validar (motor,
  genérico, con orden determinista para no provocar interbloqueos) y la regla en el destino (metadato, por `upgrades`).
  La regla ya se evalúa tras el acumulado: `SystemUpdateAsync` valida las reglas del registro modificado.
- **Bitácora (hallazgo 1):** el superusuario no se filtra (tampoco por empresa): la bitácora es la herramienta para
  investigar, y él ya puede cambiar de empresa y ver todo. Un registro eliminado conserva su rastro para quien lo veía.
- **Aprobación (hallazgo 4):** en un entorno con aprobación se **bloquean** los caminos directos en lugar de crear una
  cola de aprobación para cada uno: el camino aprobado ya existe (paquetes de *Entornos*, con diff y huella). Los
  módulos del servidor también se bloquean: en producción llegan como paquete exportado de un entorno de pruebas.
  La suite E2E se comporta como producción y prepara datos con planes: usa `Environments:AllowDirectChanges`, y una
  segunda instancia sin esa opción prueba el bloqueo.
- **Etiquetas (hallazgo 6):** fuera de alcance se muestra `(sin acceso)` (igual que los reportes); la etiqueta de un
  registro eliminado sí se muestra (es el nombre histórico del documento y el usuario lo veía).

## Diferidos (con razón)
- **Acumulado en memoria** (`MaxRollupSources` = 10 000): el filtro del acumulado puede usar campos del documento
  (`Recepcion.Estado`), que el traductor a SQL de la 006 no admite. Está acotado (falla con mensaje claro) y no es un
  hueco de seguridad; se resolverá cuando el traductor admita rutas al padre.
- **Bloqueo de folios durante la importación:** la vista previa corre en una transacción que se revierte y conserva el
  bloqueo de `meta.Sequence` hasta el final (las altas del mismo catálogo esperan). Es disponibilidad, no integridad;
  requiere folios fuera de la transacción (huecos en la numeración), una decisión de negocio para otra spec.
- **`ModelDiff` archiva campos locales con un aviso:** es el contrato de la 009 (el paquete es la fuente de verdad, la
  vista previa lo muestra y en producción otro administrador lo aprueba). Cambiarlo requiere spec propia.
