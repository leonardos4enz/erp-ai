# Spec 023 — Capacidades del motor: listas que se fusionan, reglas de aviso y rutas de tres niveles

**Estado:** Implementado · **Depende de:** 002, 007, 008, 009, 016, 017, 022 · **Origen:** pendientes de la
[spec 022](../022-costeo-2/spec.md) y plan del dueño (2026-10-24): 023 capacidades del motor → 024 Ventas → 025 CFDI 4.0.

## Problema
Tres límites del motor que hoy obligan a los módulos a rodeos y que el módulo de Ventas (024) necesitará:

1. **Un módulo no puede extender con seguridad los catálogos de otro.** La 022 dio `merge` a `setRules` (como a
   `setAutomations` en la 020), pero el resto de las listas del catálogo (vistas, reportes, webhooks, tareas) solo se
   reemplazan completas. Costeo 1.1.0 instala la vista «Pólizas pendientes» en *Recepción* (de Compras) reemplazando todas
   sus vistas. Además, la vista previa del asistente avisa «quita reglas existentes» ante un `setRules` con `merge` aunque no
   quite nada, y un paquete de Entornos que agrega campos, reglas o automatizaciones a un catálogo que **no crea** pierde
   esas operaciones en silencio: el diff solo mira los catálogos que el paquete crea.
2. **Toda regla bloquea.** No hay forma de *avisar sin impedir*: la política 7 de la 022 (recepción en un almacén sin
   cuentas) solo puede «bloquear» o «registrar pendiente» sin decir nada a quien confirma.
3. **Las expresiones llegan a un nivel.** `Ref.Campo` (o `Documento.Campo` desde una partida). Una partida de recepción no
   puede leer la moneda de su orden (`Recepcion.OrdenCompra.Moneda.Clave`); por eso las recepciones en borrador creadas
   antes de Inventario 1.5.0 (sin `Moneda`) calculan su costo con el respaldo de la orden (riesgo residual de la 022).

Verificación en el código (2026-10-24, `HEAD` = 8673286):

| # | Dónde | Lo que pasa hoy |
|---|---|---|
| 1a | `PlanCompiler.SetViews/SetReports/SetWebhooks/SetSchedules` | Reemplazan la lista completa; no hay `merge` |
| 1b | `PlanWarnings.For` (`SetRulesOp`) | No mira `Merge`: con `merge` reporta como quitadas las reglas que conserva |
| 1c | `ModelDiff.Scope`/`Desired` | Solo los catálogos que el paquete crea: un `addField`/`setRules`/`setAutomations` sobre otro catálogo se descarta |
| 1d | `costeo.json` 1.1.0 | `setViews Recepcion` sin `merge` (catálogo de Compras) |
| 2 | `BusinessRule`, `RecordService.RuleErrors` | Toda regla incumplida es error 400 |
| 3 | `ExprParser.ParsePrimary` (`PathRef(Head, Tail)`) | Un punto como máximo; `RecordFilter.Column` traduce un nivel |

## Historias

**H1 — Un módulo extiende las listas del catálogo de otro sin borrarlas.** Como *autor de un módulo*, quiero agregar o
reemplazar por nombre una regla, vista, reporte, webhook o tarea en un catálogo ajeno sin tocar lo demás.
- CA1 (motor): `setViews`, `setReports`, `setWebhooks` y `setSchedules` aceptan `merge` con el contrato de `setRules` y
  `setAutomations`: agrega o reemplaza por nombre y conserva las demás; los topes se revisan sobre el resultado.
- CA2: la vista previa (asistente y modelador) no reporta como quitado lo que un `merge` conserva; sí lo que un reemplazo
  quita.
- CA3 (Entornos 009/017): un paquete que agrega campos, reglas, automatizaciones o vistas a un catálogo que no crea llega
  al destino (antes se descartaba), y revertirlo devuelve ese catálogo a como estaba.
- CA4 (módulos): ningún módulo reemplaza una lista de un catálogo de otro módulo: `costeo.json` instala «Pólizas
  pendientes» con `merge`. Una prueba de los paquetes lo garantiza para los que vengan.

**H2 — Reglas de aviso.** Como *responsable de un proceso*, quiero reglas que avisen sin impedir guardar, y decidir si una
acción del proceso pide confirmarlas.
- CA1 (motor): una regla declara `severity`: `error` (omitido, como hasta hoy) o `warning`. Un aviso incumplido no impide
  guardar, importar ni ejecutar una automatización.
- CA2: el formulario muestra los avisos vigentes mientras se captura (`/compute`) y al abrir o guardar el registro (la
  respuesta de la API los trae en `_warnings`); la importación los lista por fila sin rechazar la fila.
- CA3: una acción del proceso declara qué hacer si al ejecutarla quedan avisos (`onWarning`): `confirm` (omitido) — la
  persona los ve en el diálogo y confirma explícitamente; la API exige la lista de avisos aceptados —, `block` — no se
  ejecuta, como un error — o `allow` — se ejecuta sin preguntar.
- CA4: todo guardado o acción que sigue adelante con avisos los deja en la auditoría del registro.
- CA5: un aviso cuya regla lee un campo que el usuario no ve no se le muestra ni se le pide confirmar (se audita igual).
- CA6 (caso 7 de la 022): la política «póliza sin cuentas» gana la opción **avisar**: con `severity: warning` en la regla
  `PolizaConCuentas`, confirmar la recepción pide confirmación, se confirma, queda *Póliza pendiente* y el aviso aceptado
  queda en la auditoría.

**H3 — Rutas de tres niveles en expresiones.** Como *modelador*, quiero leer `Recepcion.OrdenCompra.Moneda.Clave`.
- CA1 (motor): una ruta tiene hasta **4 segmentos (3 saltos)**; cada salto intermedio es una referencia, el documento
  (padre) o, solo en el penúltimo, un usuario. Vale en fórmulas, valores por defecto, reglas, condiciones de transición y
  de layout, asignaciones y automatizaciones, y se valida contra los metadatos al previsualizar el plan.
- CA2: en los filtros que el motor traduce a SQL (acumulados `rollup`, tareas programadas `where`) la ruta se traduce con
  subconsultas por `Id` y `IsDeleted = 0`, con la misma semántica que en memoria (un registro borrado o vacío da nulo).
- CA3 (seguridad): una condición de layout o un filtro de reporte que lee por la ruta un campo oculto **en cualquier
  salto** se trata como hoy un campo oculto de un nivel (`ReadsHidden`). Las reglas de acceso (`where` de seguridad) y los
  filtros de reporte siguen sin rutas.
- CA4 (pendiente 4 de la 022, Inventario 1.7.0): el costo de una partida de recepción usa la moneda de la orden
  (`Recepcion.OrdenCompra.Moneda.Clave`) cuando la recepción no la tiene; una recepción sin `Moneda` de una orden en dólares
  con moneda base distinta de MXN se valúa al tipo de cambio, no queda sin costo.

**H4 — Llegan como actualización.** Inventario 1.7.0 se instala sobre 1.6.1 sin reinstalar ni perder datos; los paquetes
existentes siguen compilando y el motor se comporta igual que antes cuando no se usan las opciones nuevas.

## Decisiones (CLARIFY)
La sesión que abrió la spec (por encargo del dueño) delegó estas decisiones; quedan registradas aquí.

- **Qué listas llevan `merge` (1).** Las cuatro restantes: vistas y reportes (Costeo ya agrega una vista ajena; Ventas
  agregará reportes a Producto o Inventario), webhooks y tareas (por uniformidad: el mismo contrato en las seis listas
  evita que un autor tenga que recordar cuál sí y cuál no). No llevan `merge`: `setLayout` (un formulario es un todo
  ordenado; fusionar secciones por nombre produciría formularios incoherentes), `setSecurity` (una política es un todo y
  cambiarla ya pide confirmación) y `setWorkflow` (un proceso es un todo). Quitar un elemento con `merge` no se ofrece en
  esta spec: se hace con el reemplazo completo (la vista previa dice qué quita).
- **El diff de Entornos sigue emitiendo listas completas** (estado deseado = estado completo); lo que se corrige es el
  alcance: los catálogos que el paquete *extiende* (tiene operaciones sobre ellos sin crearlos) entran al estado deseado,
  a la instantánea y a la reversión. Los permisos de un paquete siguen limitados a los catálogos que crea.
- **`costeo.json` 1.1.0 se corrige en su lugar** (`setViews` → `merge`), no con una actualización nueva: una actualización
  posterior no puede deshacer lo que la 1.1.0 reemplaza en una instalación nueva. El cambio es neutro en toda instalación
  existente (la 1.1.0 ya se aplicó) y en las nuevas de hoy (Compras no tiene vistas en *Recepción*). Las reglas que un
  módulo agrega a catálogos de otro ya usan `merge` (Inventario 1.6.0 y Costeo 1.1.0); las actualizaciones viejas de
  Compras que reemplazan reglas de *RecepcionPartida* (1.2.0) no pueden correr después de Inventario (su cadena no compila
  sobre Compras anterior a 1.6.0). Convención para los que vengan: el dueño de un catálogo que otros extienden también usa
  `merge`; la prueba de paquetes (H1 CA4) lo exige para cualquier lista de un catálogo ajeno.
- **Avisos: dónde se ven (2).** En `/compute` (`warnings`), en la salida del registro (`_warnings`: al abrirlo y en la
  respuesta de crear, editar y ejecutar una acción), en la importación (`warnings` por fila) y en la auditoría
  (`_warnings` en el «después»). No en listados ni en webhooks (evaluarlos por fila costaría lecturas y no los pide nadie).
- **Avisos en acciones del proceso (2).** `onWarning` por transición; omitido = `confirm`, porque un aviso que nadie lee
  no sirve. La confirmación es **de los avisos concretos**: la API responde **428** con los avisos y la persona reenvía
  `acceptWarnings` con esos mensajes; si al reenviar aparece un aviso distinto, se vuelve a pedir. Una automatización que
  ejecuta una transición no tiene a quién preguntar: `confirm` y `allow` siguen (y se audita); `block` la detiene. Guardar
  (crear o editar) nunca pide confirmación: el aviso se ve en vivo mientras se captura.
- **Política 7 (2, CA6).** El valor por defecto sigue siendo «bloquear» (aceptado por el dueño en la 022). «Avisar» es
  cambiar `severity` de la regla `PolizaConCuentas` a `warning`; sigue habiendo una sola palanca por opción: bloquear
  (regla `error`), avisar (regla `warning`: confirmar y queda *Póliza pendiente*) o registrar en silencio (sin la regla).
- **Tope de la ruta (3).** 4 segmentos fijos (3 saltos), suficientes para documento → documento → catálogo → campo. No es
  configurable: cada salto es una lectura por `Id` (con la caché por operación de `RelatedLoader`), y el tope acota el costo
  de una fórmula por partida. `SUM`/`COUNT` siguen leyendo un campo de la partida (un nivel).
- **Seguridad de la ruta (3).** Se protege lo que el motor ya protegía, en cada salto: seguridad por campo (`ReadsHidden`
  en layouts, reportes y ahora avisos). El registro del primer salto ya debe ser visible al elegirlo (spec 006); los saltos
  siguientes son los registros a los que apunta ese registro (como la etiqueta de una referencia), no un registro que el
  usuario elige. Las reglas de acceso por registro siguen sin rutas (se evalúan por fila en SQL sobre el propio catálogo).

## Fuera de alcance
El módulo de Ventas (024), CFDI 4.0 con PAC (025) y, después, pólizas en moneda extranjera, revaluación de existencias y
reversa de pólizas con otra póliza (anotados en el roadmap en ese orden). También: quitar elementos con `merge`, rutas en
reportes agrupados y en reglas de acceso, y avisos en listados.
