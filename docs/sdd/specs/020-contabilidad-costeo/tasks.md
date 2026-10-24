# Tareas 020

## Pruebas primero (unitarias)
- [x] `CostMath`: entrada promedio y FIFO, salida parcial y total, faltante con y sin respaldo, entrada sin costo, redondeo exacto
- [x] `LedgerRules`: libro válido e inválido (campos, tipos, estados, periodo, empresa) en la vista previa
- [x] `AutomationRules`: acción `cost` y `create` con `createLines`/`transition` válidas e inválidas; efectos para ciclos
- [x] `ExprAnalyzer`: `UNITCOST` con catálogo literal existente
- [x] Módulos: Compras + Inventario + Contabilidad + Costeo compilan con todas sus actualizaciones; Inventario 1.4 → 1.5

## Motor
- [x] Modelos: `LedgerDef`, `SetLedgerOp`, campos nuevos de `AutomationAction`, `AutomationActions.Cost`
- [x] `Bootstrap`: `meta.Entity.LedgerJson`, `meta.CostLayer` con índice filtrado (idempotente)
- [x] `PlanCompiler`/`PlanService`/`MetadataStore`/`ModelDiff`: `setLedger` de punta a punta; pasada final de libros
- [x] `RecordService.Ledger.cs`: cuadre, periodo con `HOLDLOCK`, traslape; enganchado en alta, edición, transición, motor y baja
- [x] `RecordService.Costing.cs`: acción `cost` (capas, saldo, bloqueo por llave) y `UNITCOST` en `RelatedLoader`
- [x] `create` con `createLines` y `transition`
- [x] Asistente: `setLedger`, `cost`, `createLines`, `UNITCOST` en el prompt

## Módulos
- [x] Inventario 1.5.0 (costo de entrada, CONVERT, traspaso con UNITCOST)
- [x] `modules/contabilidad.json` 1.0.0 (cuentas, periodos, pólizas, saldos, balanza, tarea en pausa, roles, seed)
- [x] `modules/costeo.json` 1.0.0 (método, valuación, valor de existencias, cuentas del almacén, póliza de recepción)

## UI
- [x] Modelador: JSON del libro en la sección avanzada; tipos de `api/types.ts`

## E2E (`valuacion-contable.spec.ts`, corre después de Inventario)
- [x] H4: instalar Contabilidad y Costeo desde la UI; Costeo explica que requiere Contabilidad
- [x] H1: póliza descuadrada no se aplica (UI muestra totales); cuadrada se aplica y mueve saldos; cancelar los revierte
- [x] H1: sin periodo / periodo cerrado rechaza aplicar y cancelar; traslape; reabrir solo administrador
- [x] H2/H5: promedio y FIFO por movimientos; valor y costo promedio de la existencia; salida sin capas; traspaso
- [x] H3/H5: recepción en moneda extranjera → entrada a moneda base con CONVERT y póliza automática aplicada
- [x] H5: almacén sin cuentas: sin póliza; capas por empresa
- [x] Accesibilidad de pólizas y periodos; referencias visuales revisadas en corrida completa (sin cambios)

## Verificación
- [x] `dotnet test` y `npm --prefix web run e2e` en verde, dos corridas limpias
- [x] Revisión exploratoria en el navegador integrado → `verify.md`
- [x] Roadmap, índice del README, commit y push
