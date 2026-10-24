# Tareas 018

## Clientes OAuth y servidor de tokens
- [x] T1 Esquema `meta.OAuthClient` y `meta.OAuthToken` (migración idempotente) y acción de auditoría `integration.oauthclient`.
- [x] T2 `OAuthFormat`: client_id, secreto, token, alcances (parseo, validación, hash). Unitarias.
- [x] T3 `OAuthClientService`: listar, crear, cambiar, rotar (0/24 h, 0 revoca tokens), revocar; compuerta de la 015
      (`oauthclient.create`/`oauthclient.update`, solo superusuario). Unitarias de `SecurityGrants.OAuthClient`.
- [x] T4 `POST /oauth/token`: client credentials por Basic o cuerpo, `scope` opcional, errores RFC 6749 §5.2, `no-store`,
      límite por IP, purga de vencidos.
- [x] T5 `OAuthTokenHandler` y selector de esquemas; `UseBearerScope` por alcance (y `/odata` para claves de API). Unitarias
      de la tabla de rutas permitidas.

## OData
- [x] T6 `ODataFilter`: tokenizador, parser, AST, traducción a SQL con forma normal negada y tipos. Unitarias (inyección,
      nulos con `not`/`ne`, límites, propiedades ocultas, literales del tipo equivocado).
- [x] T7 `ODataQuery`: `$select`, `$orderby`, `$top`, `$skip`, `$count`, `$expand`, `$format`; opciones no admitidas → 400.
- [x] T8 CSDL por usuario (`$metadata`) y documento de servicio.
- [x] T9 `RecordService.ODataAsync`: listado, por id, paginación con `nextLink`, conteo, expansión de referencias y partidas
      con seguridad del destino.
- [x] T10 `ODataEndpoints` (GET; 405 lo demás; formato de error OData) y cabeceras `no-store` en `/odata` y `/oauth`.

## Interfaz
- [x] T11 *Integraciones › Clientes OAuth*: lista, alta (secreto una vez), edición, rotación, revocación, URLs.

## VERIFY
- [x] T12 E2E `oauth-odata.spec.ts`: H1 (UI), H2 (token y alcances), H3 (metadata por usuario), H4 (consultas y seguridad).
      Aprobación de seguridad de clientes en la instancia :5093 de `jerarquia.spec.ts`.
- [x] T13 Dos corridas limpias de `dotnet test` y `npm --prefix web run e2e`.
- [x] T14 Revisión exploratoria en el navegador integrado; `verify.md`; roadmap e índice; commit.
