# Plan 001 — Diseño técnico

## Stack
- **Backend:** ASP.NET Core (.NET 10), Minimal APIs, `Microsoft.Data.SqlClient` + Dapper.
- **BD:** SQL Server. Esquemas: `meta` (metadatos, seguridad, auditoría; `sys` está reservado por SQL Server) y `app` (tablas dinámicas).
- **Frontend:** React + TypeScript + Vite, React Router, iconos `lucide-react`, CSS con tokens.
- **Pruebas:** xUnit para el núcleo de seguridad.

## Estructura
```
src/Erp.Api/            API (endpoints, auth, middleware)
src/Erp.Api/Metadata/   modelo de metadatos, validación, generador DDL, planes de cambios
src/Erp.Api/Data/       motor CRUD dinámico
src/Erp.Api/Security/   usuarios, roles, permisos
src/Erp.Api/Infrastructure/  BD, bootstrap, errores, middleware de seguridad
tests/Erp.Tests/        pruebas unitarias
web/                    SPA React
```

## Modelo de datos (esquema `meta`)
| Tabla | Propósito |
|---|---|
| `meta.Entity` | Name, Label, LabelPlural, Icon, Description, DisplayField, IsArchived |
| `meta.Field` | EntityId, Name, Label, Type, IsRequired, IsUnique, MaxLength, Precision, Scale, OptionsJson, ReferenceEntity, DisplayOrder, ShowInList, RequiredForRolesJson, IsArchived |
| `meta.[User]` | Username, DisplayName, PasswordHash, IsActive, FailedAttempts, LockoutEnd |
| `meta.Role` | Name, Description, IsSuperuser, CanManageModel, CanManageUsers, CanViewAudit |
| `meta.UserRole` | UserId, RoleId |
| `meta.EntityPermission` | RoleId, EntityName, CanRead, CanCreate, CanUpdate, CanDelete |
| `meta.ChangeSet` | Plan de cambios aplicado: JSON, descripción, usuario, fecha |
| `meta.AuditLog` | At, UserId, Action, EntityName, RecordId, BeforeJson, AfterJson |

Tabla dinámica `app.[Nombre]`: `Id bigint identity`, campos del usuario (NULL),
`CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsDeleted, RowVer rowversion`.

## Contrato de API
Todas las rutas bajo `/api`. Cookie de sesión `erp.auth`. **Toda petición que no sea GET
debe enviar la cabecera `X-ERP-Request: 1`** (defensa CSRF). Errores en formato
ProblemDetails (`{ title, status, detail?, errors?: { campo: [mensajes] }, traceId }`).

### Autenticación
| Método | Ruta | Cuerpo / Respuesta |
|---|---|---|
| POST | `/auth/login` | `{ username, password }` → `Me`. 401 genérico (también si la cuenta está bloqueada, para no permitir enumerar usuarios), 429 por rate limit |
| POST | `/auth/logout` | 204 |
| GET | `/auth/me` | `Me` o 401 |

```ts
type Me = { id: number; username: string; displayName: string; roles: string[];
  isSuperuser: boolean; canManageModel: boolean; canManageUsers: boolean; canViewAudit: boolean;
  entities: Record<string, { read: boolean; create: boolean; update: boolean; delete: boolean }> }
```

### Modelo (requiere `canManageModel` para escribir; leer: cualquier usuario autenticado,
filtrado a entidades con permiso de lectura salvo modeladores)
| Método | Ruta | |
|---|---|---|
| GET | `/model/entities` | `EntityDef[]` (no archivadas; `?includeArchived=true`) |
| GET | `/model/entities/{name}` | `EntityDef` |
| POST | `/model/plan/preview` | `ChangePlan` → `PlanPreview` |
| POST | `/model/plan/apply` | `ChangePlan` → `{ changeSetId }` · 400 si inválido |
| GET | `/model/changes` | `{ id, description, appliedAt, appliedBy, operationCount }[]` |

```ts
type FieldType = 'text'|'longtext'|'integer'|'decimal'|'boolean'|'date'|'datetime'|'choice'|'reference'
type FieldDef = { name: string; label: string; type: FieldType; required?: boolean; unique?: boolean;
  maxLength?: number; precision?: number; scale?: number; options?: string[];
  referenceEntity?: string; showInList?: boolean; requiredForRoles?: string[];
  displayOrder?: number; isArchived?: boolean }
type EntityDef = { name: string; label: string; labelPlural: string; icon: string;
  description?: string; displayField?: string; isArchived: boolean; fields: FieldDef[] }

type Operation =
 | { op: 'createEntity'; name; label; labelPlural; icon?; description?; displayField?; fields: FieldDef[] }
 | { op: 'updateEntity'; entity; label?; labelPlural?; icon?; description?; displayField? }
 | { op: 'archiveEntity'; entity }
 | { op: 'addField'; entity; field: FieldDef }
 | { op: 'updateField'; entity; field; label?; required?; showInList?; options?; requiredForRoles?; displayOrder? }
 | { op: 'archiveField'; entity; field }
type ChangePlan = { description: string; operations: Operation[] }
type PlanPreview = { valid: boolean; errors: { operation: number; message: string }[];
  steps: { operation: number; description: string; sql?: string }[] }
```
Iconos: nombre de icono Lucide en kebab-case (`package`, `users`, `truck`...).

### Datos (permiso por entidad)
| Método | Ruta | |
|---|---|---|
| GET | `/data/{entity}?page=1&pageSize=25&search=&sort=Campo&dir=asc` | `{ items: Row[], total }` |
| GET | `/data/{entity}/{id}` | `Row` |
| GET | `/data/{entity}/lookup?search=` | `{ id, label }[]` (para campos referencia) |
| POST | `/data/{entity}` | `{ [campo]: valor }` → `Row` (201) |
| PUT | `/data/{entity}/{id}` | `{ [campo]: valor, _rowVersion }` → `Row` · 409 conflicto |
| DELETE | `/data/{entity}/{id}` | 204 (borrado lógico) |

`Row = { id, _rowVersion, createdAt, createdBy, updatedAt, updatedBy, [campo]: valor }`.
Referencias se devuelven como `{ campo: id, "campo__label": "texto" }`.

### Administración (requiere `canManageUsers`)
| Método | Ruta | |
|---|---|---|
| GET/POST | `/admin/users` | `User[]` / crear `{ username, displayName, password, roleIds }` |
| PUT | `/admin/users/{id}` | `{ displayName, isActive, roleIds, password? }` |
| GET/POST | `/admin/roles` | `Role[]` / crear |
| PUT | `/admin/roles/{id}` | `{ name, description, canManageModel, canManageUsers, canViewAudit, permissions: EntityPermission[] }` |

### Auditoría (requiere `canViewAudit`)
`GET /audit?entity=&page=&pageSize=` → `{ items: { id, at, user, action, entityName, recordId, before, after }[], total }`

## Análisis de amenazas (STRIDE)
| Amenaza | Vector | Mitigación |
|---|---|---|
| **S**poofing | robo de sesión, fuerza bruta | cookie HttpOnly/Secure/SameSite=Strict, PBKDF2, bloqueo, rate limit |
| **T**ampering | inyección SQL vía nombres de campo/orden | identificadores solo desde metadatos con lista blanca + `[ ]`; `sort` se resuelve contra metadatos; valores parametrizados |
| **T**ampering | CSRF | SameSite=Strict + cabecera obligatoria `X-ERP-Request` |
| **R**epudiation | negar cambios | `meta.AuditLog` (inmutable por trigger) y `meta.ChangeSet` con usuario y fecha |
| **I**nformation disclosure | errores, campos sin permiso | ProblemDetails genérico, filtro de entidades por permiso |
| **D**enial of service | listados enormes | `pageSize` máx. 200, rate limit global |
| **E**levation of privilege | IA o usuario modificando modelo | permiso `canManageModel`; la IA solo propone planes |
| XSS | datos capturados | React escapa por defecto, CSP sin `unsafe-inline` en scripts |

## Decisiones (ADR)
- ADR-0001: Tablas reales generadas por DDL en lugar de EAV.
- ADR-0002: Sesión por cookie en lugar de JWT en el navegador.
