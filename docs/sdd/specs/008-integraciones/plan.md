# Plan 008 — Diseño técnico

## Metadatos nuevos

### Webhooks (`setWebhooks`) — `meta.Entity.WebhooksJson`
```ts
type WebhookDef = {
  name: string;                 // identificador, único en el catálogo
  label: string;
  description?: string;
  events: { on: 'create' | 'update' | 'enterState' | 'transition'; state?: string; transition?: string }[];  // 1..8
  when?: string;                // expresión (spec 002); desconocido = no se envía
  role: string;                 // rol de servicio: la carga se arma como la vería ese rol (006)
  fields?: string[];            // campos a enviar (encabezado y campos de partidas); omitido = todos los visibles para el rol
};
Operation += { op: 'setWebhooks'; entity: string; webhooks: WebhookDef[] }   // reemplaza la lista; máx. 10; solo catálogos raíz
```
Validación (`WebhookRules`, también en la pasada final cuando el plan toca algo): nombre identificador y único;
etiqueta ≤ 100; `enterState` exige un estado del proceso y `transition` una transición; `create`/`update` no admiten
`state`/`transition`; `when` válida; `role` existente; `fields` existentes, activos, sin repetir y **visibles para el rol**
(si la política `read` del campo no incluye el rol, error). El destino no forma parte del metadato.

### Roles en actualizaciones de módulo
`ModuleUpgrade += { roles?: ModuleRole[] }`: la actualización crea los roles que falten y aplica sus permisos (después del
plan, igual que la instalación). Una instalación nueva crea los roles de la base y de todas las actualizaciones. La vista
previa los cuenta como roles conocidos.

## Esquema (migración idempotente)
```sql
meta.Entity       + WebhooksJson nvarchar(max) NULL
meta.[User]       + IsService bit NOT NULL DEFAULT 0
meta.ApiKey (Id int PK, Name nvarchar(100), PublicId char(12) UNIQUE, SecretHash varbinary(32),
             PreviousSecretHash varbinary(32) NULL, PreviousExpiresAt datetime2 NULL, UserId int FK User,
             CreatedBy int FK User, CreatedAt, ExpiresAt NULL, RotatedAt NULL, RevokedAt NULL, LastUsedAt NULL)
meta.WebhookEndpoint (EntityName, WebhookName PK, UrlProtected nvarchar(max), Destination nvarchar(300),
             SecretProtected nvarchar(max), IsActive bit, SecretCreatedAt, UpdatedAt, UpdatedBy)
meta.WebhookDelivery (Id bigint PK, DeliveryId uniqueidentifier UNIQUE, EntityName, WebhookName, RecordId bigint NULL,
             Event nvarchar(20), EventKey nvarchar(50) NULL, PayloadJson nvarchar(max), Status nvarchar(12),
             Attempts int, NextAttemptAt datetime2(3), LeaseUntil datetime2(3) NULL, LastStatusCode int NULL,
             LastError nvarchar(300) NULL, CreatedAt, DeliveredAt NULL)   -- índice (Status, NextAttemptAt)
```
`UrlProtected` y `SecretProtected` se cifran con `IDataProtector` (propósito `Erp.Webhooks.v1`); `Destination` guarda
solo `esquema://host[:puerto]/…` para mostrarlo. Las llaves de Data Protection viven fuera del repositorio (perfil del
usuario con DPAPI en Windows; en producción, `DataProtection:KeysPath`).

## Webhooks: flujo
1. **Encolar** (`RecordService.EnqueueWebhooksAsync`, al final de `FireAsync`, que ya corre para crear, editar, entrar a
   estado y transición — de personas y del motor). Por cada webhook del catálogo con evento coincidente: destino
   configurado y activo; `when` verdadero; usuario sintético del rol (sus permisos por catálogo, sin superusuario); el
   rol puede leer el catálogo y el registro (`RecordFilter.For`); carga = `LoadOutputAsync` con ese usuario
   (`FieldAccess.Strip`), sin `_workflow`/`_layout`/`_rowVersion`, recortada a `fields`. `INSERT meta.WebhookDelivery`
   en la misma transacción.
2. **Entregar** (`WebhookDispatcher`, `BackgroundService`): cada `Webhooks:PollSeconds` (2 s) toma hasta 20 pendientes
   vencidas con `UPDATE TOP … WITH (READPAST, UPDLOCK)` y un *lease* de 60 s (varias instancias no repiten), descifra
   destino y secreto, firma y envía. 2xx → `delivered`; si no, `pending` con la siguiente espera
   (`Webhooks:RetryDelaysSeconds`, por defecto `60,300,1800,7200,21600`) o `failed` al agotarlas. Destino desactivado o
   eliminado → `canceled`. Purga de entregas cerradas con más de `Webhooks:RetentionDays` (30).
3. **Enviar** (`WebhookSender`): `HttpClient` propio con `SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false,
   ConnectCallback }`: el *callback* resuelve el DNS y rechaza cualquier IP bloqueada antes de conectar (evita *DNS
   rebinding*). 10 s máximo; el cuerpo de la respuesta no se lee ni se guarda.

```
POST <destino>
Content-Type: application/json
User-Agent: ErpAi-Webhooks/1
X-ERP-Event: enterState
X-ERP-Delivery: 3f0c…                (igual en cada reintento)
X-ERP-Timestamp: 1792700000          (nuevo en cada intento)
X-ERP-Signature: sha256=<hex(HMAC-SHA256(secreto, "1792700000." + cuerpo))>

{ "id": "3f0c…", "webhook": "OrdenAutorizada", "entity": "OrdenCompra", "event": "enterState", "state": "Autorizada",
  "transition": null, "recordId": 12, "occurredAt": "2026-10-23T18:00:00Z", "data": { "Folio": "OC-000012", … } }
```
Evento `ping` (botón «Probar»): `data: {}`, se envía en el momento y se registra.

### Política de URL (`WebhookUrlPolicy`)
- Absoluta, ≤ 2000 caracteres, `https` (`http` solo con `Webhooks:AllowHttp`), sin `usuario:contraseña@`, sin fragmento.
- IP bloqueada (salvo las de `Webhooks:AllowedInternalAddresses`, lista exacta que solo usa la suite E2E con
  `127.0.0.1`; `localhost` y cualquier otra interna siguen bloqueadas): sin especificar, loopback, privadas
  (10/8, 172.16/12, 192.168/16), CGNAT 100.64/10, enlace local 169.254/16 (metadatos de nube), 0/8, multicast,
  reservadas 240/4, broadcast, 192.0.0/24, 198.18/15; IPv6 `::`, `::1`, `fc00::/7`, `fe80::/10`, `ff00::/8`,
  y mapeadas/compatibles IPv4 se evalúan como IPv4. Se revisa al configurar (si el host es una IP literal) y siempre al
  conectar.

## Claves de API
- Formato `erp_<12 hex>_<43 base64url>`. Se guarda `SHA256(clave completa)`; búsqueda por id público, comparación con
  `CryptographicOperations.FixedTimeEquals`.
- `ApiKeyHandler` (esquema `ApiKey`): un esquema de política elige `ApiKey` si llega `Authorization: Bearer erp_…`;
  si no, la cookie. Sin clave válida (formato, hash, revocada, vencida, gracia vencida, cuenta inactiva) → 401 genérico.
  Carga el `UserContext` de la cuenta de servicio; si tuviera un rol con superusuario, modelador o administración de
  usuarios, se rechaza (defensa en profundidad). `LastUsedAt` se actualiza como máximo una vez por minuto.
- Middleware tras la autenticación: con clave, solo `/api/data`, `/api/reports` y `/api/auth/me`; lo demás 403. La
  revisión anti-CSRF no aplica a peticiones autenticadas con clave (no hay credenciales ambientales).
- Cuenta de servicio: `meta.[User]` con `IsService = 1`, usuario `svc.<id público>`, contraseña aleatoria no usable;
  `login` la rechaza; `/admin/users` y `/users/lookup` la excluyen; editar sus roles desde *Usuarios* → 404.
- Rotar: `PreviousSecretHash = SecretHash`, `PreviousExpiresAt = ahora + gracia (0 o 24 h)`. Revocar: `RevokedAt` y la
  cuenta queda inactiva.

## Importación y exportación
- **Lectores** (`Data/ImportReader.cs`): CSV RFC 4180 (BOM, separador `,` o `;` según el encabezado, UTF-8 o Latin-1) y
  XLSX (primera hoja, cadenas compartidas, en línea, números y booleanos). XLSX: máx. 50 entradas y 20 MB descomprimidos
  por entrada leída (anti *zip bomb*), `XmlReader` con `DtdProcessing.Prohibit` (anti XXE).
- **`RecordService.ImportAsync`**: encabezados → campos (nombre o etiqueta normalizados). Estado por columna: `ok`; `ignored`
  (calculado, folio, estado, asignado por el sistema o de solo lectura para el rol); `error` (no reconocida — incluye
  ocultos para el rol —, partidas, repetida). Celda → JSON tipado (texto, número invariante, sí/no, fecha, opción sin
  distinguir mayúsculas, referencia por campo para mostrar con el filtro de lectura del catálogo referido y caché,
  usuario por usuario o nombre). Cada fila: `SAVE TRANSACTION` → `CreateInTransactionAsync` → alcance de escritura
  (006) → error → `ROLLBACK TRANSACTION` al punto. Con `dryRun` o con errores se revierte todo; si no, auditoría
  `data.import` y *commit*.
- **Exportar listado**: `RecordService.ExportAsync` reutiliza la consulta del listado (alcance, búsqueda, filtros,
  estado, orden) con `MaxExport = 5000`; columnas pedidas ∩ campos visibles; arma un `ReportResult` (columnas tipo
  `field`, sin fila de total) para `ReportExport.Csv/Xlsx`. Auditoría `data.export` (filtros y número de filas).

## Contrato de API
```ts
Operation += { op: 'setWebhooks'; entity: string; webhooks: WebhookDef[] }
EntityDef += { webhooks?: WebhookDef[] /* solo modeladores */ }

GET  /api/data/{e}/export?format=csv|xlsx&columns=a,b&search=&sort=&dir=&state=&filters=   → archivo; X-ERP-Truncated: true si se cortó
POST /api/data/{e}/import?dryRun=true|false   cuerpo: bytes del archivo (CSV o XLSX)          → ImportResult (400 si hay errores y no es dryRun)
type ImportResult = { dryRun: boolean; format: 'csv' | 'xlsx'; total: number; imported: number; valid: boolean;
  columns: { column: string; field?: string; label?: string; status: 'ok' | 'ignored' | 'error'; message?: string }[];
  errors: { row: number; column?: string; field?: string; message: string }[] /* máx. 200 */; truncatedErrors: boolean }

// Solo superusuario
GET  /api/admin/webhooks                         → { entity; entityLabel; name; label; description?; events: WebhookEvent[]; role; fields?;
                                                     configured: boolean; active: boolean; destination?: string; secretCreatedAt?: string;
                                                     lastStatus?: string; lastAt?: string }[]
PUT  /api/admin/webhooks/{e}/{name}             { url?: string; active: boolean } → { secret?: string }   // secret solo la primera vez
POST /api/admin/webhooks/{e}/{name}/rotate      → { secret: string }
POST /api/admin/webhooks/{e}/{name}/test        → { ok: boolean; status?: number; error?: string; deliveryId: string }
DELETE /api/admin/webhooks/{e}/{name}           → 204 (borra destino y secreto)
GET  /api/admin/webhooks/deliveries?entity=&name=&status=  → { id; deliveryId; entity; webhook; event; eventKey?; recordId?; status;
                                                     attempts; lastStatusCode?; lastError?; createdAt; nextAttemptAt?; deliveredAt? }[] (100)
POST /api/admin/webhooks/deliveries/{id}/retry  → 204
GET  /api/admin/api-keys                         → { id; name; prefix; roleIds; username; createdAt; createdBy; expiresAt?; lastUsedAt?;
                                                     rotatedAt?; previousExpiresAt?; revokedAt?; status: 'active'|'expired'|'revoked' }[]
POST /api/admin/api-keys                         { name; roleIds; expiresInDays?: number } → { key: ApiKey; secret: string }
PUT  /api/admin/api-keys/{id}                    { name; roleIds } → ApiKey
POST /api/admin/api-keys/{id}/rotate             { graceHours: 0 | 24 } → { key: ApiKey; secret: string }
POST /api/admin/api-keys/{id}/revoke             → 204
```
Auditoría: `integration.webhook` (configurar, rotar, probar, borrar, reintentar; con destino enmascarado y nunca el
secreto), `integration.apikey` (crear, editar, rotar, revocar; nunca la clave), `data.import`, `data.export`.

## UI
- **Listado:** botones «Exportar» (menú CSV / Excel con la vista actual) e «Importar» (si puede dar de alta y el catálogo
  no lo administra el sistema). Diálogo de importación: elegir archivo, «Descargar plantilla», vista previa con tabla de
  columnas y errores por fila, «Importar N registros» (deshabilitado si hay errores).
- **Configuración › Integraciones** (`/admin/integrations`, superusuario; enlace después de «Auditoría» para no empujar
  la configuración fuera de la pantalla): pestañas «Webhooks» (lista, «Configurar», «Probar», «Rotar secreto», entregas
  recientes con «Reintentar») y «Claves de API» (crear, editar, rotar, revocar). El secreto y la clave se muestran una
  vez en un diálogo con «Copiar».
- **Modelador:** «Webhooks (JSON)» en la sección avanzada, validado en la vista previa.

## Compras 1.5.0 e Inventario 1.3.0 (entradas en `upgrades`)
- Compras: rol «Integración de almacén» (lee órdenes, proveedores, productos, almacenes y unidades); webhook
  `OrdenAutorizada` en *Orden de compra* al entrar a *Autorizada*, con folio, proveedor, fechas, almacén y partidas (sin
  importes: la 006 se los oculta a ese rol y la vista previa rechazaría pedirlos).
- Inventario: rol «Integración de tienda» (lee existencias, productos y almacenes); webhook `ExistenciaActualizada` en
  *Existencia* al crear y al editar (las escribe el motor por automatización).

## Amenazas (STRIDE)
| Amenaza | Mitigación |
|---|---|
| **S** Falsificar un webhook hacia el receptor | HMAC-SHA256 con secreto por webhook (256 bits) sobre marca de tiempo + cuerpo |
| **S** Adivinar o robar una clave de API | 256 bits aleatorios; solo se guarda el hash; se muestra una vez; comparación en tiempo constante; vencimiento, rotación y revocación; rate limit global por cuenta |
| **T / R** Reenvío (*replay*) de una entrega capturada | Marca de tiempo firmada (ventana recomendada 5 min) e id de entrega único para deduplicar |
| **I** SSRF: usar el ERP para llegar a la red interna o a metadatos de nube | Solo `https`; IPs internas bloqueadas al configurar y **al conectar** (`ConnectCallback`, también contra *DNS rebinding*); sin redirecciones ni proxy; sin leer la respuesta |
| **I** Fuga de secretos (URL con token, secreto HMAC, clave) | Fuera de los metadatos, del plan, de la auditoría y del repositorio; cifrados con Data Protection; destino enmascarado; secreto y clave solo en la respuesta que los crea |
| **I** Webhook o exportación que entrega campos o registros ocultos | Carga armada con `FieldAccess.Strip` del rol de servicio y `RecordFilter`; `fields` validado contra el rol; exportación con la seguridad del listado |
| **T** Inyección de fórmulas (CSV/Excel) | Exportación con `ReportExport` (apóstrofo en CSV, celdas de texto en XLSX); importación quita el apóstrofo solo antes de `= + - @` y guarda texto |
| **T / E** Importar campos de solo lectura o registros fuera de alcance | Mismo motor que la captura (campos sin permiso ignorados), referencias con filtro de lectura, alcance de escritura por fila |
| **D** Archivos de importación hostiles (zip bomb, XXE, filas infinitas) | 2 MB, 1000 filas, 200 columnas, límite descomprimido por entrada, DTD prohibido |
| **D** Receptor lento o caído | 10 s por intento, lote de 20, *lease* de 60 s, reintentos acotados, purga a 30 días |
| **E** Clave de servicio con privilegios de administración | Roles sin superusuario/modelador/usuarios (al crear, editar y al autenticar); rutas permitidas en lista blanca |
| **E** CSRF con la autenticación por clave | El esquema de clave no usa cookies; una petición con `Authorization` nunca usa la cookie |
| **R** Cambiar destinos o claves sin rastro | Auditoría `integration.*`; importaciones y exportaciones auditadas |
