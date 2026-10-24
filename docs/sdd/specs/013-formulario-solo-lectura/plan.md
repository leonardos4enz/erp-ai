# Plan 013 — Diseño técnico

## 1. Servidor (`RecordService.LoadOutputAsync`)
- `canWrite` (alcance de escritura del registro: `RecordFilter.For(write: true)` + `MatchesAsync`) se calcula una vez
  para todo registro, antes del bloque de proceso, y lo siguen usando las transiciones (012).
- `row["_access"] = RecordService.AccessOf(e, user.Access(e.Name), canWrite)` (pura):
  `update = !e.SystemManaged && role.Update && canWrite`, `delete = !e.SystemManaged && role.Delete && canWrite`,
  `reason = canWrite ? null : CannotWrite` (el mismo texto del 403).
- `_access` se agrega a `IgnoredKeys` (un cliente que reenvía el registro no lo convierte en campo).

## 2. SPA (`EntityFormPage`)
- `RecordAccess` en `api/types.ts`. `readOnly` suma `row._access?.update === false`; `canDelete` suma
  `row._access?.delete !== false`. Sin `_access` (servidor anterior) se comporta como antes.
- Aviso: si el bloqueo viene del alcance (rol con permiso de edición), «Solo lectura» con `_access.reason`; los avisos de
  estado, sistema y rol no cambian.
- `applyRow` ya reemplaza la fila tras guardar o ejecutar una transición (CA4).

## Contrato de API (cambios)
```ts
type RecordAccess = { update: boolean; delete: boolean; reason: string | null };
GET|POST|PUT /api/data/{entity}[/{id}]       → { ...; _access: RecordAccess }
POST /api/data/{entity}/{id}/transition      → { ...; _access: RecordAccess }
```

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **E** Un cliente ignora `_access` y guarda o elimina | Sin cambio: `EnsureAccessAsync(write: true)` responde 403 (CA5, cubierto por `seguridad › H2 CA2`) |
| **I** `_access` revela algo del registro | Solo dice lo que el propio usuario puede hacer con un registro que ya ve; fuera del alcance de lectura sigue el 404 |
| **I** `_access` sale en webhooks con los permisos del rol de servicio | `Trim` descarta las claves `_*` |
| **T** El cliente envía `_access` como dato | En `IgnoredKeys` |
| **D** Costo de la consulta extra | Una consulta por registro en el detalle (ya se hacía con proceso); no en el listado |
