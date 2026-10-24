# ADR-0005 — Aprobación de cambios de seguridad que amplían acceso

**Estado:** Aceptado · **Fecha:** 2026-10-23 · **Spec:** [015](../sdd/specs/015-jerarquia-roles-equipos/spec.md) (H4) ·
**Revisa:** [ADR-0004](0004-seguridad-como-dato-del-entorno.md)

## Contexto
El ADR-0004 dejó roles, permisos, usuarios y claves de API como dato de cada entorno, sin cuatro ojos: una compuerta solo
en los permisos de un rol no protegía nada mientras asignar roles, crear usuarios y emitir claves siguieran en vivo. Dejó
pendiente una aprobación **genérica** para la spec 015, que además agrega tres vías nuevas de ampliar acceso: la herencia
de roles, los equipos dueños de registros y el árbol de unidades.

## Decisión
1. **Opcional y aparte.** `Environments:RequireSecurityApproval` (configuración del servidor, como `RequireApproval`) y
   solo en un entorno que ya exige aprobación del modelo. `AllowDirectChanges` no la omite: es del modelo.
2. **Ampliar espera, reducir no.** Una lista cerrada de operaciones amplía acceso (altas de usuarios, roles con permisos,
   equipos con miembros y claves; agregar roles, empresas, permisos, capacidades, herencia o miembros; cambiar la unidad;
   reactivar; renombrar un rol; mover una unidad bajo otra; poner contraseña a otra persona). Esas quedan como
   **solicitud**. Lo que solo reduce (quitar, desactivar, revocar, rotar una clave, llevar una unidad a la raíz) se aplica
   de inmediato, como pedía el ADR-0004. Una petición mixta espera completa.
3. **Huella y antes.** La solicitud guarda la petición validada (contraseñas solo como hash PBKDF2; de una clave, solo el
   SHA-256 del secreto) y una instantánea de lo que decide el acceso del destino. Otro administrador (para claves, otro
   superusuario) aprueba con la huella; la operación se ejecuta con la carga guardada y compara el antes **dentro de su
   transacción**: si el destino cambió, 409 y se vuelve a solicitar. Aprobar aplica; no hay tercer paso.
4. **Auditoría.** `security.request`, `security.approve` y `security.reject`, además del cambio aplicado con su antes y
   después (el autor es quien aprueba).

## Consecuencias
- (+) Con la opción activa, ninguna persona sola amplía su acceso ni el de otro por ninguna vía de la app.
- (+) Revocar sigue siendo inmediato; rotar una clave filtrada también.
- (−) Con un solo administrador nada se aprueba: se da de alta al segundo antes de activar la opción, o se desactiva en
  la configuración del servidor (fuera del alcance de la app, como `AllowDirectChanges`).
- (−) Reducir no espera: un administrador puede desactivar a otros (denegación de servicio, no escalada). Queda auditado.
- (−) Poner contraseña a otra persona espera; en producción, un restablecimiento necesita a dos administradores.
- El ADR-0004 sigue vigente en lo demás: la seguridad es dato del entorno y no viaja en paquetes.
