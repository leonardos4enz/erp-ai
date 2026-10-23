# Tareas 006

## Motor
- [x] Modelos: `SecurityPolicy`, `FieldSecurity`, `RecordGrant`, `SetSecurityOp`; `FieldDef.Access/EditableIn` (salida)
- [x] Esquema: `meta.Entity.SecurityJson`, `meta.[User].Unit` (migración idempotente); carga y guardado
- [x] `PlanCompiler`: `setSecurity` y su revalidación en la pasada final
- [x] `RecordFilter`: traducción de `where` y alcances a SQL parametrizado
- [x] `FieldAccess`: acceso por rol y por estado
- [x] `RecordService`: listados, lectura, historial, buscador, referencias, `/compute`, alta, edición, baja y transiciones
- [x] `systemManaged` en el servidor; automatizaciones sin cambios (autoridad del motor)
- [x] Unidad del usuario: `UserContext`, `/auth/me`, administración de usuarios, `/admin/units`, `CURRENTUNIT()`
- [x] `/model/entities`: anotar `access`/`editableIn`, quitar ocultos y la política a quien no modela
- [x] Unitarias: compilador, traductor SQL y acceso por campo

## Paquetes
- [x] Compras 1.3.0 en `upgrades` (F-25, requisiciones propias, importes ocultos al almacenista)
- [x] Inventario 1.1.0 en `upgrades` (Existencias administradas por el sistema, F-04)

## UI
- [x] Tipos de la API
- [x] Formulario y partidas: ocultos, solo lectura por rol y por estado, pistas
- [x] Listado y formulario: `systemManaged`
- [x] Modelador: «Seguridad (JSON)»
- [x] Seguridad › Usuarios: «Unidad»

## Verificación
- [x] E2E por criterio (`seguridad.spec.ts`) y ajuste de pruebas afectadas
- [x] Regresiones corridas contra el código anterior
- [x] `dotnet test` y E2E en verde dos veces
- [x] Actualización real Compras 1.2.0 → 1.3.0 e Inventario 1.0.0 → 1.1.0 en `ErpAi`, revisada en navegador
- [x] `verify.md`, roadmap e índice; revisión de secretos y artefactos; commit y push
