# Tareas 017

## Pruebas primero
- [x] Unitarias `RollbackTests`: `restoreEntity` y `restoreField` (válidas, errores, pasada final: referencia a archivado,
      partida sin documento, índice por empresa, ida y vuelta archivar → reactivar); diff que reactiva en vez de conflicto
      (y la estructura distinta sigue en conflicto); `ModelDiff.Revert` (lo creado se archiva en orden, lo cambiado vuelve,
      lo archivado se reactiva, tableros, deriva, campos locales con *después*, conflicto de proceso); ida y vuelta de la
      instantánea; revertir y volver a aplicar da diff vacío
- [x] Actualizar `EnvironmentTests`: reactivar un campo ya no es conflicto
- [x] E2E `rollback.spec.ts`: instantánea al aplicar (H1), revertir con diff, archivar lo creado y reactivar lo archivado
      (H2), deriva y campos locales (CA5), versión y estados (CA6), roles conservados (CA7), cuatro ojos y auditoría (H3),
      huella alterada (H3 CA3), modelador y asistente (H4), UI, accesibilidad y móvil
- [x] Actualizar `entornos.spec.ts` (H2 CA3): el conflicto ahora es quitar un proceso; reactivar se prueba en la 017

## Motor
- [x] Modelos: `RestoreEntityOp`, `RestoreFieldOp`
- [x] `PlanCompiler`: operaciones y pasada final; `DdlGenerator.RestoreIndexes`; `PlanService` recalcula fórmulas
- [x] `ModelDiff`: reactivar, `DiffOptions` (archivar alcance, `Applied`), `Revert` y orden de archivado
- [x] Esquema: `meta.ModelSnapshot`, columnas de `PackageImport`
- [x] `EnvironmentService`: instantáneas al aplicar; `RevertAsync`; detalle, aprobar, rechazar y aplicar por tipo;
      `canRevert`; auditoría
- [x] Endpoint `POST /imports/{id}/revert`
- [x] Prompt y advertencias del asistente

## UI
- [x] Tipos y endpoints
- [x] Entornos: tabla, diálogo de paquete aplicado (instantánea, revertir) y de reversión
- [x] Modelador: reactivar catálogo y campo

## Documentación
- [x] `verify.md`, roadmap e índice
