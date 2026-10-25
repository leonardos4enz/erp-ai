# Spec 027 — Documentos PDF con plantillas visuales

**Estado:** Implementado · **Depende de:** 002, 006, 009, 010, 011, 015, 017, 019, 023, 025, 026 · **Origen:** pedido del
dueño (2026-10-25): diseñar desde el sistema, con un editor visual, el PDF de los documentos (orden de compra, póliza y,
más adelante, la representación impresa de la factura CFDI). Ventas pasa a la 028 y CFDI 4.0 a la 029.

## Problema
| # | Hallazgo (verificado en el código) | Lo que pasa hoy |
|---|---|---|
| 1 | No se genera ningún PDF | No hay librería, ni `@media print`, ni plantillas; solo se exporta CSV/XLSX (008) |
| 2 | No hay dónde guardar un logo | El sistema no almacena archivos; Kestrel limita el cuerpo a 2 MB «porque los catálogos no reciben archivos» |
| 3 | Una plantilla escrita en código violaría la constitución I | Cada módulo necesitaría su propio formato impreso; debe ser metadato, viajar en paquetes y poderlo proponer la IA |

## Principio
Capacidades **genéricas** del motor: (a) archivos de imagen del entorno, (b) plantillas de impresión como metadato del
catálogo (operación nueva `setPrintTemplates` en el Plan de Cambios, con `merge` como las listas de la 023), (c) un
generador de PDF en el servidor que respeta la seguridad por registro, por campo y por empresa. Compras y Contabilidad
declaran sus plantillas con `upgrades` de su paquete; ningún módulo tiene código propio.

## Historias

**H1 — Logo de la empresa.** Como *superusuario*, quiero subir el logo de cada empresa para que salga en sus documentos.
- CA1: en *Configuración › Empresas* cada empresa tiene «Logo»: subir (PNG o JPEG), ver la miniatura, cambiarlo y quitarlo.
  También RFC (identificación fiscal) y domicilio, que el encabezado del PDF imprime.
- CA2: el servidor valida el archivo por su **firma de bytes** y su estructura completa (no por la extensión ni por el
  `Content-Type` del cliente): PNG o JPEG, máximo 1 MB, máximo 3 000 px por lado y 6 megapíxeles; un archivo que no lo
  cumple se rechaza con un mensaje que dice qué falló y cómo resolverlo (p. ej. «Vuelve a exportarlo como PNG»).
- CA3: SVG, WebP, GIF y cualquier otro formato se rechazan (decisión 2); también un PNG o JPEG con datos después de su
  final (*polyglot*), dimensiones falsas o datos comprimidos que no corresponden a sus dimensiones (bomba de descompresión).
- CA4: el archivo se sirve desde el mismo origen, autenticado, con el `Content-Type` que fijó el servidor al validarlo,
  `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox` y caché por huella
  (`ETag` = SHA-256; la URL lleva la huella). Subirlo, cambiarlo y quitarlo queda en la auditoría.
- CA5: solo el superusuario sube o quita el logo (las empresas son suyas, 010); sin permiso, la opción no aparece y la API
  responde 403.

**H2 — Imágenes de las plantillas.** Como *modelador*, quiero subir imágenes (un sello, una firma escaneada) para usarlas
en plantillas.
- CA1: en *Configuración › Modelo › Imágenes* el modelador sube imágenes con una **clave** (identificador, p. ej.
  `SelloCalidad`), las ve en miniatura con tamaño y dimensiones, las reemplaza y las archiva. Mismas validaciones que H1.
- CA2: una plantilla las nombra por su clave (no por un id), así que la plantilla viaja entre entornos y cada entorno tiene
  su imagen (como el destino de un webhook, 008). Una clave que no existe en el entorno no imprime nada y la vista previa
  lo avisa.

**H3 — Plantillas como metadato.** Como *modelador*, quiero declarar varias plantillas por catálogo.
- CA1: `setPrintTemplates` define las plantillas de un catálogo (sin `merge`, la lista completa; con `merge`, agrega o
  reemplaza por nombre). Cada plantilla: nombre, etiqueta, descripción, roles que la usan, empresas (claves; vacío =
  todas), condición de cuándo aplica (expresión), predeterminada, tamaño de página (carta o A4), orientación y márgenes, y
  sus bloques.
- CA2: el compilador valida todo: identificadores, etiquetas, roles existentes, expresiones con `ExprAnalyzer` en el ámbito
  correcto (el registro; la partida en las columnas de una tabla), rutas de hasta tres saltos (023), claves de imagen,
  límites (30 plantillas por catálogo, 60 bloques, 12 columnas, 4 000 caracteres de texto por bloque) y que no haya dos
  predeterminadas para el mismo conjunto de empresas. Un campo archivado o una partida eliminada que rompa una plantilla
  se reporta aunque el plan no toque su catálogo (como los reportes, 007).
- CA3: las plantillas viajan en los paquetes de Entornos (`ModelDiff`, instantáneas de la 017, rollback) y en los módulos;
  la vista previa de actualizar un módulo cuenta sus choques (026); el explorador las muestra en la ficha y en «¿dónde se
  usa?» (025); el asistente las conoce y las puede proponer (011), con aviso si su plan quita plantillas.

**H4 — Editor visual de plantillas.** Como *modelador*, quiero diseñar el PDF sin escribir JSON.
- CA1: pestaña *Impresión* del editor del catálogo, con el mismo estilo de los editores de la 026: lista de plantillas
  (agregar, subir, bajar, quitar, plegar, origen del paquete) y «Ver como JSON» plegado.
- CA2: bloques: **encabezado** (logo, nombre y datos de la empresa, título y subtítulo), **texto** libre con campos
  insertados (`{{ expresión }}`, con el editor de expresiones de la 026), **campos con etiqueta** (en 1 a 3 columnas),
  **tabla de partidas** (columnas elegibles con su formato, ancho y alineación), **totales**, **imagen**, **separador**,
  **firmas**, **salto de página** y **pie** (texto y «Página N de M» en cada hoja).
- CA3: los bloques se agregan desde un menú, se reordenan con teclado (botones Subir y Bajar con nombre accesible) y con
  el mouse (arrastrar y soltar), y se quitan; el foco no se pierde al moverlos.
- CA4: cada bloque tiene visibilidad condicional («Mostrar solo cuando», constructor de condiciones de la 026).
- CA5: formato por valor: texto, número (decimales), moneda (con la clave de moneda de un campo o la moneda base de la
  empresa), porcentaje, fecha corta, fecha larga («25 de octubre de 2026») y fecha y hora; sí/no para casillas; las
  referencias, usuarios y estados se imprimen con su etiqueta.
- CA6: **vista previa** con un registro real que elige el modelador: genera el PDF con la plantilla del borrador (aunque
  no esté aplicada) y lo muestra junto al editor, con «Abrir en otra pestaña» (en el teléfono no se incrusta el PDF). Los
  avisos (imagen inexistente, campos sin acceso) se listan junto a la vista previa.
- CA7 (constitución IV): iconos Lucide, AA en claro y oscuro, todo con teclado y lector de pantalla, 390 px sin
  desbordamiento.

**H5 — Descargar el PDF de un registro.** Como *usuario*, quiero descargar el PDF del documento que estoy viendo.
- CA1: el formulario de un registro guardado muestra «Descargar PDF» si el catálogo tiene alguna plantilla que aplique al
  registro y al usuario (roles, empresa actual y condición); con varias, un menú para elegir (la predeterminada primero).
- CA2: el PDF respeta la seguridad de la 006/010/015: el registro debe estar al alcance de lectura (si no, 404 como en la
  lectura), la empresa del registro manda en el encabezado, y **un valor que lee un campo oculto para el rol imprime
  «(sin acceso)»**; una columna de tabla que lee un campo oculto no se imprime; un bloque condicionado por un campo oculto
  no se imprime (decisión 4). Una ruta que pasa por un registro relacionado fuera del alcance del usuario imprime
  «(sin acceso)».
- CA3: cada generación queda en la auditoría (catálogo, registro, plantilla y quién), sin el contenido.
- CA4: límites contra consumo de recursos: hasta 500 partidas por tabla (las demás se resumen en una fila «y N partidas
  más»), 10 imágenes por documento, 4 generaciones simultáneas en el servidor, 20 s por generación y 30 generaciones por
  minuto por usuario; al pasarlos, 429/503 con un mensaje claro.
- CA5: la API es `GET /api/data/{entidad}/{id}/pdf?template=` (también para claves de API y OAuth con alcance de lectura,
  008/018); el nombre del archivo sale de los metadatos y del folio, nunca de la petición.

**H6 — Ejercerlo con metadatos.** Como *dueño*, quiero que los módulos traigan sus formatos.
- CA1: Compras (actualización 1.10.0) declara «Orden de compra para proveedor» (predeterminada) y «Orden de compra
  interna» en `OrdenCompra`.
- CA2: Contabilidad (actualización 1.3.0) declara «Póliza» en `Poliza`, con la tabla de cuentas, cargos, abonos y totales.

## Decisiones (CLARIFY)
Resueltas con el criterio del dueño («decide en CLARIFY») y la constitución:

1. **Motor: PDFsharp + MigraDoc 6.2.4 (MIT)**, ver [ADR-0007](../../../adr/0007-motor-pdf.md). QuestPDF se descarta: su
   licencia comunitaria (v3.0, vigente desde el 6 de julio de 2026) no es de código abierto y obliga a comprar licencia a
   empresas de más de 1 MDD de ingresos, a entidades públicas y a empresas que cotizan en bolsa: un ERP público no puede
   imponer eso a quien lo instale. iText (AGPL) y wkhtmltopdf/Chromium (procesos externos, tamaño, superficie de ataque)
   también se descartan. La fuente es **Lato** (SIL OFL 1.1) incluida en el repositorio, con acentos y `ñ`, igual en
   Windows y Linux (el resolvedor de fuentes es propio: no depende de las fuentes del sistema).
2. **Formatos de imagen: PNG y JPEG.** WebP no: MigraDoc no lo incrusta y habría que decodificarlo con otra librería (más
   superficie de ataque) para un caso que el PNG ya cubre. **SVG no**: sanitizarlo de forma demostrable exige un
   analizador XML con lista blanca de elementos y atributos, y aun así su conversión a PDF necesitaría otra librería; se
   difiere. GIF y BMP tampoco.
3. **Almacenamiento: `meta.File` en la base** (`varbinary(max)`, tipo validado, tamaño, ancho, alto, SHA-256, clave, uso,
   quién y cuándo). Los archivos son **datos del entorno**: no viajan en paquetes ni en instantáneas (como el destino de un
   webhook). Reemplazar un archivo crea uno nuevo; el anterior se conserva archivado (constitución II.9).
4. **Campos ocultos en el PDF:** un valor que los lee imprime «(sin acceso)» (el lector sabe que hay un dato que no le toca
   ver, igual que una referencia fuera de alcance en la 012); una **columna** de tabla que lee un campo oculto se omite
   (una columna llena de «(sin acceso)» solo estorba); una **condición** de visibilidad que lee un campo oculto oculta el
   bloque (su resultado revelaría el dato, criterio de la 007 al revés: aquí lo seguro es no imprimir).
5. **Tipo de campo `image`/`file` en catálogos: se difiere.** Necesita DDL, captura en el formulario, importación y
   exportación, OData, listados con miniaturas, límites por registro y limpieza de huérfanos: es una spec por sí mismo. Esta
   spec deja la base (`meta.File`, validación y servicio) para que esa spec solo agregue el tipo.
6. **Plantilla de quién decide cuál usar:** el usuario elige entre las que le aplican; sin elección, la predeterminada que
   aplique, y si no, la primera que aplique. Una plantilla que no aplica (rol, empresa o condición) no se genera aunque se
   pida por nombre (404).
7. **Vista previa:** el servidor genera el PDF del borrador con la sesión del modelador (su propia seguridad), lo guarda en
   memoria 5 minutos ligado a su usuario y lo sirve en un `iframe` del mismo origen; la página no cambia su CSP global, solo
   esa respuesta permite enmarcarse en el mismo origen (`frame-ancestors 'self'`).
8. **Encabezado y pie:** el encabezado se imprime al inicio del documento; el pie, en todas las hojas.

## Fuera de alcance (anotado en el roadmap)
Enviar el PDF por correo (no hay capacidad de correo); adjuntarlo automáticamente al ejecutar una transición; firma
electrónica; tipo de campo imagen/archivo en catálogos (decisión 5); SVG y WebP (decisión 2); diseño libre por
coordenadas (los bloques fluyen de arriba abajo); códigos QR y de barras (los necesitará el CFDI, 029).
