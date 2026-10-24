# Spec 020 — Contabilidad y costeo (promedio y FIFO)

**Estado:** Implementado · **Depende de:** 005, 010, 016 · **Origen:** [roadmap](../../roadmap.md#020--contabilidad-y-costeo-promedio-y-fifo)

## Problema
1. Inventario (005) lleva cantidades, no valor: no se sabe cuánto vale lo que hay en cada almacén ni cuánto costó lo que
   salió. «Costo último» (Compras) es solo una sugerencia de precio.
2. No hay contabilidad: ni catálogo de cuentas, ni pólizas, ni periodos que se cierren. Una recepción no deja rastro
   contable.
3. Resolverlo con código por módulo viola la constitución (I.1). Faltan tres capacidades **genéricas** del motor:
   capas de costo con promedio ponderado y FIFO, partida doble con cuadre obligatorio y periodos, y que una
   automatización pueda crear un documento **con partidas** (una póliza).

## Historias

**H1 — Partida doble (motor).** Como *modelador*, quiero declarar que un documento con partidas es un libro contable,
para que el motor garantice el cuadre y respete los periodos cerrados por cualquier vía.
- CA1: Un plan declara el libro de un catálogo (`setLedger`): campo de partidas, campo de cargo y de abono, campo de
  fecha, estados contabilizados y, opcionalmente, el catálogo de periodos (inicio, fin y la condición de «abierto»).
- CA2: Un documento en un estado contabilizado **cuadra**: al menos dos partidas, cada una con cargo **o** abono (ni
  ambos, ni ninguno, ni negativos) y la suma de cargos igual a la de abonos. Si no, se rechaza por cualquier vía (UI,
  API, importación o automatización) con un mensaje que da los dos totales y la diferencia.
- CA3: Un documento contabilizado (antes o después del cambio) debe caer en un periodo **abierto**: sin periodo para
  su fecha, o con el periodo cerrado, se rechaza con un mensaje que nombra la fecha y el periodo. Esto incluye
  cancelar una póliza aplicada de un periodo cerrado. Los borradores no se restringen.
- CA4: Dos periodos de la misma empresa no se traslapan: el alta o el cambio que lo provocaría se rechaza.
- CA5: Un libro mal declarado (campos inexistentes o de otro tipo, estados que no existen, condición inválida) se
  rechaza en la vista previa del plan.
- CA6: Cerrar un periodo mientras se aplica una póliza de esa fecha no deja una póliza aplicada en un periodo cerrado.

**H2 — Capas de costo (motor).** Como *modelador*, quiero valuar movimientos con costo promedio ponderado o FIFO sin
programar.
- CA1: Una acción de automatización `cost` lleva capas por llave (p. ej. almacén + producto): una entrada agrega una
  capa con su costo; una salida consume capas. Con **promedio** las capas de la llave se funden en una; con **FIFO**
  se consumen de la más antigua a la más nueva. El método es una expresión por registro (p. ej. el del producto).
- CA2: La acción escribe en el movimiento su costo total (con signo) y su costo unitario, y en el registro de saldo
  (p. ej. la existencia) el valor que queda en las capas.
- CA3: Una entrada sin costo se valúa al costo promedio vigente de la llave (cero si no hay capas). Una salida mayor
  que lo valuado valúa el faltante al costo de respaldo declarado (o cero) y no deja capas negativas.
- CA4: `UNITCOST(catálogo, cantidad, llave…)` da el costo unitario de las siguientes *cantidad* unidades que saldrían
  (para valuar un traspaso al costo de origen).
- CA5: Las capas son por empresa en un catálogo por empresa; dos movimientos simultáneos de la misma llave no se
  cruzan (bloqueo por llave).
- CA6: Una acción `cost` inválida (catálogo, llave, campos, expresiones) se rechaza en la vista previa.

**H3 — Documentos con partidas desde una automatización (motor).**
- CA1: La acción `create` puede declarar partidas del documento nuevo (`createLines`: campo de partidas, condición y
  asignaciones) y una transición que se ejecuta en cuanto se crea (`transition`).
- CA2: El documento se crea con las mismas validaciones que una captura (obligatorios, reglas, libro) y la transición
  con las suyas; si algo falla, se revierte todo y el mensaje nombra la automatización.
- CA3: Partidas mal declaradas (campo que no es de partidas, campos inexistentes, obligatorios sin asignar) se
  rechazan en la vista previa.

**H4 — Módulo Contabilidad 1.0.0.** Como *contador*, quiero un catálogo de cuentas, pólizas y periodos con cierre.
- CA1: Cuentas contables por empresa (código único, nombre, tipo, naturaleza, activa) con cargos, abonos y saldo que
  calcula el sistema. Al instalar se crea un catálogo mínimo en la empresa de quien instala.
- CA2: Pólizas por empresa (folio, fecha, tipo, concepto, referencia, origen) con partidas (cuenta, concepto, cargo,
  abono) y totales. Proceso *Borrador → Aplicada*, y *Cancelada* (con comentario) desde ambos. Son el libro: el cuadre
  y el periodo los garantiza el motor.
- CA3: Aplicar o cancelar una póliza recalcula cargos, abonos y saldo de sus cuentas (solo pólizas aplicadas).
- CA4: Periodos contables por empresa (nombre, inicio, fin): *Abierto → Cerrado* por el contador; reabrir solo el
  administrador y con comentario.
- CA5: Reporte «Balanza de comprobación» y una tarea programada «Cerrar periodos vencidos» que llega en pausa (016).
- CA6: Roles *Contador* (todo lo anterior) y *Cierre contable* (rol de servicio de la tarea).

**H5 — Costo de entrada (Inventario 1.5.0) y módulo Costeo 1.0.0.** Como *almacenista y contador*, quiero que cada
movimiento tenga su costo, las existencias su valor y cada recepción su póliza.
- CA1 (Inventario 1.5.0, actualización): la partida de la recepción calcula su costo unitario en **moneda base** con
  `CONVERT` a la fecha de la recepción (si no hay tipo de cambio, el de la orden); el movimiento de entrada lo recibe.
  Una entrada o un ajuste manual pueden capturar su costo unitario. Un traspaso entra al destino al costo de origen.
- CA2 (Costeo): cada producto tiene método de costeo (Promedio por defecto, o FIFO). Cada movimiento se valúa con capas
  por almacén y producto; la existencia muestra su valor y su costo promedio.
- CA3 (Costeo): confirmar una recepción en un almacén con cuenta de inventario y cuenta de entradas por facturar crea
  una póliza aplicada (cargo a inventario, abono a entradas por facturar, por el costo total en moneda base). Sin
  cuentas, la recepción se valúa pero no genera póliza.
- CA4: Costeo requiere Inventario y Contabilidad; sin ellos no se instala y lo explica. Las actualizaciones llegan sin
  reinstalar ni perder datos.

## Decisiones (CLARIFY)
Sesión sin interlocutor disponible: se tomaron estas decisiones y se registran para revisión.
- **El libro es una capacidad del motor, no una regla.** Una regla `SUM(Cargo) = SUM(Abono)` la puede quitar
  cualquiera con el modelador y no sabe de periodos. `setLedger` hace que el motor exija el cuadre en toda escritura
  de un estado contabilizado y que busque el periodo por fecha (una regla no puede consultar otro catálogo).
- **Estados contabilizados.** El cuadre se exige al entrar o guardar en ellos; los borradores pueden quedar
  descuadrados mientras se capturan. El periodo se revisa con el estado y la fecha de **antes** y de **después**: no se
  aplica en un periodo cerrado ni se cancela o modifica lo aplicado ahí. Sin proceso, todo estado está contabilizado.
- **Periodo por empresa.** Si el catálogo de periodos es por empresa, se busca el de la empresa del documento.
  Exactamente un periodo debe contener la fecha; dos se reportan como traslape (y se impide crearlos).
- **El cierre se serializa con la aplicación.** El motor lee el periodo con bloqueo compartido hasta el final de la
  transacción: cerrar espera a que termine la póliza en curso, y la siguiente ya lo ve cerrado.
- **Una póliza cancelada no se revierte con otra póliza** (fuera de alcance): deja de contar en los saldos. Las
  pólizas aplicadas no se editan ni se eliminan.
- **Moneda.** Pólizas y saldos en la moneda base de su empresa. La conversión ocurre antes, al valuar la recepción
  (`CONVERT` de la moneda de la orden a la base, a la fecha de la recepción). Pólizas en moneda extranjera: fuera de alcance.
- **Capas en una tabla del motor** (`meta.CostLayer`), no en un catálogo: son estado interno del costeo, como la
  bandeja de automatizaciones. Lo visible está en catálogos: costo del movimiento y valor de la existencia.
- **Promedio = una sola capa por llave.** Una entrada promedio funde las capas abiertas en una nueva; una salida toma
  la parte proporcional (todo, si sale todo). Cambiar el método de un producto aplica desde el siguiente movimiento.
- **Redondeo.** El costo de cada consumo se redondea a la escala del campo de costo del movimiento y se descuenta
  exacto de la capa: la suma de costos de los movimientos y el valor de las capas no se separan.
- **Existencias anteriores al costeo.** No tienen capas: su valor queda vacío hasta su primer movimiento y después
  refleja solo lo valuado. Su salida se valúa al costo de respaldo del módulo (el costo último del producto, convertido a
  la moneda base) y no deja capas negativas. Para valuarlas con exactitud: un ajuste negativo y
  otro positivo con costo, o una entrada de apertura.
- **Traspasos.** La entrada al destino se crea antes que la salida del origen y se valúa con `UNITCOST` sobre las capas
  del origen, redondeado a 4 decimales (puede diferir centavos de la salida). Dos partidas del mismo producto en un
  traspaso FIFO leen las mismas capas: riesgo residual documentado.
- **El costo de entrada vive en Inventario (1.5.0)** —la recepción ya genera la entrada— y la valuación y la póliza en
  Costeo. Inventario agrega campos a la recepción de Compras, como ya agregaba su automatización; Compras no cambia.
- **Póliza de recepción en una sola partida por lado** (por el costo total), con referencia al folio de la recepción.
  Una por producto exige rutas de tres niveles en expresiones (fuera de alcance).
- **Sin cuentas configuradas no hay póliza** (la recepción no se bloquea): el almacén elige sus cuentas. Así instalar
  Costeo no detiene la operación de almacenes que aún no se configuran.
- **Salidas sin póliza** (costo de ventas): fuera de alcance; el costo queda en cada movimiento para usarlo después.
- **La tarea de cierre llega en pausa** y cierra periodos abiertos cuya fecha fin pasó hace más de 4 días.
- **Catálogo inicial mínimo** de cuentas en la empresa de quien instala; cada empresa puede agregar las suyas.
