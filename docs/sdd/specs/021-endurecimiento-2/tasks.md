# Tareas 021

## Pruebas primero (deben fallar con el código anterior)
- [x] E2E `endurecimiento-2.spec.ts`: escalamiento (H1 CA1–CA3), aprobación sin capacidades y beneficiario (H1 CA4–CA5)
- [x] E2E: plan con versión vieja y sin/con `baseHash` distinto (H2), estado con registros (H8 CA3)
- [x] E2E: trabajo con *lease* vencido y sin intentos (H3 CA4), efectos y estado juntos (H3 CA1)
- [x] E2E: tarea activa que cambia por plan o se re-declara queda en pausa (H4)
- [x] E2E: reversión con `securityChanges` exige `confirmSecurity` (H5)
- [x] E2E: póliza automática aplicada no se cancela (H6); OData `$skip` y `$expand` (H7); token auditado y `applying` vencido (H8)
- [x] E2E UI: editor con catálogo cambiado por otra persona; aviso al cancelar con cambios
- [x] Unitarias: `AdminCapabilities` (`Or`, `MissingFor`), `RequireSecurityConfirmed`, `ModelVersion`, `ScheduleCalendar.ContentHash`, `PlanWarnings.Security`, `ODataQuery` `$skip`

## Motor
- [x] H1: regla de otorgar en usuarios y roles; `canApprove`/`ClaimAsync`; beneficiario; `ClaimedAt`
- [x] H2: `ModelVersion`, `version` en el modelo, `BaseVersions`/`BaseHash`, huella en el asistente; estados con registros
- [x] H3: `LeaseToken`, éxito en la transacción, dueño en cada escritura, renovación, tope de intentos
- [x] H4: `ContentHash`, pausa en la transacción del plan, cancelación de ocurrencias cambiadas
- [x] H5: `PlanWarnings.Security`, `securityChanges`, `confirmSecurity`
- [x] H7: topes de OData
- [x] H8: auditoría del token OAuth

## Módulos
- [x] Contabilidad 1.1.0: «Cancelar póliza» solo manual o borrador

## UI
- [x] Modelador: `baseVersions` y `baseHash`; aviso de cambios sin aplicar
- [x] Entornos: lista de cambios de seguridad y casilla en reversiones

## Verificación
- [x] Regresiones corridas contra el código anterior
- [x] `dotnet test` y E2E en verde dos veces
- [x] Revisión exploratoria con navegador (F-01 corregido con regresión)
- [x] `verify.md`, roadmap; revisión de secretos y artefactos; commit y push
