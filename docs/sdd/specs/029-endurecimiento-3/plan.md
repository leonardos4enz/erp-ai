# Plan 029 — Diseño técnico

## 1. Modelo de datos
Sin tablas ni columnas nuevas, sin operaciones nuevas del Plan de Cambios. Lo nuevo es **derivado al cargar el modelo**
(`MetadataStore.LoadAsync` → `DerivedAccess.Annotate`): para cada fórmula, los campos que lee (propios, de sus partidas y
de registros relacionados, transitivamente a través de otras fórmulas) y los catálogos a los que llega por una referencia.
Se guarda en una `ConditionalWeakTable` ligada a la instancia del campo: no viaja en paquetes, no cambia `ModelVersion` ni
`ModelDiff` y un modelo recargado trae instancias nuevas.

## 2. Contrato de la API
**Ninguna ruta nueva.** Todas declaran su capacidad como metadato; el inventario completo (139 rutas) está en
[`docs/sdd/rutas.md`](../../rutas.md), generado de los metadatos. Cambios de comportamiento:

| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| (todas) | la declarada en `rutas.md` | — | 401 sin sesión; **403 ProblemDetails** sin la capacidad, *antes* de leer el cuerpo (requisito de autorización, no filtro) |
| `GET /api/files/{id}` | **modelador** (antes: cualquier sesión) | — | 403 a quien no modela |
| `GET /api/model/print/preview/{token}` | **modelador** (antes: sesión + token propio) | — | 403 |
| `GET /api/search` | registros, **costosa** | `q` | 429 tras `Security:SearchesPerMinute` (60) por usuario |
| `GET /api/data/{e}/export`, `GET /api/reports/{e}/{n}/export`, `POST /api/data/{e}/import` | registros, **costosas** | — | 429 tras `Security:BulkPerMinute` (20) por usuario |
| `POST /api/admin/modules/{id}/preview`, `…/upgrade/preview` | superusuario | — | + `baseHash` (64 hex): módulo, versión instalada, versión del paquete y modelo completo |
| `POST /api/admin/modules/{id}/install`, `…/upgrade` | superusuario, cambio directo | `{ baseHash }` (cuerpo opcional) | 400 sin huella; 409 «vuelve a revisarla» si cambió; los 409 de «ya instalado / requiere / sin pendientes» siguen primero |
| `POST /api/data/{e}/compute` | registros | igual | `values`/`layout`/`warnings` calculados con el alcance del usuario en cada salto; las fórmulas que no ve no salen |
| `POST /api/data/{e}/{id}/transition` con `onWarning: block` | registros | igual | 400 con solo los avisos visibles; si todos leen ocultos, 400 genérico «incumple una regla del catálogo» |
| `GET /api/data/{e}/{id}/pdf` | registros, costosa | igual | 422 si las imágenes suman más de 12 MP; el semáforo de 4 se libera al terminar el render |

Metadatos de endpoint (`Infrastructure/Capabilities.cs`): `.Requires(Capability…)` (una por ruta, nunca en el grupo),
`.Writes(Effect, gate)` / `.ReadsOnly()`, `.Costly(por qué, límite propio?)` y `.AppliesReviewed(huella)`. Con
`Gates.DirectChange` la declaración **es** la compuerta (filtro que llama a `EnvironmentOptions.RequireDirectChange`). Las
comprobaciones de los servicios (`RequireAdmin(u)`, `RequireUserAdmin` de las operaciones que reaplica una aprobación) se
conservan como defensa en profundidad (decisión 7 de la spec).

## 3. SPA
Solo `ModulesPage` manda la huella de la vista previa al instalar o actualizar (`adminApi.installModule(id, baseHash)`).
Sin pantallas nuevas: no hay capturas de referencia nuevas.

## 4. Pruebas
- **Unitarias nuevas:** `RouteInventoryTests` (8: capacidad única, lista blanca anónima, efecto declarado, compuerta, límite,
  huella, foto versionada) y `Hardening3Tests` (13: lo derivado de un oculto, partidas, orden, catálogo restringido, por
  empresa, `UNITCOST`, tipos de cambio con regla, unidad bajo la propia, semáforo por tiempo y por cancelación, capacidades).
- **E2E nuevos:** `capacidades.spec.ts` (recorre `rutas.md`: 403/401 por ruta, imágenes, 429 de búsqueda, huella de módulos)
  y `canario.spec.ts` (canario en todas las rutas GET y en los POST que devuelven datos, alcance por salto, fórmulas
  guardadas, PDF, avisos de bloqueo). Modificado: `seguridad.spec.ts` (Compras: el costo de la recepción). Con `--veces=3`:
  los dos nuevos y `seguridad.spec.ts` con `compras.spec.ts` e `inventario.spec.ts` (de los que depende).
- **Contra el código anterior:** `npm run e2e:regresion -- 5de6fa0 e2e/capacidades.spec.ts e2e/canario.spec.ts` (ahora
  copia también `docs/sdd/rutas.md`); el inventario unitario no compila contra `5de6fa0` (no existen los metadatos) y se
  demuestra quitando la capacidad de una ruta.

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | ¿Qué rutas, operaciones o caminos agrega o cambia? ¿Qué capacidad pide cada una? | Ninguna ruta nueva; las 139 declaran capacidad, efecto, compuerta, costo y huella como metadato. `/api/files/{id}` y la vista previa de PDF pasan a modelador; búsqueda, exportación e importación tienen límite. | `RouteInventoryTests › Cada_ruta_declara_exactamente_una_capacidad`, `› Solo_la_lista_blanca_es_anonima`, `› El_inventario_versionado_coincide_con_las_rutas`; `capacidades › H1 CA4` (403 y 401 por ruta), `› H1 CA5` |
| Caminos alternos | ¿Todos los caminos al mismo dato aplican la misma regla? | La visibilidad de una fórmula se decide en `FieldAccess.ByRole`, que usan salida, orden, filtros, vistas, reportes, exportación, OData, bitácora (`AuditFilter`), impresión y webhooks. Lo vivo usa el cargador con alcance en `/compute`, `_layout` y `_warnings`. El canario recorre **todas** las rutas GET del inventario y los POST que devuelven datos. | `canario › H2 CA2` (GET e inventario; POST), `› H3 CA2`; `Hardening3Tests › Ordenar_por_una_formula_que_lee_un_oculto_se_ignora`. Transiciones con autoridad del motor: [R-029-3](../../riesgos.md#029) |
| Lectura de datos | ¿Pasa por `RecordFilter`, `FieldAccess` y empresa? | Cada salto de lo vivo pasa por `RecordFilter.ForRecordOrParent` (incluye empresa); las fórmulas guardadas que cruzan un catálogo con permisos por registro restringidos para el usuario, o de compartido a por empresa, se ocultan. `Access(e).Read` no se exige a lo relacionado (como `__label`). | `canario › H3 CA1`, `› H3 CA2`, `› H3 CA3`; `Hardening3Tests › Una_formula_que_recorre_un_catalogo_con_alcance_restringido…`, `› De_un_catalogo_compartido_a_uno_por_empresa…`; [R-029-1](../../riesgos.md#029) |
| Delegación | ¿Alguien aprueba lo que lo beneficia? | `WhyNotApprover` sin `default` permisivo: un tipo sin regla no se aprueba; mover una unidad bajo la propia (o bajo una que cuelga de ella) lo aprueba otro. | `Hardening3Tests › Cada_tipo_de_cambio_de_seguridad_tiene_su_regla`, `› No_se_aprueba_mover_una_unidad_bajo_la_propia` |
| Concurrencia | ¿«Revisar → aplicar» sin huella? ¿10 a la vez? | Instalar y actualizar módulos exigen la huella; se compara dentro de la transacción tras bloquear `meta.Module` (instalar: `TABLOCKX`; actualizar: `UPDLOCK` de la fila). La prueba de inventario exige huella a todo cambio de modelo y a cada «aplicar» con «vista previa» hermana. | `capacidades › H6 CA2`; `RouteInventoryTests › Aplicar_lo_revisado_recibe_su_huella` |
| Fallas parciales | ¿Qué pasa si el proceso cae entre dos pasos? | El render del PDF libera su lugar en el `finally` del propio render (no en el de la petición): un render atorado mantiene ocupado su lugar hasta terminar. La instalación sigue en una transacción. | `Hardening3Tests › El_lugar_del_semaforo_se_libera_al_terminar_el_render…`, `› Cancelar_el_cliente_tampoco_libera_el_lugar…` |
| Límites | Tamaño, tasa, memoria | 4 PDF a la vez de verdad; ≤ 12 MP de imágenes por documento (antes hasta 10 × 6 MP); búsqueda 60/min y exportar/importar 20/min por usuario. | `capacidades › H5 CA3`; `Hardening3Tests › El_lugar_del_semaforo…`; el 422 de 12 MP: [R-029-7](../../riesgos.md#029) (sin E2E: subir 3 imágenes de 6 MP tarda más que el tope de la suite) |
| Semántica | Nulos, vacíos | Un salto fuera de alcance vale vacío: `ISBLANK` es verdadero, la aritmética da 0 (lección 022 F-04) y una condición de layout desconocida oculta. Un valor por defecto que lee un campo oculto sigue escribiéndose. | `canario › H3 CA1`; [R-029-2](../../riesgos.md#029) |

Amenazas propias de la spec (S, T, R, I, D, E):

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **S** | Una cuenta de servicio usa una ruta de administración | Una clave no puede tener capacidades administrativas (`LoadServiceAsync`) y `UseBearerScope` limita sus prefijos; la capacidad se exige igual. | `api-security.spec.ts` (claves); `capacidades › H1 CA4` |
| **T** | Se aplica un módulo distinto del revisado | Huella de módulo + modelo | `capacidades › H6 CA2` |
| **R** | — | Sin cambios: todo sigue auditado | — |
| **I** | Fuga por fórmula, por salto, por aviso de bloqueo o por imagen | `DerivedAccess`, cargador con alcance, `VisibleWarnings` en `block`, imágenes solo a modeladores | `canario.spec.ts` completo; `capacidades › H1 CA5` |
| **D** | PDF y búsqueda agotan CPU o memoria | Semáforo real, 12 MP, límites por usuario | `Hardening3Tests › El_lugar_del_semaforo…`; `capacidades › H5 CA3` |
| **E** | Una ruta nueva sin permiso; aprobar lo propio | Inventario que falla; `WhyNotApprover` sin `default` | `RouteInventoryTests`; `Hardening3Tests › Cada_tipo_de_cambio…` |
