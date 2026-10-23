# ADR-0002 — Sesión por cookie en lugar de JWT en el navegador

**Estado:** Aceptado · **Fecha:** 2026-10-23

## Contexto
La SPA y la API se sirven desde el mismo origen. Guardar un JWT en `localStorage` lo expone
a cualquier XSS y dificulta revocarlo.

## Decisión
Cookie de autenticación de ASP.NET Core: `HttpOnly`, `Secure`, `SameSite=Strict`, 8 h deslizantes.
En cada petición se valida contra la BD que el usuario siga activo y que su `SecurityStamp`
no haya cambiado. CSRF: SameSite=Strict + cabecera obligatoria `X-ERP-Request: 1` en métodos
que modifican estado.

## Consecuencias
- (+) Revocación inmediata al desactivar usuario, cambiar contraseña o roles.
- (+) El token no es accesible desde JavaScript.
- (−) Una consulta a BD por petición (barata; se puede cachear unos segundos si hiciera falta).
- (−) Para clientes externos (apps móviles, integraciones) se necesitará otro esquema (spec futura).
- Las llaves de Data Protection deben persistirse y protegerse en producción (p. ej. en un
  directorio compartido cifrado o Azure Key Vault) si hay varias instancias.
