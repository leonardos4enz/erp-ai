# Plan 012 — Diseño técnico

Todo es capacidad genérica del motor; lo único específico de Compras va como metadato en `upgrades` (1.7.0).

## 1. Bitácora (`Security/AuditFilter.cs`, `AuditEndpoints`)
- `AuditFilter.Build(model, user)` → condición SQL sobre el alias `a` de `meta.AuditLog` (o null para el superusuario):
  ```sql
  a.Action NOT IN (<datos>) AND a.Action NOT LIKE 'workflow.%' AND a.Action NOT IN ('data.import','data.export','view.change')
  OR (a.EntityName = @aeN AND (a.Action IN ('data.import','data.export','view.change')
      OR EXISTS (SELECT 1 FROM app.X t WHERE t.[Id] = TRY_CAST(a.RecordId AS bigint) AND <RecordFilter.For(X, user, read)>)))
  ```
  Una rama por catálogo raíz que el usuario puede leer; sin filtro de registro la rama es solo `a.EntityName = @aeN`.
  Sin `IsDeleted`: el rastro de un registro eliminado se conserva para quien lo veía. Parámetros con `Prefixed("ae{n}_")`.
- `AuditFilter.Redact(e, json, user, model)` (pura) quita del antes/después los campos que `FieldAccess.CanSee` niega,
  también dentro de las partidas. Se aplica a los eventos de datos de un catálogo del modelo.
- El conteo usa la misma condición.

## 2. Transiciones (`RecordService`)
- `TransitionAsync`: `EnsureAccessAsync(write: t.Roles is null or empty)`.
- `CanTransition(..., bool canWrite)`: sin roles exige `Access.Update && canWrite`, con el mensaje del 403 de escritura.
  `LoadOutputAsync` calcula `canWrite` una vez (`MatchesAsync` con `RecordFilter.For(write: true)`).

## 3. Concurrencia (`RecordService.Automations.cs`)
- `LockAutomationTargetsAsync(c, e, draft, t)`: de las automatizaciones de `e` para `transition t.Name` y `enterState
  t.To`, toma las acciones con `target` (campo referencia del encabezado o de las partidas indicadas en `lines`), reúne
  los ids presentes en el borrador, resuelve su documento raíz y los bloquea con `UPDLOCK, HOLDLOCK` en orden
  (catálogo, id). Los bloqueos posteriores del motor sobre los mismos documentos son reentrantes en la transacción.
- `TransitionAsync` lo llama antes de crear el `RelatedLoader` que evalúa `CanTransition` y las reglas.
- Compras 1.7.0: regla `RecibidoNoExcede` en `OrdenCompraPartida` (`CantidadRecibida <= Cantidad`; `setRules` repite las
  cuatro existentes).

## 4. Aprobación (`Environments/Packages.cs` + endpoints)
- `EnvironmentOptions.AllowDirectChanges` (`Environments:AllowDirectChanges`, falso por defecto).
- `EnvironmentOptions.RequireDirectChange(string what)` lanza `Conflict` si `RequireApproval && !AllowDirectChanges`.
  Se llama al inicio de `POST /api/model/plan/apply`, `ModuleService.InstallAsync`/`UpgradeAsync` y
  `AssistantService.ApplyAsync` (antes de tocar la base).
- `GET /api/assistant` agrega `directChanges: boolean`; la propuesta tiene `canApply = false` y `blockedReason`.

## 5. Asistente (`AssistantService`)
- Reserva: transacción con `sp_getapplock('erp-assistant:{userId}')`, conteo de la última hora (incluye `pending`) e
  `INSERT` de la fila `pending`; se confirma antes de llamar al LLM. Al terminar, `UPDATE` de la fila (y reemplazo de la
  propuesta anterior). Si algo inesperado falla, la fila queda `failed` (`error`), nunca `pending`.
  La conversación y el historial ignoran filas `pending`. Esquema: `Status` admite `pending` (no hay CHECK; el tipo de la
  UI se amplía).
- Huella: `ReviewHash = SHA-256(planJson ‖ pasos (descripción y SQL) ‖ advertencias)` calculada al compilar la vista
  previa. `Proposal.reviewHash`. `POST /apply { reviewHash }`: 400 si falta; dentro de la transacción toma el bloqueo
  `erp-model`, recompila contra el modelo de la transacción y compara; distinta → 409.

## 6. Etiquetas (`RecordService.SelectClause`, `FormatValues`)
- `SelectClause(e, model, user)` devuelve además los parámetros. Para cada referencia con
  `RecordFilter.ForRecordOrParent(destino, model, user)` no nulo, la etiqueta es
  `CASE WHEN rX.[Id] IS NULL THEN NULL WHEN EXISTS (SELECT 1 FROM app.Destino t WHERE t.[Id] = rX.[Id] AND <filtro>) THEN <etiqueta> ELSE N'(sin acceso)' END`.
- `FormatValues` (cálculo en vivo) usa `loader.Visible` con el mismo criterio.

## 7. Menores
- Compras 1.7.0: `Producto.MonedaCosto` (texto 3, no editable) y `AcumularRecibido` (fusión) asigna también
  `MonedaCosto = BASECURRENCY()`.
- `PUT /api/admin/companies/{id}`: si cambia `BaseCurrency` y existe algún registro de un catálogo por empresa con esa
  empresa → 409.

## Contrato de API (cambios)
```ts
GET  /api/audit                       // mismo contrato; filas y campos filtrados para quien no es superusuario
POST /api/{entity}/{id}/transition    // 403 en transición sin roles fuera del alcance de escritura
_workflow.transitions[].allowed/reason // reflejan lo anterior
POST /api/model/plan/apply            // 409 en entorno con aprobación (salvo AllowDirectChanges)
POST /api/admin/modules/{id}/install|upgrade // 409 ídem
GET  /api/assistant → { ...; directChanges: boolean }
type Proposal = { ...; status: ProposalStatus | 'pending'; reviewHash?: string; blockedReason?: string }
POST /api/assistant/requests/{id}/apply { reviewHash: string }  // 400 sin huella · 409 huella distinta o entorno con aprobación
PUT  /api/admin/companies/{id}        // 409 si cambia la moneda base con registros por empresa
```

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **I** Un auditor sin permiso lee precios, registros de otra empresa o de otro dueño en la bitácora | `AuditFilter`: catálogo legible, alcance de lectura (empresa + registros) en SQL y campos ocultos quitados del JSON |
| **I** Deducir la existencia de registros por el total del paginado | El conteo usa la misma condición |
| **E** Ejecutar acciones (y capturar sus campos) en registros ajenos con solo «editar propios» | Transición sin roles exige el filtro de escritura del registro |
| **T** Carrera entre validación y escritura (sobre-recepción, recepción contra orden cancelada) | Bloqueo de destinos antes de validar, en orden determinista; regla en el destino; `rowversion` en la transición de la orden |
| **D** Interbloqueos por los bloqueos nuevos | Orden fijo (catálogo, id); los bloqueos se toman una sola vez por transición; los del motor después son reentrantes |
| **E/R** Una sola persona cambia el modelo de producción | Bloqueo de los caminos directos con aprobación; solo paquetes aprobados por otro administrador |
| **D** Rebasar el límite del asistente (costo) con solicitudes en paralelo | Reserva en la base con bloqueo por usuario antes de llamar al proveedor |
| **T** Aplicar algo distinto a lo revisado (el modelo cambió) | Huella de la revisión recalculada en la transacción bajo el bloqueo del modelo |
| **I** Ver el nombre de un registro fuera de alcance a través de una referencia | Etiqueta `(sin acceso)` con el mismo filtro de lectura que el buscador |
