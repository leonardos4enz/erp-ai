# Spec 004 — Campos asignados por el sistema y actualización de módulos

**Estado:** En implementación · **Depende de:** 002, 003 · **Origen:** petición del usuario + hallazgo de seguridad

## Problema
1. En la requisición, *Fecha* y *Solicitante* son valores **sugeridos**: el usuario puede cambiarlos.
   El negocio pide que la fecha sea siempre la del alta y el solicitante siempre el usuario que la crea.
2. **Hallazgo de seguridad:** en la orden de compra, *Autorizado por* y *Fecha de autorización* se
   pueden capturar a mano en Borrador. Solo debe asignarlos la transición «Autorizar».
3. La requisición no indica a qué almacén(es) va lo solicitado.
4. El módulo Compras 1.0.0 ya está instalado: los cambios deben llegar **sin reinstalar ni perder datos**.

## Historias

**H1 — Campo asignado por el sistema.** Como *modelador*, quiero marcar un campo como no editable
(`editable: false`) para que solo lo asigne el motor.
- CA1: Al crear, toma su valor por defecto (p. ej. `TODAY()`, `CURRENTUSER()`), aunque el cliente envíe otro.
- CA2: Después de creado, nadie lo cambia editando, ni siquiera por API directa. Solo las asignaciones
  (`set`) de una transición pueden escribirlo.
- CA3: La pantalla lo muestra bloqueado, con la leyenda «Lo asigna el sistema».

**H2 — Actualizar un módulo instalado.** Como *administrador*, quiero ver «Actualizar a 1.1.0» en
*Módulos*, revisar los cambios y aplicarlos en una transacción, sin perder los registros existentes.
- CA1: Una instalación nueva aplica el plan base y luego todas las actualizaciones, en orden.
- CA2: Una actualización aplica solo las versiones posteriores a la instalada.

**H3 — Almacenes en la requisición (Compras 1.1.0).** Como *solicitante*, quiero elegir el almacén
destino en el encabezado y, si hace falta, un almacén distinto por partida.
- CA1: Cada partida sugiere el almacén del encabezado (`Requisicion.Almacen`).

## Decisiones (CLARIFY)
- «La fecha siempre será la actual» se interpreta como **la fecha del alta**: no se recalcula cada día.
- Varios almacenes se modelan **por partida**, no con un campo multivalor. Es lo usual en un ERP:
  cada renglón tiene su destino, y no requiere un tipo de campo nuevo.
- En Compras 1.1.0 también quedan bloqueados *Autorizado por* y *Fecha de autorización* de la orden.

## Diseño
- `FieldDef.editable` (por omisión `true`), persistido en `meta.Field.IsEditable` (migración idempotente).
  Las operaciones `createEntity`, `addField` y `updateField` lo aceptan.
- El motor ignora los campos no editables que llegan en el cuerpo de alta o edición (igual que con las
  fórmulas). Los valores por defecto se aplican cuando el campo está vacío, o sea, al crear.
- Paquete de módulo: `version` (la más reciente), `plan` (base) y `upgrades: [{ version, description, plan }]`.
  `meta.Module.Version` registra la instalada.
- API: `GET /admin/modules` agrega `installedVersion` y `upgradeAvailable`;
  `POST /admin/modules/{id}/upgrade/preview` y `POST /admin/modules/{id}/upgrade`.
