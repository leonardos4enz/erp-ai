# Verificación 016 — Automatizaciones asíncronas y programadas

**Fecha:** 2026-10-23 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 332/332 (+19 en `AutomationJobTests`: calendario por frecuencia, fin de mes y bisiesto, días de la semana, estrictamente posterior, ocurrencias perdidas, hora inexistente por horario de verano, descripción y clave; validación de `setSchedules` (consulta no traducible, calendario, rol, lote, acciones, propio registro, partidas, archivar un campo usado); `mode` validado y omitido en el JSON si es síncrono; paquetes (`ModelDiff`); `ADDDAYS(TODAY(), n)` como parámetro; consulta con condición y alcance del rol sin valores en el SQL; qué se reintenta) |
| E2E | 221/221 en dos corridas completas limpias seguidas, tras las correcciones de la revisión exploratoria y el cambio a página propia (+15 en `programadas.spec.ts`; la configuración de la suite agrega `Automations__PollSeconds=1` y `Automations__RetryDelaysSeconds=1,1`) |
| Contra el código anterior | Worktree en `ddf2682` con `programadas.spec.ts`: falla en la preparación (`setSchedules` no existe → 400) y, por ser serial, no corre el resto. `AutomationJobTests` no compila contra el código anterior (tipos nuevos). Las cuatro regresiones de la revisión exploratoria se vieron fallar, una por una, antes de su corrección |
| Visual | Sin cambios en las capturas de referencia: el enlace *Automatizaciones* va al final de *Configuración* (como el Asistente en la 011) y el campo JSON del modelador está en la sección avanzada, cerrada en un catálogo nuevo |
| Accesibilidad | axe sin violaciones serias en *Tareas programadas*, *Bitácora* (escritorio y 390 px) y el diálogo de detalle |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 encolar en la transacción; revertido o `when` falso no deja trabajo | `programadas › H1 CA1` |
| H1 CA2 datos actuales, a nombre de quien la disparó | `H1 CA1` (camino feliz, `CURRENTUSER()`), `H1 CA2 · CA3 · H4 CA3` (el reintento lee la nota cambiada después del evento; auditoría a nombre de la persona) |
| H1 CA3 quien guarda no se entera; lo del negocio no se reintenta | `H1 CA2 · CA3 · H4 CA3` (201, `failed` con 1 intento y el mensaje de la regla); lo transitorio, unitaria `Solo_se_reintenta_lo_transitorio` |
| H1 CA4 cancelar si la persona ya no está activa o el registro no existe | `H1 CA4` |
| H1 CA5 máximo 5 generaciones | `H1 CA5` (cinco se completan; seis fallan sin dejar el registro) |
| H2 CA1 validación en la vista previa | `H2 CA1 · CA2 · H5 CA1`, unitarias de `ScheduleRules` |
| H2 CA2 llega en pausa; activar, pausar, ejecutar ahora | `H2 CA1 · CA2 · H5 CA1`, `H2 CA3 · CA4 · …` (UI), `H2 CA2 · CA5` (UI), `H4 CA4` |
| H2 CA3 alcance del rol, una transacción por registro, sin encimarse | `H2 CA3 · CA4 · …` (el pedido del sur no se toca; la falla de uno no revierte al otro), `H2 CA3` (409 con una ejecución en curso) |
| H2 CA4 límite de lote y pendientes | `H2 CA3 · CA4 · …` (lote 2: «Quedaron registros para la siguiente ocurrencia») |
| H2 CA5 ocurrencias perdidas no se acumulan | `H2 CA2 · CA5` (tres días atrás → una sola ejecución y la siguiente en el futuro), unitaria `Las_ocurrencias_perdidas_no_se_acumulan` |
| H2 CA6 `ADDDAYS(TODAY(), n)` | Unitaria `ADDDAYS_de_TODAY_se_traduce_como_parametro` (también en reglas de acceso) |
| H3 CA1 rol de servicio y su alcance | `H2 CA3 · CA4 · …`, unitarias de `ScheduleQuery` |
| H3 CA2 cuenta de sistema y auditoría con la tarea | `H2 CA3 · CA4 · …` (historial «Tareas programadas», auditoría `tarea:J16Pedido.VencerPedidos`, no inicia sesión ni aparece en usuarios o buscadores) |
| H3 CA3 el rol pierde la lectura | `H3 CA3` |
| H4 CA1–CA3 pantallas, detalle y reintento | `H2 CA3 · CA4 · …`, `H4 CA1 · CA2` (móvil y filtro), `H1 CA2 · CA3 · H4 CA3` |
| H4 CA4 auditoría y solo superusuario | `H4 CA4` (dos pruebas: enlace y ruta solo para el superusuario; API 403 para otros roles) |
| H5 CA1 modelador | `H2 CA1 · CA2 · H5 CA1` |
| H5 CA2 paquetes y asistente | Unitaria `Las_tareas_viajan_en_los_paquetes_de_entorno`; prompt del asistente con `mode` y `setSchedules` |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | La tarea mostraba «Fallida · 0 procesados» sin el motivo (el rol había perdido la lectura); la bitácora repetía el «0 procesados» junto al error | Corregido: el resumen trae el error de la última ejecución y los conteos solo se muestran si se procesó algo o salió bien. Regresión en `H3 CA3` (falló con *Received …Fallida0 procesados…*) |
| F-02 | Media | A 1280 px el botón *Detalle* de la bitácora quedaba fuera de la tabla visible, y en las tareas el interruptor y *Ejecutar ahora* también en pantallas angostas: demasiadas columnas | Corregido: calendario, rol y lote van en la celda de la tarea; registro, a nombre de quién e intentos, en las celdas de automatización y estado. Regresión `H4 CA1 · CA2: a 1280 px …` con `elementFromPoint` (falló: «el control debe verse sin desplazar la tabla») |
| F-03 | Baja | El detalle de una ocurrencia repetía en el resumen el mismo error de la tabla por registro | Corregido: con errores por registro no se muestra el resumen. Regresión en `H2 CA3 · CA4 · …` (falló con *Expected 1, Received 2*) |
| F-04 | Baja | Después de *Ejecutar ahora*, la fila se quedaba en «Pendiente» hasta pulsar *Actualizar* (la bitácora sí se actualizaba sola) | Corregido: la pestaña se actualiza cada 2 s mientras la última ejecución de alguna tarea esté pendiente o en curso. Regresión en `H2 CA3 · CA4 · …` (falló: seguía «Pendiente») |
| F-05 | Media | (dueño) Tareas y bitácora estaban como pestañas de *Integraciones*, donde nadie las buscaría: una tarea programada no integra nada con otro sistema | Movidas a *Configuración › Automatizaciones* (`/admin/automations`); *Integraciones* vuelve a Webhooks y Claves de API. Cubierto por `H4 CA4: solo el superusuario ve Automatizaciones …` |

## Revisión exploratoria (navegador integrado)
- **Instancia temporal** en `localhost:5095` sobre `ErpAi_E2E` (preparada por `programadas.spec.ts` con una contraseña de
  administrador generada para la sesión y guardada fuera del repositorio), con `RequireApproval`, `AllowDirectChanges` y la
  bandeja cada segundo.
- Tema oscuro a 800 y 1280 px y tema claro a 1280 px: detectados F-01, F-02 y F-03; tras corregir, la tabla de tareas y la
  bitácora caben sin desplazamiento y el motivo de la falla se lee en la fila.
- Activar con el teclado (foco en el interruptor y Espacio): se agenda para las 7:00 del día siguiente. Doble clic en
  *Ejecutar ahora*: el botón se bloquea mientras responde y no encola dos veces; detectado F-04.
- Detalle de una ocurrencia con errores: estado, a nombre de quién, conteos, fechas y la tabla por registro con enlace al
  registro; Esc cierra el diálogo.
- 375 px: las pestañas se acomodan en renglones y la página no se desplaza a lo ancho (`scrollWidth` = ancho visible); la
  tabla de tareas se desplaza dentro de su marco, como las demás tablas de la app.
- Tras moverla a su propia página (F-05): el enlace aparece al final de *Configuración*, la página abre en *Tareas
  programadas* y la página no se desplaza a lo ancho.
- Consola: solo los 401 previos al inicio de sesión; sin errores de JavaScript ni de CSP.

## Riesgo residual
- Entrega «al menos una vez»: si el servidor se detiene a mitad de un trabajo asíncrono, su transacción se revierte y el
  trabajo se repite al vencer el *lease* (5 min). Si un trabajo tarda más que el *lease*, otra instancia podría repetirlo.
- La tarea no recuerda qué procesó: si su `where` no deja de cumplirse al terminar la acción, la siguiente ocurrencia vuelve
  a procesar los mismos registros (decisión de la spec; queda documentado en el modelador y el asistente).
- Un registro que falla siempre ocupa un lugar del lote en cada ocurrencia; si todo el lote falla, los demás esperan.
- La hora de las tareas es la local del servidor: en un servidor con UTC, «a las 07:00» es 07:00 UTC.
- Una tarea en catálogo compartido corre con la empresa predeterminada; lo que cree en un catálogo por empresa queda en esa empresa.
- Al volver a levantar `erp-api`, `Bootstrap` agrega a la base de desarrollo las tablas nuevas, la columna `SchedulesJson`
  y la cuenta `sistema.tareas` (migración idempotente, como en cada spec).
