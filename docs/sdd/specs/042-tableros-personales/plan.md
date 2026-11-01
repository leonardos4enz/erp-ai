# Plan 042 — Diseño técnico

## 1. Modelo de datos
Tablas nuevas en `Bootstrap` (datos personales, no metadatos: no viajan en paquetes ni en la reversión de entornos, como
`meta.PersonalReport`):

```sql
CREATE TABLE meta.PersonalDashboard (
  Id int IDENTITY PRIMARY KEY,
  UserId int NOT NULL REFERENCES meta.[User](Id),
  Label nvarchar(100) NOT NULL,
  Description nvarchar(300) NULL,
  DefinitionJson nvarchar(max) NOT NULL,      -- { widgets: [...] }
  Version int NOT NULL DEFAULT 1,
  CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
  UpdatedAt datetime2(3) NULL,
  CONSTRAINT UQ_PersonalDashboard_Label UNIQUE (UserId, Label)
);
CREATE TABLE meta.PersonalReportShare (
  ReportId int NOT NULL REFERENCES meta.PersonalReport(Id) ON DELETE CASCADE,
  RoleId int NOT NULL REFERENCES meta.Role(Id),
  PRIMARY KEY (ReportId, RoleId)
);
CREATE TABLE meta.PersonalDashboardShare (
  DashboardId int NOT NULL REFERENCES meta.PersonalDashboard(Id) ON DELETE CASCADE,
  RoleId int NOT NULL REFERENCES meta.Role(Id),
  PRIMARY KEY (DashboardId, RoleId)
);
```
Los roles no se borran (solo se renombran): el reparto guarda el id.

Tarjeta (`PersonalWidget`, en `DefinitionJson`): `type` (kpi, chart, table), `label?`, `wide`, y **una** fuente:
`entity` + `report` (del modelo) o `personalReport` (id de un reporte personal **del dueño del tablero**).

`PersonalSharing` (nuevo, `Data/PersonalSharing.cs`):
- `ResolveRolesAsync(names, user, cn, tx)` → ids. Solo roles de `user.Roles` (efectivos: asignados y heredados); el
  superusuario, cualquiera existente. Uno que no tiene y uno inexistente: el mismo 400 («El rol 'X' no existe o no lo
  tienes»). Máximo 20.
- `Visible(shareTable, key, alias)`: condición SQL de candidatos «compartido con uno de los roles de quien lee y el dueño
  activo»; `RevalidateAsync` comprueba en C# que el dueño siga teniendo ese rol (asignado, heredado con `RoleGraph.Expand` o
  superusuario). Revisión adversarial F-A: antes era una CTE recursiva sobre todas las personas. Nombres de tablas y
  columnas constantes del código; roles de quien lee como parámetros.

`PersonalDashboards` (nuevo, `Data/PersonalDashboards.cs`): validar tarjetas contra el dueño al guardar (fuente única,
reporte del modelo con `ReportService.Available`, reporte personal propio con `PersonalReports.Check`, tipo compatible:
gráfica con agrupación, indicador sin agrupación o comparable — `InterfaceRules.IsComparable`) y resolver cada tarjeta para
quien la ve al correr (lo mismo, con quien ve).

`CostlyGate` (nuevo, `Infrastructure/CostlyGate.cs`, singleton): contador por persona; `Enter(user)` da un `IDisposable` o
lanza 429. Tope `Security:CostlyConcurrentPerUser` (4).

## 2. Contrato de la API
| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `GET /api/my-reports` (cambia) | registros | — | además `shared: bool` (lo compartió con algún rol) |
| `GET /api/my-reports/shared` | registros | — | `200 [{ id, label, owner, entity?, entityLabel?, chart?, available, updatedAt }]`: compartidos con quien pide (sin los suyos); `entity` y `entityLabel` solo si corre para él |
| `GET /api/my-reports/{id:int}` (cambia) | registros | — | además `owned`, `owner`; si es suyo, `roles` y `shareableRoles`; si es compartido y no corre para quien pide, `definition: null` y `problems: ["No disponible para ti: usa algo que no puedes ver."]`; filtros sin `label` si no es suyo. `404` si no es suyo ni se lo compartieron |
| `PUT /api/my-reports/{id:int}/share` | registros · efecto sesión | `{ roles: string[], version }` | `200 { id, version, roles }`; `404` ajeno; `409` versión; `400` rol |
| `POST /api/my-reports/explore`, `/explore/export` (cambian) | igual que la 041 | igual | además `429` con más de 4 corridas a la vez de la misma persona |
| `GET /api/my-dashboards` | registros | — | `200 { mine: [{ id, label, description?, version, shared, updatedAt }], shared: [{ id, label, description?, owner, updatedAt }] }` |
| `GET /api/my-dashboards/{id:int}` | registros · costosa · `aggregate` | — | `200 { id, label, description?, version, owned, owner, roles?, shareableRoles?, widgets?, cards: [{ type, label, wide, entity?, report?, personalReport?, link?, available, problems, result? }] }`; `widgets` (definición) solo al dueño; para quien no es dueño se omiten las tarjetas que no corren; `404`; `429` |
| `POST /api/my-dashboards` | registros · efecto sesión | `{ label, description?, widgets }` | `201 { id, label, version, ... }`; `400` (nombre, tope 20, tarjetas) |
| `PUT /api/my-dashboards/{id:int}` | registros · efecto sesión | `{ label, description?, widgets, version }` | `200`; `404`; `409` (antes de validar); `400` |
| `DELETE /api/my-dashboards/{id:int}?version=N` | registros · efecto sesión | — | `204`; `404`; `409` |
| `PUT /api/my-dashboards/{id:int}/share` | registros · efecto sesión | `{ roles, version }` | `200 { id, version, roles }`; `404`; `409`; `400` |

Todo bajo `/api/my-reports` y `/api/my-dashboards`: fuera de `ApiKeys.AllowedPaths` y `OAuthScopes.Required` (solo sesión).
`link` de una tarjeta: `/reports/{entity}/{report}` para las del modelo; `/explore/{id}` solo si quien ve es el dueño o el
reporte personal también se le compartió; si no, nulo.

Bitácora: `report.personal` agrega `roles` (nombres) al antes/después del reporte; `dashboard.personal` (nueva, en
`AuditFilter.OtherActions`) con `label`, `definitionSha256` y `roles`, en la misma transacción que la fila.

## 3. SPA
- `DashboardsPage`: «Nuevo tablero» para todos (el del modelo sigue como «Nuevo tablero del modelo (JSON)» para el
  modelador); secciones «Mis tableros» y «Compartidos conmigo» (con el nombre de quien comparte), sin `?app=`.
- **`PersonalDashboardPage`** (`/my-dashboards/:id?`): ver (tarjetas como el tablero del modelo; no disponibles con su
  motivo) y, para el dueño, «Editar»: nombre, descripción y lista de tarjetas (reporte agrupado por «Reportes del modelo» y
  «Mis reportes», tipo, título, ancha, subir, bajar, quitar), «Guardar», «Borrar» (confirmación), «Compartir». 409 con
  «Volver a abrir»; errores 400 en un `Alert` sin perder lo armado; 429 con su mensaje.
- **`ShareDialog`** (componente): casillas con los roles que puede elegir, guarda con la versión; lo usan el explorador y el
  tablero personal.
- `ReportsPage`: sección «Compartidos conmigo»; «Mis reportes» marca los compartidos. `ReportExplorerPage`: «Compartir»
  para el dueño; abierto por quien no es dueño, solo lectura (sin Guardar, Borrar ni Compartir; «Guardar como mío») y
  «Compartido por X».
- Accesibilidad: cada control con etiqueta; las tarjetas en `section` con título; el foco vuelve a «Compartir» o «Editar»
  al cerrar el diálogo.

## 4. Pruebas
- Unitarias `tests/Erp.Tests/PersonalDashboardsTests.cs`: `CostlyGate` (tope, libera, por persona), validación de tarjetas
  del modelo (fuente única, tipo compatible, reporte que no ve), credenciales sin acceso a `/api/my-dashboards`.
- Integración `tests/Erp.IntegrationTests/PersonalDashboardsTests.cs` (nuevo): reparto por rol (solo propios, revalidado,
  inactivo), corre con quien ve (alcance, empresa, campos), sin definición ni etiquetas para quien no ve, dueño único,
  versión, tope con altas simultáneas, tope de corridas simultáneas con `HoldAsync`, bitácora en la transacción.
- E2E `web/e2e/tableros-personales.spec.ts` (nuevo): un escenario por CA; corre después de `compras` (usa sus datos) y de
  nada más (prepara sus reportes por API). Canario (`canario.spec.ts`): reporte y tablero compartidos con el lector cuya
  definición va al secreto; rutas `my-dashboards`; control positivo con el dueño.
- Inventario de rutas regenerado (`ACTUALIZAR_RUTAS=1`); captura de referencia del tablero personal.

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas nuevas y su capacidad | 7 rutas nuevas (2 de reparto, 5 de tableros) y 3 que cambian, todas `registros` y solo con sesión; las que escriben, efecto «sesión»; abrir un tablero personal, costosa con `aggregate`. Roles por nombre resueltos contra `meta.Role` (parámetros); tarjetas validadas contra el modelo y contra las filas del dueño. | `RouteInventoryTests`; unit `PersonalDashboardsTests › Credenciales_no_llegan_a_mis_tableros`; IT `Tablero_valida_tarjetas_y_marca_las_que_no_corren` |
| Caminos alternos | ¿Otros caminos al mismo dato? | Un reporte compartido se ve por `GET /api/my-reports/{id}` (definición) y corre por `explore` (la de la 041) con quien ve; un tablero, por `GET /api/my-dashboards/{id}`, que corre cada tarjeta con `PersonalReports.Check`/`ReportService.Available` + `RunAsync` con quien ve. Listas (`/shared`, `/my-dashboards`) con la misma condición SQL de reparto. Bitácora sin definición. MCP, OData, Studio, claves y webhooks no llegan; paquetes y reversión no copian las tablas. | IT `Compartido_corre_con_quien_lo_ve`; IT `Tablero_compartido_corre_cada_tarjeta_con_quien_lo_ve`; IT `Bitacora_de_tableros_sin_definicion_y_en_la_transaccion`; unit `Credenciales_no_llegan_a_mis_tableros` |
| Lectura de datos | `RecordFilter`, `FieldAccess` y empresa | Nunca con el dueño: `ReportService.RunAsync` con quien ve (alcance, empresa activa, «(sin acceso)» por salto). La definición compartida pasa `PersonalReports.Check` con quien ve; si no, no se entrega (ni los nombres de lo que falta) y la etiqueta de los filtros se quita. | IT `Compartido_corre_con_quien_lo_ve`; IT `Compartido_no_entrega_lo_que_quien_lo_ve_no_puede_ver`; canario `H4 (042)`; los valores de los filtros sí llegan ([R-042-2](../../riesgos.md#042)); el nombre de un reporte que va solo con el tablero, también ([R-042-3](../../riesgos.md#042)) |
| Delegación | ¿Otorgar o heredar lo que no tiene? | Solo se comparte con roles que se tienen (o superusuario); un rol ajeno responde como inexistente. El reparto se revalida en cada lectura: dueño activo y con el rol (`RoleGraph.Expand` de los dueños candidatos, F-A). Lo compartido no da permisos: corre con quien ve. Nadie cambia, borra ni comparte lo de otro (404). | IT `Solo_comparte_con_roles_que_tiene`; IT `Reparto_se_revalida_si_el_dueno_pierde_el_rol`; IT `Reparto_con_herencia_en_rombo_se_revalida_a_tiempo` ([R-042-1](../../riesgos.md#042)); IT `Nadie_cambia_ni_comparte_lo_de_otro` |
| Concurrencia | Invariantes y huella | (a) Tope de 20 tableros y nombre único: `sp_getapplock` por dueño (el mismo recurso de la 041 no: uno propio `erp-personal-dashboard:{id}`) y `UNIQUE` de respaldo. (b) «abrir → guardar» y compartir con `Version` (`UPDLOCK`, antes de validar). (c) Corridas costosas a la vez por persona: `CostlyGate`. | IT `Veinte_altas_simultaneas_no_pasan_el_tope_de_tableros`; IT `Tablero_con_version_vieja_da_409`; IT `Compartir_con_version_vieja_da_409`; IT `Corridas_simultaneas_por_persona_tienen_tope`; unit `CostlyGate_tope_por_persona_y_libera` |
| Fallas parciales | ¿Cae entre dos pasos? | Fila, reparto y bitácora en una transacción (o todo o nada). Abrir un tablero no escribe; si una tarjeta falla por tiempo, falla la petición (como el tablero del modelo). `CostlyGate` libera en `finally` (también si se cancela la petición). | IT `Bitacora_de_tableros_sin_definicion_y_en_la_transaccion` (un reparto rechazado no deja fila ni bitácora); unit `CostlyGate_tope_por_persona_y_libera` |
| Límites | Tamaño, topes, tasa | 20 tableros por persona, 20 tarjetas, 20 roles, nombre ≤ 100, descripción ≤ 300, definición ≤ 20 000; topes de la 041 en cada tarjeta personal (2 agrupaciones, 6 medidas, 16 uniones, 1 000 filas, 30 s); `aggregate` (una petición = hasta 20 corridas en serie) y 4 corridas a la vez por persona. Las rutas de la 007/039 no tienen tope de concurrencia (R-041-1). | IT `Veinte_altas_simultaneas_no_pasan_el_tope_de_tableros`; IT `Corridas_simultaneas_por_persona_tienen_tope`; unit `Tarjetas_fuera_de_limite_se_rechazan`; [R-041-1](../../riesgos.md#041) (resto); tope por proceso y por petición: [R-042-4](../../riesgos.md#042) |
| Semántica | Nulos, fechas, redondeo | Las de la 039/041 (mismo `ReportService`, sin cambios): un filtro `$me`/`$today` compartido se resuelve al correr, con **quien ve** (la 041 ya lo resuelve con quien corre). El indicador compara periodos como el del modelo. | IT `Compartido_corre_con_quien_lo_ve` (la misma definición da cifras distintas por persona); la semántica de `ReportService`, las pruebas de la 039/041 |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **E** — compartir con un rol que no se tiene para llegar a otro equipo | Media | Solo roles propios; revalidado al leer | IT `Solo_comparte_con_roles_que_tiene`; IT `Reparto_se_revalida_si_el_dueno_pierde_el_rol` |
| **I** — el lector ve datos con los permisos del dueño | Alta | Todo corre con quien ve | IT `Compartido_corre_con_quien_lo_ve`; IT `Tablero_compartido_corre_cada_tarjeta_con_quien_lo_ve`; canario `H4 (042)` |
| **I** — la definición compartida nombra campos o catálogos que el lector no ve, o la etiqueta de un registro fuera de su alcance | Media | Sin definición si no corre para él; filtros sin etiqueta | IT `Compartido_no_entrega_lo_que_quien_lo_ve_no_puede_ver` |
| **I** — oráculo de roles (¿existe el rol «Dirección»?) | Baja | Mismo 400 para inexistente y ajeno | IT `Solo_comparte_con_roles_que_tiene` |
| **E/T** — cambiar, borrar o compartir lo de otro por su id | Alta | Toda escritura lleva `UserId = @uid`; 404 igual | IT `Nadie_cambia_ni_comparte_lo_de_otro` |
| **D** — un tablero con 20 tarjetas del peor caso compartido con un rol | Media | `aggregate`, 4 corridas a la vez por persona, tarjetas en serie, 30 s por consulta | IT `Corridas_simultaneas_por_persona_tienen_tope` · [R-041-1](../../riesgos.md#041) |
| **R** — negar haber compartido | Baja | Bitácora con roles en la transacción (la ve todo auditor: [R-042-5](../../riesgos.md#042)) | IT `Bitacora_de_tableros_sin_definicion_y_en_la_transaccion` |
