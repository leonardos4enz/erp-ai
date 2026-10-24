# Plan 018 — Diseño técnico

## Piezas
```
 Integración                         ERP
 ───────────                         ───────────────────────────────────────────────────────────────────────
 POST /oauth/token  ───────────────▶ OAuthTokenEndpoint: límite por IP → cliente (SHA-256 del secreto, vigente)
   client_id + secreto                 → alcances pedidos ⊆ del cliente → token opaco erpat_… (SHA-256 en
 ◀── access_token (15 min)             meta.OAuthToken, vence en N min) → auditoría solo de altas/rotaciones
 GET /odata/Contacto?$filter=…  ───▶ esquema «erp»: Bearer erpat_ → OAuthTokenHandler (token vigente, cliente vigente,
   Authorization: Bearer erpat_…       cuenta de servicio activa) → UseBearerScope: ruta/método ⊆ alcances del token
                                     → ODataEndpoints → ODataQuery.Parse (lista blanca) → RecordService.ODataAsync:
                                       mismo SelectClause/RecordFilter/FieldAccess que la API de datos
```
Nada conoce un catálogo concreto: el documento CSDL, las propiedades y los filtros salen del modelo en cada petición.

## Esquema (migración idempotente en `Bootstrap`)
```sql
meta.OAuthClient (Id int PK, Name nvarchar(100), ClientId char(20) UNIQUE,   -- 'erpcl_' + 14 hex
                  SecretHash varbinary(32), PreviousSecretHash varbinary(32) NULL, PreviousExpiresAt datetime2(3) NULL,
                  Scopes nvarchar(200),                                       -- lista separada por espacios (RFC 6749 §3.3)
                  TokenMinutes int, UserId int FK User, CreatedBy int FK User, CreatedAt, RotatedAt NULL,
                  RevokedAt NULL, LastTokenAt NULL)
meta.OAuthToken  (Id bigint PK, ClientId int FK OAuthClient, TokenHash varbinary(32) UNIQUE, Scopes nvarchar(200),
                  IssuedAt datetime2(3), ExpiresAt datetime2(3), RevokedAt NULL)          -- índice (ClientId, ExpiresAt)
```
El secreto (`erpcs_` + 43 base64url) y el token (`erpat_` + 43 base64url) son 256 bits de `RandomNumberGenerator`; se
guarda su SHA-256 y se comparan con `FixedTimeEquals`. Al emitir se borran tokens vencidos hace más de un día (purga
barata, sin servicio en segundo plano). Un cliente usa una cuenta de servicio `meta.[User]` (`svc.<clientId>`,
`IsService = 1`) igual que una clave de API (008): roles, empresa de quien lo creó (010) y `IsActive = 0` al revocar.

## Contrato

### Administración (solo superusuario, CSRF y cookie como el resto de `/api/admin`)
```ts
GET    /api/admin/oauth-clients            → OAuthClient[]
POST   /api/admin/oauth-clients            { name, roleIds: number[], scopes: Scope[], tokenMinutes?: 5..60 }
       → 200 { client: OAuthClient, secret }   | 202 { securityChange, secret }   (015: espera aprobación)
PUT    /api/admin/oauth-clients/{id}       { name, roleIds, scopes, tokenMinutes }
       → 200 OAuthClient                    | 202 { securityChange }   (si agrega roles o alcances)
POST   /api/admin/oauth-clients/{id}/rotate { graceHours: 0 | 24 } → { client, secret }
POST   /api/admin/oauth-clients/{id}/revoke → 204
GET    /api/admin/oauth-clients/endpoints  → { token: "<origen>/oauth/token", odata: "<origen>/odata/" }

type Scope = 'odata.read' | 'data.read' | 'data.write';
type OAuthClient = { id, name, clientId, roleIds, username, scopes: Scope[], tokenMinutes, createdAt, createdBy,
                     rotatedAt?, previousExpiresAt?, revokedAt?, lastTokenAt?, activeTokens: number,
                     status: 'active' | 'revoked' };
```
Validación: nombre 1..100; roles existentes y sin superusuario, modelo ni usuarios (la misma regla de la 008); alcances
de la lista, sin repetir, al menos uno; `tokenMinutes` 5..60. Solicitudes de seguridad nuevas: `oauthclient.create` y
`oauthclient.update` (las revisa solo un superusuario, como `apikey.*`). Ampliar = roles nuevos **o** alcances nuevos.
Quitar roles o alcances se aplica ya: el handler lee alcances del token ∩ alcances actuales del cliente en cada petición.

Auditoría `integration.oauthclient` con `action` = `create | update | rotate | revoke` (nunca secreto ni hash).

### Servidor de tokens
```
POST /oauth/token   Content-Type: application/x-www-form-urlencoded
     grant_type=client_credentials [&scope=odata.read data.read] [&client_id=…&client_secret=…]
     Authorization: Basic base64(client_id:client_secret)     (preferida; cuerpo como alternativa, no ambos)
200  { access_token, token_type: "Bearer", expires_in, scope }     Cache-Control: no-store, Pragma: no-cache
400  { error: "invalid_request" | "unsupported_grant_type" | "invalid_scope", error_description }
401  { error: "invalid_client", error_description }   (+ WWW-Authenticate: Basic realm="erp" si usó Basic)
429  límite por IP (política "oauth", Security:TokenRequestsPerMinute, 30 por defecto)
```
`AllowAnonymous`, fuera de `/api` (sin cabecera anti-CSRF: no usa cookies). El cuerpo tiene un tope de 4 KB. Se rechaza
un `Content-Type` distinto de formulario (`invalid_request`). Un token emitido no se audita uno por uno (sería una
escritura por llamada); queda `LastTokenAt` en el cliente y la fila del token.

### Autenticación con token
`ForwardDefaultSelector`: `Bearer erpat_…` → esquema `OAuth`; otro `Bearer` → `ApiKey`; sin `Bearer` → cookie.
`OAuthTokenHandler`: formato → fila por `TokenHash` → `RevokedAt IS NULL AND ExpiresAt > ahora` → cliente sin revocar →
`LoadServiceAsync` (cuenta activa). Alcances efectivos = del token ∩ del cliente; van en un claim `scope`. Cualquier
falla: 401 con `WWW-Authenticate: Bearer error="invalid_token"`, sin decir cuál.

### Reja por superficie (`UseBearerScope`, reemplaza `UseApiKeyScope`)
| Credencial | Permitido |
|---|---|
| Clave de API (008) | `/api/data`, `/api/reports`, `/api/auth/me` y ahora `GET /odata` |
| Token con `odata.read` | `GET /odata/**` |
| Token con `data.read` | `GET /api/data/**`, `GET /api/reports/**` (y `POST /api/data/{e}/compute`, que no escribe) |
| Token con `data.write` | `POST/PUT/DELETE /api/data/**` |
| Cualquier token | `GET /api/auth/me` |
Fuera de la tabla: 403 `{ title }` con `WWW-Authenticate: Bearer error="insufficient_scope", scope="…"`. La sesión de la
app (cookie) también puede usar `GET /odata` (útil para revisar el documento desde el navegador).

### OData (solo GET; otro método → 405)
```
GET /odata/                         documento de servicio { "@odata.context": ".../$metadata", value: [{ name, kind: "EntitySet", url }] }
GET /odata/$metadata                CSDL 4.0 (application/xml), generado para el usuario
GET /odata/{E}                      { "@odata.context", "@odata.count"?, value: [...], "@odata.nextLink"? }
GET /odata/{E}({id})                { "@odata.context": ".../$metadata#E/$entity", ...propiedades }
    $filter $select $orderby $top $skip $count=true|false $expand $format=json
```
**Tipos Edm.** `text, longtext, choice, state, autonumber` → `Edm.String`; `integer` → `Edm.Int64`; `decimal` →
`Edm.Decimal` (`Precision`/`Scale` del campo); `boolean` → `Edm.Boolean`; `date` → `Edm.Date`; `datetime` →
`Edm.DateTimeOffset`; `user` → `Edm.Int32`; `formula` → el de su `resultType`; `reference` → `_X_value: Edm.Int64` +
`NavigationProperty X` (si el destino es raíz y el rol lo lee); `lines` → `NavigationProperty X: Collection(Erp.Hijo)`
con `ContainsTarget="true"`. Siempre `Id: Edm.Int64` (clave), `CreatedAt`, `UpdatedAt: Edm.DateTimeOffset`. Todas las
propiedades admiten nulo salvo `Id` y `CreatedAt` (un campo obligatorio agregado después puede estar vacío en registros
viejos). Etiquetas como `Core.Description`. Espacio de nombres `Erp`, contenedor `Erp.Default`.

**Gramática de `$filter`** (`ODataFilter.Parse`, descenso recursivo, sin `eval` ni concatenación de valores):
```
or   := and ('or' and)*            and := unary ('and' unary)*        unary := 'not' unary | primary
primary := '(' or ')' | cmp | func
cmp  := prop op lit | lit op prop | prop 'in' '(' lit (',' lit)* ')'      op := eq ne gt ge lt le
func := ('contains' | 'startswith' | 'endswith') '(' prop ',' string ')'
lit  := 'texto' ('' escapa) | número | true | false | null | aaaa-mm-dd | aaaa-mm-ddThh:mm[:ss[.fff]](Z|±hh:mm)
prop := Id | CreatedAt | UpdatedAt | campo visible | _Ref_value
```
**Traducción** a SQL sobre el alias `t` en forma normal negada (sin `CASE`, conserva índices):
`eq v` → `col = @p`; `eq null` → `col IS NULL`; `ne v` → `(col <> @p OR col IS NULL)`; `ne null` → `col IS NOT NULL`;
`gt/ge/lt/le` → `col > @p`…; `contains/startswith/endswith` → `LIKE` con comodines escapados (`ESCAPE '\'`); `in` →
`col IN (@p…)`. `not` se empuja hacia las hojas con De Morgan y el complemento con nulos de cada hoja (`not (col > v)`
→ `(col <= @p OR col IS NULL)`, `not (col eq null)` → `col IS NOT NULL`…). Los literales se convierten al tipo de la
propiedad (texto con texto, número con número, `Edm.Date` con fecha…); `choice`/`state` validan la opción en `eq`/`ne`/`in`.

**Paginación.** Página = mín(`$top` restante, 500; `OData:MaxPageSize` la reduce, solo para la suite E2E). Si quedan filas, `@odata.nextLink` con `$skip` y `$top`
recalculados y las demás opciones tal cual. `$orderby` siempre termina con `Id` del mismo sentido (orden estable).
`$count=true` cuenta con el mismo `WHERE` (alcance + filtro).

**`$expand`.** Lista separada por comas de navegaciones (máx. 10). Referencias: una consulta por navegación con
`WHERE t.Id IN (@ids) AND IsDeleted = 0 AND <RecordFilter del destino>` y `SelectClause`/`Format`/`FieldAccess.Strip` del
destino; lo que no vuelve sale `null`. Partidas: una consulta por navegación con `t.<Padre> IN (@ids) AND IsDeleted = 0`
(los ids ya pasaron el alcance del documento), `Strip` del hijo. Los ids van como parámetros en tandas de 500.

**Errores.** `{ "error": { "code": "BadRequest" | "NotFound" | "Forbidden", "message": "…" } }` (formato OData JSON),
mensajes en español. 404 para un catálogo que no existe, que es partida o que el rol no lee (no revela cuál).

### Implementación
- `Integrations/OAuth.cs`: `OAuthFormat` (ids, secretos, tokens, alcances), `OAuthTokenHandler`, `OAuthClientService`
  (alta/cambio/rotación/revocación con la compuerta de la 015), `OAuthTokenService` (emisión).
- `OData/ODataFilter.cs` (AST + parser + traducción), `OData/ODataQuery.cs` (opciones), `OData/Csdl.cs` (documento).
- `Data/RecordService.OData.cs`: listado y registro por id reutilizando `SelectClause`, `Format`, `RecordFilter` y
  `FieldAccess` privados del motor (no un segundo camino de lectura con reglas propias).
- `Endpoints/ODataEndpoints.cs`, `IntegrationEndpoints` (+ clientes y token), `SecurityMiddleware.UseBearerScope`.
- SPA: `IntegrationsPage` gana la pestaña *Clientes OAuth* (lista, alta, edición, rotación, revocación, URLs).

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **S** Adivinar un secreto o un token | 256 bits aleatorios; comparación de hash en tiempo constante; límite de 30 peticiones/min por IP en `/oauth/token`; respuesta idéntica para cliente inexistente, revocado o secreto malo |
| **S** Token robado en tránsito o en logs | Vida corta (5–60 min); HTTPS + HSTS en producción; `no-store`; los logs solo registran el `client_id`; revocar el cliente o rotar sin gracia corta todos sus tokens al instante |
| **S/E** Confundir esquemas (una clave de API como token o al revés; la cookie con Bearer) | Prefijos distintos (`erp_`, `erpat_`) y selector explícito; con `Bearer` nunca se usa la cookie; cuenta de servicio no inicia sesión ni usa cookie (008) |
| **E** Un token hace más que su alcance | Reja por ruta y método antes de cualquier endpoint; alcances efectivos = token ∩ cliente actual en cada petición; solo `/odata`, `/api/data`, `/api/reports`, `/api/auth/me` |
| **E** Un alcance da más que el rol | El alcance solo filtra superficies; dentro, el endpoint usa el mismo `UserContext` del rol (006, 010, 015). Roles de administración prohibidos para clientes |
| **E** Ampliar un cliente sin cuatro ojos | Alta y ampliación de roles o alcances esperan aprobación de otro superusuario con la huella (015); reducir no espera |
| **I** `$metadata` revela campos ocultos o catálogos sin permiso | El documento se genera por usuario con `Access().Read` y `FieldAccess.CanSee`; `$select`/`$filter`/`$orderby`/`$expand` de algo oculto dan el mismo 400 que algo que no existe |
| **I** Deducir valores ocultos por filtro u orden | Igual que la 006: solo propiedades visibles en `$filter`/`$orderby`; alcance en el `WHERE` también del conteo |
| **I** Expandir hacia registros fuera de alcance o de otra empresa | `$expand` aplica `RecordFilter` del destino (empresa incluida) y `Strip`; fuera de alcance = `null` |
| **T** Inyección SQL por `$filter`/`$orderby`/`$select` | Parser de lista blanca; identificadores solo desde el modelo, citados; todo literal como `SqlParameter`; sin concatenar texto de la petición |
| **D** Consultas caras | `$top` por página ≤ 500; `$filter` ≤ 2000 caracteres, ≤ 50 comparaciones, `in` ≤ 50; `$expand` ≤ 10 de un nivel; `$orderby` ≤ 5; límite global por usuario (012); cuerpo del token ≤ 4 KB; purga de tokens vencidos |
| **R** Repudiar el alta o el uso de un cliente | Auditoría `integration.oauthclient` de alta, cambios, rotación y revocación; las escrituras con token quedan a nombre de la cuenta de servicio del cliente, como con la clave de API |
| **T** CSRF en `/oauth/token` u `/odata` | `/odata` es solo GET; `/oauth/token` no lee cookies (credenciales en la petición) y exige formulario; un sitio ajeno no obtiene nada que no tenga ya |
