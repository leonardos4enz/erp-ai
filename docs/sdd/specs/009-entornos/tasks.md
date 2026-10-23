# Tareas 009

## Motor
- [x] `ModelDiff` (crear con dependencias y campos diferidos, cambios de campos y catálogos, conflictos, `set*`, tableros)
- [x] Exportación: selección (partidas con su documento), roles citados o con permisos, dependencias, advertencias
- [x] `PackageSigner` (canónico, huella, firma HMAC) y `PackageRules` (id, versión, roles, permisos, seed, firma)
- [x] Esquema: `meta.PackageImport`, `meta.PackageExport`
- [x] `EnvironmentService`: exportar (vista previa y archivo), subir, listar, detalle con diff, aprobar, rechazar, aplicar
- [x] Endpoints `/api/admin/environments/**` (superusuario) y auditoría `environment.*`
- [x] Unitarias: diff (ida y vuelta de Compras e Inventario, cambios, conflictos, ciclos), firma, reglas del paquete

## UI
- [x] Tipos y endpoints (descarga como archivo)
- [x] Configuración › Entornos: paquetes recibidos (subir, revisar, aprobar, rechazar, aplicar) y exportar

## Verificación
- [x] E2E por criterio (`entornos.spec.ts`) con aprobación obligatoria y clave de firma en la suite
- [x] Regresiones corridas contra el código anterior
- [x] `dotnet test` y E2E en verde dos veces
- [x] Revisión exploratoria en `ErpAi` (exportar y reaplicar sobre el propio entorno)
- [x] `verify.md`, roadmap e índice; revisión de secretos y artefactos; commit y push
