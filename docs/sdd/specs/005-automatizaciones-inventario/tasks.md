# Tareas 005

## Motor
- [x] Modelos: `Automation`, `AutomationAction`, `SetAutomationsOp`, `WorkflowTransition.System`, `FieldDef.LookupFilter`
- [x] Esquema: `meta.Entity.AutomationsJson`, `meta.Field.LookupFilterJson` (migración idempotente); carga y guardado
- [x] `PlanCompiler`: `setAutomations` (con `merge`), validación final de acciones y expresiones, detección de ciclos
- [x] `PlanCompiler`: `lookupFilter`, `system`, campo para mostrar de fórmula de texto
- [x] Motor: eventos en crear/editar/transición, acciones update/transition/create/rollup en la misma transacción
- [x] Profundidad máxima, bloqueos del destino, errores con el nombre de la automatización, auditoría e historial
- [x] Transiciones del sistema: ocultas y rechazadas por API
- [x] Lookup filtrado (`filterField`/`filterValue`)
- [x] Recalcular fórmulas guardadas al agregar o cambiar una fórmula
- [x] Módulos: `requires` y `missingRequirements`
- [x] Unitarias: compilador (válidas, inválidas, ciclos, merge) y escala decimal; el recálculo de fórmulas se cubre en E2E

## Paquetes
- [x] Compras 1.2.0 en `upgrades` (sin tocar el plan base)
- [x] `modules/inventario.json` 1.0.0

## UI
- [x] Modelador: «Automatizaciones (JSON)» validado
- [x] Buscador de referencias con filtro (partida de OC en la recepción)
- [x] Módulos: dependencia visible e instalación bloqueada si falta
- [x] Tipos de la API

## Verificación
- [x] E2E por criterio (`h1…`, Compras 1.2.0, Inventario) y ajuste de pruebas de Compras afectadas
- [x] Regresiones corridas contra el código anterior
- [x] `dotnet test` y E2E en verde dos veces
- [x] Actualización real 1.1.0 → 1.2.0 e instalación de Inventario en `ErpAi`, revisada en navegador
- [x] `verify.md`, roadmap e índice; revisión de secretos y artefactos; commit y push
