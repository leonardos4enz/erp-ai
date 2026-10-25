# Spec 031 — Endurecimiento 4: lo que se le dice a una persona, con su alcance

**Estado:** Implementado · **Depende de:** 006, 015, 020, 023, 027, 029, 030 · **Origen:** la **auditoría externa 5**
(2026-10-25, solo lectura sobre la 029, commit `6535085`): 5 defectos (uno alto) y 9 observaciones sobre el canario y el
inventario de rutas. La 030 dejó planeada, antes de Ventas, «la auditoría adelantada de la 029–030 y una spec con lo que
encuentre»; esta es esa spec.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | A-01 (alta) · `CreateInTransactionAsync` e `InsertDraftAsync` (`RecordService.cs`) crean `RelatedLoader` sin `viewer`; `ImportAsync` devuelve los avisos de ese cálculo filtrados solo por seguridad de campo (`VisibleWarnings`) | `POST /api/data/{e}/import?dryRun=true` es un oráculo de un bit por fila sobre registros fuera de alcance: con los datos del canario, «el dato secreto está vacío» sale en `/compute` (029) y no en la importación. Igual en el 428/400 de una transición (`ApplyTransitionAsync`, `CheckWarnings`) |
| 2 | A-03 (media) · `modules/costeo.json` agrega a `Existencia` `Valor`, `CantidadCosteada` y `CostoPromedio` sin seguridad por campo; «Integración de tienda» (`inventario.json` 1.3.0) lee `Existencia` | Una clave de API u OAuth de la tienda lee el valor y el costo promedio por `/api/data/Existencia` y `/odata/Existencia` (el webhook sí los recorta). R-029-6 decía solo «el almacenista los ve» |
| 3 | A-02 (media) · `WhyNotApprover` (`SecurityApproval.cs`) en `UserUpdate` y `TeamUpdate` solo mira roles y miembros | Quien revisa aprueba pasar a un usuario o equipo (con sus registros) a su unidad y, con alcance `unit`/`unitTree`, gana verlos: el caso que la 029 cerró para `unit.move` (D-03) |
| 4 | A-04 (baja) · `CheckWarnings` con `Block` y sin aceptados lanza `ValidationFailed` con **todos** los avisos; `FirstMessage` (`RecordService.Automations.cs`) los une en el 400 | Una automatización síncrona que ejecuta una transición con `onWarning: block` le dice a quien guardó el aviso que lee un campo oculto (D-08 por otro camino) |
| 5 | A-05 (baja) · `PrintChoicesAsync` y `PrintAsync` (`RecordService.Print.cs`) crean el cargador sin usuario | `UNITCOST` en una plantilla PDF lee las capas de un catálogo de saldo que el usuario tiene restringido (029 F-C lo cortó en `/compute`) |
| 6 | Canario e inventario | `expand()` pide con el texto literal un parámetro que no conoce (`{code}`) y la ruta pasa; los POST son una lista a mano; no cubre XLSX, `lookup` con `filterField` ni ordenar por fórmula oculta; sin `FallbackPolicy`, una ruta mapeada fuera de `MapErpRoutes` sería anónima y no saldría en `rutas.md`; tableros, reportes y OData no están marcados como costosos |

## Principio
Ninguna capacidad nueva de negocio. Una regla del motor, aplicada en todos los caminos: **lo que se le dice o se le pregunta
a una persona se evalúa con su alcance** (el cargador con `viewer` de la 029, en cada salto) y sin lo que lee campos ocultos;
**lo que se guarda y decide** (valores, auditoría, si un bloqueo bloquea) sigue con la autoridad del motor. Los costos se
protegen con metadatos (`upgrades` de Costeo con `setSecurity`), no con código por módulo. El inventario de rutas se hace
cumplir con la política por omisión de ASP.NET (deniega lo no declarado).

## Historias

**H1 — Avisos de la importación y de las transiciones con el alcance de quien los recibe (A-01).** Como *usuario con
alcance limitado*, quiero que la importación y las transiciones me digan lo mismo que el formulario, para que no revelen
registros que no puedo ver.
- CA1: la vista previa (y la importación) devuelve por fila los avisos que `/compute` daría al mismo borrador: calculados con
  el cargador del usuario, sin los que leen ocultos. La auditoría guarda los del motor.
- CA2: con `onWarning: confirm`, el 428 pregunta por los avisos que la persona ve (aunque el motor no tenga ninguno: la
  pregunta no revela nada); los que solo existen con la autoridad del motor no se preguntan y quedan en la auditoría.
- CA3: con `onWarning: block` el motor decide si bloquea; el 400 lista lo que la persona ve, o un mensaje genérico. Nunca
  la intersección con los del motor (revelaría un bit por aviso).
- CA4 (canario): el aviso del canario en `/import` coincide con el de `/compute`.

**H2 — Costos fuera del alcance de las cuentas de servicio (A-03).** Como *dueño*, quiero que una credencial de terceros no
lea costos, para que la integración con la tienda no exponga el valor del inventario.
- CA1: Costeo 1.2.0 (`upgrades`) declara seguridad por campo: `Existencia.Valor`, `CantidadCosteada` y `CostoPromedio`,
  `MovimientoInventario.CostoUnitario` y `CostoTotal`, y `TraspasoPartida.CostoOrigen` solo los leen *Supervisor de
  inventario*, *Almacenista* y *Contador* (y el superusuario).
- CA2: con la clave de «Integración de tienda», `/api/data/Existencia`, `/odata/Existencia` y `$metadata` no traen esos
  campos (ni `CosteoIncompleto`, que se calcula con ellos); el supervisor los sigue viendo.

**H3 — Quien aprueba no gana al mover a alguien a su unidad (A-02).** Como *administrador que revisa*, quiero que no pueda
aprobar pasar a un usuario o a un equipo a mi unidad (o a una que cuelga de ella), para que nadie apruebe lo que lo beneficia.
- CA1: en `user.update` y `team.update`, si la unidad nueva queda bajo la de quien revisa y la anterior no, lo aprueba otro
  (403 con la razón; `canApprove` falso). Al crear, el principal aún no tiene registros: no aplica.

**H4 — El error de una automatización no dice lo oculto (A-04).** Como *rol al que se le oculta un campo*, quiero que el
error de una automatización que bloquea por avisos tampoco me diga el que lo lee.
- CA1: la transición que ejecuta una automatización con `block` dice los avisos que ve quien disparó el evento (o un mensaje
  genérico si no ve el registro); al administrador, todos los que ve.

**H5 — `UNITCOST` en el PDF con el alcance de quien imprime (A-05).** Como *usuario con el catálogo de saldo restringido*,
quiero que el PDF no me imprima su costo.
- CA1: el PDF y la elección de plantillas usan el cargador del usuario: `UNITCOST` de un catálogo de saldo con permisos
  por registro restringidos para él queda vacío; el administrador lo ve.

**H6 — Canario e inventario más estrictos (observaciones de la auditoría).** Como *auditor*, quiero que una ruta nueva no
pueda pasar sin recorrerse ni sin declarar su capacidad.
- CA1: el canario falla si una ruta del inventario tiene un parámetro que no sabe llenar; recorre las rutas que no son GET a
  partir del inventario (efecto «ninguno» o «datos», que el lector puede llamar) y falla si una no tiene su llamada; cubre
  XLSX (descomprimido), `lookup` con `filterField` y ordenar por fórmulas y campos ocultos (el orden no cambia).
- CA2: `FallbackPolicy` niega toda ruta sin autorización declarada, también al superusuario; la SPA (`MapFallbackToFile`)
  se declara anónima. (Una ruta de un grupo con `.RequireAuthorization()` y sin capacidad la sigue deteniendo la prueba de
  inventario, no el pipeline: R-031-5.)
- CA3: las rutas de la aplicación real (`Program`, no solo `MapErpRoutes`) están todas en `rutas.md`.
- CA4: `GET /api/dashboards/{name}`, `/api/reports/{entity}/{name}` y `/odata/{segment}` son costosas con límite por
  usuario (`Security:AggregatesPerMinute`, 120).

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (6, no) · más de ~2 500 líneas de producto
   (~200, no) · seguridad o concurrencia junto con UI nueva (no hay UI) · más de una pantalla principal (ninguna). → **No se
   divide.**
2. **Separar «lo que se guarda» de «lo que se le dice».** Se descartó evaluar todo con el alcance de la persona: los valores
   guardados, las reglas de error y si un bloqueo bloquea deben valer igual para todos (029 decisión 2 y 6). Se descartó
   también mostrar la intersección de los avisos del motor con los visibles: cada aviso de la intersección es un bit sobre
   el registro fuera de alcance. Se decide: **lo que se le dice = lo que `/compute` le diría** (se recalcula una copia del
   borrador con el cargador del usuario). Costo: una persona puede ver un aviso que, con los datos completos, no aplica
   (lo mismo que ya ve en el formulario) y un bloqueo puede citarlo. Que bloquee o no sigue siendo un bit del motor
   (R-029-3, ampliado).
3. **Confirmar ante avisos que solo ve la persona.** Se pregunta por los avisos que ve aunque el motor no tenga ninguno:
   preguntar solo cuando el motor tiene avisos revelaría si los tiene. El costo es una confirmación de más en un caso raro.
4. **Quién lee los costos (A-03).** El almacenista **sí** los lee: captura entradas y ajustes con costo unitario
   (`valuacion-politicas`), y quitárselos cambia el proceso del módulo. Lo que se cierra es la salida por cualquier otro
   rol, en particular la cuenta de servicio. Que el almacenista deduzca el precio de compra del costo de entrada sigue
   abierto como R-029-6 (decisión del dueño). Solo se pueden nombrar roles de los módulos requeridos (Inventario y
   Contabilidad): los de Compras no leen esos catálogos.
5. **`Producto.CostoUltimo` (Compras), que también lee la Integración de tienda**, se encontró al revisar A-03. Protegerlo
   exige que Compras nombre roles de Inventario (no lo requiere) o un permiso por campo para cuentas de servicio: queda
   como riesgo R-031-1 y propuesta en el roadmap, no entra (alcance congelado).
6. **Crear un usuario o un equipo** no cuenta como «moverlo a mi unidad»: aún no es dueño de registros (decisión H3).
7. **Límite de lo agregado.** 120 por minuto por usuario (un tablero cuenta una vez aunque corra un reporte por tarjeta). La
   suite y la integración lo suben a 1000.

## Fuera de alcance (anotado en el roadmap y en riesgos)
- D-06 (tareas programadas que se saltan registros, R-029-4), D-07 (importación de números y fechas, R-029-5), R-021-5
  (`ForwardedHeaders`) y R-021-6 (bloqueo de navegación en la SPA): la otra mitad de la «spec de pendientes» que la 030
  planeó antes de Ventas; quedan como spec propia (el dueño decide si antes de Ventas).
- `Producto.CostoUltimo` y un permiso por campo para cuentas de servicio (R-031-1).
- De la revisión adversarial: nombres de unidad con otra intercalación (R-031-4), grupos con `.RequireAuthorization()`
  (R-031-5), diagnóstico de bloqueos asíncronos (R-031-6) y el oráculo de las reglas de error en la vista previa de la
  importación (R-031-7, a la spec de pendientes).
- Que el almacenista vea el costo de entrada (R-029-6).
