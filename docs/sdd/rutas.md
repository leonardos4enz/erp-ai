# Inventario de rutas

Generado por `tests/Erp.Tests/RouteInventoryTests.cs` de los metadatos de cada ruta (spec 029). **No se edita a mano:**
`ACTUALIZAR_RUTAS=1 dotnet test --filter RouteInventory` lo regenera y el diff muestra cada ruta nueva o cambiada.
La prueba falla si una ruta no declara capacidad, si escribe sin declarar su efecto, si cambia el modelo o la seguridad
sin compuerta, si es costosa sin límite o si aplica lo revisado sin huella. `web/e2e/capacidades.spec.ts` y
`web/e2e/canario.spec.ts` recorren esta tabla.

Capacidades: **anónima** (lista blanca) · **sesión** (lo propio del usuario) · **registros** (el motor exige permiso del
catálogo, alcance por registro y seguridad por campo) · **modelador** · **usuarios** · **auditoría** · **superusuario**.

Total: 139 rutas.

| Método | Ruta | Capacidad | Efecto | Compuerta | Costosa | Huella |
|---|---|---|---|---|---|---|
| GET | `/api/admin/api-keys/` | superusuario |  |  |  |  |
| POST | `/api/admin/api-keys/` | superusuario | seguridad | aprobación de seguridad |  |  |
| PUT | `/api/admin/api-keys/{id:int}` | superusuario | seguridad | aprobación de seguridad |  |  |
| POST | `/api/admin/api-keys/{id:int}/revoke` | superusuario | seguridad | no amplía acceso |  |  |
| POST | `/api/admin/api-keys/{id:int}/rotate` | superusuario | seguridad | no amplía acceso |  |  |
| GET | `/api/admin/automations/runs` | superusuario |  |  |  |  |
| GET | `/api/admin/automations/runs/{id:long}` | superusuario |  |  |  |  |
| POST | `/api/admin/automations/runs/{id:long}/retry` | superusuario | datos |  |  |  |
| GET | `/api/admin/automations/schedules` | superusuario |  |  |  |  |
| PUT | `/api/admin/automations/schedules/{entity}/{name}` | superusuario | configuración |  |  |  |
| POST | `/api/admin/automations/schedules/{entity}/{name}/run` | superusuario | datos |  |  |  |
| GET | `/api/admin/companies` | superusuario o usuarios |  |  |  |  |
| POST | `/api/admin/companies` | superusuario | configuración | solo superusuario |  |  |
| PUT | `/api/admin/companies/{id:int}` | superusuario | configuración | solo superusuario |  |  |
| DELETE | `/api/admin/companies/{id:int}/logo` | superusuario | configuración | solo superusuario |  |  |
| PUT | `/api/admin/companies/{id:int}/logo` | superusuario | configuración | solo superusuario |  |  |
| GET | `/api/admin/environments/` | superusuario |  |  |  |  |
| POST | `/api/admin/environments/export` | superusuario | configuración |  |  |  |
| POST | `/api/admin/environments/export/preview` | superusuario | ninguno |  |  |  |
| GET | `/api/admin/environments/exportable` | superusuario |  |  |  |  |
| GET | `/api/admin/environments/imports` | superusuario |  |  |  |  |
| POST | `/api/admin/environments/imports` | superusuario | configuración |  |  |  |
| GET | `/api/admin/environments/imports/{id:int}` | superusuario |  |  |  |  |
| POST | `/api/admin/environments/imports/{id:int}/apply` | superusuario | modelo | aprobación del paquete |  | diffHash |
| POST | `/api/admin/environments/imports/{id:int}/approve` | superusuario | configuración |  |  | diffHash |
| POST | `/api/admin/environments/imports/{id:int}/reject` | superusuario | configuración |  |  |  |
| POST | `/api/admin/environments/imports/{id:int}/revert` | superusuario | configuración |  |  |  |
| GET | `/api/admin/exchange-rates` | superusuario |  |  |  |  |
| PUT | `/api/admin/exchange-rates` | superusuario | configuración | solo superusuario |  |  |
| DELETE | `/api/admin/exchange-rates/{id:int}` | superusuario | configuración | solo superusuario |  |  |
| GET | `/api/admin/images/` | modelador |  |  |  |  |
| DELETE | `/api/admin/images/{key}` | modelador | configuración |  |  |  |
| PUT | `/api/admin/images/{key}` | modelador | configuración |  |  |  |
| GET | `/api/admin/modules` | superusuario |  |  |  |  |
| POST | `/api/admin/modules/{id}/install` | superusuario | modelo | cambio directo |  | baseHash |
| POST | `/api/admin/modules/{id}/preview` | superusuario | ninguno |  |  |  |
| POST | `/api/admin/modules/{id}/upgrade` | superusuario | modelo | cambio directo |  | baseHash |
| POST | `/api/admin/modules/{id}/upgrade/preview` | superusuario | ninguno |  |  |  |
| GET | `/api/admin/oauth-clients/` | superusuario |  |  |  |  |
| POST | `/api/admin/oauth-clients/` | superusuario | seguridad | aprobación de seguridad |  |  |
| GET | `/api/admin/oauth-clients/endpoints` | superusuario |  |  |  |  |
| PUT | `/api/admin/oauth-clients/{id:int}` | superusuario | seguridad | aprobación de seguridad |  |  |
| POST | `/api/admin/oauth-clients/{id:int}/revoke` | superusuario | seguridad | no amplía acceso |  |  |
| POST | `/api/admin/oauth-clients/{id:int}/rotate` | superusuario | seguridad | no amplía acceso |  |  |
| GET | `/api/admin/roles` | usuarios o modelador |  |  |  |  |
| POST | `/api/admin/roles` | usuarios | seguridad | aprobación de seguridad |  |  |
| PUT | `/api/admin/roles/{id:int}` | usuarios | seguridad | aprobación de seguridad |  |  |
| GET | `/api/admin/security-changes/` | usuarios o superusuario |  |  |  |  |
| POST | `/api/admin/security-changes/{id:int}/approve` | usuarios o superusuario | seguridad | aprobación de seguridad |  | hash |
| POST | `/api/admin/security-changes/{id:int}/reject` | usuarios o superusuario | seguridad | no amplía acceso |  |  |
| GET | `/api/admin/teams` | usuarios |  |  |  |  |
| POST | `/api/admin/teams` | usuarios | seguridad | aprobación de seguridad |  |  |
| PUT | `/api/admin/teams/{id:int}` | usuarios | seguridad | aprobación de seguridad |  |  |
| GET | `/api/admin/units` | usuarios |  |  |  |  |
| POST | `/api/admin/units` | usuarios | seguridad | no amplía acceso |  |  |
| PUT | `/api/admin/units/{id:int}` | usuarios | seguridad | aprobación de seguridad |  |  |
| GET | `/api/admin/users` | usuarios |  |  |  |  |
| POST | `/api/admin/users` | usuarios | seguridad | aprobación de seguridad |  |  |
| PUT | `/api/admin/users/{id:int}` | usuarios | seguridad | aprobación de seguridad |  |  |
| GET | `/api/admin/webhooks/` | superusuario |  |  |  |  |
| GET | `/api/admin/webhooks/deliveries` | superusuario |  |  |  |  |
| POST | `/api/admin/webhooks/deliveries/{id:long}/retry` | superusuario | configuración |  |  |  |
| DELETE | `/api/admin/webhooks/{entity}/{name}` | superusuario | configuración |  |  |  |
| PUT | `/api/admin/webhooks/{entity}/{name}` | superusuario | configuración |  |  |  |
| POST | `/api/admin/webhooks/{entity}/{name}/rotate` | superusuario | configuración |  |  |  |
| POST | `/api/admin/webhooks/{entity}/{name}/test` | superusuario | configuración |  |  |  |
| GET | `/api/assistant/` | modelador |  |  |  |  |
| GET | `/api/assistant/conversations` | modelador |  |  |  |  |
| GET | `/api/assistant/conversations/{id:int}` | modelador |  |  |  |  |
| DELETE | `/api/assistant/key` | superusuario | configuración |  |  |  |
| PUT | `/api/assistant/key` | superusuario | configuración |  |  |  |
| POST | `/api/assistant/requests` | modelador | sesión |  | llama al modelo de lenguaje (Assistant:RequestsPerHour por usuario) |  |
| POST | `/api/assistant/requests/{id:int}/apply` | modelador | modelo | cambio directo |  | reviewHash |
| POST | `/api/assistant/requests/{id:int}/discard` | modelador | sesión |  |  |  |
| GET | `/api/audit` | auditoría |  |  |  |  |
| POST | `/api/auth/company` | sesión | sesión |  |  |  |
| POST | `/api/auth/login` | anónima | sesión |  | contraseña con PBKDF2 (login) |  |
| POST | `/api/auth/logout` | sesión | sesión |  |  |  |
| GET | `/api/auth/me` | sesión |  |  |  |  |
| GET | `/api/dashboards/` | registros |  |  |  |  |
| GET | `/api/dashboards/{name}` | registros |  |  |  |  |
| GET | `/api/data/{entity}/` | registros |  |  |  |  |
| POST | `/api/data/{entity}/` | registros | datos |  |  |  |
| POST | `/api/data/{entity}/compute` | registros | ninguno |  |  |  |
| GET | `/api/data/{entity}/export` | registros |  |  | hasta 5000 filas (bulk) |  |
| POST | `/api/data/{entity}/import` | registros | datos |  | archivo de hasta 2 MB por el motor (bulk) |  |
| GET | `/api/data/{entity}/lookup` | registros |  |  |  |  |
| DELETE | `/api/data/{entity}/{id:long}` | registros | datos |  |  |  |
| GET | `/api/data/{entity}/{id:long}` | registros |  |  |  |  |
| PUT | `/api/data/{entity}/{id:long}` | registros | datos |  |  |  |
| GET | `/api/data/{entity}/{id:long}/history` | registros |  |  |  |  |
| GET | `/api/data/{entity}/{id:long}/pdf` | registros |  |  | genera un PDF (print) |  |
| GET | `/api/data/{entity}/{id:long}/print` | registros |  |  |  |  |
| POST | `/api/data/{entity}/{id:long}/transition` | registros | datos |  |  |  |
| GET | `/api/explorer/automations` | modelador |  |  |  |  |
| GET | `/api/explorer/database` | modelador |  |  |  |  |
| GET | `/api/explorer/database/ddl` | modelador |  |  |  |  |
| GET | `/api/explorer/entities/{name}` | modelador |  |  |  |  |
| GET | `/api/explorer/navigation` | modelador |  |  |  |  |
| GET | `/api/explorer/relations` | modelador |  |  |  |  |
| GET | `/api/explorer/security` | modelador |  |  |  |  |
| GET | `/api/explorer/usages` | modelador |  |  |  |  |
| GET | `/api/files/{id:int}` | modelador |  |  |  |  |
| GET | `/api/health` | anónima |  |  |  |  |
| GET | `/api/model/apps` | sesión |  |  |  |  |
| GET | `/api/model/changes` | modelador |  |  |  |  |
| GET | `/api/model/entities` | sesión |  |  |  |  |
| GET | `/api/model/entities/{name}` | sesión |  |  |  |  |
| POST | `/api/model/expressions/check` | modelador | ninguno |  |  |  |
| GET | `/api/model/expressions/functions` | modelador |  |  |  |  |
| POST | `/api/model/plan/apply` | modelador | modelo | cambio directo |  | baseHash |
| POST | `/api/model/plan/preview` | modelador | ninguno |  |  |  |
| POST | `/api/model/print/preview` | modelador | ninguno |  | genera un PDF (print) |  |
| GET | `/api/model/print/preview/{token}` | modelador |  |  |  |  |
| GET | `/api/model/provenance` | modelador |  |  |  |  |
| GET | `/api/reports/` | registros |  |  |  |  |
| GET | `/api/reports/{entity}/{name}` | registros |  |  |  |  |
| GET | `/api/reports/{entity}/{name}/export` | registros |  |  | hasta 1000 grupos en un archivo (bulk) |  |
| GET | `/api/search` | registros |  |  | busca en todos los catálogos con folio (search) |  |
| GET | `/api/users/lookup` | sesión |  |  |  |  |
| GET | `/api/views/{entity}/` | registros |  |  |  |  |
| POST | `/api/views/{entity}/` | registros | sesión |  |  |  |
| DELETE | `/api/views/{entity}/{id:int}` | registros | sesión |  |  |  |
| PUT | `/api/views/{entity}/{id:int}` | registros | sesión |  |  |  |
| * | `/api/{**path}` | anónima |  |  |  |  |
| DELETE | `/oauth/token` | anónima | ninguno |  |  |  |
| GET | `/oauth/token` | anónima | ninguno |  |  |  |
| PATCH | `/oauth/token` | anónima | ninguno |  |  |  |
| POST | `/oauth/token` | anónima | sesión |  | valida el secreto del cliente (oauth) |  |
| PUT | `/oauth/token` | anónima | ninguno |  |  |  |
| * | `/oauth/{**path}` | anónima |  |  |  |  |
| GET | `/odata/` | registros |  |  |  |  |
| * | `/odata/{**path}` | anónima |  |  |  |  |
| DELETE | `/odata/{**rest}` | registros | ninguno |  |  |  |
| MERGE | `/odata/{**rest}` | registros | ninguno |  |  |  |
| PATCH | `/odata/{**rest}` | registros | ninguno |  |  |  |
| POST | `/odata/{**rest}` | registros | ninguno |  |  |  |
| PUT | `/odata/{**rest}` | registros | ninguno |  |  |  |
| GET | `/odata/{segment}` | registros |  |  |  |  |
