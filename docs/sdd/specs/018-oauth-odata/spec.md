# Spec 018 — OAuth2 (client credentials) y API OData con descubrimiento

**Estado:** Implementado · **Depende de:** 006, 008, 010, 015 · **Origen:** [roadmap](../../roadmap.md#018--oauth2-client-credentials-y-api-odata-con-descubrimiento) ·
**ADR:** [0006](../../../adr/0006-oauth-tokens-opacos-y-odata.md)

## Problema
1. Las integraciones usan claves de API (008): una credencial estática que viaja en **cada** petición durante meses. Si se
   filtra (un log, un proxy, un repositorio), sirve hasta que alguien la revoca. Lo habitual en un ERP es que el servicio
   cambie su secreto por un **token de vida corta** y que solo el token viaje con los datos.
2. Una clave da todo lo que su rol permite en toda la API de datos: no hay forma de dar a un tablero de BI **solo lectura**
   sin crear un rol aparte, ni de saber qué superficies usa cada integración.
3. Cada integrador aprende la API a mano: no hay un documento que diga qué catálogos, campos y tipos existen, y la API de
   datos tiene sus propios parámetros (`page`, `filters` en JSON). Herramientas como Excel, Power BI o cualquier cliente
   OData no se pueden conectar sin código.
4. Lo que se consulte de otra forma no puede abrir un hueco: la seguridad por registro y por campo (006, 015) y la de
   empresa (010) deben aplicar igual que en la API de siempre, también al filtrar, ordenar y expandir.

## Historias

**H1 — Clientes OAuth.** Como *administrador del sistema*, quiero registrar una integración como cliente OAuth con sus
roles y alcances, para darle solo lo que necesita y poder cambiar su secreto sin cortarla.
- CA1: En *Integraciones › Clientes OAuth* creo un cliente con nombre, roles (los mismos que una clave de API: nunca de
  administración), **alcances** (`odata.read`, `data.read`, `data.write`, al menos uno) y vida del token (5 a 60 minutos,
  15 por defecto). Al crearlo veo **una sola vez** su `client_id` y su secreto; después solo se ve el `client_id`.
- CA2: Puedo cambiar nombre, roles, alcances y vida del token. Agregar roles o alcances amplía acceso: con aprobación de
  seguridad obligatoria (015) espera la aprobación de otro administrador; quitarlos se aplica de inmediato y los tokens ya
  emitidos pierden lo quitado en su siguiente petición.
- CA3: **Rotar el secreto** da uno nuevo; el anterior deja de servir ya (y los tokens emitidos se revocan) o sirve 24 horas
  más para cambiarlo sin cortar el servicio. **Revocar** el cliente invalida su secreto y todos sus tokens al instante.
- CA4: Cada alta, cambio, rotación y revocación queda en la auditoría inmutable sin el secreto. En la base solo se guarda
  el SHA-256 del secreto y de cada token.
- CA5: La lista muestra estado (vigente, revocado), alcances, último token emitido y tokens activos.

**H2 — Servidor de tokens (client credentials).** Como *integración*, quiero cambiar mi `client_id` y secreto por un token
corto con los alcances que necesito.
- CA1: `POST /oauth/token` con `grant_type=client_credentials` (RFC 6749 §4.4), credenciales en `Authorization: Basic` o
  en el cuerpo, responde `access_token`, `token_type: "Bearer"`, `expires_in` y `scope`, con `Cache-Control: no-store`.
- CA2: `scope` opcional pide un subconjunto de los alcances del cliente; omitido, recibe todos. Un alcance que el cliente
  no tiene responde `invalid_scope`; credenciales malas, cliente revocado o secreto vencido, `invalid_client` (401) sin
  decir cuál fue; otro `grant_type`, `unsupported_grant_type`. Errores en el formato JSON de RFC 6749 §5.2.
- CA3: El token vence a los minutos configurados del cliente; vencido, revocado, o con el cliente revocado responde 401.
  No hay *refresh token* (§4.4.3): se pide otro.
- CA4: El token solo llega a lo que sus alcances permiten: `odata.read` → `GET /odata`; `data.read` → lecturas de
  `/api/data` y `/api/reports`; `data.write` → altas, cambios, transiciones, bajas e importación en `/api/data`.
  Fuera de eso, 403 con `WWW-Authenticate: Bearer error="insufficient_scope"`. Nunca llega a administración, modelo ni
  auditoría. Dentro de lo permitido, manda el rol (006, 010, 015): el alcance limita, nunca agrega.
- CA5: El servidor de tokens tiene límite de peticiones por dirección, como el inicio de sesión.

**H3 — Descubrimiento.** Como *integrador*, quiero un documento generado del modelo que diga qué puedo consultar.
- CA1: `GET /odata/$metadata` devuelve el documento CSDL (OData v4, XML) con un tipo por catálogo, sus campos con tipo
  Edm, clave `Id`, navegación por referencias y partidas, y un conjunto de entidades por catálogo raíz.
- CA2: El documento se genera **para quien lo pide**: solo los catálogos que su rol puede leer y solo los campos que ve.
  Un campo oculto no aparece en el documento; los archivados tampoco.
- CA3: `GET /odata/` devuelve el documento de servicio (JSON) con los conjuntos de entidades disponibles.
- CA4: En *Integraciones › Clientes OAuth* se muestran la URL del token y la de la API OData.

**H4 — Consultas OData de solo lectura.** Como *integrador*, quiero consultar con el estándar OData y recibir solo lo que
mi rol permite.
- CA1: `GET /odata/{Catálogo}` y `GET /odata/{Catálogo}({id})` con `$filter` (`eq`, `ne`, `gt`, `ge`, `lt`, `le`, `and`,
  `or`, `not`, paréntesis, `in`, `contains`, `startswith`, `endswith`, comparación con `null`), `$select`, `$orderby`
  (hasta 5 propiedades), `$top`, `$skip`, `$count=true` y `$expand` de un nivel (referencias y partidas).
- CA2: Los valores siempre van como parámetros; las propiedades se resuelven contra el modelo (lista blanca). Una opción
  no admitida, una propiedad que no existe **o que no ve** y un literal del tipo equivocado responden 400 con un mensaje
  claro en el formato de error de OData, sin distinguir «oculto» de «no existe».
- CA3: Las filas, el conteo y la paginación respetan el alcance por registro y por empresa del usuario (006, 010, 015),
  con el mismo filtro de la base que la API de datos. Un registro fuera de alcance por id responde 404.
- CA4: Los campos ocultos no salen en las filas ni en lo expandido; un registro expandido fuera del alcance del usuario
  sale como `null`. Las partidas expandidas heredan el alcance de su documento.
- CA5: Las páginas son de 500 filas como máximo con `@odata.nextLink`; `$top` limita el total.
- CA6: Solo lectura: cualquier otro método en `/odata` responde 405. Funciona con sesión de la app, con clave de API (008)
  y con token OAuth con `odata.read`.

## Decisiones (CLARIFY)
Sesión sin interlocutor disponible: se tomaron estas decisiones y se registran para revisión.
- **Tokens opacos, no JWT.** El token es un valor aleatorio de 256 bits (`erpat_…`) del que la base guarda el SHA-256. Cada
  petición ya revalida la cuenta contra la base (008, 015), así que un JWT no ahorra consultas y sí obliga a administrar
  llaves de firma y a no poder revocar antes de que venza. Con tokens opacos, revocar el cliente corta al instante.
- **Un cliente OAuth es una cuenta de servicio**, como una clave de API (008): sus roles deciden qué ve y su empresa es la
  de quien lo creó (010). Los alcances son una segunda reja por superficie, no permisos nuevos: un token nunca puede más
  que su rol. Las claves de API siguen existiendo; esta spec no las retira.
- **Alcances fijos y por superficie** (`odata.read`, `data.read`, `data.write`). Alcances por catálogo duplicarían los
  permisos del rol (para eso está el rol). Se pueden agregar más alcances en otra spec.
- **Secreto del cliente:** 256 bits aleatorios; SHA-256 basta (no hay diccionario que probar), como en la 008. Rotar con
  0 horas de gracia revoca los tokens emitidos (supone que el secreto se filtró); con 24 horas, los tokens siguen hasta
  vencer. Revocar es definitivo.
- **Sin *refresh token*, sin otros flujos** (`authorization_code`, `password`): el alcance de esta spec son servicios.
  Tampoco hay introspección ni revocación de tokens por el cliente (RFC 7662/7009): pendiente si se necesita.
- **Rutas fuera de `/api`:** `/oauth/token` y `/odata/…`, como es costumbre en esos estándares. No usan cookies para
  escribir, así que la cabecera anti-CSRF no aplica: el token solo acepta POST de formulario sin cookies y OData es solo
  GET. Tienen `Cache-Control: no-store` y las mismas cabeceras de seguridad.
- **Descubrimiento con CSDL (`$metadata`)**, no OpenAPI: es lo que consumen los clientes OData (Excel, Power BI,
  bibliotecas) y describe tipos y navegación. Se genera por usuario (H3 CA2); un documento global revelaría campos ocultos.
- **Nombres OData:** el conjunto y el tipo se llaman como el catálogo; las propiedades, como los campos. Una referencia
  `Proveedor` se publica como navegación `Proveedor` y su id como `_Proveedor_value` (convención de Dataverse; un
  identificador del modelo no empieza con `_`, así que no choca). Un campo de usuario sale como su id (`Edm.Int32`);
  `Id`, `CreatedAt` y `UpdatedAt` siempre. Las partidas son un tipo sin conjunto propio, navegables desde su documento.
- **`$filter`:** solo *propiedad operador literal* (o literal operador propiedad), sin aritmética, sin comparar dos
  propiedades y sin rutas a otros catálogos. `not` y `ne` siguen la lógica de OData con nulos (nulo `ne` 'x' es
  verdadero), traducida sin perder índices. Máximo 2000 caracteres, 50 comparaciones y 50 valores en `in`.
- **Lo no admitido responde 400**, nunca se ignora en silencio: `$search`, `$apply`, `$compute`, `$levels`, opciones
  anidadas en `$expand` (`$expand=X($select=…)`), rutas en `$filter`/`$orderby`, `$format` distinto de `json`.
- **`$expand` de un nivel:** referencias a catálogos raíz que el rol puede leer (el registro destino con su alcance y sus
  campos visibles, o `null`) y partidas del documento (sus campos visibles). Una referencia a una partida o a un catálogo
  que el rol no lee se publica solo como `_X_value`.
- **Fuera de alcance:** escritura por OData, `$batch`, deltas, OpenAPI, nombres de usuario en las filas, filtros por
  rutas (`Proveedor/Nombre eq 'x'`), opciones anidadas en `$expand` y clientes OAuth en paquetes de entornos (009): como
  las claves de API, son credenciales de cada entorno.
