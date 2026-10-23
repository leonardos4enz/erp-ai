# Spec 002 — Capacidades del motor: documentos, fórmulas, reglas y procesos

**Estado:** En implementación · **Constitución:** v1.1

## Problema
El motor de la spec 001 solo crea catálogos planos. Un módulo real (compras, ventas,
inventario) necesita documentos con partidas, totales calculados, folios, reglas de negocio
y procesos de aprobación. Todo debe seguir declarándose como **metadatos**, sin código por
módulo (constitución I.1), para que después la IA pueda generarlo.

## Historias de usuario

**H1 — Maestro-detalle.** Como *modelador*, quiero que un documento (Orden de compra) tenga
partidas (renglones) que se capturen dentro del mismo formulario y se guarden juntas.
- CA1: Las partidas se guardan, editan y eliminan en la misma transacción que el documento.
- CA2: Una partida no se puede editar fuera de su documento.

**H2 — Fórmulas.** Como *modelador*, quiero campos calculados (`Cantidad * Precio`,
`SUM(Partidas.Importe)`) que el usuario ve en vivo y que el servidor recalcula al guardar.
- CA1: El valor guardado siempre es el que calcula el servidor, aunque el cliente envíe otro.
- CA2: El lenguaje de fórmulas no permite ejecutar código ni SQL.

**H3 — Folios.** Como *usuario*, quiero que cada documento reciba un folio consecutivo único
(`OC-000001`) sin huecos por concurrencia.

**H4 — Valores por defecto.** Como *modelador*, quiero valores sugeridos: fecha de hoy,
moneda MXN o el costo del producto elegido (`Producto.CostoUltimo`). El valor sugerido se usa
cuando el campo está vacío.

**H5 — Reglas de negocio.** Como *modelador*, quiero declarar validaciones con mensaje propio:
"la fecha de entrega no puede ser anterior a la de la orden" o "el proveedor debe estar activo".

**H6 — Procesos.** Como *modelador*, quiero definir estados (Borrador, Por autorizar,
Autorizada…) y transiciones con:
- roles que pueden ejecutarlas;
- condiciones con mensaje, como "más de $50,000 requiere Director";
- comentario o campos obligatorios, como el motivo de rechazo;
- campos que se llenan solos, como quién autorizó y cuándo;
- estados en los que el documento ya no se puede editar.

Criterios de aceptación:
- CA1: El usuario solo ve los botones de las transiciones que puede ejecutar, y si una está
  bloqueada sabe por qué.
- CA2: Cada transición queda en un historial con usuario, fecha y comentario.
- CA3: En un estado no editable el servidor rechaza cambios aunque se llame a la API directo.

**H7 — Módulos instalables.** Como *administrador*, quiero instalar un módulo completo
(catálogos, documentos, procesos, roles, permisos y datos iniciales) con vista previa y un clic.

## Fuera de alcance
- Automatizaciones entre documentos (p. ej. que una recepción actualice la orden): spec futura.
- Editor visual de procesos (se editan como JSON validado; la IA los generará en la spec de IA).

## CLARIFY — supuestos tomados (corregir si no aplican)
- Las fórmulas se evalúan en memoria en el servidor (C#), nunca se traducen a SQL.
- "Vacío" activa el valor por defecto; si el usuario borra un valor sugerido, se vuelve a sugerir.
- Las partidas heredan los permisos del documento.
