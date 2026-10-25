# Plan 031 — Diseño técnico

## 1. Modelo de datos
Sin tablas ni columnas nuevas ni operaciones nuevas del Plan de Cambios. **Costeo 1.2.0** (`modules/costeo.json`,
`upgrades`) declara con `setSecurity` la seguridad por campo de los costos:

| Catálogo | Campos | Leen |
|---|---|---|
| `Existencia` (conserva `systemManaged`) | `Valor`, `CantidadCosteada`, `CostoPromedio` | Supervisor de inventario, Almacenista, Contador |
| `MovimientoInventario` | `CostoUnitario`, `CostoTotal` | ídem |
| `TraspasoPartida` | `CostoOrigen` | ídem |

`CosteoIncompleto` (fórmula sobre `CantidadCosteada`) queda oculta por `DerivedAccess` (029) a quien no lee la cantidad.

## 2. Contrato de la API
**Ninguna ruta nueva** (siguen 139, [`rutas.md`](../../rutas.md)). Cambios de comportamiento:

| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `POST /api/data/{e}/import` (`dryRun` o no) | registros, costosa | igual | `warnings` por fila = los que `/compute` daría al mismo borrador (cargador del usuario, sin los que leen ocultos). La auditoría guarda los del motor |
| `POST /api/data/{e}/{id}/transition`, `onWarning: confirm` | registros | `acceptWarnings` | **428** si hay avisos *que la persona ve* sin aceptar, aunque el motor no tenga ninguno; los del motor que ella no ve no se preguntan |
| ídem, `onWarning: block` | registros | igual | **400** si el motor tiene avisos: con los que la persona ve, o el genérico «incumple una regla del catálogo» |
| `PUT`/`POST` que dispara una automatización síncrona con una transición `block` | registros | igual | 400 «No se pudo completar «…»: …» con los avisos que ve quien guardó (genérico si no ve el registro destino) |
| `GET /api/data/{e}/{id}/pdf`, `GET /api/data/{e}/{id}/print` (plantillas que aplican) | registros | igual | `UNITCOST` con el alcance del usuario (vacío si tiene restringido el catálogo de saldo) |
| `GET /api/dashboards/{name}`, `GET /api/reports/{e}/{n}`, `GET /odata/{segment}` | registros, **costosas** | igual | 429 tras `Security:AggregatesPerMinute` (120) por usuario |
| cualquier ruta sin `.Requires` | — | — | 401 sin sesión, **403** ProblemDetails con ella (`FallbackPolicy`), también al superusuario |
| `POST /api/admin/security-changes/{id}/approve` (`user.update`, `team.update`) | usuarios | `{ hash, comment }` | 403 «…a tu unidad…» si la unidad nueva queda bajo la de quien aprueba y la anterior no |

## 3. SPA
Sin cambios: el 428 y el 400 ya muestran los avisos que lleguen. `MapFallbackToFile` se declara anónima.

## 4. Pruebas
- **Integración nueva:** `Hardening031Tests` (8): importación = `/compute`; confirmar pregunta por lo que ve; bloquear dice lo
  que ve; automatización que bloquea; `UNITCOST` en el PDF (texto extraído de los flujos Flate); aprobación al mover un
  usuario y un equipo (instancia con aprobación de seguridad); costos con la clave de «Integración de tienda» por API, OData
  y `$metadata`; rutas de `Program` en `rutas.md`. 7 fallan con `7fce1d6` (`it:regresion`); la de rutas es una guardia.
- **Unitarias nuevas:** `Hardening4Tests` (4: `GainsMember` en altas y cambios), `RouteInventoryTests` (+2: la política por
  omisión niega al superusuario; lo que agrega es costoso). Cambiada: `CostingPolicyTests` (versión de Costeo).
- **E2E modificado:** `canario.spec.ts` (parámetros sin resolver, rutas no GET del inventario, XLSX, `lookup` filtrado, orden
  por ocultos, importación = `/compute`, H4 CA3 con el aviso que ve). Con `--veces=3`: `canario.spec.ts` (autocontenido)
  y `valuacion-contable`, `valuacion-politicas` con `compras` e `inventario` (Costeo 1.2.0).
- **Contra el código anterior:** `npm --prefix web run it:regresion -- 7fce1d6 Hardening031Tests` y
  `npm --prefix web run e2e:regresion -- 7fce1d6 e2e/canario.spec.ts`. La política por omisión: `RouteInventoryTests` con
  `FallbackPolicy` nula falla (mutación).

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones o caminos agrega o cambia? ¿Qué capacidad pide cada una? | Ninguna ruta nueva. Tableros, reportes y OData pasan a costosas con límite por usuario; una ruta sin autorización se niega a todos (la de un grupo con `.RequireAuthorization()` la detiene el inventario: [R-031-5](../../riesgos.md#031)); la SPA se declara anónima. Costeo 1.2.0 es una actualización de módulo (superusuario, cambio directo, huella de la 029). | `RouteInventoryTests › Una_ruta_sin_capacidad_declarada_no_responde_a_nadie`, `› Lo_que_agrega_o_recorre_un_catalogo_es_costoso`; `Hardening031Tests › H6_CA3_cada_ruta_de_Program_esta_en_el_inventario`; módulos con huella: [R-029-8](../../riesgos.md#029) |
| Caminos alternos | ¿Todos los caminos al mismo dato aplican la misma regla? | Lo que se le dice a una persona sale del cargador con su alcance en `/compute`, `_layout`, `_warnings` (029), importación, 428/400 de transiciones (humanas y por automatización síncrona), PDF y elección de plantillas. Quedan con autoridad del motor, por diseño: reglas de error, condiciones de transición, si un bloqueo bloquea y las automatizaciones asíncronas (su error va a la bitácora de trabajos, que ven administradores). | `Hardening031Tests › H1_CA1…`, `› H1_CA2…`, `› H1_CA3…`, `› H4_CA1…`, `› H5_CA1…`; `canario › H1 CA1 (031)`, `› H4 CA3`; el bit del bloqueo y de las condiciones: [R-029-3](../../riesgos.md#029); reglas de error en la vista previa: [R-031-7](../../riesgos.md#031) |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | `UNITCOST` cuenta como lectura del valor y la cantidad costeada del saldo (los campos que alimentan sus acciones `cost`): se ocultan la fórmula, el aviso y el costo en vivo a quien no los ve. El cálculo para la persona usa `RelatedLoader(viewer)` (cada salto por `RecordFilter.ForRecordOrParent`, que incluye empresa) sobre una **copia** del borrador antes de calcular (los defaults también se recalculan con su alcance), y `VisibleWarnings` quita los que leen ocultos. Si no ve el registro (automatización sobre otro), no se le dice nada. Los costos de Costeo se ocultan por campo a todo rol que no sea supervisor, almacenista o contador. | `Hardening031Tests › H2_CA1_la_integracion_de_tienda_lee_existencias_sin_costos…`; `Hardening4Tests › UNITCOST_se_oculta_a_quien_no_ve_el_valor_del_saldo`; `canario` completo; `Producto.CostoUltimo`: [R-031-1](../../riesgos.md#031) |
| Delegación | ¿Alguien aprueba lo que lo beneficia? | `WhyNotApprover` aplica a `user.update` y `team.update` la regla de `unit.move`: si la unidad nueva queda bajo la de quien revisa (y la anterior no, o la nueva es la suya y la anterior no: alcance «unit» exacto), lo aprueba otro; tampoco reactiva un equipo del que es miembro. Crear no cuenta (sin registros). | `Hardening4Tests › Mover_a_un_usuario_o_equipo_a_la_unidad_propia_lo_aprueba_otro`, `› Reactivar_un_equipo_del_que_se_es_miembro_lo_aprueba_otro`, `› Al_crear_aun_no_hay_registros_que_ganar`; `Hardening031Tests › H3_CA1…`; nombres con otra intercalación: [R-031-4](../../riesgos.md#031) |
| Concurrencia | ¿Qué invariante protege? ¿10 a la vez? ¿«revisar → aplicar» sin huella? | No agrega escrituras ni «revisar → aplicar». El cálculo para la persona lee dentro de la misma transacción y después de los candados de `LockAutomationTargetsAsync`; no escribe. | No aplica (sin escrituras nuevas); la huella de módulos sigue en `capacidades › H6 CA2` |
| Fallas parciales | ¿Qué pasa si el proceso cae entre dos pasos? | Si el cálculo para la persona lanza (`ExprException`, `AppException`), no se le dice nada y la operación sigue con la decisión del motor; un error de SQL sigue abortando la transacción como antes. No hay pasos nuevos que marcar. | `Hardening031Tests › H1_CA3…` (el bloqueo sigue aunque la persona no vea el aviso que lo causa); sin falla inyectada: [R-031-2](../../riesgos.md#031); bloqueos asíncronos sin diagnóstico: [R-031-6](../../riesgos.md#031) |
| Límites | Tamaño, tasa, memoria | El doble cálculo cuesta una evaluación más por fila importada (≤ las filas de `ImportReader`, archivo ≤ 2 MB, 20/min por usuario) y por transición con reglas de aviso (solo si el catálogo tiene avisos). Tableros, reportes y OData: 120/min por usuario. | `RouteInventoryTests › Lo_que_agrega_o_recorre_un_catalogo_es_costoso`; el 429 de agregados sin E2E: [R-031-3](../../riesgos.md#031) |
| Semántica | Nulos, vacíos | Un salto fuera de alcance vale vacío (029): la persona puede ver un aviso que con los datos completos no aplica, y un bloqueo puede citarlo (el mismo aviso que ve en el formulario). Las unidades se comparan sin distinguir mayúsculas, como `GainsUnit`. | `Hardening031Tests › H1_CA3…` (el 400 cita el aviso que ve, no el del motor); `Hardening4Tests` |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** | Una cuenta de servicio lee como si fuera un rol interno | Los costos se leen solo por rol nombrado; la clave de la tienda no los ve | `Hardening031Tests › H2_CA1…` |
| **T** | — | Sin escrituras nuevas; lo que se guarda sigue con la autoridad del motor | — |
| **R** | Se pierde qué avisos tenía un registro | La auditoría guarda los del motor (`AuditAfter`), no los que se le dijeron a la persona | sin prueba nueva (sin cambio en `AuditAfter`) |
| **I** | Oráculo de un bit por importación, 428 o mensaje de bloqueo; costo por PDF o por la integración | Avisos con el alcance de quien los recibe; costos por campo; `UNITCOST` con alcance | `Hardening031Tests` H1, H2, H4, H5; `canario.spec.ts` |
| **D** | Tableros y OData sin límite propio; doble cálculo en importaciones grandes | Límite `aggregate`; el doble cálculo solo con reglas de aviso | `RouteInventoryTests › Lo_que_agrega…`; [R-031-3](../../riesgos.md#031) |
| **E** | Una ruta nueva anónima por olvido; aprobar el propio beneficio por cambio de unidad | `FallbackPolicy`; `GainsMember` | `RouteInventoryTests › Una_ruta_sin_capacidad…`; `Hardening4Tests` |
