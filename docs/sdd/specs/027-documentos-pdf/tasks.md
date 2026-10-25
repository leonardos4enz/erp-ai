# Tareas 027

## Archivos e imágenes (H1, H2)
- [x] T1 `meta.[File]`, `meta.Company.LogoFileId/TaxId/Address` en `Bootstrap`.
- [x] T2 `Files/ImageValidator` (PNG/JPEG por firma y estructura, polyglot, bomba, dimensiones) con pruebas unitarias primero.
- [x] T3 `Files/FileStore` y `Endpoints/FileEndpoints` (servir con cabeceras, imágenes de plantilla con clave, auditoría).
- [x] T4 Logo de la empresa y datos fiscales en `CompanyEndpoints`.

## Plantillas como metadato (H3)
- [x] T5 `PrintModels` (`PrintTemplate`, `PrintBlock`, `PrintValue`, `SetPrintTemplatesOp`) y `EntityDef.PrintTemplates`.
- [x] T6 `PrintRules` + `TemplateText` y pasada final del compilador; `merge`.
- [x] T7 Persistencia (`MetadataStore`, `PlanService`) y salida solo a modeladores (`ModelEndpoints`).
- [x] T8 Entornos (`ModelDiff`), origen (`Provenance`), explorador (`Usages`, `ModelExplorer`, ficha), asistente (prompt, contexto, avisos).

## Generación (H5)
- [x] T9 ADR-0007, paquete PDFsharp-MigraDoc 6.2.4 y fuente Lato (OFL) incrustada.
- [x] T10 `PrintDocument` independiente del motor y `PdfRenderer`.
- [x] T11 `PrintFormat` (es-MX).
- [x] T12 `RecordService.Print`: plantillas que aplican, `PrintScope` con alcance por salto, `ReadsHidden` por valor/columna/condición, límites.
- [x] T13 `PrintEndpoints` (`print`, `pdf`, vista previa con token), política de tasa y auditoría.

## SPA (H1, H2, H4, H5)
- [x] T14 `ImageUpload`, Empresas (RFC, domicilio, logo) e `ImagesPage` en *Configuración › Modelo*.
- [x] T15 `PrintTemplatesEditor` (plantillas, bloques con teclado y arrastre, valores, texto con campos, condición) y pestaña *Impresión*.
- [x] T16 Vista previa con registro real.
- [x] T17 `PrintMenu` en el formulario.

## Módulos (H6)
- [x] T18 Compras 1.10.0: «Orden de compra para proveedor» e «interna».
- [x] T19 Contabilidad 1.3.0: «Póliza».

## VERIFY
- [x] T20 Pruebas unitarias (`PrintTests`, `ProvenanceTests`) y E2E `visual-pdf.spec.ts`.
- [x] T21 Capturas de referencia afectadas regeneradas y revisadas.
- [x] T22 Revisión exploratoria (teclado, lector, claro/oscuro, móvil, PDF abiertos) y `verify.md`.
- [x] T23 Roadmap (Ventas → 028, CFDI → 029, diferidos) e índice de specs.
