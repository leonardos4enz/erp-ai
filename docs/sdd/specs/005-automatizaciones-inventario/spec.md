# Spec 005 — Automatizaciones entre registros, cantidades e Inventario

**Estado:** Implementado · **Depende de:** 002, 003, 004 · **Origen:** auditoría externa ([roadmap](../../roadmap.md#005--automatizaciones-y-cantidades-prioridad-1))

## Problema
1. Una transición solo modifica su propio registro. Confirmar una recepción no toca la orden de compra:
   alguien tiene que marcarla «Recibida» a mano, aunque falten piezas.
2. La orden no sabe cuánto se ha recibido ni cuánto falta, por partida.
3. No hay inventario: la mercancía recibida no aparece en ninguna existencia.
4. Resolverlo con código por módulo viola la constitución (I.1). Hace falta una capacidad **genérica**
   del motor: automatizaciones declaradas como metadatos.

## Historias

**H1 — Automatizaciones declarativas.** Como *modelador*, quiero declarar «cuando pase X en este
catálogo, haz Y en otros registros», sin programar.
- CA1: Eventos: **al crear**, **al editar**, **al entrar a un estado** y **al ejecutar una transición**,
  con condición opcional (`when`).
- CA2: Acciones: **asignar campos** de un registro relacionado, **ejecutar una transición** en un
  registro relacionado, **crear registros** y **acumular cantidades** (suma sobre registros relacionados,
  no solo partidas propias). Cada acción puede repetirse **por partida** del documento.
- CA3: Todo ocurre en la **misma transacción** que el evento: si una acción falla, no queda nada a medias
  y el usuario ve un mensaje que nombra la automatización.
- CA4: Un plan con automatizaciones inválidas (campos, estados, transiciones o expresiones inexistentes,
  o un ciclo entre catálogos) se rechaza en la vista previa, como las fórmulas.
- CA5: Aunque una cadena escape a la validación estática, el motor la corta al pasar de **5 niveles**.
- CA6: Lo que hace una automatización queda en la auditoría y, si cambia un estado, en el historial
  del documento con el comentario «Automática: …».
- CA7: El modelador edita las automatizaciones como JSON validado, igual que reglas y procesos.

**H2 — Cantidades recibidas y pendientes (Compras 1.2.0).** Como *comprador*, quiero ver por partida
cuánto se ha recibido y cuánto falta.
- CA1: La partida de la OC gana *Cantidad recibida* (la asigna el sistema) y *Pendiente* (fórmula).
  La orden muestra el total pendiente por recibir.
- CA2: La partida de la recepción se liga a una partida de la orden. El buscador solo ofrece partidas de
  la orden elegida y sugiere el producto y la cantidad pendiente.
- CA3: Confirmar una recepción acumula lo recibido en cada partida de la OC y mueve la orden sola a
  «Recibida parcial» o «Recibida». Las personas ya no pueden marcarla recibida a mano.
- CA4: No se puede recibir más de lo pendiente, ni una partida de otra orden.
- CA5: Al recibir, el costo último del producto se actualiza con el precio de la orden.
- CA6: Llega como actualización 1.1.0 → 1.2.0 sin reinstalar ni perder datos, y los campos calculados
  nuevos se llenan también en los registros existentes.

**H3 — Inventario 1.0.0.** Como *almacenista*, quiero existencias por almacén y producto que se muevan
solas con las recepciones, y registrar salidas, ajustes y traspasos.
- CA1: Confirmar una recepción genera un movimiento de **entrada** por partida y actualiza la existencia
  del almacén de la recepción (la crea si no existía).
- CA2: Salidas y ajustes manuales actualizan la existencia. Nunca queda negativa: la operación se rechaza.
- CA3: Un traspaso aplicado genera una salida en el almacén origen y una entrada en el destino.
- CA4: Los movimientos no se editan ni se eliminan; las existencias no se capturan a mano.
- CA5: Solo el supervisor de inventario (o un administrador) registra ajustes.
- CA6: El módulo declara que requiere Compras; sin él, no se instala y lo explica.

## Decisiones (CLARIFY)
Sesión sin interlocutor disponible: se tomaron estas decisiones y se registran para revisión.
- **Acumular = recalcular, no sumar incrementos.** La acción `rollup` vuelve a sumar desde los registros
  de origen (fuente de verdad). Es idempotente: repetirla o cancelar un origen no descuadra el total.
- **Las automatizaciones actúan con autoridad del motor.** El evento ya pasó los permisos del usuario;
  las acciones no vuelven a revisar roles en el destino ni el estado editable (una OC «Enviada» no es
  editable por personas, pero el motor sí acumula en ella). Sí se respetan las **reglas** del registro
  modificado y las condiciones propias de la transición destino.
- **Transiciones «del sistema»** (`system: true`): solo las ejecuta una automatización; no aparecen como
  botón. Así «Recibida» deja de ser manual en Compras 1.2.0.
- «Al entrar a un estado» se dispara por transiciones (de personas o automáticas), no al crear: para el
  alta existe «al crear».
- Las automatizaciones se declaran en el catálogo **que origina** el evento. Un módulo puede agregar las
  suyas a un catálogo de otro módulo (`merge: true`) sin borrar las existentes: Inventario agrega la
  entrada de almacén a la recepción de Compras.
- Eliminar un registro no dispara automatizaciones (fuera de alcance; los documentos que importan se
  cancelan, no se eliminan).
- La liga recepción → partida de OC se valida con reglas del paquete, no en el buscador: el filtro del
  buscador es solo comodidad.
- Recepciones confirmadas antes de 1.2.0 no tienen liga a la partida: sus cantidades no se reconstruyen.
