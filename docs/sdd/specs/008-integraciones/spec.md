# Spec 008 — Integraciones: webhooks, claves de API, importación y exportación

**Estado:** Implementado · **Depende de:** 002, 005, 006, 007 · **Origen:** [roadmap](../../roadmap.md#008--integraciones)

## Problema
1. Otro sistema (almacén, portal de proveedores, tienda en línea) no se entera de lo que pasa en el ERP: tiene que
   preguntar a cada rato. La roadmap eligió los webhooks como la vía de escape a «extensión con código»: el código vive
   fuera del ERP.
2. Un servicio no tiene cómo autenticarse: tendría que usar la contraseña de una persona (sin rotación, sin alcance, y
   su auditoría se confunde con la de esa persona).
3. Cargar 300 productos o proveedores es capturarlos uno por uno. Una importación por fuera del motor se saltaría los
   valores por defecto, las fórmulas, las reglas, la seguridad de la 006 y la auditoría.
4. Pendiente de la 007: el listado no se exporta (solo los reportes).
5. Todo lo anterior abre la puerta a fugas: un webhook o una exportación no pueden entregar un campo o registro que el rol
   no ve, y una importación no puede escribir lo que la persona no puede capturar.

## Historias

**H1 — Webhooks salientes.** Como *modelador*, quiero declarar que un catálogo avise a otro sistema cuando ocurre algo,
y como *administrador*, decidir a dónde se envía en este entorno.
- CA1: Un webhook se declara como metadato del catálogo (`setWebhooks`): nombre, eventos (al crear, al editar, al
  entrar a un estado, al ejecutar una transición), condición opcional, **rol de servicio** y campos a enviar. Se valida
  en la vista previa del plan (eventos, estados, transiciones, condición, rol y campos existentes; un campo que el rol no
  ve no se puede pedir).
- CA2: El destino (URL) y el secreto **no** son metadatos: el administrador los configura por entorno en
  *Integraciones*. El secreto lo genera el sistema, se muestra **una sola vez** y se puede rotar. La URL solo admite
  `https`, sin usuario ni contraseña en la URL; nunca se conecta a direcciones internas (localhost, redes privadas,
  metadatos de la nube), tampoco por redirección ni por DNS.
- CA3: El evento se registra en la **misma transacción** que el cambio: si el cambio se revierte, no se envía nada. Los
  eventos que provoca el motor (automatizaciones, importación) también se notifican.
- CA4: La entrega es asíncrona, con **reintentos** con espera creciente; cada entrega queda en la **bitácora de
  entregas** (estado, intentos, código de respuesta, error) y un administrador puede reintentarla o enviar una prueba.
- CA5: Cada envío va **firmado con HMAC-SHA256** sobre la marca de tiempo y el cuerpo, con un id de entrega único, para
  que el receptor verifique el origen y descarte reenvíos (replay).
- CA6: La carga respeta la 006 **para el rol de servicio**: sin campos ocultos para ese rol y sin registros fuera de su
  alcance (esos no se envían).

**H2 — Claves de API.** Como *administrador*, quiero dar a un servicio una clave con permisos acotados que pueda rotar y
revocar.
- CA1: Crear una clave pide nombre, roles (no administradores) y vencimiento opcional; crea una **cuenta de servicio**
  con esos roles. La clave se muestra **una sola vez**; el sistema solo guarda su hash.
- CA2: Con `Authorization: Bearer <clave>` el servicio usa la API de datos y reportes con la seguridad de sus roles
  (006): mismo motor, mismas reglas, auditoría a nombre de la cuenta de servicio. No llega a administración ni al
  modelador. No necesita la cabecera anti-CSRF (no usa cookies).
- CA3: **Rotar** emite una clave nueva; la anterior deja de servir de inmediato o tras un periodo de gracia de 24 h.
  **Revocar** la invalida para siempre. Una clave vencida, revocada o mal escrita responde 401 sin revelar cuál fue el
  motivo.
- CA4: La lista muestra el prefijo público, roles, último uso, vencimiento y estado; nunca la clave. Las cuentas de
  servicio no inician sesión, no aparecen en *Usuarios* ni en el buscador de usuarios.

**H3 — Importación masiva.** Como *usuario con permiso de alta*, quiero cargar registros desde un CSV o Excel.
- CA1: Desde el listado elijo un archivo `.csv` o `.xlsx` (máx. 2 MB y 1000 filas); las columnas se reconocen por nombre
  o etiqueta del campo (sin importar mayúsculas ni acentos). Puedo descargar una plantilla con las columnas importables.
- CA2: Antes de importar veo una **vista previa**: columnas reconocidas, ignoradas (calculadas o de solo lectura) y no
  reconocidas, y los **errores por fila** con la columna y el mensaje.
- CA3: Cada fila pasa por el mismo motor que la captura: valores por defecto, fórmulas, folios, reglas, referencias por
  su campo para mostrar, automatizaciones, webhooks y auditoría.
- CA4: La seguridad de la 006 aplica: una columna oculta para mi rol no se reconoce, una de solo lectura se ignora, una
  referencia a un registro que no veo «no existe», y una fila que quedaría fuera de mi alcance es un error.
- CA5: Es **todo o nada**: si alguna fila tiene error no se importa ninguna. La importación queda en la auditoría.

**H4 — Exportar listados.** Como *usuario*, quiero exportar el listado tal como lo veo.
- CA1: «Exportar» genera CSV o Excel con la vista actual (búsqueda, filtros, estado y orden) y las columnas visibles, con
  las etiquetas de referencias y usuarios en lugar de ids (máx. 5000 filas).
- CA2: Nunca incluye un campo oculto para el usuario ni registros fuera de su alcance; el CSV no permite inyectar
  fórmulas; la exportación queda en la auditoría. Un archivo exportado se puede volver a importar.

**H5 — Compras 1.5.0 e Inventario 1.3.0.** Como *usuario de Compras e Inventario*, quiero que los módulos traigan sus
integraciones típicas declaradas, sin direcciones ni secretos.
- CA1: Una actualización de módulo puede agregar roles con permisos (capacidad nueva del motor).
- CA2: Compras: rol de servicio «Integración de almacén» (sin acceso a importes) y webhook «Orden autorizada» al entrar
  a *Autorizada*. Inventario: rol «Integración de tienda» y webhook «Existencia actualizada».
- CA3: Ninguno trae URL ni secreto: quedan «sin configurar» hasta que el administrador elige el destino.

## Decisiones (CLARIFY)
Sesión sin interlocutor disponible: se tomaron estas decisiones y se registran para revisión.
- **Qué y a dónde por separado.** El *qué* (eventos, rol, campos) es metadato: auditable, empaquetable, validado en la
  vista previa. El *a dónde* (URL) y el secreto son configuración del entorno: muchas URLs de webhook llevan un token
  (Slack, Zapier), los paquetes de módulos viven en un repositorio público y la 009 moverá metadatos entre entornos.
  La URL y el secreto se guardan cifrados (Data Protection de ASP.NET Core), nunca en el plan ni en la auditoría.
- **Bandeja de salida (outbox).** El evento se guarda en `meta.WebhookDelivery` dentro de la transacción; un servicio en
  segundo plano entrega. Entrega «al menos una vez»: el receptor deduplica por id de entrega. Sin orden garantizado.
- **La carga es una foto del evento**, tomada después de las automatizaciones de ese mismo evento; se firma al enviar
  (marca de tiempo nueva en cada intento, mismo cuerpo e id).
- **Firma:** `X-ERP-Signature: sha256=<hex(HMAC-SHA256(secreto, "{timestamp}.{cuerpo}"))>`, `X-ERP-Timestamp` (segundos
  Unix) y `X-ERP-Delivery`. Se recomienda al receptor rechazar marcas con más de 5 minutos de diferencia.
- **Reintentos:** 6 intentos (inmediato, 1 min, 5 min, 30 min, 2 h, 6 h); cualquier respuesta que no sea 2xx o un error
  de red cuenta como falla. Tiempo máximo por intento 10 s; no se siguen redirecciones. Agotados, la entrega queda
  «Fallida» y se puede reintentar a mano. Las entregas se purgan a los 30 días.
- **Rol de servicio** (no una persona): la carga se arma como la vería un usuario con solo ese rol, sin superusuario.
  Si el rol no puede leer el catálogo o el registro, no se envía nada.
- **Administración de integraciones: solo superusuario** (como los módulos): configurar un destino es decidir que datos
  salgan del sistema.
- **Una cuenta de servicio por clave**, con los roles de la clave; no admite roles con superusuario, modelador ni
  administración de usuarios. La clave tiene la forma `erp_<id público>_<secreto>`; se guarda el SHA-256 del secreto
  (256 bits aleatorios: no necesita un hash lento) y se compara en tiempo constante.
- **Alcance de una clave:** `/api/data/**`, `/api/reports/**` y `/api/auth/me`. Todo lo demás, 403.
- **Importación solo da de alta** (no actualiza por clave) y es todo o nada; la vista previa corre las filas en una
  transacción que se revierte, así los errores son exactamente los que daría importar. Referencias por el campo para
  mostrar del catálogo referido (coincidencia exacta, sin distinguir mayúsculas); usuarios por usuario o nombre; sí/no
  acepta `sí, si, no, true, false, 1, 0, x`; fechas `aaaa-mm-dd`, `dd/mm/aaaa` o fecha de Excel. Una celda que empieza
  con `'` seguida de `= + - @` (como las deja la exportación) se importa sin el apóstrofo.
- **Exportación de listados:** máximo 5000 filas (se avisa si se cortó); solo columnas de campos (sin id ni fechas del
  sistema) para que el archivo se pueda reimportar.
- **Fuera de alcance:** actualizar registros por importación, importar partidas, webhooks entrantes, OAuth, URL por
  plantilla, firmas asimétricas.
