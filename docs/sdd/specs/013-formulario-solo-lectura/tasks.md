# Tareas 013

## Pruebas primero (deben fallar con el código anterior)
- [x] E2E `seguridad.spec.ts › Spec 013`: vend2 abre la oportunidad pública de vend1 por la UI: campos y partidas
      deshabilitados, sin «Guardar cambios» ni «Eliminar», aviso con el motivo; vend1 la ve editable; `_access` en la API
- [x] Unitarias: `RecordService.AccessOf` (rol, alcance, administrado por el sistema)

## Motor
- [x] `_access` en `LoadOutputAsync` reutilizando `canWrite`; `_access` en `IgnoredKeys`

## UI
- [x] `RecordAccess`; `EntityFormPage` en solo lectura y sin «Eliminar» según `_access`, aviso con el motivo

## Verificación
- [x] Regresión corrida contra el código anterior
- [x] `dotnet test` y E2E en verde
- [x] `verify.md`, roadmap; revisión de secretos y artefactos; commit
