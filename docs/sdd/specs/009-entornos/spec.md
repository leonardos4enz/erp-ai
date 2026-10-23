# Spec 009 — Entornos: ciclo de vida de metadatos (sandbox → pruebas → producción)

**Estado:** Implementado · **Depende de:** 002, 004, 006, 007, 008 · **Origen:** [roadmap](../../roadmap.md#009--ciclo-de-vida-de-metadatos)

## Problema
1. Lo que se construye en el modelador vive solo en la base donde se hizo. Para llevarlo a otra base hay que repetirlo
   a mano (con errores) o escribir un paquete JSON de módulo a mano.
2. Los módulos se instalan y actualizan (002, 004), pero solo desde la carpeta del servidor y con actualizaciones
   escritas por una persona: no hay forma de saber **qué cambiaría** en un entorno que ya tiene una versión anterior
   o que se modificó por su cuenta.
3. Producción no tiene una compuerta: quien tiene acceso al modelador cambia el modelo en vivo. Falta un flujo
   *sandbox → pruebas → producción* con **aprobación** y rastro de quién aprobó qué.
4. Mover metadatos entre entornos abre riesgos propios: un paquete manipulado en el camino, un paquete que crea un rol
   con privilegios de administración o se da permisos sobre catálogos ajenos, una exportación que se lleva datos o
   secretos, y aprobaciones que después nadie reconoce.

## Historias

**H1 — Exportar un paquete.** Como *administrador del entorno de desarrollo*, quiero exportar lo que hice en el
modelador como un paquete, para llevarlo a otro entorno.
- CA1: Elijo los catálogos (sus partidas van con su documento) y los tableros, y doy identificador, nombre, versión
  y descripción. El paquete tiene **el mismo formato que `modules/*.json`**: un plan de cambios que crea lo elegido
  con sus procesos, reglas, automatizaciones, seguridad, layouts, vistas, reportes y webhooks, más los roles que usa.
- CA2: Antes de descargar veo una vista previa: pasos del plan, roles que viajan con sus permisos, catálogos de los que
  depende y que no van en el paquete, y advertencias (p. ej. una vista compartida que filtra por un registro concreto).
- CA3: **Nunca viajan datos ni secretos:** registros, usuarios, contraseñas, claves de API, vistas personales,
  bitácoras, folios, ni el destino o el secreto de los webhooks (008). Solo viajan metadatos.
- CA4: Si el entorno tiene clave de firma, el paquete va **firmado**; siempre lleva su **huella** (SHA-256) y el
  nombre del entorno de origen. La exportación queda en la auditoría y la versión siguiente se sugiere sola.

**H2 — Revisar un paquete contra el entorno destino (diff).** Como *administrador del entorno destino*, quiero ver
exactamente qué cambiaría antes de aplicarlo.
- CA1: Subo el archivo; el sistema verifica formato, firma y versión (debe ser mayor que la aplicada de ese paquete;
  un id de módulo del servidor no se acepta) y lo guarda como **pendiente** con su huella.
- CA2: Veo el **diff** contra el modelo actual: catálogos y campos que se crean, cambian o se archivan, procesos,
  reglas, seguridad, layouts, vistas, reportes, automatizaciones, webhooks y tableros que cambian, y roles que se
  crean con sus permisos. Es un **Plan de Cambios** compilado contra este entorno: errores en la vista previa, nunca
  a mitad de la aplicación.
- CA3: Lo que el plan no puede expresar se muestra como **conflicto** y bloquea la aplicación: cambiar el tipo o la
  estructura de un campo existente, reactivar un campo o catálogo archivado, quitar un proceso.
- CA4: El paquete manda sobre sus catálogos: un campo del destino que no viene en el paquete se **archiva** (sus datos
  se conservan) y se advierte. Catálogos y tableros que no van en el paquete no se tocan.

**H3 — Aplicar como actualización versionada.** Como *administrador*, quiero aplicar el paquete en una sola
transacción y saber qué versión tiene cada entorno.
- CA1: Aplicar ejecuta el diff por el Plan de Cambios (validado, en una transacción, con auditoría), crea los roles
  nuevos con sus permisos y registra el paquete y su versión. Si algo falla, el entorno queda como estaba.
- CA2: Si el modelo del entorno cambió después de que revisé (o aprobé) el diff, aplicar se rechaza y pide revisar
  de nuevo: se aplica exactamente lo que se vio.
- CA3: El historial muestra cada paquete con versión anterior y nueva, huella, firma, quién lo subió, aprobó y aplicó.
  Un mismo paquete aplicado otra vez o una versión menor se rechazan.

**H4 — Aprobación en producción.** Como *responsable de producción*, quiero que ningún paquete se aplique sin la
aprobación de una segunda persona.
- CA1: Un entorno configurado con aprobación obligatoria no aplica un paquete pendiente: primero un administrador
  **distinto** de quien lo subió lo aprueba (con comentario opcional) o lo rechaza (con motivo).
- CA2: La aprobación queda ligada a la huella del paquete y al diff que se vio; si el entorno cambia, la aprobación
  ya no sirve para aplicar.
- CA3: Subir, aprobar, rechazar y aplicar quedan en la auditoría inmutable con usuario, huella y versión.
- CA4: Solo los administradores (superusuario) ven *Entornos*; la configuración del entorno (nombre, aprobación,
  clave de firma) no se cambia desde la aplicación.

**H5 — Seguridad del paquete.** Como *responsable de seguridad*, quiero que un paquete no sirva para escalar privilegios.
- CA1: Un paquete no puede traer roles con administración del modelo o de usuarios, ni permisos sobre catálogos que no
  van en el paquete, ni datos iniciales.
- CA2: Los roles que ya existen en el destino conservan sus capacidades; solo reciben permisos sobre los catálogos que
  el paquete **crea**. Las diferencias de permisos sobre catálogos existentes se advierten, no se aplican.
- CA3: Con clave de firma configurada, un paquete sin firma o con un byte alterado se rechaza antes de guardarse, y el
  intento queda en la auditoría.

## Decisiones (CLARIFY)
Sesión sin interlocutor disponible: se tomaron estas decisiones y se registran para revisión.
- **Un entorno = una instalación** (base de datos + servidor). No se conectan entre sí: el paquete viaja como archivo.
  Así producción nunca expone una API para recibir cambios de otro servidor, y el archivo se puede revisar en git.
- **Qué viaja:** catálogos (campos, procesos, reglas, automatizaciones, seguridad por campo y registro, layout, vistas
  compartidas, reportes, webhooks — sin destino), tableros, y los roles que el metadato usa o que tienen permisos
  sobre los catálogos elegidos (con sus permisos sobre **esos** catálogos y «ver auditoría»).
- **Qué no viaja:** datos (registros, `seed` vacío), usuarios y sus roles, unidades, contraseñas, claves de API,
  destino y secreto de webhooks, bitácora de entregas, vistas personales, folios, auditoría, roles del sistema
  (*Administrador*, *Usuario*: existen en todo entorno) y las capacidades de administrar modelo o usuarios (un rol que
  las tiene viaja sin ellas, con advertencia).
- **El diff compara estados, no historiales.** El estado deseado es el modelo que resulta de compilar el plan del
  paquete sobre el destino sin sus catálogos; el diff genera el plan mínimo para llevar el destino a ese estado. Así
  funciona igual con un destino vacío, con una versión anterior o con cambios hechos a mano.
- **El paquete manda sobre sus catálogos:** campos que el destino tiene y el paquete no, se archivan (no se borran).
  Archivar un catálogo completo **no viaja** en esta versión (se archiva en cada entorno).
- **Formato:** el de `modules/*.json` (`id`, `name`, `version`, `icon`, `description`, `plan`, `roles`, `seed`) más
  `source` (entorno y fecha de origen) y `signature`. Un paquete exportado se puede copiar a `modules/` e instalar
  desde *Módulos* en un entorno vacío. Los ids de módulos del servidor (`compras`, `inventario`) no se usan para
  paquetes de entorno: sus actualizaciones siguen el camino de *Módulos*.
- **Versión:** semver `x.y.z`. Se registra en `meta.Module` (como los módulos); aplicar exige una versión mayor que la
  registrada para ese id. La exportación sugiere la siguiente versión menor.
- **Firma:** HMAC-SHA256 sobre el contenido canónico con una clave compartida por los entornos de la misma cadena
  (`Environments:SigningKey`, fuera del repositorio). Con clave configurada en el destino, la firma es obligatoria.
  Sin clave, se acepta el paquete y se muestra «Sin firma». Producción debe configurar la clave.
- **Aprobación:** `Environments:RequireApproval` (producción: `true`). Cuatro ojos: quien aprueba no puede ser quien
  subió el paquete. La aprobación guarda la huella del diff; aplicar recalcula el diff y exige que coincida.
  Sin aprobación obligatoria (sandbox, pruebas) se aplica directo, con la misma comprobación del diff revisado.
- **Quién:** solo superusuario exporta, sube, aprueba y aplica (como *Módulos* e *Integraciones*): el paquete crea
  roles y permisos.
- **Fuera de alcance:** conexión directa entre entornos, revertir un paquete aplicado (se promueve uno nuevo), mover
  datos de configuración (catálogos de monedas, unidades), archivar catálogos completos, fusión de cambios en conflicto,
  indicador del entorno en la barra superior.
