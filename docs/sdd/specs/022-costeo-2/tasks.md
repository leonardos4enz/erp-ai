# Tareas 022

## Pruebas primero
- [x] T1 Unitarias de `CostMath`: `uncostedEntry` (las tres), `methodChange` (las dos), `totalCost` exacto y
  `RemainingQuantity` (`CostingPolicyTests.cs`).
- [x] T2 Unitarias de validación: opciones desconocidas, `quantityField`, `capture` (campo, expresión en el ámbito
  del creado, solo en `create`), ciclo por `capture`, `setRules` con `merge`.
- [x] T3 Unitarias de módulos: Inventario 1.6.0 y Costeo 1.1.0 llegan como actualización y compilan instalados de cero.
- [x] T4 E2E `valuacion-politicas.spec.ts`: un escenario por criterio (defecto y alternativa) y ajuste del escenario
  «almacén sin cuentas» de `valuacion-contable.spec.ts` al nuevo defecto.
- [x] T5 Correr las pruebas nuevas contra `HEAD` y registrar cómo fallan.

## Motor
- [x] T6 `Models`: `UncostedEntry`, `MethodChange`, `TotalCost`, `QuantityField`, `Capture`; `CostPolicies`;
  `SetRulesOp.Merge`.
- [x] T7 `CostMath.Apply` con `CostOptions`; `CostAsync` evalúa `totalCost`, pasa las opciones y escribe `quantityField`.
- [x] T8 `capture` en `RunActionAsync` (`create`), con el id de la partida de origen.
- [x] T9 `AutomationRules`: validación y efectos de `capture` y de las opciones; `PlanCompiler.SetRules` con `merge`.
- [x] T10 Asistente (`AssistantPrompt`) y tipos de la SPA (`types.ts`).

## Módulos
- [x] T11 Inventario 1.6.0 (costo con respaldo solo en MXN, `TipoCambioDeLaRecepcion`, `CostoCalculado`, fecha de la entrada).
- [x] T12 Costeo 1.1.0 (cantidad costeada, promedio y aviso, `Valuar` con opciones, traspaso con `capture`, póliza con
  cuentas o pendiente).

## Cierre
- [x] T13 `dotnet test` y E2E dos veces en verde; revisión exploratoria en el navegador; `verify.md`.
- [x] T14 Roadmap, índice de specs, commit y push.
