# Verificación 017 — Rollback de entornos

**Fecha:** 2026-10-24 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 350/350 (+18 en `RollbackTests`: `restoreField`/`restoreEntity` válidas y con error, ida y vuelta archivar → reactivar, aviso de registros vacíos, índice único recreado por empresa, referencia a un archivado, partida sin documento y documento sin partidas, el diff de un paquete reactiva en vez de marcar conflicto (y la estructura distinta sigue en conflicto); instantánea solo del alcance y su ida y vuelta por JSON; `ModelDiff.Revert`: archiva lo creado en orden (campo que referencia → catálogo; campo de partidas → partida → documento), devuelve lo cambiado, reactiva lo archivado, tableros, deriva y campos agregados después, conflicto de proceso; volver a aplicar tras revertir converge. `EnvironmentTests`: reactivar un campo ya no es conflicto) |
| E2E | 231/231 en dos corridas completas limpias seguidas (+10 en `rollback.spec.ts`; `entornos.spec.ts` H2 CA3 ahora prueba el conflicto de quitar un proceso, porque reactivar dejó de serlo) |
| Contra el código anterior | Worktree en `d4e4a98` con `rollback.spec.ts`: falla en la preparación (`restoreField` no existe → 400) y, por ser serial, no corre el resto. `RollbackTests` no compila contra el código anterior (`ModelSnapshot` no existe). Las dos regresiones de la revisión exploratoria se vieron fallar con la UI anterior (F-01: *Expected substring «No se puede aplicar por los conflictos»*; F-02: *Expected ≤ 1, Received 13*) |
| Visual | Sin cambios en las capturas de referencia (ninguna muestra Entornos ni catálogos archivados del modelador) |
| Accesibilidad | axe sin violaciones serias en el diálogo de una solicitud de reversión (`H2 CA1 · CA2 · CA7`) |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 instantáneas antes y después con huella, en la misma transacción | `rollback › H1 · H4 CA4` (dos filas, huellas distintas, en la auditoría de `environment.apply`); unitaria `La_instantanea_es_solo_el_alcance…` |
| H1 CA2 solo metadatos | `H1 · H4 CA4` (el dato «nota antigua» no está en `meta.ModelSnapshot`) |
| H1 CA3 todo o nada | Las instantáneas se escriben en la transacción de `ApplyAsync` (mismo `tx` que el plan; un error revierte ambos) |
| H2 CA1 solo el último instalado con instantánea | `H2 CA1 · CA2 · CA7`, `H2 CA1: lo revertido…` (revertido, reversión y repetida → 409) |
| H2 CA2 solicitud con diff inverso por el mismo compilador | `H2 CA1 · CA2 · CA7` (UI: pasos, advertencias, `roles = []`) |
| H2 CA3 archivar lo creado, reactivar lo archivado, sin borrar | `H2 CA3 · CA6 · H3 CA2` (contrato archivado, su fila sigue en `app.RbContrato`; «Notas» vuelve con su dato) |
| H2 CA4 conflicto bloquea | `H2 CA4` (proceso agregado después), unitaria `Un_proceso_que_agrego_el_paquete…` |
| H2 CA5 deriva y campos agregados después | `H3 CA1 · H2 CA5` (advertencia, `localArchives`, 409 al aplicar con el diff aprobado), `H2 CA3 · CA6 …` (confirmación en la UI) |
| H2 CA6 versión anterior y estados | `H2 CA3 · CA6 · H3 CA2` (sin instalar, `reverted` con `revertedBy`, solicitud `applied`) |
| H2 CA7 roles se conservan | `H2 CA1 · CA2 · CA7` (advertencia), `H2 CA3 · CA6 …` (Gestor17 sigue) |
| H3 CA1 cuatro ojos ligado al diff | `H3 CA1 · H2 CA5` (403 a quien la pidió; diff cambiado → 409 y nueva solicitud) |
| H3 CA2 auditoría | `H2 CA1 · CA2 · CA7` (`environment.revertRequest`), `H2 CA3 · CA6 · H3 CA2` (`environment.revert` con versiones, conjunto de cambios y campos locales) |
| H3 CA3 instantánea alterada | `H3 CA3` (detalle sin diff con el motivo, aprobar → 409, el paquete aplicado ya no se ofrece) |
| H3 CA4 solo superusuario | Mismo `RequireSuperuser` que el resto de *Entornos* (cubierto por `entornos › H4 CA4`) |
| H4 CA1–CA2 operaciones y validación | Unitarias de `RollbackTests` |
| H4 CA3 modelador y asistente | `H4 CA3` (reactivar campo desde el editor y catálogo desde la lista, con vista previa); el prompt del asistente lista `restoreEntity`/`restoreField` y `PlanWarnings` los advierte |
| H4 CA4 el paquete reactiva | `H1 · H4 CA4` (catálogos, partidas y campos archivados), `H2 CA1: … volver a subir el paquete lo reactiva todo` |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | Una reversión (o un paquete) con conflictos y sin errores del plan mostraba «El plan tiene errores · Corrige los puntos marcados y vuelve a revisar», sin ningún punto marcado: no hay nada que la persona pueda corregir | Corregido: `PlanPreviewView` acepta el título y texto del bloqueo; en *Entornos*, con solo conflictos dice «No se puede aplicar por los conflictos». Regresión en `H2 CA4` (falló: *Expected substring «No se puede aplicar por los conflictos»*) |
| F-02 | Baja | En el diálogo de una reversión, «Aplicación #N» (enlace a lo que deshace) quedaba corrido 13 px a la derecha de su etiqueta «Deshace» por el relleno del botón | Corregido: enlace en línea (`.env-link`) con foco visible. Regresión en `H2 CA1 · CA2 · CA7`, midiendo dónde empieza el texto (falló: *Received 13*) |

## Revisión exploratoria (navegador integrado)
- **Instancia temporal** en `localhost:5097` sobre `ErpAi_E2E` (preparada por la segunda corrida completa con una contraseña
  de administrador generada para la sesión y guardada fuera del repositorio), con `RequireApproval` y `AllowDirectChanges`.
  El nombre del entorno se vio como «ProducciÃ³n E2E»: lo causó el script temporal (PowerShell 5.1 leyó el archivo como
  ANSI), no la aplicación; las corridas de la suite lo muestran bien.
- Paquete aplicado con instantánea alterada (dejado por `H3 CA3`): el diálogo no ofrece revertir y explica por qué.
- *Marcas* (aplicado por `entornos.spec` con la 017): «Revertir la instalación» con **doble clic** → una sola solicitud
  (la segunda petición es 409 y el botón se bloquea mientras responde). La solicitud muestra el conflicto del proceso que
  `entornos.spec` agregó después, la deriva y los roles que se conservan; detectado F-01.
- 390 px: el diálogo de reversión cabe, las huellas se parten sin desbordar y los botones se acomodan; detectado F-02.
  Esc cierra el diálogo.
- Modelador a 1280 px en tema claro: los catálogos archivados muestran *Reactivar* (visible según `elementFromPoint`);
  con el teclado (foco y Enter) abre la vista previa «Reactivar catálogo 'Caso 015' (vuelve con los registros que
  conservó)»; se cerró sin aplicar.
- Consola: solo los 401 previos al inicio de sesión; sin errores de JavaScript ni de CSP.

## Riesgo residual
- La reversión no deshace **datos**: los registros creados o cambiados después del paquete quedan como están; los de un
  catálogo archivado dejan de verse hasta reactivarlo.
- Un campo obligatorio o un folio reactivado queda vacío en los registros creados mientras estuvo archivado (la vista
  previa lo advierte); se llenan al editarlos.
- Un ciclo de referencias entre catálogos creados por el paquete (A ↔ B) no tiene orden de archivado: la reversión queda
  bloqueada con el error del compilador.
- Los paquetes aplicados antes de la 017 no tienen instantánea y no se pueden revertir (se explica en el diálogo).
- El diálogo dice «Firmado y verificado» con la clave de firma vigente del entorno; si la clave cambia después de subir el
  paquete, la verificación de la subida ya no se puede repetir (comportamiento de la 009, sin cambios).
- El contexto del asistente no lleva lo archivado (011): solo propone reactivar algo que la persona nombre.
- Al volver a levantar `erp-api`, `Bootstrap` agrega a la base de desarrollo `meta.ModelSnapshot` y las columnas nuevas de
  `meta.PackageImport` (migración idempotente, como en cada spec).
