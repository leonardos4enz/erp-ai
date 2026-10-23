# Tareas 010

## Motor
- [x] Esquema: `meta.Company`, `meta.UserCompany`, `meta.[User].CompanyId`, `meta.ExchangeRate`, `meta.Entity.PerCompany`; empresa predeterminada y asignación de usuarios
- [x] Metadato `perCompany`: modelo, almacén, compilador (partidas, volver a compartido), DDL (columna, FK, índices, únicos por empresa)
- [x] `ModelDiff`: `perCompany` al crear y al cambiar; conflicto al volver a compartido
- [x] `UserContext` con empresa actual y permitidas; `POST /auth/company`; `Me` con empresas
- [x] `RecordFilter`: filtro de empresa (también superusuario) combinado con los permisos 006
- [x] `RecordService`: `CompanyId` al crear, acumular en la misma empresa, contexto de empresa en expresiones y al recalcular
- [x] Expresiones: `BASECURRENCY`, `CURRENTCOMPANY`, `RATE`, `CONVERT`; validación en fórmulas de catálogos compartidos
- [x] Endpoints de empresas, tipos de cambio y empresas de usuarios; claves de API y webhooks con empresa
- [x] Unitarias: compilador y DDL, filtro, expresiones con tipos de cambio, diff

## Módulos
- [x] Compras 1.6.0 e Inventario 1.4.0 (entradas en `upgrades`)

## UI
- [x] Tipos y endpoints
- [x] Selector de empresa en la barra superior y empresa en el menú de usuario
- [x] Configuración › Empresas (empresas y tipos de cambio)
- [x] Empresas en el diálogo de usuario; «Un registro por empresa» en el modelador

## Verificación
- [x] E2E por criterio (`multiempresa.spec.ts`)
- [x] Regresiones corridas contra el código anterior
- [x] `dotnet test` y E2E en verde dos veces
- [x] Revisión exploratoria en `ErpAi`
- [x] `verify.md`, roadmap e índice; revisión de secretos y artefactos; commit y push
