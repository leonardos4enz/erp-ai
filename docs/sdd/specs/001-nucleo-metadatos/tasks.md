# Tareas 001

## Fase A — Núcleo (seguridad crítica, con pruebas primero)
- [x] A1 `Identifiers`: lista blanca, palabras reservadas, columnas de sistema, `Quote` defensivo
- [x] A2 `DdlGenerator`: CREATE TABLE / ADD COLUMN, índices únicos filtrados, FKs
- [x] A3 `PlanCompiler`: validación pura de planes, modelo resultante, vista previa con SQL
- [x] A4 Pruebas unitarias A1–A3 (incluye intentos de inyección)

## Fase B — Persistencia
- [x] B1 Bootstrap idempotente: esquemas `meta`/`app`, tablas, trigger de auditoría inmutable
- [x] B2 `MetadataStore` con caché e invalidación
- [x] B3 `PlanService.Apply`: `sp_getapplock`, recompilación en transacción, ChangeSet, auditoría

## Fase C — Seguridad
- [x] C1 PBKDF2, política de contraseñas (NIST), bloqueo 5 intentos / 15 min
- [x] C2 Cookie segura + revalidación por SecurityStamp
- [x] C3 CSRF por cabecera, CSP y cabeceras de endurecimiento, rate limiting
- [x] C4 RBAC: permisos por catálogo, capacidades de rol, último superusuario protegido

## Fase D — Datos
- [x] D1 Listado con búsqueda, orden y paginación resueltos contra metadatos
- [x] D2 Alta/edición con conversión tipada, reglas "obligatorio para rol", únicos, rowversion
- [x] D3 Borrado lógico con verificación de referencias
- [x] D4 Lookup para campos referencia

## Fase E — UI
- [x] E1 Login, layout, menú dinámico, tema claro/oscuro
- [x] E2 Listado y formulario dinámicos
- [x] E3 Modelador con vista previa del plan
- [x] E4 Usuarios y roles, auditoría

## Fase F — Verificación ([verify.md](verify.md))
- [x] F1 Pruebas de API portadas a Playwright (`api-security.spec.ts`)
- [x] F2 Un escenario E2E por criterio de aceptación (H1–H6)
- [x] F3 Accesibilidad automática (axe, WCAG 2.1 AA) y guardia de errores JS/CSP
- [x] F4 Regresión visual claro/oscuro/móvil con referencias revisadas
- [x] F5 Revisión exploratoria: 9 hallazgos resueltos, cada uno con prueba

## Pendiente (siguientes specs)
- [x] Spec 002: motor de procesos (estados, aprobaciones), fórmulas, reglas, módulos
- [x] Spec 003: módulo de Compras
- [x] Spec 011: asistente IA (Claude, [ADR-0003](../../../adr/0003-asistente-ia-anthropic.md)) que genera `ChangePlan` → vista previa → confirmación humana
- [ ] Prueba automática de zona horaria para campos fecha-hora (F-02)
- [ ] 2FA (TOTP) para roles con permisos de modelo/usuarios
