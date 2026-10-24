# Tareas 015

## Pruebas primero
- [x] Unitarias: `RoleGraph` (cierre, ciclos, niveles), traducción de `unitTree` y `own` con equipos, validación de la
      política (`unitTree`, `teamOwners`), decisión «amplía o reduce» de cada tipo de cambio, huella de la solicitud
- [x] E2E `jerarquia.spec.ts`: árbol de unidades (H1), equipos dueños (H2), herencia (H3), una sola regla en listados,
      buscadores, reportes, bitácora y etiquetas (H5), y aprobación de seguridad en una instancia aparte (H4)

## Motor
- [x] Esquema: `BusinessUnit`, `User.IsTeam`, `TeamMember`, `RoleInherit`, `SecurityChange`; unidades desde las etiquetas
- [x] `RecordFilter`: `unitTree` y `own` con equipos; `SecurityRules`: alcance y `teamOwners`
- [x] `RoleGraph` en `UserContextLoader` y `ServiceRole`; equipos fuera de login, contexto, listados y buscadores
- [x] Campo dueño con equipos (`RelatedLoader.Principal`, `acceptsTeams`, buscador con `teams=true`)
- [x] API de unidades y equipos con auditoría; herencia en roles
- [x] `SecurityApproval`: solicitar, listar, aprobar con huella, rechazar; en usuarios, roles, equipos, unidades y claves
- [x] `EnvironmentOptions.RequireSecurityApproval`; prompt del asistente con `unitTree` y `teamOwners`

## UI
- [x] Tipos y endpoints
- [x] Seguridad: Equipos, Unidades, Solicitudes; unidad de una lista; «Hereda de»; aviso de solicitud enviada
- [x] Formulario: dueño persona o equipo
- [x] Integraciones: clave solicitada

## Documentación
- [x] ADR-0005 (aprobación de cambios de seguridad) y estado de ADR-0004
- [x] Roadmap e índice

## Verificación
- [x] Pruebas nuevas corridas contra el código anterior
- [x] `dotnet test` y E2E en verde (dos corridas)
- [x] Revisión exploratoria con navegador; `verify.md`; commit
