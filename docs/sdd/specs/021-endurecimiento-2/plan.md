# Plan 021 — Diseño técnico

Todo es capacidad genérica del motor; lo único de un módulo (Contabilidad) va por `upgrades` (1.1.0).

## 1. Nadie otorga lo que no tiene (`AdminEndpoints`, `SecurityApproval`)
- `SecurityGrants.Capabilities(roles)` (pura): unión de `IsSuperuser`, `CanManageModel`, `CanManageUsers`,
  `CanViewAudit` de un conjunto de roles. `SecurityGrants.MissingFor(actor, required)` devuelve la primera capacidad que
  falta (superusuario cubre todo).
- `RequireGrantableRolesAsync(cn, tx, actor, before, after)`: roles agregados ∪ quitados → sus capacidades → 403 si al
  actor le falta alguna. Se llama en `CreateUserCore` y `UpdateUserCore` antes de `gate.MustWait`, así corre con quien
  solicita y otra vez con quien aprueba (la aprobación ejecuta la misma operación con el aprobador como autor).
- `SaveRole`: si el rol es superusuario y el actor no lo es → 403; capacidades del rol antes ∪ después que el actor no
  tiene → 403.
- `SecurityApproval.LoadAsync`: `CanApprove` también exige `CanGrant` (capacidades de los roles que la carga agrega o
  quita, o del rol que cambia) y que el aprobador no sea el beneficiario (`user.*` con `TargetId = yo`; `role.update`
  de un rol que tengo, directo o heredado; `team.*` que me agrega). `ClaimAsync` lo comprueba con el mismo cálculo.
- `applying` atorado: columna `meta.SecurityChange.ClaimedAt`; reclamar acepta `pending` o `applying` con
  `ClaimedAt < ahora − 10 min`, y la lista lo muestra como pendiente.

## 2. Cambios de modelo sin pisarse (`ModelVersion`, `PlanService`, modelador)
- `ModelVersion.Of(EntityDef)` = SHA-256 del JSON canónico (`MetadataStore.Json`) de la definición guardada.
  `ModelVersion.Base(model, boards, entities, dashboards)` = SHA-256 de `nombre=versión` ordenado (catálogo inexistente =
  `-`), con tableros igual.
- `ChangePlan` gana `BaseVersions` (`{ catálogo: versión }`) y `BaseHash`; no se guardan en `meta.ChangeSet`.
- `PreviewAsync`: compila, agrega errores de versión (`«X» cambió desde que lo abriste…`) y devuelve
  `PlanPreview.BaseHash` = `Base(modelo, tocados)`.
- `ApplyAsync` (endpoint del modelador): `BaseHash` obligatorio (400). `ApplyInTransactionAsync` compara, bajo
  `erp-model` y con el modelo de la transacción, versiones (si vienen) y `BaseHash` → 409. Módulos, paquetes y reversiones
  llaman sin huella (tienen la suya).
- `GET /api/model/entities[/name]`: `version` para modeladores. El editor manda `baseVersions: { [original.name]: version }`
  en el plan; `PlanReviewDialog` aplica con el `baseHash` de su vista previa.
- Asistente: `ReviewHash.Of(...)` agrega `baseHash` (calculado igual al revisar y al aplicar dentro de la transacción).
- Estados con registros: tras compilar, por cada `SetWorkflowOp` sobre un catálogo con proceso, los estados que
  desaparecen se cuentan (`SELECT Estado, COUNT(*) … WHERE Estado IN @quitados GROUP BY Estado`, parámetros); con
  registros → error en la vista previa y `ValidationFailed` al aplicar.

## 3. Bandeja: una sola ejecución (`AutomationService`, `RecordService.Background`)
- `meta.AutomationRun.LeaseToken uniqueidentifier NULL`. El reclamo pone `LeaseToken = NEWID()` y lo devuelve; retoma
  `running` vencido solo si `Attempts < @max`. Antes del reclamo, `UPDATE … SET Status = 'failed'` a los vencidos con
  `Attempts >= @max` («se interrumpió N veces sin terminar»).
- `RunLease(id, token)`: `MarkAsync(cn, tx, status, …)` hace `UPDATE … WHERE Id = @id AND LeaseToken = @token AND
  Status = 'running'`; 0 filas → `LeaseLost` (la transacción se revierte, no se reintenta ni se registra nada más).
- Asíncrono: `RunQueuedAsync(…, beforeCommit)`; `beforeCommit` marca `succeeded` en la misma transacción. Fallos,
  cancelaciones y reintentos también con dueño.
- Renovación: un temporizador de 60 s renueva `LeaseUntil` con dueño; si pierde el *lease*, cancela el trabajo.
- Tarea: cada registro, antes de confirmar, renueva el *lease* con dueño en su transacción (sin dueño → se revierte y la
  ocurrencia termina). El cierre de la ocurrencia, con dueño.

## 4. Tareas atadas a su contenido (`ScheduleCalendar.ContentHash`, `PlanService`, `AutomationService`)
- `ContentHash(s)` = SHA-256 de la tarea serializada (calendario, rol, consulta, lote, acciones; la etiqueta y la
  descripción no cuentan). `meta.ScheduleState.ContentHash char(64) NULL`, guardada al activar.
- `ApplyInTransactionAsync`, al final: `PauseChangedSchedulesAsync` lee `ScheduleState` activos; los de tareas que ya
  no existen (o catálogo archivado) o con huella distinta se pausan (`IsActive = 0, NextRunAt = NULL`) con auditoría.
  Huella nula = adopta la actual (tareas activadas antes de la 021).
- Despachador: la ocurrencia no manual compara la huella al empezar; distinta → `canceled`.

## 5. Reversión con seguridad visible (`PlanWarnings.Security`, `EnvironmentService`)
- `PlanWarnings.Security(plan, model)`: los avisos de `For` de `SetSecurity`, `SetWorkflow` con roles,
  `SetAutomations`, `SetWebhooks` y `SetSchedules` (incluye lo que quitan).
- `PackageDiffDto.SecurityChanges` (vacío en paquetes); `ApproveRequest`/`ApplyRequest.ConfirmSecurity`;
  `RequireSecurityConfirmed` → 409. UI: lista y casilla en el diálogo de la importación.

## 6. Contabilidad 1.1.0
- `setWorkflow` de `Poliza` igual al 1.0.0, con `cancelar.when = "Estado = 'Borrador' OR Origen = 'Manual'"` y
  `whenMessage`.

## 7. OData
- `ODataQuery.MaxSkip = 100_000` (400). `ExpandLinesAsync` con `SELECT TOP (@max + 1)`; si pasa → `ODataException`.
  `OData:MaxExpandedLines` (1..5000, predeterminado 5000; la suite usa 10).

## 8. Menores
- Token OAuth: `Audit.WriteAsync(…, "integration.oauthtoken", "OAuthClient", id, after: { clientId, scopes, expiresInMinutes })`
  con el usuario de servicio del cliente.
- Editor: `beforeunload` mientras haya operaciones pendientes; «Cancelar» con cambios abre un diálogo de confirmación.

## Contrato de API (cambios)
```ts
GET  /api/model/entities[/name]      // EntityDef + version?: string (solo modeladores)
type ChangePlan = { description; operations; baseVersions?: Record<string, string>; baseHash?: string }
POST /api/model/plan/preview → PlanPreview & { baseHash?: string }   // errores de versión y de estados con registros
POST /api/model/plan/apply           // 400 sin baseHash · 409 versión o huella distinta
POST /api/admin/users, PUT /users/{id}, POST/PUT /roles   // 403 al otorgar/quitar lo que no se tiene
SecurityChangeDto.canApprove          // también falso sin las capacidades o siendo el beneficiario
POST /api/admin/security-changes/{id}/approve            // 403 ídem
PackageImport.diff.securityChanges: string[]
POST /api/admin/environments/imports/{id}/approve|apply { …, confirmSecurity?: boolean }  // 409 si falta
GET  /odata/{set}?$skip>100000 → 400 · $expand de partidas > OData:MaxExpandedLines → 400
```

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **E** Un administrador de usuarios se asigna superusuario, modelador o auditor | Solo se otorga/quita lo que el actor tiene; superusuario solo por superusuario; la misma regla al aprobar |
| **E** Editar un rol para darle capacidades que el actor no tiene | `SaveRole` compara capacidades antes y después contra las del actor |
| **E/R** Aprobar un cambio que me beneficia | El beneficiario no aprueba (usuario, rol que tengo, equipo que me agrega) |
| **T** Un cambio de modelo borra el de otra persona (lost update) | Versión por catálogo desde que se abre y `baseHash` desde la vista previa, bajo el candado del modelo |
| **T** Efectos dobles de un trabajo (caída entre confirmar y marcar, otra instancia lo retoma) | Éxito en la transacción del trabajo, dueño del *lease* en cada escritura, renovación, tope de intentos |
| **D** Un trabajo que se cae siempre se reintenta para siempre | Tope de intentos también para *lease* vencido |
| **E** Un plan cambia el rol o las acciones de una tarea activa sin que el superusuario la revise | Huella del contenido al activar; el plan pausa la tarea en su transacción |
| **T/E** Una reversión reabre seguridad sin que el aprobador lo note | `securityChanges` y `confirmSecurity` en aprobar y aplicar |
| **T** Descuadre contable al cancelar una póliza automática | La transición solo para manuales o borradores |
| **D** Consultas OData costosas (`$expand` masivo, `$skip` profundo) | Topes con 400 explícito |
| **R** Emisión de tokens sin rastro | Auditoría `integration.oauthtoken` |
| **D** Solicitud de seguridad atorada | `ClaimedAt` y reclamo tras 10 minutos |
| **T** Registros en un estado que deja de existir | Rechazo del plan con el conteo |
