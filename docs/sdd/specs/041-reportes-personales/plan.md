# Plan 041 — Diseño técnico

## 1. Modelo de datos
Tabla nueva en `Bootstrap` (dato personal, no metadato: no viaja en paquetes ni en la reversión de entornos, como
`meta.SavedView`):

```sql
CREATE TABLE meta.PersonalReport (
  Id int IDENTITY PRIMARY KEY,
  UserId int NOT NULL REFERENCES meta.[User](Id),
  EntityName nvarchar(50) NOT NULL,
  Label nvarchar(100) NOT NULL,
  DefinitionJson nvarchar(max) NOT NULL,
  Version int NOT NULL DEFAULT 1,
  CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
  UpdatedAt datetime2(3) NULL,
  CONSTRAINT UQ_PersonalReport_Label UNIQUE (UserId, Label)
);
```

Definición (`PersonalReportDef`, JSON en `DefinitionJson`): `entity`, `groupBy` (`ReportGroup`), `measures`
(`ReportMeasure`), `filters` (`ViewFilter`), `orderBy`, `dir`, `limit`, `chart`, `chartOptions`. Se vuelve un `ReportDef`
sin `roles` ni `filter` para validarse (`InterfaceRules.ValidateReports`) y correr (`ReportService.RunAsync`). Tope de
20 000 caracteres serializada.

`PersonalReports` (nuevo, `Data/PersonalReports.cs`):
- `Check(def, user, model)` → `(EntityDef, ReportDef)` o `ValidationFailed`. Orden: el catálogo existe, no está archivado y
  el usuario lee su raíz (si no: 404 «El catálogo no existe», igual que inexistente); **cada** ruta de agrupaciones y
  medidas resuelve y el usuario la ve y puede leer cada salto (`InterfaceRules.CanSee` + `ReportService.CanReadPath`); si
  no, «'X' no existe o no puedes verlo» (mismo texto para los dos casos, H4 CA2); los filtros usan solo campos propios
  que ve (mismo texto); luego `ValidateReports` (gráfica, calculadas, `MaxJoins`, límites) y `ValidateQuery` de filtros.
- `Sources(user, model)` y `Fields(entity, user, model)`: lo que se ofrece, con la misma regla.

## 2. Contrato de la API
| Método y ruta | Capacidad | Entrada | Respuesta |
|---|---|---|---|
| `GET /api/my-reports/sources` | registros | — | `200 [{ entity, label, group, parent? }]`: catálogos cuya raíz lee y sus partidas |
| `GET /api/my-reports/sources/{entity}` | registros | — | `200 { entity, label, fields: [{ path, label, type, kind }] }`; `404` si no lo lee o no existe |
| `POST /api/my-reports/explore` | registros · efecto ninguno · costosa · `aggregate` | `{ label?, definition }` | `200 ReportResult`; `400 { errors: { report: [...] } }`; `404` catálogo |
| `POST /api/my-reports/explore/export` | registros · efecto ninguno · costosa · `bulk` | `{ label?, definition, format: csv\|xlsx }` | archivo; mismos errores |
| `GET /api/reports/{entity}/{name}` (cambia) | registros | — | además `definition` (`PersonalReportDef` del reporte, sin roles ni filtro) y `fixedFilter: bool` |
| `GET /api/my-reports` | registros | — | `200 [{ id, label, entity, entityLabel, chart, version, available, updatedAt }]` (solo los suyos) |
| `GET /api/my-reports/{id:int}` | registros | — | `200 { id, label, version, definition, available, problems }`; `404` si no es suyo |
| `POST /api/my-reports` | registros · efecto sesión | `{ label, definition }` | `201 { id, label, version, ... }`; `400` (nombre repetido, tope 50, definición) |
| `PUT /api/my-reports/{id:int}` | registros · efecto sesión | `{ label, definition, version }` | `200`; `404` ajeno o inexistente; `409` versión vieja (antes de validar, F-F); `400` (también si cambia de catálogo, F-C) |
| `DELETE /api/my-reports/{id:int}?version=N` | registros · efecto sesión | — | `204`; `404`; `409` |

Todo lo nuevo vive en `/api/my-reports` (decisión de PLAN): bajo `/api/reports`, `GET /api/reports/sources/{entity}`
chocaba con `GET /api/reports/{entity}/{name}` (un catálogo llamado «Sources» perdía sus reportes) y una clave de API
habría llegado a `explore`. Así, ninguna credencial (clave de API, token OAuth, token MCP) llega a las rutas nuevas: fuera
de `ApiKeys.AllowedPaths` y de `OAuthScopes.Required`; solo una sesión.
Bitácora: acción nueva `report.personal` (de catálogo: la ve quien audita y lee el catálogo **raíz**; la de una partida va bajo
su documento), con el nombre y la huella SHA-256 de la definición antes y después, **no** la definición (revisión
adversarial F-B: rutas y valores de filtros del reporte personal de otro). Un reporte guardado no cambia de catálogo (F-C).

## 3. SPA
- `ReportsPage`: botón «Nuevo reporte» y sección «Mis reportes» (no disponibles marcados con texto).
- `ReportPage` (del modelo): botón «Explorar» → `/reports/explore?from=Entidad/Reporte`.
- **`ReportExplorerPage`** (`/reports/explore`, `/reports/mine/:id`): una sola pantalla. Panel de diseño (catálogo,
  agrupaciones, medidas, gráfica, orden, límite, filtros con `FilterDialog`) y resultado (`ReportChart` + `ReportTable`).
  Cada cambio pide `explore` tras 400 ms (la respuesta vieja se descarta: `AbortSignal`). Errores 400 en un `Alert`
  (`role="alert"`) sin perder lo armado. «Guardar», «Guardar como», «Borrar» (con confirmación) y exportar CSV/Excel.
  El 409 dice que otra pestaña lo cambió y ofrece recargar.
- Accesibilidad: cada control con etiqueta; el resultado anuncia «Actualizado» en una región `aria-live`; foco al
  `Alert` del error de guardar.

## 4. Pruebas
- Unitarias `tests/Erp.Tests/PersonalReportsTests.cs`: `Check` (catálogo no leído = inexistente, ruta oculta = inexistente,
  salto por catálogo no leído salvo el campo para mostrar, filtro oculto, `MaxJoins`, gráfica inválida), `Fields`.
- Integración `tests/Erp.IntegrationTests/PersonalReportsTests.cs`: dueño único (404 ajeno en GET/PUT/DELETE), versión
  (409), diez altas simultáneas al tope, nombre único simultáneo, revalidar al correr tras ocultar un campo, empresa y
  alcance en `explore`, exportación igual a la pantalla, bitácora, credenciales (clave de API y OAuth).
- E2E `web/e2e/reportes-personales.spec.ts` (nuevo): un escenario por CA; corre después de `compras` (usa sus datos).
  Canario (`canario.spec.ts`): `explore` y `explore/export` del lector con rutas al secreto; sus reportes.
- Inventario de rutas regenerado (`ACTUALIZAR_RUTAS=1`).

## 5. STRIDE
| Fila | Pregunta | Respuesta | Prueba o riesgo |
|---|---|---|---|
| Entradas | Rutas nuevas y su capacidad | 9 rutas (tabla del §2), todas `registros`; las que escriben, efecto «sesión»; `explore` y su exportación, efecto «ninguno», costosas y con límite por persona. La definición es entrada no confiable: identificadores solo de metadatos (rutas resueltas contra el modelo), valores como parámetros, funciones, periodos y gráficas de listas cerradas; sin filtro de expresión. | `RouteInventoryTests` (una capacidad por ruta); unit `PersonalReportsTests › Check_*`; IT `Explorar_rechaza_lo_que_no_valida_y_lo_oculto_responde_como_lo_inexistente` |
| Caminos alternos | ¿Otros caminos al mismo dato? | Pantalla, exportación del explorador y reporte guardado pasan por `PersonalReports.Check` + `ReportService.RunAsync`. MCP, Studio, OData y webhooks no exponen reportes personales; ninguna credencial (clave de API, OAuth, MCP) llega a `/api/my-reports`. Paquetes y reversión no copian `meta.PersonalReport`. La bitácora no lleva la definición (F-B). `GET /api/reports/{entity}/{name}` ahora da la definición también a claves de API y tokens OAuth (F-H): metadato de campos que ya ven. | IT `Exportar_da_lo_mismo_que_explorar`; unit `Credenciales_no_llegan_a_mis_reportes`; IT `Bitacora_no_lleva_la_definicion_y_va_bajo_el_documento`; F-H en [R-041-2](../../riesgos.md#041) |
| Lectura de datos | `RecordFilter`, `FieldAccess` y empresa | `ReportService.RunAsync` con el usuario que pide: alcance y empresa activa en el catálogo y en **cada** unión, «(sin acceso)» en cualquier salto (039/040). `Check` exige ver cada campo del camino y leer cada catálogo (salvo el campo para mostrar del último) **en cada corrida**. Lo que no ve y lo que no existe responden igual. | IT `Explorar_respeta_alcance_y_empresa`; IT `Guardado_deja_de_correr_si_se_oculta_un_campo`; unit `Check_oculto_igual_que_inexistente`; canario `H5 (041)` |
| Delegación | ¿Otorgar o heredar? | Un reporte personal no se comparte en la 041; corre siempre con quien lo pide (no con quien lo guardó). Nadie lee ni cambia el de otro: 404 igual que inexistente. | IT `Solo_el_dueno_ve_y_cambia_su_reporte` |
| Concurrencia | Invariantes y huella | (a) Tope de 50 por persona y (b) nombre único: un candado de aplicación por dueño (`sp_getapplock`, dueño la transacción) serializa altas y cambios; `UNIQUE (UserId, Label)` como respaldo (2601/2627 → 400). (c) «abrir → guardar» con `Version`: la fila se lee con `UPDLOCK` y la versión se compara **antes** de validar (F-F); sin versión o vieja → 409; borrar igual. | IT `Diez_altas_simultaneas_no_pasan_el_tope`; IT `Nombre_unico_con_altas_simultaneas`; IT `Guardar_con_version_vieja_da_409`; IT `Version_vieja_da_409_antes_que_la_validacion` |
| Fallas parciales | ¿Cae entre dos pasos? | Alta, cambio y borrado escriben fila y bitácora en una transacción: o las dos o ninguna. Explorar no escribe. | IT `Alta_y_bitacora_en_la_misma_transaccion` (la bitácora tiene la fila; una alta rechazada no deja rastro) |
| Límites | Tamaño, topes, tasa | ≤ 2 agrupaciones, ≤ 6 medidas, ≤ 2 saltos, ≤ 16 uniones, ≤ 1 000 filas, 30 s, ≤ 20 filtros (≤ 50 valores), expresión ≤ 500, definición ≤ 20 000 caracteres, 50 reportes por persona, campos ofrecidos ≤ 2 000; `aggregate` (120/min) en explorar y `bulk` en exportar; elementos nulos → 400 (F-E); la SPA espera 400 ms entre cambios. **No** hay tope de corridas simultáneas por persona (F-A) ni límite propio de `/sources/{entity}` (solo el global). | unit `Check_rechaza_mas_de_16_uniones`; unit `Definicion_demasiado_grande_se_rechaza`; unit `Check_con_elementos_nulos_da_400`; F-A en [R-041-1](../../riesgos.md#041); IT `Diez_altas_simultaneas_no_pasan_el_tope`; `RouteInventoryTests › Lo_que_agrega_o_recorre_un_catalogo_es_costoso` |
| Semántica | Nulos, fechas, redondeo | Las mismas de la 039 (calculadas con vacío y `/0`, periodos ISO, suma vacía = 0): mismo `ReportService`. Un filtro `$me`/`$today` se resuelve al correr, con quien corre. | IT `Explorar_da_lo_mismo_que_el_reporte_del_modelo_y_avisa_el_filtro_fijo` |

Amenazas propias de la spec:

| Amenaza | Riesgo | Mitigación | Prueba o riesgo |
|---|---|---|---|
| **I** — armar una agrupación o filtro por un campo oculto o a través de un catálogo que no lee para deducir valores | Alto | `Check` en cada corrida: ruta visible y saltos legibles; filtros solo propios y visibles; uniones con `RecordFilter` | unit `Check_*`; canario `H5 (041)`; IT `Explorar_respeta_alcance_y_empresa` |
| **I** — oráculo de nombres (un campo oculto responde distinto que uno inexistente) | Media | Mismo mensaje y mismo 404 para catálogo | unit `Check_oculto_igual_que_inexistente`; IT `Fuentes_no_ofrecen_ocultos_ni_catalogos_que_no_lee` |
| **E** — leer o cambiar el reporte de otro por su id | Alta | Toda consulta lleva `UserId = @uid`; 404 igual | IT `Solo_el_dueno_ve_y_cambia_su_reporte` |
| **D** — explorar en bucle (cada definición agrega el catálogo) | Media | `aggregate` por persona, 30 s, `TOP`, `MaxJoins`, espera de 400 ms en la SPA; sin tope de concurrencia | `RouteInventoryTests` (costosa con límite) · [R-041-1](../../riesgos.md#041) |
| **T** — perder el cambio de otra pestaña | Baja | `Version` (409) | IT `Guardar_con_version_vieja_da_409` |
| **R** — negar haber cambiado un reporte | Baja | Bitácora `report.personal` en la transacción (nombre y huella) | IT `Alta_y_bitacora_en_la_misma_transaccion` |
| **I** — el auditor lee por la bitácora el reporte personal de otro (rutas, campos ocultos, valores de filtros) | Media | Solo nombre y huella; partidas bajo su documento; sin cambio de catálogo (F-B, F-C, F-D) | IT `Bitacora_no_lleva_la_definicion_y_va_bajo_el_documento`; IT `Un_reporte_guardado_no_cambia_de_catalogo` |
