# Spec 025 — Explorador del modelo y de la base de datos

**Estado:** Implementado · **Depende de:** 001, 002, 005, 006, 007, 015, 016, 019, 023, 024 · **Origen:** pedido del dueño
(2026-10-24): quiere ver la estructura completa del sistema, en especial un diagrama ER **físico** literal (lo que de verdad
existe en SQL Server), además de las vistas del modelo lógico. Ventas pasa a la 026 y CFDI 4.0 a la 027.

## Problema
El modelo vive en metadatos (`meta.Entity`, `meta.Field`, JSON de reglas, procesos, automatizaciones…) y el motor genera
las tablas reales del esquema `app` (ADR-0001). Hoy la única forma de entenderlo es abrir cada catálogo en el modelador:

| # | Pregunta | Lo que pasa hoy |
|---|---|---|
| 1 | ¿Qué tablas, columnas, tipos, llaves e índices hay en SQL Server? | Hay que entrar a SQL Server Management Studio; nada lo muestra en la app |
| 2 | ¿La base coincide con el modelo? | Las columnas de campos archivados siguen en la tabla (constitución II.9) y nadie las ve |
| 3 | ¿Cómo se relacionan los catálogos? | Cada referencia está en el editor de su catálogo; no hay vista de conjunto |
| 4 | ¿Qué se rompe si cambio este campo? | Nadie sabe qué fórmulas, reglas, transiciones, reportes o reglas de acceso lo mencionan |
| 5 | ¿Qué automatización mueve qué catálogo? | Hay que leer el JSON de cada catálogo |
| 6 | ¿Quién puede ver qué? | Los permisos están por rol en *Usuarios y roles*; los campos ocultos, en la política de cada catálogo |

## Principio
Área de **solo lectura** en *Configuración › Modelo › Explorador* (`/admin/explorer`). Nada se edita aquí: las ediciones
siguen por el modelador y el Plan de Cambios (constitución I.3). Hay enlaces «Abrir en el modelador» y «Ver pantalla», y
cada pantalla de catálogo tiene «Ver en el explorador» para quien tenga la capacidad.

## Historias

**H1 — Base de datos: diagrama ER físico (prioridad del dueño).** Como *modelador*, quiero ver lo que de verdad existe en
SQL Server.
- CA1: el diagrama muestra cada tabla del esquema `app` con su nombre real (`app.OrdenCompra`), sus columnas con el tipo SQL
  exacto (`nvarchar(200)`, `decimal(18,2)`, `bigint`, `datetime2(3)`, `rowversion`…), nulabilidad, identidad, valor por
  defecto, llave primaria, llaves foráneas con su tabla destino, índices y únicos (con su filtro `WHERE`), y distingue las
  columnas de sistema (`Id`, `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, `IsDeleted`, `RowVer`, `CompanyId`).
- CA2: se lee del catálogo de SQL Server (`sys.tables`, `sys.columns`, `sys.types`, `sys.foreign_keys`, `sys.indexes`,
  `sys.default_constraints`), no se reconstruye de los metadatos.
- CA3: las relaciones se dibujan con notación pata de gallo y su cardinalidad: del lado de la tabla referida, «uno» o «cero o
  uno» según la nulabilidad de la llave foránea; del lado de la que refiere, «cero o muchos» (o «cero o uno» si la llave
  foránea tiene un índice único).
- CA4: **deriva**: se señalan las tablas y columnas que existen en SQL pero no en el modelo (de catálogos o campos
  archivados, o sin ningún metadato), las que el modelo espera y no existen, y las columnas cuyo tipo no es el que el motor
  generaría. Hay un resumen y un filtro «solo con deriva».
- CA5: cada tabla se liga a su catálogo (etiqueta, módulo) con «Ver ficha», «Abrir en el modelador» y «Ver pantalla».
- CA6: el esquema `meta` (tablas del sistema) solo lo ve el superusuario, con un selector de esquema aparte.
- CA7: exportar el diagrama como imagen (SVG y PNG) y el **DDL** de las tablas mostradas (script `CREATE TABLE` con llaves,
  valores por defecto e índices; solo estructura). Nunca datos de negocio: el explorador no lee filas de `app`.
- CA8: usable con 100+ tablas: filtro por módulo y por texto (en el servidor), foco en una tabla con sus vecinas a 1 o 2
  niveles, zoom, desplazamiento arrastrando y modo «solo llaves».
- CA9 (accesibilidad, constitución IV.3): alternativa en tabla navegable por teclado con las mismas columnas, llaves e
  índices; en el diagrama, las tablas se recorren con flechas y Enter abre su detalle.

**H2 — Relaciones (modelo lógico).** Como *modelador*, quiero ver cómo se relacionan los catálogos.
- CA1: un nodo por catálogo activo, con color por módulo y leyenda; aristas por referencia, partidas (documento → partida),
  padre y, opcional, usuario.
- CA2: filtro por módulo y foco en un catálogo con 1 o 2 niveles de vecinos.
- CA3: cada nodo se expande a sus campos con tipo lógico e insignias (obligatorio, único, fórmula, folio, valor por defecto,
  estado) y el catálogo indica si es por empresa.
- CA4: alternativa en lista navegable por teclado.

**H3 — Ficha del catálogo.** Como *modelador*, quiero todo lo de un catálogo en una página.
- CA1: campos (tipo lógico, columna física, obligatorio, único, valor por defecto, fórmula), reglas (error o aviso, con
  condición), proceso (el diagrama de la 019 en solo lectura, con su lista de transiciones), automatizaciones (evento, modo,
  acciones), tareas programadas, webhooks, contabilidad (libro), layout (secciones y pestañas), vistas, reportes y tableros
  que lo usan, seguridad por rol (CRUD con herencia, alcance por registro, campos ocultos y de solo lectura), módulo,
  paquete y versión que lo instaló (y los que lo modifican) y su tabla física.
- CA2: «Abrir en el modelador», «Ver pantalla», «¿Dónde se usa?» y «Ver en la base de datos».

**H4 — ¿Dónde se usa?** Como *modelador*, antes de cambiar un campo quiero saber qué lo menciona.
- CA1: para un catálogo o un campo: cada fórmula, valor por defecto, regla, condición o asignación de transición, campo
  obligatorio de transición, condición de layout, automatización, tarea, reporte, vista, tablero, webhook, regla de acceso
  (campos y `where`), filtro de búsqueda (`lookupFilter`), libro contable, campo que lo muestra (`displayField`) y referencia
  que apunta al catálogo.
- CA2: lo calcula el servidor con el analizador de expresiones del motor (`ExprParser`/`ExprAnalyzer`), incluidas las rutas
  de tres niveles de la 023 (`Recepcion.OrdenCompra.Moneda.Clave` menciona los cuatro catálogos) y `SUM`/`COUNT` de
  partidas, en caché por versión del modelo. Genérico: sin código por módulo.
- CA3: cada mención dice dónde está (catálogo y elemento) y la expresión, con enlace al modelador.

**H5 — Automatizaciones (flujo entre catálogos).** Como *modelador*, quiero ver qué mueve qué.
- CA1: «cuando pasa X en A → actualiza / crea / transiciona / acumula / valúa B», como diagrama y como lista.
- CA2: se marca si corre en la transacción (síncrona), después (asíncrona, 016) o por calendario (tarea programada).
- CA3: se señalan los ciclos (aristas que vuelven a disparar automatizaciones del catálogo de origen).

**H6 — Seguridad y navegación.** Como *administrador*, quiero ver quién puede qué y dónde está cada cosa.
- CA1: matriz roles × catálogos con CRUD, la herencia de roles (015) marcada, el alcance por registro y los campos ocultos o
  de solo lectura por rol.
- CA2: árbol Módulo → menú → pantalla → secciones (y pestañas) → campos, navegable con teclado.

**H7 — Acceso y seguridad.**
- CA1: todo el explorador exige *administrar modelo*; el esquema `meta`, superusuario. Sin la capacidad no aparece en el área
  de configuración, la ruta muestra «sin permiso» y la API responde 403.
- CA2: todo se filtra en el servidor; las consultas al catálogo de SQL son parametrizadas y limitadas a los esquemas
  permitidos (lista blanca en el servidor, nunca de la petición).

**H8 — Diseño.** Claro y oscuro, escritorio y móvil (390 px) sin desbordes; solo iconos Lucide; estados vacíos, de carga y
de error con diseño.

## Decisiones (CLARIFY)
La sesión que abrió la spec (por encargo del dueño) delegó estas decisiones; quedan registradas aquí.

- **Distribución de los grafos: SVG propio, sin librería.** Se evaluó `elkjs` empaquetado con la app (la CSP estricta impide
  cargarlo de un CDN). En contra: más de 1 MB minificado adicional (más que toda la SPA sin iconos), su versión en *worker* necesita
  `worker-src`/`blob:` que la CSP no permite y la versión síncrona bloquea el hilo con grafos grandes; su resultado no es
  determinista entre versiones (capturas visuales). A favor de lo propio: el diseñador de la 019 ya usa capas desde el
  estado inicial con el mismo SVG y estilos, y para 100+ tablas lo que da usabilidad es el **foco** (1–2 niveles), el
  filtro y el zoom, no un trazado global perfecto. Decisión: distribución jerárquica propia al estilo Sugiyama (`lib/graphLayout.ts`,
  pura): capas por camino más largo, partición de capas largas, nodos virtuales para que las flechas largas pasen por carriles
  entre las cajas, barrido de baricentros con transposición para reducir cruces y etiquetas acomodadas sin encimarse (pedido
  del dueño durante la revisión). Sin dependencia nueva, no requiere ADR.
- **Deriva.** Se compara por nombre (sin distinguir mayúsculas). Tabla de un catálogo archivado y columna de un campo
  archivado: «archivado» (esperado por la constitución II.9, se informa). Sin ningún metadato: «sin metadato». Esperada y
  ausente: «falta en SQL». Tipo distinto del que generaría `DdlGenerator.ColumnType`: «tipo distinto». Las columnas de
  sistema se esperan en toda tabla de catálogo.
- **DDL exportado.** Se genera desde el catálogo de SQL (lo que existe), no desde los metadatos; incluye `CREATE TABLE` con
  identidad, nulabilidad, valores por defecto con nombre, llave primaria, llaves foráneas y `CREATE [UNIQUE] INDEX` con su
  filtro. Es un archivo de texto para descargar: el explorador nunca lo ejecuta.
- **Sin conteo de filas.** Ni `sys.partitions.rows`: el dueño pidió nunca datos de negocio y el volumen lo es.
- **Llaves hacia `meta`.** Las referencias a `meta.User` y `meta.Company` se dibujan como tablas externas (solo el nombre)
  cuando se ve el esquema `app`; sus columnas solo las ve el superusuario en el esquema `meta`.
- **Ciclos de automatizaciones.** Se usa el mismo criterio que el compilador (`AutomationRules.Effects`): una arista dispara
  si el catálogo destino tiene automatizaciones del evento que la acción provoca; un ciclo es una componente fuerte de esas
  aristas (o una arista a sí mismo que dispara). El compilador ya rechaza esos ciclos al aplicar un plan, así que
  hoy el explorador no debería encontrar ninguno; los marca igual (defensa en profundidad, p. ej. un modelo anterior a la
  regla). Las tareas programadas se muestran como punto de entrada: no las dispara un evento, así que no cierran ciclos.
- **Ruta y nombre.** `/admin/explorer` («Explorador», grupo *Modelo*), con la vista en `?vista=` y el catálogo en
  `?catalogo=`, para que cada vista sea un enlace guardable.

## Fuera de alcance
Editar desde el explorador; datos de negocio (filas, conteos); comparar dos entornos (eso es Entornos, 009); generar
migraciones a partir de la deriva (las columnas archivadas se conservan a propósito); vistas, procedimientos y disparadores de
SQL (el motor no los crea).
