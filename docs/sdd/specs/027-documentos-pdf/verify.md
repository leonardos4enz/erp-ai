# Verify 027 — Documentos PDF con plantillas visuales

## 1. Pruebas unitarias
`dotnet test`: **607 correctas** (580 de antes + 27 nuevas: 26 en `PrintTests` y 1 en `ProvenanceTests`).
- **Imágenes** (`ImageValidator`): PNG y JPEG reales aceptados con el tipo y las dimensiones del archivo; SVG con script,
  HTML, GIF, WebP, PDF y texto rechazados por su contenido; *polyglot* (HTML o ZIP después del final, en PNG y en JPEG);
  bomba de descompresión (datos que se inflan más de lo que dicen las dimensiones) y dimensiones falsas o mayores a
  3 000 px (rechazadas antes de descomprimir); CRC alterado, fragmento crítico desconocido (los auxiliares se toleran),
  APNG, JPEG sin EOI y más de 1 MB.
- **Plantillas** (`PrintRules`, compilador): plantilla válida compilada y guardada; rol inexistente, expresión inválida en
  «cuándo aplica», tamaño, margen, sin bloques, tipo de bloque desconocido, `{{` sin cerrar, campo inexistente en un texto,
  columnas en el ámbito de la partida, `lines` que no es partidas, clave de imagen con `../`, dos pies, formato
  desconocido, clave de empresa inválida, plantillas en una partida, dos predeterminadas para las mismas empresas;
  archivar un campo de **otro catálogo** que usa una plantilla se reporta; `merge` conserva las demás; origen
  (`printTemplate`) y forma canónica sin cambios para catálogos sin plantillas.
- **Formato** es-MX: número, moneda con clave, negativo, porcentaje, entero, fecha corta y larga («25 de octubre de 2026»),
  sí/no y vacío.
- **PDF real** (MigraDoc): `%PDF`, fuente Lato incrustada, 400 partidas pasan a varias hojas, texto con `\bold{}`,
  `\pagebreak`, `<script>` y caracteres de control se imprime literal en una hoja.
- **Seguridad del documento resuelto** (datos simulados con `IPrintData`): quien ve todo recibe referencias con su
  etiqueta, usuario con su nombre, estado con su etiqueta y moneda; un rol sin el campo recibe «(sin acceso)» en el valor,
  en el subtítulo (`{{ Total }}`) y en el segundo salto de una ruta (`Proveedor.RFC`), **sin la columna** del precio y
  **sin el bloque** condicionado por el total; una ruta a un proveedor fuera de alcance imprime «(sin acceso)» y no filtra
  su nombre ni su RFC; `ROUND(Total)` e `IF(Total > 0, …)` tampoco lo revelan. Se comprobó que tres de estas pruebas
  **fallan** quitando `ReadsHidden` y la revisión de alcance por salto.
- `ProvenanceTests`: las cadenas reales de Compras 1.10.0 y Contabilidad 1.3.0 compilan paso por paso y sus plantillas
  dicen su paquete y versión. Se actualizaron dos aserciones de versión instalada (1.9.0 → 1.10.0, 1.2.0 → 1.3.0).

## 2. E2E
`npm --prefix web run e2e`: **385 pruebas, en verde dos veces seguidas** con el código final (corridas 7 y 8).
- Nuevo `visual-pdf.spec.ts` (19 pruebas; corre después de instalar Compras y Contabilidad): un escenario por criterio de
  H1–H6, con accesibilidad automática en Empresas, Imágenes, el editor, la vista previa y el menú del formulario:
  logo, RFC y domicilio por la UI; siete archivos maliciosos o fuera de límite rechazados con su motivo; cabeceras del
  archivo (tipo, `nosniff`, `sandbox`, `immutable`, 304, 401 sin sesión) y auditoría; 403 sin capacidad; imágenes con
  clave (subir, reemplazar, motivo de rechazo); `setPrintTemplates` inválido y `merge`; explorador y origen; editor con
  bloque nuevo, subir con teclado (el foco sigue al bloque y se anuncia) y JSON avanzado; vista previa con un registro
  real (PDF, `frame-ancestors 'self'`, de otro usuario → 404), imagen inexistente avisada, plantilla inválida → 400,
  sin capacidad → 403; descarga desde el formulario con el menú (teclado, nombre `OrdenCompra-OC-…pdf`), condición de la
  plantilla (borrador sin la de proveedor; pedida por nombre → 404), catálogo sin plantillas sin botón, almacenista solo
  con la interna, fuera de alcance → 404 y sin lectura → 403, auditoría `data.print`, póliza con su plantilla; y las dos
  regresiones de móvil de la revisión (F-03, F-04).
- Ajustados por el cambio de versión de los paquetes (decisión del plan, no regresiones): `integraciones` (versión
  instalada 1.10.0), `navegacion` (*Configuración › Modelo* incluye «Imágenes»), `visual-modelador` (Compras 1.10.0; la
  prueba de choques ya **no fija** la versión al restaurarla: la lee antes, para que la siguiente spec no tenga que tocarla).
- Capturas de referencia: ninguna cambió más allá de la tolerancia (se corrió con `--update-snapshots` y `git status` no
  mostró imágenes nuevas). La captura `formulario-movil` **detectó** que la primera corrección de F-03 bajaba «Volver» a
  otro renglón sin necesidad; se ajustó (ver F-03).

## 3. Revisión exploratoria
Con el navegador integrado sobre una instancia de la API contra la base desechable `ErpAi_E2E` (puerto 5093, credencial de
prueba generada en esta sesión y no guardada en el repositorio; la base `ErpAi` y la cuenta del dueño no se tocaron),
después de la suite, como `e2e-comprador`, `e2e-almacen`, `e2e-contador` y el administrador de prueba.

**PDF abiertos y revisados** (descargados por la API con cada rol):
- *Orden de compra para proveedor* de una OC en francos suizos (comprador): logo, empresa con RFC y domicilio en dos
  renglones con `ñ`, título y folio, campos en dos columnas (proveedor y su RFC, fechas largas, condición de pago, moneda,
  comprador), domicilio de entrega, partidas, totales «$… CHF», nota, firmas con «Comprador E2E» y «Gerente E2E» y pie
  «Página 1 de 1».
- *Orden de compra interna* de la misma OC (almacenista): sin la columna Importe y con el total «(sin acceso)».
- *Póliza* (contador): «Póliza de Diario», cuentas con código y nombre, cargos y abonos, totales y tres firmas en blanco.

**Pantallas:** formulario con «Descargar PDF» (botón con menú: Enter abre, flechas recorren, Esc cierra y regresa el
foco); modelador › *Impresión* (origen «Compras · v1.10.0» y «Local», bloques, tabla con columnas, constructor de
condiciones), vista previa con un registro elegido; Empresas (RFC, domicilio, logo y confirmación de quitar sobre el
diálogo, Esc cierra solo la confirmación); Imágenes. Oscuro y claro, escritorio y 390 px sin desbordamiento horizontal.
El navegador integrado no tiene visor de PDF: el marco de la vista previa carga el documento (`application/pdf`,
`pdfViewerEnabled`) pero su captura sale vacía y abrirlo en otra pestaña lo descarga; el contenido se revisó abriendo los
archivos. En Chrome el marco usa su visor.

### Hallazgos
| ID | Severidad | Hallazgo | Corrección | Prueba (falla con el código anterior) |
|---|---|---|---|---|
| F-01 | Baja | En la OC interna, un campo vacío (Requisición) dejaba la etiqueta sin valor: parecía un error de impresión. | Campos con etiqueta y totales vacíos imprimen «—». | `PrintTests.Un_valor_vacio_imprime_una_raya` (verificado: falla sin la corrección) |
| F-02 | Baja | En la póliza cada celda de cargo y abono repetía «MXN». | La plantilla de Contabilidad 1.3.0 usa número en la tabla y moneda solo en los totales. | — (contenido de la plantilla; la cadena compila en `ProvenanceTests`) |
| F-03 | Media | En el teléfono, con «Descargar PDF» y «Volver», el título del formulario se encogía a una palabra por renglón y el botón lo tapaba. | El título conserva un ancho mínimo (9rem) y las acciones bajan a su renglón. La primera versión (12rem) también bajaba «Volver» en formularios sin PDF: la captura `formulario-movil` lo detectó y se ajustó. | `visual-pdf` › «H5 CA1 (móvil): el botón no tapa el título» (verificado: falla con el CSS anterior) |
| F-04 | Baja | A 390 px el nombre del bloque se partía («Encabez-ado») y el asa quedaba sola en su renglón. | Nombre en una línea, base de 160 px y resumen oculto en el teléfono. | `visual-pdf` › «H4 CA7 (móvil): los bloques caben…» (verificado: falla con el CSS anterior; la primera versión de la prueba no lo detectaba y se hizo más precisa) |

### Lista de comprobación
- [x] El camino feliz de cada historia se completa sin ayuda (logo, imagen, plantilla, vista previa, descarga).
- [x] Los errores dicen qué pasó y cómo resolverlo («Vuelve a exportarlo como PNG», «súbela en Configuración › …»).
- [x] Doble clic y recarga: la descarga desactiva el botón mientras genera; la vista previa caduca y se regenera.
- [x] Datos límite: acentos y `ñ` en datos y empresa, monedas extranjeras, campos vacíos (F-01), 400 partidas (unitaria).
- [x] Claro y oscuro, 1280 px y 390 px (F-03, F-04 corregidos).
- [x] Solo iconos Lucide; estados vacíos con diseño (sin plantillas, sin imágenes, vista previa sin registro).
- [x] Teclado: menú de plantillas, bloques (Subir/Bajar con foco y anuncio), subir imagen con botón, diálogos con Esc.
- [x] Sin permiso: sin botón ni página, y la API responde 403/404.
- [x] Consola sin errores ni violaciones de CSP (guardia de la suite en cada prueba).
