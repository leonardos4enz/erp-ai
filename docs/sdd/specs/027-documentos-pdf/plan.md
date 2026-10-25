# Plan 027 — Diseño técnico

Tres capacidades genéricas: archivos de imagen del entorno (`meta.[File]`), plantillas de impresión como metadato
(`setPrintTemplates`) y un generador de PDF en el servidor (PDFsharp + MigraDoc, [ADR-0007](../../../adr/0007-motor-pdf.md)).

## 1. Modelo de datos
- `meta.[File]`: `Id`, `Purpose` (`logo` | `image`), `FileKey` (clave de las imágenes de plantilla; índice único filtrado
  entre las vigentes), `ContentType` (el que fijó el validador), `Size`, `Width`, `Height`, `Sha256`, `Content`
  (`varbinary(max)`), `IsArchived`, `CreatedBy`, `CreatedAt`, `ArchivedAt`. Reemplazar o quitar archiva (II.9).
- `meta.Company`: `LogoFileId` (FK a `meta.[File]`), `TaxId` (≤ 20) y `Address` (≤ 500).
- `meta.Entity.PrintTemplatesJson`: lista de `PrintTemplate` del catálogo.

## 2. Metadato: `setPrintTemplates`
```json
{ "op": "setPrintTemplates", "entity": "OrdenCompra", "merge": true, "templates": [
  { "name": "Proveedor", "label": "Orden de compra para proveedor", "description": "…", "roles": ["Comprador"],
    "companies": ["DEMO"], "when": "Estado IN ('Autorizada')", "default": true,
    "page": { "size": "letter|a4", "orientation": "portrait|landscape", "margin": 15 },
    "blocks": [
      { "type": "header", "logo": true, "company": true, "title": "Orden de compra", "subtitle": "{{ Folio }}" },
      { "type": "fields", "perRow": 2, "items": [{ "label": "Proveedor", "value": "Proveedor" }] },
      { "type": "table", "lines": "Partidas", "columns": [{ "label": "Importe", "value": "Importe", "format": "number", "width": 3, "align": "right" }] },
      { "type": "totals", "items": [{ "label": "Total", "value": "Total", "format": "money", "currency": "Moneda.Clave", "bold": true }] },
      { "type": "text", "text": "Notas: {{ Notas }}", "size": 9, "bold": false, "italic": false, "align": "left", "visibleWhen": "NOT ISBLANK(Notas)" },
      { "type": "image", "file": "SelloCalidad", "width": 40, "align": "right" },
      { "type": "separator" }, { "type": "pageBreak" },
      { "type": "signatures", "signatures": [{ "label": "Autorizó", "name": "{{ AutorizadoPor }}" }] },
      { "type": "footer", "text": "OC {{ Folio }}", "pageNumbers": true } ] } ] }
```
- Validación (`PrintRules`, en la pasada final del compilador para **todos** los catálogos, como reportes y webhooks): ver
  spec H3 CA2. El ámbito de las columnas de una tabla es la partida (el documento por su campo padre).
- Textos con `{{ expresión }}` (`TemplateText`): hasta 40 campos por texto; cada expresión con `ExprAnalyzer`.
- Viaja en paquetes de Entornos (`ModelDiff.Sets`), instantáneas y rollback (017, por `EntityOf`), origen (026, kind
  `printTemplate`), explorador (025, uso `print` con pestaña `impresion`), asistente (011: prompt, contexto sin claves de
  empresa, aviso si quita plantillas). `EntityDef.PrintTemplates` y `merge` se omiten en nulo/falso: la forma canónica de
  lo ya firmado no cambia.

## 3. Contrato de la API
| Método y ruta | Quién | Respuesta |
|---|---|---|
| `GET /api/files/{id}?v={sha256}` | cualquier sesión | La imagen vigente con `Content-Type` del servidor, `nosniff`, `Content-Security-Policy: default-src 'none'; …; sandbox`, `ETag` = SHA-256, `Cache-Control: private, max-age=31536000, immutable` si `v` coincide (si no, `no-cache`); 304 con `If-None-Match`; 404 si no existe o está archivada; 401 sin sesión. |
| `GET /api/admin/images` | `CanManageModel` | `[{ id, key, contentType, size, width, height, sha256, createdBy, createdAt, url }]` |
| `PUT /api/admin/images/{key}` (cuerpo = archivo) | `CanManageModel` | La imagen guardada; reemplaza (y archiva) la vigente con esa clave. 400 con `errors.file` si no pasa la validación; `errors.key` si la clave no es identificador. |
| `DELETE /api/admin/images/{key}` | `CanManageModel` | 204; archiva. |
| `PUT /api/admin/companies/{id}/logo` (cuerpo = archivo) | superusuario | `CompanyDto` con `logoUrl`. |
| `DELETE /api/admin/companies/{id}/logo` | superusuario | `CompanyDto`. |
| `GET/POST/PUT /api/admin/companies` | (010) | `CompanyDto` agrega `taxId`, `address`, `logoUrl`; la petición acepta `taxId` y `address`. |
| `GET /api/data/{entity}/{id}/print` | lectura del catálogo y del registro | `[{ name, label, description, default }]`: las plantillas que aplican (rol, empresa actual, condición); la predeterminada primero. |
| `GET /api/data/{entity}/{id}/pdf?template=` | lectura del catálogo y del registro; política de tasa `print` | `application/pdf` como adjunto (`{Entidad}-{folio}.pdf`, caracteres seguros), `Cache-Control: no-store`, `frame-ancestors 'none'`. 404 si el registro no está a su alcance o la plantilla no aplica; 403 sin lectura del catálogo; 429 por tasa; 503 si hay 4 generando o tarda más de 20 s. Auditoría `data.print` con `{ template }`. |
| `POST /api/model/print/preview` `{ entity, id, template }` | `CanManageModel`; tasa `print` | `{ token, warnings }`. 400 con `errors.template` si la plantilla no pasa `PrintRules`. Se genera con la seguridad del modelador. |
| `GET /api/model/print/preview/{token}` | quien la generó (5 min) | `application/pdf` en línea, `frame-ancestors 'self'`, `X-Frame-Options: SAMEORIGIN`; 404 si caducó o es de otro usuario. |

Las rutas de `/api/data` admiten clave de API y token OAuth con alcance de lectura (008/018), como la exportación.

## 4. Generación (`RecordService.Print.cs`, `Printing/`)
1. Catálogo raíz, permiso de lectura y **alcance del registro** (`EnsureAccessAsync`: 404 fuera de alcance).
2. Borrador tipado del registro y sus partidas; `PrintScope` (como `DocScope`) que en **cada salto** a otro registro
   pregunta `Visible` (filtro de la 006/010/015, en caché por documento) y, si no, lanza `PrintNoAccess`.
3. Plantilla: la pedida o la predeterminada entre las que **aplican** (roles —el superusuario todas—, empresa actual y
   `when`).
4. `ResolvePrint` → `PrintDocument`: cada expresión pasa antes por `ReadsHidden` (seguridad por campo en cada salto, 007/023):
   valor → «(sin acceso)», columna → se omite, condición → el bloque no se imprime. Una referencia se imprime con el campo
   para mostrar de su destino (un salto más, con las mismas revisiones); un usuario con su nombre; el estado con su
   etiqueta. Formatos es-MX (`PrintFormat`). La empresa del encabezado es la del registro (por empresa) o la actual.
5. `PdfRenderer.Render` (MigraDoc) en un hilo aparte, con semáforo de 4 y tope de 20 s. Hasta 500 partidas por tabla (fila
   «… y N partidas más») y 10 imágenes.

## 5. SPA
- `components/PrintMenu.tsx`: «Descargar PDF» en el encabezado del formulario (botón o menú ARIA con flechas y Esc).
- `components/ImageUpload.tsx`: miniatura, subir, cambiar y quitar (con confirmación); los mensajes del servidor tal cual.
- `CompaniesPage`: RFC, domicilio y logo (solo superusuario). `ImagesPage` (`/admin/images`, *Configuración › Modelo*).
- `sections/PrintTemplatesEditor.tsx`: pestaña *Impresión* (ancha) con `ItemList` de plantillas (origen 026), datos de la
  plantilla, `BlocksEditor` (menú «Agregar bloque», Subir/Bajar con foco que sigue al bloque y anuncio `aria-live`,
  arrastrar y soltar por el asa), editores por tipo (`ValuesEditor`, `TemplateText` con «Insertar campo» usando
  `ExprInput`, constructor de condiciones en «Mostrar solo cuando») y `Preview` (registro con `ReferenceSelect`, `iframe`
  del mismo origen y «Abrir en otra pestaña»). «Ver como JSON» plegado como las demás secciones.
- Explorador: ficha con «Plantillas de impresión» y tipo de uso «Plantilla de impresión».

## 6. Pruebas
- Unitarias `PrintTests`: validador de imágenes (PNG/JPEG reales, SVG/HTML/GIF/WebP/PDF, polyglot PNG y JPEG, bomba
  de descompresión, dimensiones falsas o excesivas, CRC, fragmentos críticos desconocidos, APNG, sin EOI, > 1 MB); reglas
  de plantillas y compilador (rol, expresión, tamaño, margen, bloques, tipo desconocido, llave sin cerrar, ámbito de la
  partida, lines, clave de imagen, dos pies, formato, empresa, partida sin plantillas, dos predeterminadas, campo archivado
  en otro catálogo, `merge`); origen y forma canónica; formato es-MX; PDF real (fuente incrustada, varias hojas, texto que
  parece DDL); **seguridad del documento resuelto** con datos simulados (valores, columnas, condiciones, rutas a registros
  fuera de alcance, fórmulas que leen campos ocultos). `ProvenanceTests`: las plantillas de Compras y Contabilidad compilan
  en la cadena real y llevan su paquete.
- E2E `visual-pdf.spec.ts` (después de instalar Compras y Contabilidad): un escenario por criterio.

## 7. STRIDE
| Amenaza | Riesgo | Mitigación | Prueba |
|---|---|---|---|
| **S** | Subir o leer archivos sin sesión o sin la capacidad | `/api/files` exige sesión; imágenes solo `CanManageModel`; logo solo superusuario | E2E H1 CA4, CA5 |
| **T** (archivo malicioso) | SVG con script, HTML o ZIP disfrazados de imagen (*polyglot*), tipo falso del cliente | Firma de bytes + estructura completa (CRC de cada fragmento PNG, marcadores JPEG hasta EOI) y **nada después del final**; el tipo lo fija el servidor; se sirve con `nosniff`, CSP `sandbox` y `Content-Disposition: inline` con nombre fijo | Unitarias y E2E H1 CA3 |
| **D** (bomba de descompresión) | Un PNG de pocos KB que se infla a GB al decodificarse | ≤ 1 MB leído con tope; dimensiones ≤ 3 000 px y ≤ 6 MP **antes** de descomprimir; los IDAT se inflan con tope y deben dar exactamente los bytes de sus dimensiones | Unitarias, E2E H1 CA3 |
| **T** (inyección en la plantilla) | Texto de la plantilla o del registro que cambia el formato (DDL de MigraDoc) o ejecuta algo | Todo texto entra por el modelo de objetos como texto literal; se quitan caracteres de control; las expresiones son el lenguaje de la 002 (sin SQL ni E/S); bloques y formatos por lista blanca | Unitaria «el texto del registro no cambia el formato», `PrintRules` |
| **I** (campo oculto) | El PDF muestra un campo que el rol no ve (directo, en una ruta, dentro de una fórmula o por una condición) | `ReadsHidden` antes de evaluar cada valor, columna y condición (decisión 4) | Unitarias H5 CA2 |
| **I** (registro fuera de alcance) | Una ruta (`Proveedor.RFC`) lee un registro de otra empresa o fuera del alcance por registro | Cada salto pregunta `Visible` (filtro de la 006/010/015) y si no, «(sin acceso)»; el propio registro: 404 | Unitaria H5 CA2, E2E H5 CA2 |
| **I** (asistente) | Las claves de empresa de una plantilla llegan al LLM | El contexto del asistente las quita | Revisión de `AssistantContext` |
| **I** (vista previa) | Otro usuario abre la vista previa de un modelador | Token aleatorio de 128 bits, ligado al usuario, 5 minutos, máx. 3 por usuario | E2E H4 CA6 |
| **R** | Generar PDF o cambiar imágenes sin rastro | Auditoría `data.print` y `file.change` (sin contenido) | E2E H1 CA4, H5 CA3 |
| **D** (consumo) | Muchas partidas, muchas imágenes, muchas peticiones | 500 partidas, 10 imágenes, 60 bloques, 4 simultáneas, 20 s, 30 por minuto por usuario | E2E H5 CA4, `PrintLimits` |
| **E** | Una plantilla «concede» acceso (roles, empresas) | Las plantillas solo filtran; la lectura del catálogo y del registro se exige siempre | E2E H5 CA2 |
| **T** (clickjacking) | Enmarcar el PDF en otro sitio | `frame-ancestors 'none'` salvo la vista previa (`'self'`) | E2E H4 CA6, H5 CA2 |
