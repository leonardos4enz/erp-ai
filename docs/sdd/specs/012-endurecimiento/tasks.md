# Tareas 012

## Pruebas primero (deben fallar con el código anterior)
- [x] E2E `endurecimiento.spec.ts`: bitácora filtrada (H1), transición sin roles en registro ajeno (H2), recepciones
      simultáneas y cancelación en paralelo (H3), etiquetas fuera de alcance (H6), moneda base con registros (H7)
- [x] E2E segunda instancia «producción» sin `AllowDirectChanges` (H4)
- [x] E2E asistente: solicitudes simultáneas contra el límite y huella al aplicar (H5)
- [x] Unitarias: `AuditFilter.Redact`, `EnvironmentOptions.RequireDirectChange`, huella de revisión

## Motor
- [x] `AuditFilter` (condición SQL y redacción) en `/api/audit`
- [x] Transición sin roles con filtro de escritura (servidor y `_workflow`)
- [x] `LockAutomationTargetsAsync` antes de validar transiciones
- [x] `AllowDirectChanges` y bloqueo de plan, módulos y asistente con aprobación
- [x] Asistente: reserva `pending`, huella de revisión
- [x] Etiquetas de referencias con alcance
- [x] Moneda base bloqueada con registros por empresa

## Módulos
- [x] Compras 1.7.0: regla `RecibidoNoExcede`, `Producto.MonedaCosto`

## UI
- [x] Asistente: enviar la huella, mostrar el motivo cuando no se puede aplicar; estado `pending`

## Verificación
- [x] Regresiones corridas contra el código anterior
- [x] `dotnet test` y E2E en verde
- [x] `verify.md`, roadmap; revisión de secretos y artefactos; commit
