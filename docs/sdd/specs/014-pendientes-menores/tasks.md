# Tareas 014

## Pruebas primero (deben fallar con el código anterior)
- [x] Unitarias: sugerencias de Compras 1.8.0 con tipo de cambio (H1), traducción del filtro del acumulado (H2),
      `localArchives` en el diff (H4), Teredo (H5)
- [x] E2E `pendientes.spec.ts`: precio sugerido en USD (H1), más de 10 000 orígenes (H2), alta durante una importación y
      folios en bloque (H3), salidas simultáneas del mismo producto (H6 CA1), aviso de cambios directos (H6 CA2)
- [x] E2E `entornos.spec.ts`: aprobar/aplicar sin confirmar los campos locales → 409; con casilla (H4)
- [x] E2E `integraciones.spec.ts`: 400 sin reintento, 429 con reintentos (H5)

## Motor
- [x] Suma del acumulado en SQL
- [x] Folios: vista previa sin secuencia, bloque reservado en la importación
- [x] `LockAutomationTargetsAsync` con claves de `match` en alta, edición y transición
- [x] `ModelDiff.LocalArchives`, confirmación en aprobar/aplicar
- [x] Webhooks: 4xx definitivo, Teredo
- [x] Aviso de `AllowDirectChanges` (arranque y API)

## Módulos
- [x] Compras 1.8.0: sugerencias convertidas, `MonedaCosto` capturable

## UI
- [x] Entornos: sección de campos locales con casilla; aviso de cambios directos

## Documentación
- [x] ADR-0004 (seguridad como dato del entorno)
- [x] Roadmap: 014 y specs planeadas 015+

## Verificación
- [x] Regresiones corridas contra el código anterior
- [x] `dotnet test` y E2E en verde (dos corridas)
- [x] Revisión exploratoria con navegador; `verify.md`; commit
