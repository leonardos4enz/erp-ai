# Plan 016 — Diseño técnico

Todo es capacidad genérica del motor. Los módulos no cambian.

## Metadatos

### Automatización asíncrona
```ts
Automation += { mode?: 'sync' | 'async' }   // omitido = 'sync' (005); se omite al guardar si es 'sync'
```
Validación (`AutomationRules`): `mode` ∈ {sync, async}. El resto, igual que la 005 (acciones, ámbitos, ciclos: el grafo ya
incluye a las asíncronas).

### Tareas programadas (`setSchedules`) — `meta.Entity.SchedulesJson`
```ts
type ScheduleDef = {
  name: string;               // identificador, único en el catálogo
  label: string;              // ≤ 100
  description?: string;       // ≤ 300
  every: 'minutes' | 'hour' | 'day' | 'week' | 'month';
  interval?: number;          // minutes: 5–60 y divide a 60 (5, 10, 12, 15, 20, 30, 60)
  minute?: number;            // hour: 0–59 (por defecto 0)
  at?: string;                // day | week | month: 'HH:mm' (24 h)
  weekdays?: number[];        // week: 1 = lunes … 7 = domingo, sin repetir, 1–7 elementos
  day?: number;               // month: 1–31; un mes sin ese día usa su último día
  where: string;              // consulta: expresión (002) traducible a SQL (traductor de acumulados, 014)
  role: string;               // rol de servicio (con herencia, 015)
  batchSize?: number;         // 1–1000; por defecto 100
  actions: AutomationAction[];// las de la 005 (update, transition, create, rollup; `lines`)
};
Operation += { op: 'setSchedules'; entity: string; schedules: ScheduleDef[] }  // reemplaza la lista; máx. 10; solo catálogos raíz
EntityDef += { schedules?: ScheduleDef[] /* solo modeladores */ }
```
Validación (`ScheduleRules`, pasada final, como los webhooks: un cambio en otro catálogo no puede romper una tarea en
silencio): nombre identificador y único; etiqueta y descripción; calendario según `every` (sin campos de otra frecuencia);
`where` obligatoria, válida (`ExprAnalyzer`) y traducible (`RecordFilter.TranslateRollup`; si no: «la consulta debe poder
resolverse en la base: …»); `role` existente; `batchSize`; acciones con la misma validación que las de una automatización
(`AutomationRules.ValidateActions`, extraída de `Validate`).

**Acciones sobre el propio registro:** `update` y `transition` sin `target` ni `lines` actúan sobre el registro del ámbito
(`AutomationRules.Target` devuelve el propio catálogo; `RunActionAsync` usa su id). Aplica también a las automatizaciones; el
grafo de ciclos ya lo ve como arista del catálogo a sí mismo.

El permiso del rol sobre el catálogo es dato del entorno (`meta.EntityPermission`), no del modelo: se comprueba al ejecutar.

### Traductor: `ADDDAYS(TODAY(), n)`
`RecordFilter.Translator.Value` acepta `ADDDAYS(TODAY(), n)` con `n` literal (también negativo) → parámetro
`DateTime.Today.AddDays(n)`. Aplica a las consultas de tareas, a los acumulados (014) y a las reglas `where` de acceso.

## Esquema (migración idempotente en `Bootstrap`)
```sql
meta.Entity + SchedulesJson nvarchar(max) NULL
meta.ScheduleState (EntityName nvarchar(63), ScheduleName nvarchar(63), IsActive bit, CalendarJson nvarchar(400) NULL,
  NextRunAt datetime2(3) NULL, LastRunId bigint NULL, UpdatedAt, UpdatedBy int NULL, PK (EntityName, ScheduleName))
meta.AutomationRun (Id bigint IDENTITY PK, Kind nvarchar(10) 'async'|'schedule', EntityName nvarchar(63), Name nvarchar(63),
  RecordId bigint NULL, Event nvarchar(20) NULL, EventKey nvarchar(63) NULL, TriggeredBy int NULL → User,
  RunAsRole nvarchar(100) NULL, RunAsName nvarchar(150) NOT NULL, CompanyId int NULL, Generation int NOT NULL DEFAULT 0,
  Manual bit NOT NULL DEFAULT 0, Status nvarchar(12), Attempts int DEFAULT 0, NextAttemptAt datetime2(3), LeaseUntil datetime2(3) NULL,
  StartedAt NULL, FinishedAt NULL, Processed int DEFAULT 0, Succeeded int DEFAULT 0, Failed int DEFAULT 0, Skipped int DEFAULT 0,
  More bit DEFAULT 0, LastError nvarchar(500) NULL, ErrorsJson nvarchar(max) NULL, CreatedAt)
  -- índices (Status, NextAttemptAt) y (Kind, EntityName, Name, Id)
meta.[User]: cuenta de sistema 'sistema.tareas' («Tareas programadas»), IsService = 1, sin roles, contraseña aleatoria
  inutilizable (como las cuentas de las claves de API, 008): no inicia sesión ni aparece en usuarios ni buscadores.
```
Estados de una ejecución: `pending`, `running`, `succeeded`, `partial` (tarea con registros fallidos y correctos),
`failed`, `canceled`.

## Bandeja asíncrona
1. **Encolar** (`RecordService.FireAsync`): una automatización `async` cuyo `when` se cumple no corre sus acciones; hace
   `INSERT meta.AutomationRun (Kind='async', EntityName, Name, RecordId, Event, EventKey, TriggeredBy = usuario,
   RunAsRole = rol de servicio si el contexto es de una tarea, RunAsName, CompanyId = empresa actual,
   Generation = generación del contexto + 1, Status='pending')` en la transacción del evento. Generación > 5 → error
   (falla el trabajo que la provocó). Las síncronas del mismo evento corren como siempre, en orden de declaración.
2. **Reclamar** (`AutomationService.DispatchBatchAsync`, cada `Automations:PollSeconds`, 2 s): `UPDATE TOP (10) …
   WITH (ROWLOCK, READPAST, UPDLOCK) SET Status='running', Attempts+1, LeaseUntil = ahora + 5 min` sobre pendientes
   vencidas o en curso con *lease* vencido (proceso caído). Varias instancias no toman la misma.
3. **Ejecutar** (`RecordService.RunQueuedAsync`): identidad = `UserContextLoader` con el sello actual de `TriggeredBy`
   (o, con `RunAsRole`, la identidad de la tarea). Usuario inactivo o sin acceso a la empresa del evento, catálogo,
   automatización asíncrona o registro inexistentes → `canceled` con el motivo. Transacción nueva; acciones con
   `RunActionAsync` (005) en un `AutoCtx` de generación `Generation`; errores con el mismo formato
   «No se pudo completar «…»: …». *Commit* → `succeeded`.
4. **Reintentos:** transitorio = `SqlException` 1205 (interbloqueo), 1222 (bloqueo vencido), -2 (tiempo agotado), o
   `Conflict` (409, incluso dentro de una cadena: `AutomationFailed.Transient`). Transitorio y quedan intentos →
   `pending` con espera `Automations:RetryDelaysSeconds` (por defecto `30,120,600,3600`: 5 intentos); si no, `failed`.

## Tareas programadas
- **Activar** (`PUT …/schedules/{e}/{n} { active }`): `MERGE meta.ScheduleState`; al activar, `NextRunAt =
  ScheduleCalendar.Next(def, ahora)` y `CalendarJson` = calendario canónico. Pausar deja `NextRunAt` en nulo.
- **Ocurrencias** (en cada vuelta del despachador): por cada estado activo cuya definición existe: si `CalendarJson`
  cambió (un plan cambió el calendario) se recalcula `NextRunAt` sin correr. Si `NextRunAt <= ahora`:
  `UPDATE … SET NextRunAt = Next(def, ahora) WHERE … AND NextRunAt = @leído` (solo una instancia gana; las ocurrencias
  perdidas no se acumulan) y en la misma transacción `INSERT meta.AutomationRun (Kind='schedule', Status='pending', …)`
  salvo que ya haya una ejecución de esa tarea pendiente o en curso (no se enciman).
- **Ejecutar ahora** (`POST …/run`): inserta la ejecución pendiente (`Manual = 1`), también si está pausada; 409 si ya hay
  una pendiente o en curso.
- **Correr** (`RecordService.RunScheduleAsync`): identidad por empresa (en catálogo por empresa, cada empresa activa; si
  no, la predeterminada): `ServiceRole.LoadAsync(rol, empresa)` con `Id` = cuenta de sistema y `Username` =
  `tarea:{Catálogo}.{Nombre}`. Si el rol no puede leer el catálogo → `failed` («El rol «…» no puede leer …») sin tocar
  nada. Selección:
  ```sql
  SELECT TOP (@resto + 1) t.[Id] FROM app.[E] t
   WHERE t.[IsDeleted] = 0 AND (<where traducida, @rf…>) [AND (<alcance de lectura del rol, @sec…>)] ORDER BY t.[Id]
  ```
  Por registro (hasta `batchSize` en total): transacción nueva; se vuelve a leer con `WITH (UPDLOCK, HOLDLOCK)` y la misma
  condición (si ya no la cumple o salió del alcance: `Skipped`); acciones con `RunActionAsync` y *commit*; un error
  revierte solo ese registro y se anota `{ recordId, message }` (máx. 50). Cada registro renueva el *lease* y los
  contadores (el progreso se ve en la bitácora). Más registros que el lote → `More = 1`.
  Resultado: sin fallidos → `succeeded`; con fallidos y correctos → `partial`; solo fallidos → `failed`. Una tarea no se
  reintenta sola: la siguiente ocurrencia vuelve a tomar los registros que siguen cumpliendo la consulta.
- **Purga:** cada hora, ejecuciones cerradas con más de `Automations:RetentionDays` (30).

### Calendario (`ScheduleCalendar`, puro)
`Next(def, desdeUtc, zona)` = primera ocurrencia local estrictamente posterior, convertida a UTC (una hora inexistente
por horario de verano se corre a la siguiente válida). `minutes`: múltiplos de `interval` desde la medianoche; `hour`:
minuto `minute`; `day`: `at`; `week`: `at` en `weekdays`; `month`: `at` el día `day` (o el último del mes).
`Describe(def)` → «Cada 15 minutos», «Cada hora, al minuto 05», «Diaria a las 07:00», «Semanal: lunes y jueves a las
07:00», «Mensual: día 31 a las 23:00 (o el último día del mes)».

## Contrato de API (solo superusuario; auditoría `automation.schedule`, `automation.retry`)
```ts
type ScheduleSummary = { entity; entityLabel; name; label; description?; calendar: string /* Describe */; role; batchSize: number;
  active: boolean; nextRunAt?: string; lastRun?: { id: number; status: RunStatus; finishedAt?: string; processed; succeeded; failed; more: boolean } }
type RunStatus = 'pending' | 'running' | 'succeeded' | 'partial' | 'failed' | 'canceled'
type AutomationRun = { id; kind: 'async' | 'schedule'; entity; entityLabel; name; label; recordId?; event?; eventKey?;
  runAs: string /* nombre de la persona o de la tarea */; manual: boolean; status: RunStatus; attempts: number;
  processed; succeeded; failed; skipped; more: boolean; createdAt; startedAt?; finishedAt?; nextAttemptAt?; lastError? }

GET  /api/admin/automations/schedules                    → ScheduleSummary[]
PUT  /api/admin/automations/schedules/{e}/{name}         { active: boolean } → ScheduleSummary
POST /api/admin/automations/schedules/{e}/{name}/run     → { runId: number }      // 409 si ya hay una pendiente o en curso
GET  /api/admin/automations/runs?kind=&status=&entity=&name=  → AutomationRun[]   // 100 más recientes
GET  /api/admin/automations/runs/{id}                    → { run: AutomationRun; errors: { recordId: number; message: string }[] }
POST /api/admin/automations/runs/{id}/retry              → 204                     // solo async failed | canceled
Operation += setSchedules · EntityDef += schedules · Automation += mode
```

## UI
- *Configuración › Automatizaciones* (`/admin/automations`, solo superusuario; enlace al final de *Configuración* para no
  mover lo que capturan las referencias visuales): pestañas **Tareas programadas** (tabla: tarea y catálogo, calendario, rol, estado con interruptor
  *Activa*, próxima, última con resultado; *Ejecutar ahora*) y **Bitácora** (filtros por tipo y estado,
  tabla, *Actualizar*, *Reintentar*, y *Detalle* en un diálogo con los errores por registro).
- *Modelador*: «Tareas programadas (JSON)» en la sección avanzada.

## Asistente y paquetes
- `AssistantPrompt`: `mode: "async"` en `setAutomations` y la operación `setSchedules`.
- `ModelDiff`: `SetSchedulesOp` cuando cambian; `meta.ScheduleState` no viaja.

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **E** Una tarea actúa con más privilegios de los que debe | Rol de servicio sin superusuario (las capacidades administrativas no aplican); alcance de lectura del rol en la consulta y al volver a leer cada registro; la declara un modelador (plan auditado y aprobado en producción, 009/012) y la activa un superusuario del entorno |
| **E** Seguir actuando a nombre de alguien dado de baja | El trabajo asíncrono recarga a quien lo disparó: inactivo o sin la empresa del evento → `canceled` |
| **S** Iniciar sesión o usar la API como la cuenta de sistema | `IsService = 1` (login la rechaza), sin roles, contraseña inutilizable, fuera de buscadores y *Usuarios*; no tiene clave de API |
| **R** No saber qué tarea cambió un registro | Creador/editor = «Tareas programadas»; auditoría con `tarea:Catálogo.Nombre`; bitácora por ejecución; `automation.schedule`/`automation.retry` |
| **T** Trabajo encolado de un cambio que se revirtió | `INSERT` en la transacción del evento |
| **T** Dos instancias o dos ejecuciones procesan el mismo registro | *Lease* con `READPAST, UPDLOCK`; ocurrencia reclamada con `NextRunAt = @leído`; sin ejecuciones encimadas de la misma tarea; cada registro se vuelve a leer bloqueado con la condición |
| **D** Una consulta o una cadena que no termina | Lote ≤ 1000; consulta solo SQL (sin barrer en memoria); generaciones ≤ 5; profundidad ≤ 5 (005); ≤ 10 tareas por catálogo; reintentos acotados; purga a 30 días |
| **D** Ocurrencias acumuladas tras una caída | Se corre una vez y se agenda la siguiente |
| **I** Inyección por la consulta | Traductor con lista blanca de campos y funciones; valores como parámetros; identificadores de metadatos citados |
| **I** La bitácora expone datos de catálogos | Solo superusuario; guarda id del registro y mensajes del motor, no la carga |
