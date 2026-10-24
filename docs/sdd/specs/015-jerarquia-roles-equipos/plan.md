# Plan 015 — Diseño técnico

Todo es capacidad genérica del motor y dato del entorno; los módulos no cambian.

## Modelo de datos (migración idempotente en `Bootstrap`)
```sql
meta.BusinessUnit (Id, Name nvarchar(100) UNIQUE, ParentId NULL → BusinessUnit, Path nvarchar(900) NOT NULL, CreatedAt)
  -- Path materializado con ids: '/1/', '/1/4/'. Índice por Path. Las etiquetas existentes en meta.[User].Unit se
  -- insertan como unidades raíz (una vez).
meta.[User] + IsTeam bit NOT NULL DEFAULT 0           -- un equipo es un principal sin sesión (como IsService, 008)
meta.TeamMember (TeamId → User, UserId → User, PK (TeamId, UserId))
meta.RoleInherit (RoleId → Role, ParentRoleId → Role, PK (RoleId, ParentRoleId))
meta.SecurityChange (Id, Kind nvarchar(20), TargetId int NULL, Summary nvarchar(400), PayloadJson, BeforeJson NULL,
  Hash char(64), Status nvarchar(10) 'pending'|'applied'|'rejected', RequestedBy, RequestedAt, ReviewedBy NULL,
  ReviewedAt NULL, ReviewComment nvarchar(500) NULL)
```
`meta.[User].Unit` sigue siendo el nombre de la unidad (texto): `CURRENTUNIT()` y las reglas de la 006 no cambian.

## Seguridad por registro (`RecordFilter`)
- `RecordScopes.UnitTree = "unitTree"`.
- `unitTree`: `owner IN (SELECT u.[Id] FROM meta.[User] u JOIN meta.[BusinessUnit] b ON b.[Name] = u.[Unit]
  WHERE b.[Path] LIKE (SELECT m.[Path] FROM meta.[BusinessUnit] m WHERE m.[Name] = @unit) + N'%')`. La ruta solo tiene
  dígitos y `/` (sin comodines). Sin unidad → `own`, como `unit`.
- `own` con `teamOwners`: `(owner = @uid OR owner IN (SELECT tm.[TeamId] FROM meta.[TeamMember] tm JOIN meta.[User] tu
  ON tu.[Id] = tm.[TeamId] WHERE tm.[UserId] = @uid AND tu.[IsActive] = 1))`. Sin `teamOwners`, igual que antes.
- `unit`/`unitTree` ya incluyen a los equipos: son filas de `meta.[User]` con su unidad.
- Como todo pasa por `RecordFilter.For`/`ForRecordOrParent`, aplica a listados, totales, registro, buscadores,
  referencias, reportes, bitácora (`AuditFilter`) y etiquetas (`SelectClause`) sin más cambios.

## Política del catálogo
`SecurityPolicy += { teamOwners?: boolean }` (requiere `owner`; solo catálogos raíz). `RecordGrant.scope` admite
`unitTree`. `FieldDef += { acceptsTeams?: boolean }` (salida: el campo dueño con `teamOwners`).
- Al guardar, un valor de campo usuario que es un equipo solo se acepta en el campo dueño con `teamOwners` y si el
  equipo está activo; en otro caso «El usuario seleccionado no existe.» (`RelatedLoader.Principal`).
- `GET /api/users/lookup?search=&teams=true` agrega equipos activos (`«Nombre (equipo)»`).
- Personas en buscadores, importación, `LoadUsersAsync`, inicio de sesión y `UserContextLoader`: `IsTeam = 0`.

## Herencia de roles
- `RoleGraph.Expand(direct, edges)` (cierre transitivo) en `UserContextLoader.LoadAsync` y `ServiceRole.LoadAsync`:
  `Roles` = nombres efectivos; permisos por catálogo = unión de los roles efectivos; capacidades administrativas solo de
  los roles directos (los heredables no tienen).
- `RoleRequest/RoleDto += inheritsRoleIds`. Validación: existen, no es el propio, sin ciclo (por el cierre), máximo 10
  directos y 5 niveles, ningún rol del que se hereda tiene capacidades; un rol del que otros heredan no puede ganarlas.

## Unidades y equipos (API, *administrar usuarios*)
```ts
type BusinessUnit = { id: number; name: string; parentId: number | null; depth: number; users: number; teams: number }
GET  /api/admin/units → BusinessUnit[]               // antes string[]; ordenadas por ruta
POST /api/admin/units { name, parentId? } → BusinessUnit
PUT  /api/admin/units/{id} { parentId } → BusinessUnit | 202   // mover: sin ciclo, ≤ 10 niveles, actualiza la ruta del subárbol
type Team = { id: number; name: string; unit: string | null; isActive: boolean; memberIds: number[] }
GET  /api/admin/teams → Team[]
POST /api/admin/teams { name, unit?, memberIds } → Team | 202
PUT  /api/admin/teams/{id} { name, unit?, isActive, memberIds } → Team | 202
User.unit / Team.unit: nombre de una unidad existente (400 «La unidad no existe»).
```
Auditoría: `security.unit`, `security.team` (antes y después).

## Aprobación de cambios de seguridad
`EnvironmentOptions += RequireSecurityApproval` (`Environments:RequireSecurityApproval`);
`SecurityApproval => RequireApproval && RequireSecurityApproval`.

Cada operación valida como siempre y luego decide si **amplía** acceso (spec H4 CA1). Si amplía y la aprobación está
activa, guarda la solicitud en lugar de aplicar:

| Kind | Amplía cuando |
|---|---|
| `user.create` | siempre |
| `user.update` | agrega roles o empresas, cambia la unidad a otra no vacía, reactiva, o pone contraseña a otro |
| `role.create` | trae permisos, capacidades o herencia |
| `role.update` | renombra, agrega un permiso, una capacidad o un rol del que hereda |
| `team.create` | trae miembros |
| `team.update` | agrega miembros, cambia la unidad a otra no vacía o reactiva |
| `unit.move` | el nuevo padre no es vacío |
| `apikey.create` | siempre |
| `apikey.update` | agrega roles |

- **Carga útil**: la misma petición en JSON, con la contraseña reemplazada por su hash PBKDF2 (`passwordHash`) y, en una
  clave de API, el id público y el SHA-256 del secreto generados al solicitar (el secreto se devuelve una sola vez).
- **Antes**: instantánea de lo que decide el acceso (usuario: nombre, activo, roles, unidad, empresas; rol: `RoleDto`;
  equipo: `Team`; unidad: padre; clave: nombre, roles, estado). **Huella** = SHA-256 de `kind`, destino, carga y antes.
- **Respuesta** `202 { securityChange: SecurityChange, secret? }`.
- **Aprobar** (`POST /api/admin/security-changes/{id}/approve { hash, comment? }`): pendiente, quien aprueba ≠ quien
  solicitó, puede aprobar ese tipo (*administrar usuarios*; claves: superusuario) y la huella coincide (si no, 409). Se
  ejecuta la misma operación con la carga guardada y el aprobador como autor, comparando dentro de su transacción el
  antes actual con el guardado (409 «… cambió desde la solicitud»). Al terminar: `applied` y `security.approve`.
  La solicitud se reclama primero (`pending → applying`) para que dos aprobaciones simultáneas no la apliquen dos veces;
  si la operación falla, vuelve a `pending`.
- **Rechazar** (`POST …/{id}/reject { comment? }`): quien solicitó o quien puede aprobar → `rejected`, `security.reject`.
- **Listar** (`GET /api/admin/security-changes?status=pending|all`): `{ required, items }`; quien administra usuarios ve
  las de usuarios, roles, equipos y unidades; las claves, solo el superusuario.
```ts
type SecurityChange = { id; kind; targetId: number | null; summary; status: 'pending' | 'applying' | 'applied' | 'rejected';
  before: object | null; after: object /* carga sin hashes: passwordChanged */; hash; requestedBy; requestedAt;
  reviewedBy?; reviewedAt?; reviewComment?; canApprove: boolean; canReject: boolean }
```

## UI
- *Seguridad*: pestañas **Usuarios**, **Roles y permisos**, **Equipos**, **Unidades** y **Solicitudes** (con conteo y aviso
  cuando el entorno exige aprobación). La unidad del usuario y del equipo se elige de una lista con sangría por nivel.
  El rol muestra «Hereda de» (casillas; los roles con capacidades no se ofrecen). Un 202 muestra «Solicitud enviada:
  otro administrador debe aprobarla». Solicitudes: resumen, quién y cuándo, cambios (antes → después con nombres) y
  *Aprobar*/*Rechazar* con comentario.
- Formulario: el campo dueño con `acceptsTeams` busca personas y equipos.
- *Integraciones › Claves de API*: una clave solicitada muestra el secreto con el aviso «funcionará cuando otro
  administrador apruebe la solicitud».

## Contrato de API (cambios)
```ts
RecordGrant.scope += 'unitTree' · SecurityPolicy += { teamOwners?: boolean } · FieldDef += { acceptsTeams?: boolean }
Role += { inheritsRoleIds: number[] } · RoleInput += { inheritsRoleIds?: number[] }
GET /api/admin/units → BusinessUnit[] (antes string[]) · POST/PUT /api/admin/units · GET/POST/PUT /api/admin/teams
GET /api/users/lookup?teams=true
POST/PUT usuarios, roles, equipos, unidades y claves → 202 { securityChange, secret? } cuando esperan aprobación
GET /api/admin/security-changes · POST /api/admin/security-changes/{id}/approve|reject
```

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **E** Un administrador de usuarios se da (o da a un cómplice) roles, empresas, unidad o equipo | Con aprobación de seguridad, toda ampliación espera a otro administrador; la huella cubre el antes y el después |
| **E** Ampliar por una vía no cubierta (renombrar un rol que una política nombra, herencia, mover una unidad, reactivar, contraseña ajena, clave de API) | Lista cerrada de operaciones que amplían, cada una con prueba; la petición mixta espera completa |
| **T** Aprobar algo distinto de lo revisado, o algo cuyo destino cambió | Huella de carga + antes; el antes se compara dentro de la transacción del cambio |
| **E** Aprobarse a sí mismo o aprobar dos veces | Aprobador ≠ solicitante; reclamo atómico `pending → applying` |
| **I** Ver contraseñas o secretos en una solicitud | Solo hash PBKDF2 de la contraseña y SHA-256 del secreto; la lista muestra `passwordChanged` |
| **S** Iniciar sesión o usar una clave como equipo | `IsTeam = 0` en login, contexto de usuario, servicio y buscadores; un equipo no tiene roles |
| **E** Heredar capacidades administrativas (y evadir la regla de claves de API sin administración) | No se hereda de roles con capacidades ni se les dan a roles heredados |
| **D** Herencia o árbol con ciclos o profundidad excesiva | Validación de ciclo y límites (10 directos, 5 niveles de roles; 10 niveles de unidades) |
| **E** Inyección SQL por unidades o equipos | Nombre de la unidad e id del usuario como parámetros; la ruta solo tiene dígitos y `/` |
| **I** Registro de equipo visible a quien no es miembro | `own` con equipos solo de membresías activas del propio usuario; mismo filtro en todas las vías |
| **R** Cambios de seguridad sin rastro | `security.unit`, `security.team`, `security.request`, `security.approve`, `security.reject` con antes y después |
| **D** Bloquear revocaciones con la aprobación | Reducir acceso nunca espera (incluye rotar y revocar claves, desactivar y quitar roles) |
