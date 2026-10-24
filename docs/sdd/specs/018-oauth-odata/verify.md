# Verificación 018 — OAuth2 (client credentials) y API OData

**Fecha:** 2026-10-24 · **Resultado:** Aprobada

| Compuerta | Resultado |
|---|---|
| Unitarias | 428/428 (+78 en `ODataTests`: formato y entropía de client_id, secreto y token, que no se confunden con una clave de API; alcances (parseo, normalización, desconocidos); la tabla de rutas que exige cada alcance, incluidos `/compute`, `/api/dataset` y `/oauth/token`; ampliar un cliente = ganar roles o alcances; `Basic` decodificado como pide el RFC 6749 §2.3.1; `$filter` parametrizado con lista blanca, `ne`/`not` de dos valores con De Morgan, literal a la izquierda, comodines de `LIKE` escapados, `in`, referencias, fechas y fechas con zona en UTC; 22 filtros inválidos con su mensaje (oculto = inexistente, archivado, navegación, rutas, tipos, opciones, funciones, sintaxis); límites de tamaño; opciones de consulta admitidas y 14 no admitidas; `$metadata` por usuario y partidas como colección sin conjunto propio) |
| E2E | 242/242 en dos corridas completas limpias antes de la revisión exploratoria y otras dos después de corregir sus hallazgos (+9 en `oauth-odata.spec.ts`, +1 en `endurecimiento.spec.ts` (límite del servidor de tokens en :5092), +1 en `jerarquia.spec.ts` (aprobación de clientes en :5093); `programadas.spec.ts` ahora espera la pestaña *Clientes OAuth*) |
| Contra el código anterior | Las dos regresiones de la revisión exploratoria se vieron fallar revirtiendo cada corrección en el árbol de trabajo y corriendo `oauth-odata.spec.ts`: F-01 sin el aviso del servidor (*element(s) not found* en `toBeInViewport`) y con el aviso al final de la lista (*Expected < 271.75, Received 354.25*); F-02 con la expresión anterior (*«El conjunto de entidades no existe.»*). `ODataTests` y la suite nueva no tienen contra qué correr en `7341aa3` (no existen `/oauth/token`, `/odata` ni los tipos) |
| Visual | Sin cambios en las capturas de referencia (ninguna muestra Integraciones) |
| Accesibilidad | axe sin violaciones serias en el alta del cliente, el diálogo de credenciales y la pestaña a 390 px en claro y a 1280 px en oscuro. Encontró que la tabla desplazable sin nada enfocable (todos los clientes revocados) no se podía desplazar con el teclado: la región recibe foco (`tabIndex`, `role="region"`), como `ReportTable` |

## Cobertura
| Criterio | Prueba |
|---|---|
| H1 CA1 alta con roles, alcances y vida; secreto una vez | `oauth-odata › H1 CA1 · CA5 + H3 CA4` (UI, client_id y secreto en el diálogo, axe, validación de alcances, vida y roles de administración) |
| H1 CA2 cambios; ampliar espera, reducir no | `jerarquia › Spec 018 H1 CA2` (:5093: alta y ampliación esperan, quien pidió no aprueba, revocado → 409); `oauth-odata › H2 CA4` (quitar `data.read` aplica a un token ya emitido) |
| H1 CA3 rotar 0/24 h y revocar | `oauth-odata › H1 CA3 · CA4` (UI: sin gracia corta secreto y tokens; con gracia ambos sirven; revocar corta todo) |
| H1 CA4 auditoría sin secreto, solo hashes | `H1 CA1 …` (lista y auditoría sin el secreto), `H1 CA3 · CA4` (`create`, `update`, `rotate`, `rotate`, `revoke`), `jerarquia › Spec 018` (la solicitud no trae `secretHash`) |
| H1 CA5 lista con estado, alcances, último token y activos | `H1 CA1 …`, `H1 CA3 · CA4` (tokens activos, secreto anterior en gracia, *Revocado* sin acciones) |
| H2 CA1 token por Basic o cuerpo, `no-store` | `H2 CA1 · CA2 · CA3` |
| H2 CA2 errores RFC 6749 §5.2 sin pistas | `H2 CA1 · CA2 · CA3` (`invalid_scope`, `unsupported_grant_type`, `invalid_request` (falta, JSON, repetido, Basic y cuerpo), `invalid_client` idéntico para secreto malo, cliente inexistente y basura; `WWW-Authenticate: Basic`; GET → 405) |
| H2 CA3 vencimiento y sin refresh | `H2 CA1 · CA2 · CA3` (token vencido en la base desechable → 401 `invalid_token`; inventado → 401) |
| H2 CA4 alcance por superficie; manda el rol | `H2 CA4` (403 `insufficient_scope` con el alcance que falta; administración, modelo, auditoría y buscador de usuarios 403; con `data.read` el alcance por registro, empresa y campo de la 006/010; escribir exige `data.write`, sin cabecera anti-CSRF) |
| H2 CA5 límite por dirección | `endurecimiento › Spec 018 H2 CA5` (:5092 con el límite por defecto: 30 → 401, 31.º y 32.º → 429) |
| H3 CA1–CA3 `$metadata` y documento de servicio por usuario | `H3 CA1 · CA2 · CA3` (tipos, navegación, `_X_value`, sin catálogo sin permiso, campos ocultos ni conjunto de partidas; la sesión del administrador sí los ve) |
| H3 CA4 URLs en la pantalla | `H1 CA1 · CA5 + H3 CA4` |
| H4 CA1–CA2 consultas y errores | `H4 CA1 · CA2` (10 filtros, `$select`, `$orderby`, `$top`, `$count`, `nextLink` seguido hasta el final y con `$top`; oculto = inexistente con el mismo mensaje; 10 opciones inválidas con su mensaje en formato OData; inyección = texto sin coincidencias) |
| H4 CA3 alcance y empresa, 404 por id | `H4 CA3 · CA4 · CA6` (fuera de alcance, otra empresa, inexistente y fuera de rango → 404 «El registro no existe.») |
| H4 CA4 `$expand` con la seguridad del destino | `H4 CA3 · CA4 · CA6` (referencia visible, fuera de alcance → `null` con su id, partidas sin el campo oculto ni el padre) |
| H4 CA5 páginas de 500 | `H4 CA1 · CA2` con `OData__MaxPageSize=3` en la suite |
| H4 CA6 solo lectura; sesión, clave y token | `H4 CA3 · CA4 · CA6` (POST → 405 `Allow: GET`; sesión del administrador; clave de API de la 008) |

## Hallazgos
| ID | Sev. | Hallazgo | Estado |
|---|---|---|---|
| F-01 | Media | Con muchos roles (36 en la base de la suite), «Crear cliente» sin roles parecía no hacer nada: el aviso «Elige al menos un rol» quedaba al final de la lista, fuera de la vista, y además llegaba en una segunda vuelta, después del aviso del nombre | Corregido: el servidor avisa de los roles junto con los demás errores; el aviso va arriba de la lista y el primer error se desplaza a la vista. Regresión en `H1 CA1 · CA5 + H3 CA4` (falló: *element(s) not found* y *Received 354.25*) |
| F-02 | Baja | Un id fuera de rango (`Pedido18(99999999999999999999)`) respondía «El conjunto de entidades no existe.», que confunde: el conjunto sí existe | Corregido: el segmento acepta hasta 40 dígitos y un id que no cabe es «El registro no existe.». Regresión en `H4 CA3 · CA4 · CA6` (falló: *El conjunto de entidades no existe.*) |
| F-03 | Baja | (Suite automática) La tabla de clientes, con todos revocados, no tenía nada enfocable y su desplazamiento horizontal en móvil no se alcanzaba con el teclado (axe `scrollable-region-focusable`) | Corregido antes de la primera corrida completa: región enfocable con nombre. La cubre `diseño: la pestaña se adapta…` |

## Revisión exploratoria (navegador integrado)
- **Instancia temporal** en `localhost:5094` sobre `ErpAi_E2E` (preparada por la segunda corrida completa con una contraseña
  de administrador generada para la sesión y guardada fuera del repositorio), con `RequireApproval` y `AllowDirectChanges`.
- *Integraciones › Clientes OAuth* a 1280 px en tema oscuro: los dos clientes que dejó la suite, revocados y sin acciones;
  URLs del token y de OData con el origen real. Alta con nombre «Tablero Año Fiscal — Compañía Ñandú»: sin nombre y sin
  roles se detectó F-01. Con **doble clic** en «Crear cliente» se creó un solo cliente; el diálogo de credenciales muestra
  client_id y secreto con su botón de copiar y se cierra con Esc.
- Con esas credenciales (curl): token por `Basic` (15 min, `odata.read`); `$expand=Cliente&$select=Folio,Monto` devolvió los
  cuatro pedidos a su alcance, el de Beto con `Cliente: null`; `$top=0&$count=true` da el conteo sin filas;
  `Pedido18(1)/Cliente` → 404 OData «Ruta no encontrada.»; detectado F-02.
- **Barrido del modelo completo** de la suite con la sesión del administrador: el `$metadata` (54 conjuntos, 63 tipos,
  84 KB) es XML bien formado, sin tipos repetidos y con toda navegación y vínculo apuntando a un tipo o conjunto que
  existe; los 54 conjuntos responden 200 con `$count`, `$top`, `$orderby` de dos propiedades y `$expand` de todas sus
  navegaciones, y también con un `$filter` de `eq null` / `not (… ne null)` sobre cada propiedad (fórmulas, estados,
  folios, usuarios y referencias incluidos).
- 390 px: la pestaña no se desborda (la tabla se desplaza dentro de su región) y las URLs se parten sin salirse.
- Consola: los 401 previos al inicio de sesión y los 400 de las validaciones provocadas. Al abrir `$metadata` directamente,
  el visor XML del propio Chrome intenta estilos en línea que la CSP bloquea; el documento se ve igual y no es código de
  la app.

## Riesgo residual
- Sin escritura ni `$batch` por OData, sin filtros por rutas (`Cliente/Nombre`), sin opciones anidadas en `$expand`, sin
  funciones más allá de `contains`/`startswith`/`endswith` y sin nombres de usuario en las filas (los campos de usuario
  salen como id). Lo no admitido responde 400.
- Sin introspección ni revocación de tokens por el cliente (RFC 7662/7009): se revoca desde *Integraciones*.
- Una petición anónima a `/odata` responde 401 en ProblemDetails, sin `WWW-Authenticate: Bearer`; algunos clientes OData
  (Excel, Power BI) esperan ese encabezado para ofrecer el inicio de sesión.
- Las URLs de `@odata.context` y `@odata.nextLink` salen del `Host` de la petición; detrás de un proxy hace falta que este
  conserve el host (`ForwardedHeaders`), igual que las URLs mostradas en *Clientes OAuth*.
- La tabla `meta.OAuthToken` se purga al emitir (lo vencido hace más de un día, por cliente); un cliente que deja de pedir
  tokens deja sus filas hasta el siguiente.
- La pestaña *Claves de API* tiene el mismo diseño del aviso de roles al final de la lista (F-01) y la misma tabla sin
  región enfocable (F-03); no se tocó en esta spec.
- Al volver a levantar `erp-api`, `Bootstrap` agrega a la base de desarrollo `meta.OAuthClient` y `meta.OAuthToken`
  (migración idempotente, como en cada spec).
