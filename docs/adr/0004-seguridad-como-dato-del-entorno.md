# ADR-0004 — Roles, permisos y usuarios son dato de cada entorno (sin cuatro ojos por ahora)

**Estado:** Aceptado, revisado por [ADR-0005](0005-aprobacion-de-cambios-de-seguridad.md) (cuatro ojos opcionales) · **Fecha:** 2026-10-23 · **Spec:** [014](../sdd/specs/014-pendientes-menores/spec.md) (H7)

## Contexto
Con `Environments:RequireApproval` (spec 009) el **modelo** solo cambia por paquetes que aprueba otro administrador, y la
012 cerró los atajos (planes, módulos, asistente). La auditoría posterior a la 013 señaló que
`PUT /api/admin/roles/{id}` cambia los permisos CRUD de un rol en vivo, con una sola persona, también en producción.

Opciones evaluadas:
1. **Exigir la aprobación de otro administrador** para cambiar permisos de roles cuando `RequireApproval` está activo.
2. **Mantener la seguridad como dato del entorno** y registrarlo como decisión.

## Decisión
Opción 2. Los roles, sus permisos, los usuarios con sus roles y empresas, y las claves de API se administran en cada
entorno por quien tiene *administrar usuarios*, como hasta ahora. Los paquetes solo crean roles nuevos con sus permisos
sobre los catálogos que traen (009); los permisos de un rol existente sobre un catálogo existente no viajan.

Razones:
- **Cuatro ojos solo en los permisos no protege nada.** Asignar roles a usuarios, crear usuarios y emitir claves de API
  siguen en vivo; quien administra usuarios se daría el rol que ya tiene los permisos que busca. Una compuerta parcial da
  una falsa sensación de control.
- **Es otro tipo de cambio.** El modelo se construye en un entorno y se promueve; la seguridad depende de las personas
  de cada entorno (en pruebas no están los usuarios de producción) y debe poder ajustarse de inmediato, p. ej. al
  revocar el acceso de alguien que se va.
- **El registro ya existe.** Todo cambio de rol y de usuario queda en la auditoría (`security.role`, `security.user`) con
  antes y después, que no se edita desde la app (constitución II.4).

## Consecuencias
- (−) En producción, un solo administrador de usuarios puede ampliar permisos. Mitigación: auditoría inmutable y
  *administrar usuarios* separado de *administrar el modelo* y del superusuario.
- (+) Revocar accesos sigue siendo inmediato.
- ~~Pendiente para la spec 015~~ Resuelto en la 015 ([ADR-0005](0005-aprobacion-de-cambios-de-seguridad.md)): con
  `Environments:RequireSecurityApproval`, todo lo que amplía acceso (roles, asignaciones, equipos, unidades, claves de API)
  espera la aprobación de otro administrador con huella; revocar sigue siendo inmediato. Sin esa opción, rige este ADR.
