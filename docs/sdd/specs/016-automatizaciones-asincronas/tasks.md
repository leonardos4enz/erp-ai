# Tareas 016

## Pruebas primero
- [x] Unitarias `AutomationJobTests`: calendario (`Next` en cada frecuencia, fin de mes, días de la semana, estrictamente
      posterior, sin acumular), `Describe`, validación de `ScheduleRules` (calendario, consulta no traducible, rol, lote,
      acciones), `mode`, `ADDDAYS(TODAY(), n)` traducido como parámetro, consulta de la tarea con alcance del rol,
      clasificación de errores transitorios
- [x] E2E `programadas.spec.ts`: asíncrona encolada y revertida (H1 CA1), ejecución con datos actuales y a nombre de quien
      la disparó (CA2), falla sin afectar a quien guardó y reintento manual (CA3, H4 CA3), cancelación por usuario inactivo
      (CA4), generaciones (CA5); tarea pausada al llegar, activar, ocurrencia al llegar la hora, sin acumular, ejecutar ahora,
      lote y pendientes, alcance del rol, por registro (H2); identidad y auditoría (H3); bitácora y UI (H4); modelador (H5)

## Motor
- [x] Modelos: `Automation.Mode`, `ScheduleDef`, `EntityDef.Schedules`, `SetSchedulesOp`; persistencia (`SchedulesJson`)
- [x] `PlanCompiler`: `setSchedules` y pasada final con `ScheduleRules`; `AutomationRules.ValidateActions` y `mode`
- [x] `RecordFilter`: `ADDDAYS(TODAY(), n)`
- [x] Esquema: `ScheduleState`, `AutomationRun`, cuenta `sistema.tareas`
- [x] `FireAsync`: encolar asíncronas con generación; `AutomationFailed.Transient`
- [x] `RecordService.RunQueuedAsync` y `RunScheduleAsync` (identidad, selección, por registro, contadores)
- [x] `ScheduleCalendar`, `AutomationService` (ocurrencias, reclamo, reintentos, purga, API), `AutomationDispatcher`
- [x] Endpoints `/api/admin/automations/*` con auditoría; `ModelDiff`; salida de `schedules` solo a modeladores
- [x] Prompt del asistente

## UI
- [x] Tipos y endpoints
- [x] Configuración › Automatizaciones: Tareas programadas y Bitácora (con detalle)
- [x] Modelador: Tareas programadas (JSON)

## Documentación
- [x] Roadmap e índice

## Verificación
- [x] Pruebas nuevas corridas contra el código anterior
- [x] `dotnet test` y E2E en verde (dos corridas)
- [x] Revisión exploratoria con navegador; `verify.md`; commit
