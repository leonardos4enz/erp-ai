using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Erp.Api.Files;
using Erp.Api.Metadata;
using Erp.Api.Printing;

namespace Erp.Tests;

/// <summary>Spec 027: imágenes validadas por su contenido, plantillas como metadato, formato y generación del PDF.</summary>
public class PrintTests
{
    // ---------------- Imágenes (H1 CA2-CA3) ----------------

    /// <summary>PNG real (RGB de 8 bits) con los datos comprimidos que corresponden a sus dimensiones.</summary>
    internal static byte[] Png(int width, int height, Func<byte[], byte[]>? tamperIdat = null, byte[]? trailing = null, int? declaredWidth = null,
        string? extraChunk = null)
    {
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++) { raw.WriteByte(0x2B); raw.WriteByte(0x6C); raw.WriteByte(0xB0); }
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw.ToArray());
        var idat = z.ToArray();
        if (tamperIdat is not null) idat = tamperIdat(idat);
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)(declaredWidth ?? width));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr);
        if (extraChunk is not null) Chunk(ms, extraChunk, [1, 2, 3]);
        Chunk(ms, "IDAT", idat);
        Chunk(ms, "IEND", []);
        if (trailing is not null) ms.Write(trailing);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] body)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
        s.Write(len);
        var typed = Encoding.ASCII.GetBytes(type).Concat(body).ToArray();
        s.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ImageValidator.Crc32(typed));
        s.Write(crc);
    }

    /// <summary>JPEG real de 4 × 3 px (generado con System.Drawing y guardado aquí: las pruebas no dependen del SO).</summary>
    internal static readonly byte[] Jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAADAAQDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDEooor9dPyQ//Z");

    [Fact]
    public void H1_CA2_png_y_jpeg_validos_con_el_tipo_y_las_dimensiones_del_archivo()
    {
        var (png, e1) = ImageValidator.Validate(Png(5, 4));
        Assert.Null(e1);
        Assert.Equal(new ImageInfo("image/png", 5, 4), png);
        var (jpg, e2) = ImageValidator.Validate(Jpeg);
        Assert.Null(e2);
        Assert.Equal(new ImageInfo("image/jpeg", 4, 3), jpg);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "SVG")]
    [InlineData("<html><script>alert(1)</script></html>", "HTML")]
    [InlineData("GIF89a......", "GIF")]
    [InlineData("RIFF....WEBPVP8 ", "WebP")]
    [InlineData("%PDF-1.7", "PDF")]
    [InlineData("hola", "no es una imagen")]
    public void H1_CA3_otros_formatos_se_rechazan_por_su_contenido(string content, string expected)
    {
        var (img, error) = ImageValidator.Validate(Encoding.UTF8.GetBytes(content));
        Assert.Null(img);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void H1_CA3_polyglot_con_datos_despues_del_final_se_rechaza()
    {
        var html = Encoding.UTF8.GetBytes("<html><script>alert(document.cookie)</script></html>");
        Assert.Contains("después de su final", ImageValidator.Validate(Png(4, 4, trailing: html)).Error);
        Assert.Contains("después de su final", ImageValidator.Validate([.. Jpeg, .. html]).Error);
        // Un ZIP pegado al final (el clásico polyglot GIFAR/PNG+ZIP).
        Assert.Contains("después de su final", ImageValidator.Validate(Png(4, 4, trailing: [0x50, 0x4B, 0x03, 0x04, 0, 0])).Error);
    }

    [Fact]
    public void H1_CA3_bomba_de_descompresion_y_dimensiones_falsas_se_rechazan()
    {
        // Declara 3000 × 2000 (dentro del límite de lado y de 6 MP) pero sus datos son de 4 × 4: no corresponden.
        Assert.Contains("no corresponden", ImageValidator.Validate(Png(4, 4, declaredWidth: 1500)).Error);
        // Más de 3 000 px de lado: se rechaza antes de descomprimir nada.
        Assert.Contains("máximo es 3000 px", ImageValidator.Validate(Png(4, 4, declaredWidth: 40_000)).Error);
        // Datos que se inflan más de lo que dicen las dimensiones (zlib de una imagen más grande).
        var bigger = Png(64, 64);
        var small = Png(8, 8, tamperIdat: _ => ExtractIdat(bigger));
        Assert.Contains("no corresponden", ImageValidator.Validate(small).Error);
    }

    [Fact]
    public void H1_CA3_estructura_danada_o_fragmentos_desconocidos_se_rechazan()
    {
        var png = Png(4, 4);
        png[^9] ^= 0xFF; // CRC del IEND
        Assert.Contains("dañado", ImageValidator.Validate(png).Error);
        Assert.Contains("dañado", ImageValidator.Validate(Png(4, 4, extraChunk: "ZZZZ")).Error); // crítico desconocido
        Assert.Null(ImageValidator.Validate(Png(4, 4, extraChunk: "tEXt")).Error); // auxiliar: se tolera
        Assert.Contains("animado", ImageValidator.Validate(Png(4, 4, extraChunk: "acTL")).Error);
        Assert.Contains("dañado", ImageValidator.Validate(Jpeg[..^2]).Error); // sin EOI
        Assert.Contains("pesa más", ImageValidator.Validate(new byte[ImageValidator.MaxBytes + 1]).Error);
    }

    private static byte[] ExtractIdat(byte[] png)
    {
        var pos = 8;
        while (pos < png.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            if (Encoding.ASCII.GetString(png, pos + 4, 4) == "IDAT") return png.AsSpan(pos + 8, len).ToArray();
            pos += 12 + len;
        }
        throw new InvalidOperationException();
    }

    // ---------------- Plantillas (H3) ----------------

    private static readonly IReadOnlySet<string> Roles = new HashSet<string> { "Comprador", "Almacenista" };

    private static Dictionary<string, EntityDef> Model()
    {
        var proveedor = new EntityDef
        {
            Name = "Proveedor", Label = "Proveedor", LabelPlural = "Proveedores", DisplayField = "RazonSocial",
            Fields = [new() { Name = "RazonSocial", Label = "Razón social", Type = FieldTypes.Text }, new() { Name = "RFC", Label = "RFC", Type = FieldTypes.Text }],
        };
        var oc = new EntityDef
        {
            Name = "OrdenCompra", Label = "Orden de compra", LabelPlural = "Órdenes", DisplayField = "Folio",
            Fields =
            [
                new() { Name = "Folio", Label = "Folio", Type = FieldTypes.Autonumber },
                new() { Name = "Proveedor", Label = "Proveedor", Type = FieldTypes.Reference, ReferenceEntity = "Proveedor" },
                new() { Name = "Total", Label = "Total", Type = FieldTypes.Decimal, Scale = 2 },
                new() { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "OrdenPartida" },
            ],
        };
        var line = new EntityDef
        {
            Name = "OrdenPartida", Label = "Partida", LabelPlural = "Partidas",
            Fields =
            [
                new() { Name = "Orden", Label = "Orden", Type = FieldTypes.Parent, ReferenceEntity = "OrdenCompra" },
                new() { Name = "Cantidad", Label = "Cantidad", Type = FieldTypes.Decimal, Scale = 2 },
            ],
        };
        return new(StringComparer.OrdinalIgnoreCase) { ["Proveedor"] = proveedor, ["OrdenCompra"] = oc, ["OrdenPartida"] = line };
    }

    private static PrintTemplate Valid() => new()
    {
        Name = "Proveedor", Label = "Orden para proveedor", Default = true, Roles = ["Comprador"], When = "Total > 0",
        Page = new PrintPage { Size = "a4", Margin = 12 },
        Blocks =
        [
            new() { Type = PrintBlockTypes.Header, Logo = true, Company = true, Title = "Orden {{ Folio }}" },
            new() { Type = PrintBlockTypes.Fields, PerRow = 2, Items = [new() { Label = "Proveedor", Value = "Proveedor" }, new() { Label = "RFC", Value = "Proveedor.RFC" }] },
            new() { Type = PrintBlockTypes.Table, Lines = "Partidas", Columns = [new() { Label = "Cantidad", Value = "Cantidad", Width = 2 }, new() { Label = "Folio", Value = "Orden.Folio" }] },
            new() { Type = PrintBlockTypes.Totals, Items = [new() { Label = "Total", Value = "Total", Format = PrintFormats.Money, Currency = "'MXN'", Bold = true }] },
            new() { Type = PrintBlockTypes.Image, File = "Sello", Width = 30 },
            new() { Type = PrintBlockTypes.Signatures, Signatures = [new() { Label = "Autorizó" }] },
            new() { Type = PrintBlockTypes.Footer, Text = "Gracias", PageNumbers = true },
        ],
    };

    private static List<string> Errors(PrintTemplate t, EntityDef? e = null) =>
        PrintRules.Validate(e ?? Model()["OrdenCompra"], [t], Model(), Roles).ToList();

    [Fact]
    public void H3_CA2_plantilla_valida_pasa_el_compilador_y_el_plan_la_guarda()
    {
        Assert.Empty(Errors(Valid()));
        var plan = new ChangePlan { Operations = [new SetPrintTemplatesOp { Entity = "OrdenCompra", Templates = [Valid()] }] };
        var r = PlanCompiler.Compile(plan, Model(), Roles);
        Assert.True(r.Preview.Valid, string.Join("; ", r.Preview.Errors.Select(x => x.Message)));
        Assert.Equal("Proveedor", Assert.Single(r.ResultingModel["OrdenCompra"].PrintTemplates!).Name);
    }

    [Fact]
    public void H3_CA2_el_compilador_rechaza_campos_roles_tipos_y_limites()
    {
        Assert.Contains(Errors(Valid() with { Roles = ["Nadie"] }), m => m.Contains("el rol 'Nadie' no existe"));
        Assert.Contains(Errors(Valid() with { When = "NoExiste > 0" }), m => m.Contains("cuándo aplica") && m.Contains("NoExiste"));
        Assert.Contains(Errors(Valid() with { Page = new PrintPage { Size = "oficio" } }), m => m.Contains("tamaño de página"));
        Assert.Contains(Errors(Valid() with { Page = new PrintPage { Margin = 80 } }), m => m.Contains("margen"));
        Assert.Contains(Errors(Valid() with { Blocks = [] }), m => m.Contains("de 1 a 60 bloques"));
        Assert.Contains(Errors(Valid() with { Blocks = [new() { Type = "html", Text = "<b>x</b>" }] }), m => m.Contains("el tipo 'html' no existe"));
        Assert.Contains(Errors(Valid() with { Blocks = [new() { Type = PrintBlockTypes.Text, Text = "Hola {{ Folio" }] }), m => m.Contains("no se cierra"));
        Assert.Contains(Errors(Valid() with { Blocks = [new() { Type = PrintBlockTypes.Text, Text = "{{ Secreto }}" }] }), m => m.Contains("Secreto"));
        // En una tabla el ámbito es la partida: Total no existe ahí (y Orden.Folio sí, por el padre).
        Assert.Contains(Errors(Valid() with { Blocks = [new() { Type = PrintBlockTypes.Table, Lines = "Partidas", Columns = [new() { Label = "X", Value = "Total" }] }] }),
            m => m.Contains("columna 1") && m.Contains("Total"));
        Assert.Contains(Errors(Valid() with { Blocks = [new() { Type = PrintBlockTypes.Table, Lines = "Folio", Columns = [new() { Label = "X", Value = "Folio" }] }] }),
            m => m.Contains("no es un campo de partidas"));
        Assert.Contains(Errors(Valid() with { Blocks = [new() { Type = PrintBlockTypes.Image, File = "../etc/passwd" }] }), m => m.Contains("clave de la imagen"));
        Assert.Contains(Errors(Valid() with { Blocks = [new() { Type = PrintBlockTypes.Footer, PageNumbers = true }, new() { Type = PrintBlockTypes.Footer, PageNumbers = true }] }),
            m => m.Contains("un pie"));
        Assert.Contains(Errors(Valid() with { Blocks = [new() { Type = PrintBlockTypes.Fields, Items = [new() { Label = "T", Value = "Total", Format = "bitcoin" }] }] }),
            m => m.Contains("formato 'bitcoin'"));
        Assert.Contains(Errors(Valid() with { Companies = ["mal clave"] }), m => m.Contains("clave de empresa"));
        Assert.Contains(PrintRules.Validate(Model()["OrdenPartida"], [Valid()], Model(), Roles), m => m.Contains("es una partida"));
    }

    [Fact]
    public void H3_CA2_dos_predeterminadas_para_las_mismas_empresas_se_rechazan()
    {
        var model = Model();
        var a = Valid();
        var b = Valid() with { Name = "Otra" };
        Assert.Contains(PrintRules.Validate(model["OrdenCompra"], [a, b], model, Roles), m => m.Contains("otra plantilla predeterminada"));
        Assert.Empty(PrintRules.Validate(model["OrdenCompra"], [a, b with { Companies = ["DEMO"] }], model, Roles));
    }

    [Fact]
    public void H3_CA2_archivar_un_campo_que_usa_una_plantilla_se_reporta_aunque_el_plan_no_toque_su_catalogo()
    {
        var model = Model();
        model["OrdenCompra"] = model["OrdenCompra"] with { PrintTemplates = [Valid()] };
        var r = PlanCompiler.Compile(new ChangePlan { Operations = [new ArchiveFieldOp { Entity = "Proveedor", Field = "RFC" }] }, model, Roles);
        Assert.False(r.Preview.Valid);
        Assert.Contains(r.Preview.Errors, x => x.Message.Contains("plantilla 'Proveedor'") && x.Message.Contains("RFC"));
    }

    [Fact]
    public void H3_CA1_merge_agrega_o_reemplaza_por_nombre_y_conserva_las_demas()
    {
        var model = Model();
        model["OrdenCompra"] = model["OrdenCompra"] with { PrintTemplates = [Valid(), Valid() with { Name = "Interna", Label = "Interna", Default = false }] };
        var plan = new ChangePlan { Operations = [new SetPrintTemplatesOp { Entity = "OrdenCompra", Merge = true, Templates = [Valid() with { Label = "Nueva etiqueta" }] }] };
        var t = PlanCompiler.Compile(plan, model, Roles).ResultingModel["OrdenCompra"].PrintTemplates!;
        Assert.Equal(["Proveedor", "Interna"], t.Select(x => x.Name));
        Assert.Equal("Nueva etiqueta", t[0].Label);
    }

    [Fact]
    public void H3_CA3_las_plantillas_viajan_en_el_diff_de_entornos_y_tienen_origen()
    {
        var model = Model();
        var desired = new Dictionary<string, EntityDef>(model, StringComparer.OrdinalIgnoreCase)
            { ["OrdenCompra"] = model["OrdenCompra"] with { PrintTemplates = [Valid()] } };
        Assert.Contains(Provenance.Elements(desired["OrdenCompra"]).Keys, k => k == (Provenance.Kinds.PrintTemplate, "Proveedor"));
        // Sin plantillas, la forma canónica del catálogo no cambia (paquetes ya firmados, 009).
        Assert.DoesNotContain("printTemplates", System.Text.Json.JsonSerializer.Serialize(model["OrdenCompra"], MetadataStore.Json));
    }

    [Fact]
    public void TemplateText_separa_literales_y_expresiones()
    {
        var (parts, error) = TemplateText.Parse("Orden {{ Folio }} de {{Proveedor.RazonSocial}}.");
        Assert.Null(error);
        Assert.Equal([(false, "Orden "), (true, "Folio"), (false, " de "), (true, "Proveedor.RazonSocial"), (false, ".")], parts);
        Assert.NotNull(TemplateText.Parse("{{ }}").Error);
    }

    // ---------------- Formato (H4 CA5) ----------------

    [Fact]
    public void H4_CA5_formatos_de_numero_moneda_porcentaje_y_fechas()
    {
        var dec = new FieldDef { Name = "T", Label = "T", Type = FieldTypes.Decimal, Scale = 2 };
        Assert.Equal("1,234.50", PrintFormat.Format(1234.5m, dec, null, null, null));
        Assert.Equal("$1,234.50 USD", PrintFormat.Format(1234.5m, dec, PrintFormats.Money, null, "usd"));
        Assert.Equal("-$10.00", PrintFormat.Format(-10m, dec, PrintFormats.Money, null, null));
        Assert.Equal("16.00 %", PrintFormat.Format(16m, dec, PrintFormats.Percent, null, null));
        Assert.Equal("1,235", PrintFormat.Format(1234.5m, dec, PrintFormats.Integer, null, null));
        var d = new DateTime(2026, 10, 25);
        Assert.Equal("25/10/2026", PrintFormat.Format(d, null, PrintFormats.Date, null, null));
        Assert.Equal("25 de octubre de 2026", PrintFormat.Format(d, null, PrintFormats.DateLong, null, null));
        Assert.Equal("Sí", PrintFormat.Format(true, null, null, null, null));
        Assert.Equal("", PrintFormat.Format(null, dec, PrintFormats.Money, null, "MXN"));
    }

    // ---------------- PDF (motor, ADR-0007) ----------------

    internal static PrintDocument SampleDocument(int lines = 3) => new(
        "Orden de compra OC-000001",
        new PrintPageSpec(false, false, 15),
        [
            new HeaderElement(Png(40, 20), "Comercializadora Ñandú, S.A. de C.V.", ["RFC: CÑA010101AB1", "Av. Peñón 12, Coyoacán"], "Orden de compra", "OC-000001"),
            new FieldsElement(2, [("Proveedor", "Distribuidora Pérez Núñez"), ("Fecha", "25 de octubre de 2026"), ("Moneda", "MXN"), ("Comprador", "José Ángel Muñoz")]),
            new TableElement([new("Producto", 4, "left"), new("Cantidad", 1, "right"), new("Importe", 2, "right")],
                Enumerable.Range(1, lines).Select(i => (IReadOnlyList<string>)[$"Artículo con acentos áéíóú ñ #{i}", "2.00", "$1,234.50"]).ToList(), 0),
            new TotalsElement([("Subtotal", "$2,469.00", false), ("Total", "$2,864.04 MXN", true)]),
            new TextElement("Condiciones:\nEntregar en el almacén «Norte» — gracias.", 9, false, true, "left"),
            new SeparatorElement(),
            new SignaturesElement([("Elaboró", "José Ángel Muñoz"), ("Autorizó", null)]),
        ],
        new PrintFooter("Documento generado por ERP AI", true));

    [Fact]
    public void Pdf_se_genera_con_la_fuente_incluida_acentos_y_numero_de_pagina()
    {
        var pdf = PdfRenderer.Render(SampleDocument());
        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        var text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("Lato", text); // la fuente va incrustada: igual en Windows y Linux
        if (Environment.GetEnvironmentVariable("ERP_PDF_SAMPLE") is { Length: > 0 } path) File.WriteAllBytes(path, pdf);
    }

    [Fact]
    public void Pdf_con_muchas_partidas_pasa_a_varias_hojas()
    {
        var one = PdfRenderer.Render(SampleDocument(3));
        var many = PdfRenderer.Render(SampleDocument(400));
        Assert.True(Pages(many) > Pages(one), $"{Pages(many)} hojas con 400 partidas");
    }

    [Fact]
    public void Pdf_el_texto_del_registro_no_cambia_el_formato()
    {
        // MigraDoc tiene un lenguaje propio (DDL) con \bold{…}; el texto entra como texto: no se interpreta.
        var doc = SampleDocument() with { Body = [new TextElement("\\bold{inyección} \\pagebreak <script>alert(1)</script>\u0007", 9, false, false, "left")] };
        var pdf = PdfRenderer.Render(doc);
        Assert.Equal(1, Pages(pdf));
    }

    // ---------------- Seguridad del documento resuelto (H5 CA2) ----------------

    private sealed class FakeData(Dictionary<(string, long), Erp.Api.Data.Values> rows, HashSet<(string, long)> hidden) : Erp.Api.Data.IPrintData
    {
        public bool Visible(string entity, long id) => !hidden.Contains((entity, id));
        public Erp.Api.Data.Values? Row(string entity, long id) => rows.GetValueOrDefault((entity, id));
        public Erp.Api.Data.Values? User(int id) => new() { ["Nombre"] = "Ana Peña", ["Usuario"] = "ana" };
    }

    private static Dictionary<string, EntityDef> SecuredModel()
    {
        var m = Model();
        var buyers = new[] { "Comprador" };
        m["OrdenCompra"] = m["OrdenCompra"] with
        {
            Fields = [.. m["OrdenCompra"].Fields, new() { Name = "Comprador", Label = "Comprador", Type = FieldTypes.User },
                new() { Name = "Estado", Label = "Estado", Type = FieldTypes.State }],
            Workflow = new Workflow { Initial = "Borrador", States = [new() { Name = "Borrador", Label = "Borrador" }, new() { Name = "Autorizada", Label = "Autorizada" }] },
            Security = new SecurityPolicy { Fields = [new() { Field = "Total", Read = buyers }] },
        };
        m["OrdenPartida"] = m["OrdenPartida"] with
        {
            Fields = [.. m["OrdenPartida"].Fields, new() { Name = "Precio", Label = "Precio", Type = FieldTypes.Decimal, Scale = 2 }],
            Security = new SecurityPolicy { Fields = [new() { Field = "Precio", Read = buyers }] },
        };
        // El RFC del proveedor solo lo ve compras (oculto en un salto de la ruta).
        m["Proveedor"] = m["Proveedor"] with { Security = new SecurityPolicy { Fields = [new() { Field = "RFC", Read = buyers }] } };
        return m;
    }

    private static Erp.Api.Security.UserContext UserWith(string role) => new()
    {
        Id = 5, Username = role.ToLowerInvariant(), DisplayName = role, SecurityStamp = Guid.Empty, Roles = [role],
        Entities = new Dictionary<string, Erp.Api.Security.EntityAccess> { ["OrdenCompra"] = new(true, false, false, false), ["Proveedor"] = new(true, false, false, false) },
    };

    private static PrintDocument Resolve(string role, long proveedor = 7, PrintTemplate? template = null)
    {
        var model = SecuredModel();
        var user = UserWith(role);
        var rows = new Dictionary<(string, long), Erp.Api.Data.Values>
        {
            [("Proveedor", 7)] = new() { ["Id"] = 7L, ["RazonSocial"] = "Ferretería Núñez", ["RFC"] = "FNU010101AA1" },
            [("Proveedor", 8)] = new() { ["Id"] = 8L, ["RazonSocial"] = "Otra empresa", ["RFC"] = "OTR010101AA1" },
        };
        var draft = new Erp.Api.Data.DocDraft
        {
            Id = 1,
            Header = new() { ["Id"] = 1L, ["Folio"] = "OC-000042", ["Proveedor"] = proveedor, ["Total"] = 1160m, ["Comprador"] = 3, ["Estado"] = "Autorizada" },
        };
        draft.Lines["Partidas"] = [new() { Id = 10, Values = new() { ["Id"] = 10L, ["Orden"] = 1L, ["Cantidad"] = 2m, ["Precio"] = 500m } }];
        var ctx = new Erp.Api.Data.PrintContext(new FakeData(rows, [("Proveedor", 8)]), user, model);
        var e = model["OrdenCompra"];
        var scope = Erp.Api.Data.RecordService.PrintHeaderScope(draft, e, ctx);
        var env = new Erp.Api.Expressions.ExprEnv(user.Id, user.Roles, null) { BaseCurrency = "MXN" };
        template ??= new PrintTemplate
        {
            Name = "T", Label = "Orden",
            Blocks =
            [
                new() { Type = PrintBlockTypes.Header, Title = "Orden {{ Folio }} · {{ Estado }}", Subtitle = "Total {{ Total }}" },
                new() { Type = PrintBlockTypes.Fields, Items =
                [
                    new() { Label = "Proveedor", Value = "Proveedor" }, new() { Label = "RFC", Value = "Proveedor.RFC" },
                    new() { Label = "Total", Value = "Total", Format = PrintFormats.Money }, new() { Label = "Comprador", Value = "Comprador" },
                ] },
                new() { Type = PrintBlockTypes.Table, Lines = "Partidas", Columns = [new() { Label = "Cantidad", Value = "Cantidad" }, new() { Label = "Precio", Value = "Precio" }, new() { Label = "Folio", Value = "Orden.Folio" }] },
                new() { Type = PrintBlockTypes.Text, VisibleWhen = "Total > 1000", Text = "Requiere segunda firma" },
            ],
        };
        return Erp.Api.Data.RecordService.ResolvePrint(e, template, draft, scope, env, ctx,
            new Erp.Api.Data.RecordService.PrintCompany("DEMO", "Demo", "MXN", null, null, null), new Dictionary<string, byte[]>());
    }

    [Fact]
    public void H5_CA2_quien_ve_todo_recibe_los_valores_con_su_etiqueta_y_formato()
    {
        var d = Resolve("Comprador");
        var header = Assert.IsType<HeaderElement>(d.Body[0]);
        Assert.Equal("Orden OC-000042 · Autorizada", header.Title); // el estado con su etiqueta
        Assert.Equal("Total 1,160.00", header.Subtitle);
        var fields = Assert.IsType<FieldsElement>(d.Body[1]).Items;
        Assert.Equal([("Proveedor", "Ferretería Núñez"), ("RFC", "FNU010101AA1"), ("Total", "$1,160.00 MXN"), ("Comprador", "Ana Peña")], fields);
        var table = Assert.IsType<TableElement>(d.Body[2]);
        Assert.Equal(["Cantidad", "Precio", "Folio"], table.Columns.Select(c => c.Label));
        Assert.Equal(["2.00", "500.00", "OC-000042"], table.Rows[0]);
        Assert.Contains(d.Body, x => x is TextElement { Text: "Requiere segunda firma" });
    }

    [Fact]
    public void H5_CA2_un_campo_oculto_para_el_rol_no_se_filtra_al_pdf()
    {
        var d = Resolve("Almacenista");
        Assert.Equal("Total (sin acceso)", Assert.IsType<HeaderElement>(d.Body[0]).Subtitle);
        var fields = Assert.IsType<FieldsElement>(d.Body[1]).Items;
        Assert.Equal("(sin acceso)", fields.Single(f => f.Label == "Total").Value);
        // Oculto en el segundo salto de la ruta (Proveedor.RFC): también.
        Assert.Equal("(sin acceso)", fields.Single(f => f.Label == "RFC").Value);
        Assert.Equal("Ferretería Núñez", fields.Single(f => f.Label == "Proveedor").Value);
        // Decisión 4: la columna con un campo oculto no se imprime; la condición que lee uno oculta el bloque.
        Assert.Equal(["Cantidad", "Folio"], Assert.IsType<TableElement>(d.Body[2]).Columns.Select(c => c.Label));
        Assert.DoesNotContain(d.Body, x => x is TextElement);
        Assert.DoesNotContain(d.Body.OfType<TableElement>().SelectMany(t => t.Rows).SelectMany(r => r), v => v.Contains("500"));
    }

    [Fact]
    public void H5_CA2_una_ruta_a_un_registro_fuera_de_alcance_imprime_sin_acceso()
    {
        var fields = Assert.IsType<FieldsElement>(Resolve("Comprador", proveedor: 8).Body[1]).Items;
        Assert.Equal("(sin acceso)", fields.Single(f => f.Label == "Proveedor").Value);
        Assert.Equal("(sin acceso)", fields.Single(f => f.Label == "RFC").Value);
        Assert.DoesNotContain(fields, f => f.Value.Contains("Otra empresa") || f.Value.Contains("OTR010101"));
    }

    [Fact]
    public void Un_valor_vacio_imprime_una_raya()
    {
        // Regresión (revisión exploratoria 027, F-01): «Requisición» vacía dejaba la etiqueta sin valor en la OC interna.
        var t = new PrintTemplate { Name = "T", Label = "T", Blocks = [new() { Type = PrintBlockTypes.Fields, Items = [new() { Label = "Notas", Value = "Notas" }] },
            new() { Type = PrintBlockTypes.Totals, Items = [new() { Label = "Nada", Value = "NULL" }] }] };
        var d = Resolve("Comprador", template: t);
        Assert.Equal("—", Assert.IsType<FieldsElement>(d.Body[0]).Items[0].Value);
        Assert.Equal("—", Assert.IsType<TotalsElement>(d.Body[1]).Rows[0].Value);
    }

    [Fact]
    public void H5_CA2_una_formula_no_esquiva_la_seguridad_por_campo()
    {
        // Leer el campo oculto dentro de una función o de un texto no lo revela.
        var t = new PrintTemplate
        {
            Name = "T", Label = "T",
            Blocks = [new() { Type = PrintBlockTypes.Fields, Items = [new() { Label = "X", Value = "ROUND(Total * 1, 0)" }, new() { Label = "Y", Value = "IF(Total > 0, 'caro', 'barato')" }] }],
        };
        var fields = Assert.IsType<FieldsElement>(Resolve("Almacenista", template: t).Body[0]).Items;
        Assert.All(fields, f => Assert.Equal("(sin acceso)", f.Value));
    }

    internal static int Pages(byte[] pdf) =>
        System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page[^s]").Count;
}
