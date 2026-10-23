# Tareas 002 + 003

## Motor (spec 002)
- [x] Lenguaje de expresiones: parser propio, evaluador decimal, lógica de tres valores, límites
- [x] Analizador estático (campos, rutas, agregados, funciones, roles) y orden topológico de fórmulas
- [x] Tipos: folio, fórmula, partidas, documento padre, estado, usuario; valor por defecto
- [x] Operaciones `setWorkflow` / `setRules`, validación en pasada final del plan
- [x] Migración idempotente del esquema `meta` (bases de la spec 001 siguen funcionando)
- [x] Motor de documentos: partidas en la misma transacción, defaults, fórmulas, reglas, folios con UPDLOCK
- [x] Transiciones con roles, condiciones, comentario/campos obligatorios, asignaciones e historial
- [x] `/compute` para totales en vivo; filtro por estado con conteos; lookup de usuarios
- [x] Módulos instalables en una transacción (roles → modelo → permisos → datos iniciales)
- [x] Grupo de menú por catálogo
- [x] Pruebas unitarias de seguridad del lenguaje (inyección, anidamiento, funciones desconocidas)

## Módulo de Compras (spec 003)
- [x] Paquete `modules/compras.json`: 6 catálogos pequeños, 2 grandes, 3 documentos con partidas y proceso
- [x] 5 roles con permisos; datos iniciales con referencias resueltas
- [x] UI: partidas, totales en vivo, barra de proceso, historial, filtros por estado, página de módulos

## Verificación ([verify.md](../003-modulo-compras/verify.md))
- [x] Prueba de humo por API con usuarios de cada rol
- [x] Revisión exploratoria en navegador
- [x] E2E: una prueba por regla de negocio R1–R9 + flujo completo
