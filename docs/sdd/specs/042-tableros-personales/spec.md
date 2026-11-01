# Spec 042 — Tableros personales y compartir

**Estado:** Implementado · **Depende de:** 006 (`RecordFilter`, `FieldAccess`), 007 (tableros), 010 (empresa), 015 (herencia de
roles), 021 (nadie otorga lo que no tiene), 039 (indicador con comparación), 041 (reportes personales, `PersonalReports.Check`)
· **Origen:** pedido del dueño (2026-10-31, 039 CLARIFY 1: «tableros personales») y la mitad que la 041 dejó fuera (041
CLARIFY 1 y 2); roadmap punto 12.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | Un tablero solo existe como metadato del modelo (`DashboardDef`, `setDashboard`); `DashboardsPage` ofrece «Nuevo tablero» y «Editar (JSON)» solo al modelador, y aplicarlo es un plan de cambios. | Quien no es modelador no puede juntar en una pantalla las cifras que mira cada día. |
| 2 | `DashboardWidget` apunta a `Entity` + `Report` del modelo; no hay forma de que una tarjeta apunte a un reporte de `meta.PersonalReport`. | Los reportes personales de la 041 no pueden ir en un tablero. |
| 3 | `PersonalReportService` filtra todo por `UserId = @uid`: un reporte personal lo ve solo su dueño (041 CLARIFY 2). | Para que un compañero vea «mi reporte de zonas» hay que pedirle al modelador que lo vuelva un reporte del modelo. |
| 4 | `explore`, `explore/export` y el tablero corren una o varias consultas de hasta 30 s; el límite `aggregate` cuenta peticiones por minuto, no consultas a la vez (R-041-1). | Un tablero personal compartido con un rol multiplica las consultas costosas que diseñó cualquier usuario. |

## Principio
Capacidad **genérica del motor**: un tablero personal es un dato de su dueño (`meta.PersonalDashboard`, como
`meta.PersonalReport`), con tarjetas que apuntan a un reporte del modelo o a un reporte personal **de su dueño**. Compartir
es una lista de roles por reporte o tablero personal; quien comparte solo elige roles que **tiene**, y el reparto se
revalida al leer (dueño activo y con el rol). Lo compartido corre **siempre** con `RecordFilter`, `FieldAccess` y la empresa
de **quien lo ve**, por la misma entrada que la 041 (`PersonalReports.Check` + `ReportService.RunAsync`). Ningún módulo
agrega código.

## Historias

**H1 — Mis tableros.** Como *usuario sin permiso de modelador*, quiero armar tableros con tarjetas de los reportes del
modelo y de mis reportes personales, para ver mis cifras juntas.
- CA1: «Nuevo tablero» en Tableros abre el editor: nombre, descripción y tarjetas; cada tarjeta elige un reporte (los del
  modelo que veo y mis reportes), el tipo (indicador, gráfica o tabla), un título opcional y si va ancha; se reordenan y se
  quitan. Al guardar, el tablero corre y aparece en «Mis tableros» junto a los del modelo.
- CA2: una tarjeta que no tiene sentido (gráfica de un reporte sin agrupación, indicador de uno agrupado sin fecha con
  periodo) o un reporte que no existe o no veo responde con el mensaje de qué cambiar, en la vista, sin perder lo armado.
- CA3: cambiar y borrar llevan la versión que se abrió (409 si otra pestaña se adelantó); nombre único por persona, hasta
  20 tableros por persona y 20 tarjetas por tablero, también con muchas peticiones a la vez.
- CA4: una tarjeta que dejó de correr para su dueño (un campo que ya no ve, un reporte borrado) se muestra como no
  disponible, con el motivo; el resto del tablero sigue.

**H2 — Compartir un reporte personal.** Como *dueño de un reporte personal*, quiero compartirlo con uno o varios de mis
roles, para que mis compañeros lo consulten sin pedírselo al modelador.
- CA1: «Compartir» en el explorador ofrece solo los roles que tengo (asignados o heredados; el superusuario, todos) y
  guarda con la versión; un rol que no tengo o que no existe responde igual (400, sin decir cuál de los dos).
- CA2: quien tiene uno de esos roles ve el reporte en «Compartidos conmigo» (en Reportes), con el nombre de quien lo
  comparte; lo abre en el explorador sin poder cambiarlo ni borrarlo, y «Guardar como mío» crea su copia.
- CA3: dejar de compartir (ninguna casilla) lo quita de «Compartidos conmigo» de inmediato.

**H3 — Compartir un tablero personal.** Como *dueño de un tablero personal*, quiero compartirlo con mis roles, para que
el equipo vea los mismos indicadores.
- CA1: el mismo diálogo de H2; el tablero aparece en «Compartidos conmigo» en Tableros y se abre sin poder editarlo.
- CA2: sus tarjetas de reportes personales del dueño corren para quien lo ve aunque esos reportes no se hayan compartido
  aparte (van con el tablero), pero quien lo ve no puede abrir su definición: el título de la tarjeta solo enlaza a lo que
  puede abrir.

**H4 — Con la seguridad de quien lo ve.** Como *responsable de seguridad*, quiero que lo compartido nunca muestre más de lo
que ve en el listado quien lo abre, y que nadie reparta más de lo que tiene.
- CA1: un reporte o tablero compartido corre con el alcance, la empresa activa y los campos de quien lo ve, nunca con los
  del dueño; lo que no ve sale «(sin acceso)» o deja la tarjeta fuera.
- CA2: si quien lo ve no puede ver algo que usa el reporte, no recibe la definición ni los nombres de lo que falta: solo
  «no disponible para ti» (lo oculto no se nombra, H4 CA2 de la 041); tampoco la etiqueta del valor de un filtro.
- CA3: el reparto se revalida al leer: si el dueño ya no tiene el rol (o está inactivo), quienes lo veían por ese rol dejan
  de verlo, sin que nadie lo edite.
- CA4: nadie cambia, borra ni comparte lo de otro: responde 404 igual que lo inexistente.

**H5 — Sin multiplicar el costo.** Como *responsable de la operación*, quiero que una persona no sostenga muchas consultas
costosas a la vez, para que un tablero compartido o la exploración en bucle no degraden el servidor.
- CA1: explorar, exportar lo explorado y abrir un tablero personal admiten hasta 4 corridas a la vez por persona; la
  siguiente responde 429 con un mensaje claro y la pantalla lo dice.
- CA2: cada alta, cambio, reparto o borrado de un tablero queda en la bitácora (nombre, huella y roles; no la definición),
  y el reparto de un reporte, en la de reportes personales.

## Decisiones (CLARIFY)
1. **¿Dividir la spec?** Criterios (dividir si se cumplen dos): más de 6 historias (5: no) · más de ~2 500 líneas de
   producto (≈1 800: no) · seguridad o concurrencia junto con UI nueva (**sí**: reparto por rol y pantalla de tablero
   personal) · más de una pantalla principal (una: el tablero personal con su editor; compartir es un diálogo que se usa
   en el explorador de la 041 y en el tablero, y las listas son secciones de pantallas que existen: no). → **No se divide**
   (un criterio). Ventas sigue en la 043 y MCP 2 en la 044.
2. **Con qué roles se comparte.** Solo con roles que quien comparte tiene hoy (asignados o heredados, los de
   `UserContext.Roles`); el superusuario, con cualquiera. Un rol que no se tiene responde como uno inexistente (no es un
   oráculo de nombres de roles). Se guarda el id del rol (renombrar un rol no rompe el reparto).
3. **El reparto se revalida al leer** (021-1: nadie hereda lo que no tiene): un reparto cuenta solo si el dueño está activo
   y sigue teniendo el rol (asignado, heredado o superusuario). Quien pierde el rol deja de repartir por él sin que nadie
   toque el reporte.
4. **Quién ve lo compartido:** quien tiene el rol (asignado o heredado). El superusuario **no** ve todo lo de todos: solo lo
   compartido con sus roles (es un dato personal, no gobierno del modelo; la bitácora le sigue mostrando los cambios).
5. **Lo compartido corre con quien lo ve.** Un reporte compartido se abre en el explorador con la definición y corre por
   `explore` con los permisos de quien lo ve; un tablero compartido corre cada tarjeta igual. Si la definición usa algo que
   quien lo ve no puede ver, no se le entrega (ni los nombres de lo que falta): «no disponible para ti». La etiqueta de
   presentación de los filtros (p. ej. el nombre del proveedor elegido) se quita para quien no es dueño.
6. **Tarjetas de un tablero personal:** reportes del modelo que el dueño ve hoy o reportes personales **del propio dueño**
   (no los compartidos con él: su dueño los cambiaría o dejaría de compartir y el tablero de otro cambiaría sin aviso). Al
   compartir el tablero, sus tarjetas personales van con él; quien lo ve no recibe su definición.
7. **Tarjetas que no corren:** para el dueño se muestran como «no disponible» con el motivo (H1 CA4); para quien lo ve
   compartido se omiten, como las de un tablero del modelo (no se nombra lo que no ve).
8. **Topes:** 20 tableros por persona, 20 tarjetas por tablero (`InterfaceRules.MaxWidgets`), 20 roles por reparto, nombre
   único por persona, versión en «abrir → guardar» (y en compartir). Los de la 041 siguen: 50 reportes, `MaxJoins` = 16,
   `aggregate`.
9. **Corridas simultáneas por persona (R-041-1, parte).** Las tarjetas compartidas lo agravan: un tablero que cualquiera
   arma con el peor caso lo abre todo un rol. Tope de 4 corridas a la vez por persona en `explore`, `explore/export` y el
   tablero personal (configurable, `Security:CostlyConcurrentPerUser`); 429 al pasarlo. Las rutas de la 007/039 (reporte y
   tablero del modelo) quedan en R-041-1: su diseño lo hace un modelador.
10. **Sin copiar tableros.** «Guardar como mío» existe para reportes compartidos (la 041 ya lo tiene); copiar un tablero
    ajeno copiaría tarjetas de reportes que no son de quien copia (decisión 6): fuera de alcance.
11. **Bitácora:** el reparto de un reporte es un cambio del reporte (`report.personal`, ahora con los roles). Los tableros
    personales usan `dashboard.personal` (nombre, huella SHA-256 de las tarjetas y roles; no la definición), visible para
    quien audita (no es de un catálogo).

## Fuera de alcance (anotado en el roadmap)
- Copiar un tablero compartido; tarjetas de reportes compartidos por otro.
- Compartir con personas o equipos (solo por rol); compartir tableros del modelo (ya tienen roles).
- Tope de corridas simultáneas en reportes y tableros **del modelo** (R-041-1).
- Lo personal por MCP, OData, Studio o claves de API (solo con sesión, como la 041).
