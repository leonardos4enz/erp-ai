# ADR-0007 — Motor de PDF: PDFsharp + MigraDoc (MIT) con fuente incluida

**Estado:** aceptado (2026-10-25) · **Spec:** [027](../sdd/specs/027-documentos-pdf/spec.md)

## Contexto
La spec 027 genera en el servidor el PDF de un registro (orden de compra, póliza; después la representación impresa del
CFDI, 029) a partir de una plantilla que es metadato. El repositorio es público y cualquiera puede instalar el ERP: la
licencia del motor no puede imponer costos o condiciones a quien lo use. Debe funcionar igual en Windows y Linux, con
acentos y `ñ`, sin procesos externos y con un costo de instalación razonable.

## Opciones
| Opción | Licencia (verificada 2026-10-25) | Observaciones |
|---|---|---|
| **PDFsharp + MigraDoc 6.2.4** | **MIT** (nuspec `license: MIT`; su lector de PNG, BigGustave, es de dominio público) | .NET puro, `net8/9/10`, ~2 MB. MigraDoc da párrafos, tablas que se parten entre hojas con encabezado repetido, pie con «Página N de M» e imágenes PNG/JPEG. En la compilación «Core» no busca fuentes del sistema: hay que darle un resolvedor (lo que queremos: mismo resultado en todos lados). |
| QuestPDF 2026.x | Community License v3.0 (desde 2026-07-06): *source-available*, no OSI; gratis solo para ingresos < 1 MDD, no para entidades públicas ni empresas que cotizan | API muy buena, pero un ERP público no puede obligar a quien lo instale a comprar licencia. Descartado. |
| iText 9 | AGPL o comercial | La AGPL obligaría a publicar el código de quien lo despliegue con cambios. Descartado. |
| HTML → PDF (Chromium, wkhtmltopdf) | Varias | Proceso externo, cientos de MB, superficie de ataque (motor de navegador con datos del usuario), difícil de limitar en tiempo y memoria. Descartado. |

## Decisión
**PDFsharp + MigraDoc 6.2.4 (MIT)**, con la fuente **Lato** (SIL Open Font License 1.1, Regular y Bold, ~1.3 MB) incrustada
como recurso del ensamblado y un `IFontResolver` propio que la entrega para cualquier familia (la cursiva la simula
PDFsharp). El texto entra por el modelo de objetos de MigraDoc (`AddFormattedText`), nunca por su lenguaje DDL, así que un
dato del registro no puede cambiar el formato.

El modelo de la plantilla (`PrintTemplate`, `PrintBlock`) **no conoce al motor**: el servidor resuelve la plantilla contra el
registro (con su seguridad) en un `PrintDocument` (textos finales, tablas, imágenes) y solo `PdfRenderer` usa MigraDoc.
Cambiar de motor es reescribir `PdfRenderer`, no las plantillas ni la seguridad.

## Consecuencias
- +2 MB de dependencias y +1.3 MB de fuente en el ensamblado; sin dependencias nativas.
- Imágenes: PNG y JPEG (las que MigraDoc incrusta); WebP y SVG no (spec 027, decisión 2).
- Fuente única: alfabetos fuera del latino extendido (p. ej. chino) no tendrían glifos; si hace falta, se agrega otra
  fuente OFL al resolvedor.
- Si QuestPDF u otro motor cambian su licencia, este ADR se revisa; la frontera `PrintDocument` → `PdfRenderer` lo permite.
