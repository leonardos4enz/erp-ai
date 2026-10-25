# Spec 029 — Endurecimiento 3: capacidades declaradas y canario de fugas

**Estado:** Implementado · **Depende de:** 006, 012, 015, 021, 023, 027, 028 · **Origen:** la
[retrospectiva 001](../../retrospectivas/001-specs-001-026.md) §5 (P1 y P2, planeadas en el roadmap) y la **auditoría
externa 4** (2026-10-25, solo lectura, specs 021–028: 9 defectos, uno alto), que el dueño pidió incluir.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | P1 · constitución II.2 («cada endpoint declara el permiso requerido») | Los grupos solo piden autenticación; la capacidad se revisa dentro de cada handler o servicio (`RequireModeler`, `RequireSuperuser`, `RequireUserAdmin`, `RequireAdmin(u)` en cinco servicios). Nadie puede listar qué pide cada una de las ~140 rutas, ni cuáles cambian el modelo o la seguridad, son costosas o son «revisar → aplicar» |
| 2 | P2 · las fugas por otro camino (bitácora, `__label`, reportes, OData) las encontraron auditorías | No hay una prueba que recorra todas las rutas buscando un dato que el usuario no debe ver |
| 3 | D-01 (alta) · `Data/Scopes.cs` `RelatedLoader.Walk` lee cada salto con `Row()` sin `Visible()` | `/compute`, las condiciones de layout y los avisos leen registros fuera del alcance a 2 y 3 saltos; solo el primero se revisa al elegir la referencia (`CheckReferenceVisible`). El PDF sí revisa cada salto |
| 4 | D-05 (media, latente; R-006-1) · `FieldAccess.Strip` no mira de qué depende una fórmula | Una fórmula visible revela los campos ocultos de los que depende: `/compute` como oráculo, orden y filtro por la fórmula. En Compras, el *Costo* de la partida de recepción sale de `PartidaOrden.PrecioUnitario`, que el almacenista no ve (Compras 1.3.0 dice que «no ve precios») |
| 5 | D-02 (media) · `RecordService.Print.cs` libera el semáforo en `finally` | Si `Render` sigue tras el tiempo límite o tras cancelar, ya hay más de 4 PDF a la vez; 10 imágenes de 6 MP decodificadas son ~240 MB por PDF |
| 6 | D-03 (media) · `SecurityApproval.WhyNotApprover` termina en `default: return null` | Quien revisa aprueba un `unit.move` que pone la unidad bajo la suya y con `unitTree` gana su visibilidad; un tipo de cambio nuevo nace aprobable por cualquiera |
| 7 | D-04 (media) · `POST /api/admin/modules/{id}/upgrade` (y `/install`) sin huella | Lo revisado en la vista previa (pasos y conflictos con lo local) no es lo que se aplica si el modelo cambió entre ambos |
| 8 | D-08 (baja) · `RecordService.Warnings.cs` con `onWarning: block` | El 422 lista todos los avisos, también los que leen campos ocultos |
| 9 | D-09 (baja) · `GET /api/search` | Busca en todos los catálogos con folio sin límite de frecuencia propio |
| 10 | Auditoría 4, especulativos | `RelatedLoader.Visible` no aplica `Access(e).Read`; `GET /api/files/{id}` sirve cualquier imagen a cualquier usuario autenticado por id secuencial |

## Principio
Ninguna capacidad nueva de negocio: el motor **declara** lo que ya exigía y lo **hace cumplir en un solo lugar**.
- La capacidad de cada ruta es **metadato del endpoint** (`.Requires(Capability.X)`) y un filtro la comprueba antes del
  handler; los efectos (modelo, seguridad, datos), el costo y la huella también son metadatos. Una prueba recorre
  `EndpointDataSource` y un inventario versionado (`docs/sdd/rutas.md`) deja cada ruta nueva en el diff.
- La seguridad de los datos derivados se decide en `FieldAccess` (la usan salida, orden, filtros, reportes, exportación,
  OData, bitácora, impresión y webhooks): una fórmula es tan visible como lo que lee. Lo calculado **en vivo** para una
  persona usa un cargador que respeta su alcance en **cada** salto.
- El canario es genérico: un catálogo de prueba con metadatos (por el Plan de Cambios), no código por módulo.

## Historias

**H1 — Capacidad declarada por ruta (P1).** Como *auditor o sesión que agrega una ruta*, quiero que cada ruta declare
qué capacidad pide y qué efecto tiene, para que una ruta sin permiso no pase la suite.
- CA1: cada ruta lleva exactamente una capacidad (`anonymous`, `authenticated`, `records`, `modeler`, `userAdmin`,
  `audit`, `superuser`, o varias como alternativa), un filtro común la exige (401/403) y los `Require…` de los endpoints
  desaparecen. Una ruta anónima está en una lista blanca explícita.
- CA2: la prueba de inventario falla si una ruta no declara capacidad, si una ruta que escribe no declara su efecto, si
  una que cambia el modelo o la seguridad no declara su compuerta, si una costosa no tiene límite de frecuencia o si un
  «aplicar» con «vista previa» hermana no declara su huella. Quitar la capacidad de una ruta la hace fallar.
- CA3: `docs/sdd/rutas.md` (ruta → capacidad, efecto, compuerta, costo, huella) se genera de los metadatos y la prueba
  falla si difiere: toda ruta nueva aparece en el diff.
- CA4: un usuario sin la capacidad recibe 403 en **cada** ruta que la exige (recorrido del inventario por API).
- CA5 (hallazgo 10): `GET /api/files/{id}` exige administrar el modelo (solo lo usan *Imágenes* y *Empresas*); el PDF
  sigue incrustando las imágenes en el servidor.

**H2 — Canario de fugas (P2).** Como *dueño*, quiero una prueba que busque un dato prohibido en todas las respuestas,
para no depender de que una auditoría encuentre la siguiente fuga.
- CA1: datos de prueba con `CANARIO-<guid>` en: un campo oculto para el rol, un registro fuera de su alcance, un registro
  fuera de alcance **detrás de uno visible** a 2 y a 3 saltos, y un registro de otra empresa; fórmulas, condición de
  layout, aviso y plantilla de impresión que los leen.
- CA2: como ese usuario se recorren **todas** las rutas GET del inventario (con los parámetros del canario) y los POST que
  devuelven datos (`/compute`, vista previa de importación, búsqueda); ninguna respuesta (JSON, CSV ni el texto del PDF)
  contiene el canario.
- CA3: el canario falla con el código anterior (`5de6fa0`) y nombra la ruta que fuga.

**H3 — Alcance en cada salto (D-01).** Como *usuario con alcance limitado*, quiero que lo que se calcula para mí no lea
registros que no puedo ver, aunque estén detrás de uno que sí veo.
- CA1: en `/compute` (valores, layout y avisos), `_layout` y `_warnings` de un registro, cada salto de una ruta (también el
  primero) solo lee un registro que el usuario ve; si no, el salto vale vacío.
- CA2: una fórmula **guardada** se calcula con la autoridad del motor (igual para todos: reglas, acumulados y pólizas la
  usan) y no se muestra a quien tiene restringido por registro o por empresa algún catálogo que su ruta recorre: se trata
  como un campo oculto en salida, orden, filtros, reportes, exportación, OData, bitácora e impresión.
- CA3: el PDF conserva su revisión por salto («(sin acceso)»).

**H4 — Lo derivado de un campo oculto (D-05, D-08).** Como *rol al que se le oculta un campo*, quiero que tampoco se me
muestre lo que se calcula con él.
- CA1: una fórmula cuyo grafo de dependencias (propio, de partidas o de registros relacionados, transitivo) lee un campo
  oculto para el usuario queda oculta para él: no sale en registros, listados, `/compute`, exportación, reportes, OData,
  bitácora ni impresión, y no se ordena ni filtra por ella.
- CA2: en Compras el almacenista deja de ver el *Costo* de la partida de recepción; el comprador lo sigue viendo. La
  revisión campo por campo de Inventario y Costeo queda en `verify.md`.
- CA3: con `onWarning: block` el 422 solo lista los avisos que el usuario puede ver; si todos leen ocultos, el mensaje es
  genérico y la acción sigue bloqueada.

**H5 — Límites de lo costoso (D-02, D-09).** Como *operador*, quiero que los límites de PDF y búsqueda se cumplan.
- CA1: el lugar del semáforo se libera cuando termina el render real, no al vencer el tiempo ni al cancelar el cliente:
  con 4 renders atorados el quinto recibe 503.
- CA2: las imágenes de un documento (plantilla y logo) suman a lo más 12 MP; si no, 422 con mensaje claro.
- CA3: `/api/search` es ruta costosa con límite por usuario (`Security:SearchesPerMinute`, 60).

**H6 — Aprobación y huella (D-03, D-04).** Como *administrador que revisa*, quiero que nadie apruebe lo que lo beneficia ni
aplique algo distinto de lo que revisó.
- CA1: `WhyNotApprover` no tiene caso por omisión: cada tipo de cambio declara su regla y una prueba falla si se agrega un
  tipo sin regla. Quien revisa no aprueba mover una unidad bajo la suya (o bajo una que cuelga de la suya) si antes no
  la veía.
- CA2: la vista previa de instalar y de actualizar un módulo devuelve `baseHash`; aplicar sin ella es 400 y con otra (el
  modelo o la versión cambiaron) es 409.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (6, no) · más de ~2 500 líneas de
   producto estimadas (~1 200 de producto; las pruebas no cuentan, no) · seguridad o concurrencia junto con UI nueva (no:
   la SPA solo manda la huella de módulos) · más de una pantalla principal (ninguna nueva, no). → **No se divide.**
2. **D-01, semántica de las fórmulas guardadas.** Se descartó enmascarar por fila (como `__label`): no es coherente en
   reportes, que agregan la fórmula, ni en orden y filtros. Se decide (H3 CA2): **lo vivo respeta el alcance en cada
   salto; lo guardado es del motor y se oculta entero a quien no ve completo un catálogo que su ruta recorre** (alcance
   por registro distinto de «todos», o un catálogo por empresa al que se llega desde uno compartido). Los saltos a las
   propias partidas y al propio documento no cuentan. Es más estricto que Salesforce (cuyas fórmulas entre objetos
   ignoran el *sharing*) a propósito: el canario no admite excepciones. Quien quiera mostrar ese dato lo copia con una
   automatización, decisión explícita del modelador.
3. **`RelatedLoader.Visible` sin `Access(e).Read`** (hallazgo 10, primer punto). **Se conserva**: es el mismo criterio que
   `__label` desde la 006/012 (la etiqueta y lo relacionado de un registro al alcance se ven aunque el rol no abra el
   catálogo); la seguridad por campo sí se revisa en cada salto. Exigir lectura rompería la captura de partidas para
   roles que eligen productos sin administrar el catálogo. Registrado como R-029-1.
4. **`GET /api/files/{id}`** (hallazgo 10, segundo punto): **se restringe a modeladores** (H1 CA5); los usuarios solo
   reciben imágenes dentro del PDF.
5. **Valores por defecto que leen campos ocultos** (encontrado al estudiar D-05): un valor por defecto se escribe en un
   campo capturable que el usuario ve, por diseño del modelador. No se cambia; R-029-2.
6. **Reglas de error y condiciones de transición** se siguen evaluando con la autoridad del motor (deben valer igual
   para todos): su mensaje fijo puede revelar un bit. R-029-3.
7. **Comprobaciones de los servicios** (`RequireAdmin(u)` en webhooks, claves, OAuth, automatizaciones, entornos,
   asistente): se conservan como defensa en profundidad porque esos servicios también se llaman desde fuera de HTTP; la
   capacidad de la ruta es la que manda y la que se prueba (H1 CA4).

## Fuera de alcance (anotado en el roadmap y en riesgos)
- D-06 · las tareas programadas se saltan para siempre un registro que falla (`RecordService.Background.cs`) → 030 o spec
  propia (R-029-4).
- D-07 · la importación toma «1,5» como 15 y fechas con desfase mal (`RecordService.Import.cs`) → spec propia (R-029-5,
  ver también R-008-4).
- R-021-5 `ForwardedHeaders` y R-021-6 bloqueo de navegación en la SPA → 030 o spec propia.
- Valores automáticos de Inventario y Costeo derivados de precios ocultos (escritos por automatizaciones, no fórmulas):
  decisión de seguridad por campo del módulo, R-029-6.
