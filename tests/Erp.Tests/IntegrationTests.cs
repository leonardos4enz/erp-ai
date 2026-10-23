using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Integrations;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>Spec 008: webhooks (SSRF, firma, validación), claves de API, importación y exportación.</summary>
public class IntegrationTests
{
    private static readonly IReadOnlySet<string> Roles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Vendedor", "Gerente", "Integracion" };

    // ---------------- SSRF ----------------

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")] // metadatos de la nube
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::127.0.0.1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    public void Direcciones_internas_se_bloquean(string ip) =>
        Assert.True(new WebhookUrlPolicy().IsBlocked(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Direcciones_publicas_se_permiten(string ip) =>
        Assert.False(new WebhookUrlPolicy().IsBlocked(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("http://ejemplo.com/hook", "https")]
    [InlineData("ftp://ejemplo.com/hook", "https")]
    [InlineData("https://usuario:clave@ejemplo.com/hook", "usuario")]
    [InlineData("https://ejemplo.com/hook#x", "fragmento")]
    [InlineData("https://127.0.0.1/hook", "interna")]
    [InlineData("https://[::1]/hook", "interna")]
    [InlineData("https://169.254.169.254/latest/meta-data", "interna")]
    [InlineData("https://localhost/hook", "interna")]
    [InlineData("https://api.localhost/hook", "interna")]
    [InlineData("no es url", "válida")]
    [InlineData("", "URL")]
    public void Urls_no_permitidas(string url, string motivo) =>
        Assert.Contains(motivo, new WebhookUrlPolicy().Check(url) ?? "(aceptada)");

    [Fact]
    public void Url_https_publica_se_acepta_y_se_enmascara()
    {
        Assert.Null(new WebhookUrlPolicy().Check("https://hooks.ejemplo.com/servicio/T000/B000/token-secreto"));
        // La ruta puede llevar un token: nunca se muestra ni se audita.
        Assert.Equal("https://hooks.ejemplo.com/…", WebhookUrlPolicy.Mask("https://hooks.ejemplo.com/servicio/T000/B000/token-secreto"));
        Assert.Equal("https://hooks.ejemplo.com:8443", WebhookUrlPolicy.Mask("https://hooks.ejemplo.com:8443/"));
    }

    [Fact]
    public void Http_y_una_ip_interna_solo_con_configuracion_explicita()
    {
        var e2e = new WebhookUrlPolicy(allowHttp: true, allowedInternal: [IPAddress.Loopback]);
        Assert.Null(e2e.Check("http://127.0.0.1:5099/hook"));
        Assert.False(e2e.IsBlocked(IPAddress.Loopback));
        Assert.False(e2e.IsBlocked(IPAddress.Parse("::ffff:127.0.0.1")));
        // Solo esa dirección: las demás internas y «localhost» siguen bloqueadas.
        Assert.NotNull(e2e.Check("http://169.254.169.254/latest/meta-data"));
        Assert.NotNull(e2e.Check("http://localhost:5099/hook"));
        Assert.True(e2e.IsBlocked(IPAddress.Parse("10.0.0.1")));
        Assert.NotNull(new WebhookUrlPolicy(allowHttp: true).Check("http://127.0.0.1:5099/hook"));
    }

    [Fact]
    public async Task El_envio_revisa_la_ip_al_conectar_aunque_la_url_parezca_publica()
    {
        // «localtest.me» resuelve a 127.0.0.1: la URL pasa la revisión de texto, pero la conexión se bloquea (DNS rebinding).
        using var sender = new WebhookSender(new WebhookUrlPolicy());
        IPAddress[] resolved;
        try { resolved = await Dns.GetHostAddressesAsync("localtest.me"); }
        catch (System.Net.Sockets.SocketException) { return; } // sin DNS en el entorno de pruebas
        if (!resolved.All(WebhookUrlPolicy.IsInternal)) return;
        var r = await sender.SendAsync("https://localtest.me/hook", "s", Guid.NewGuid(), "ping", "{}", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("interna", r.Error);
    }

    // ---------------- Firma ----------------

    [Fact]
    public void Firma_hmac_sobre_marca_de_tiempo_y_cuerpo()
    {
        const string secret = "whsec_prueba", body = "{\"id\":1}";
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("1700000000." + body)));
        Assert.Equal(expected, WebhookSignature.Sign(secret, 1700000000, body));

        var now = DateTimeOffset.FromUnixTimeSeconds(1700000100);
        var tolerance = TimeSpan.FromMinutes(5);
        Assert.True(WebhookSignature.Verify(secret, "1700000000", body, expected, now, tolerance));
        Assert.False(WebhookSignature.Verify(secret, "1700000000", body + " ", expected, now, tolerance));          // cuerpo alterado
        Assert.False(WebhookSignature.Verify("otro", "1700000000", body, expected, now, tolerance));               // otro secreto
        Assert.False(WebhookSignature.Verify(secret, "1700000000", body, expected, now.AddMinutes(10), tolerance)); // reenvío tardío
        Assert.False(WebhookSignature.Verify(secret, "1700000001", body, expected, now, tolerance));               // marca cambiada
    }

    [Fact]
    public void Secretos_y_claves_son_aleatorios_y_largos()
    {
        Assert.NotEqual(WebhookSignature.NewSecret(), WebhookSignature.NewSecret());
        Assert.True(WebhookSignature.NewSecret().Length >= 40);
        var (id, key) = ApiKeyFormat.New();
        Assert.Equal(id, ApiKeyFormat.PublicIdOf(key));
        Assert.StartsWith("erp_" + id + "_", key);
        Assert.True(ApiKeyFormat.Matches(key, ApiKeyFormat.Hash(key)));
        Assert.False(ApiKeyFormat.Matches(key + "x", ApiKeyFormat.Hash(key)));
        Assert.False(ApiKeyFormat.Matches(key, null));
        Assert.NotEqual(key, Convert.ToHexString(ApiKeyFormat.Hash(key))); // se guarda el hash, no la clave
    }

    [Theory]
    [InlineData("erp_0123456789ab_corta")]
    [InlineData("erp_0123456789AB_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("erp_0123456789ab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'--")]
    [InlineData("xyz_0123456789ab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("")]
    public void Clave_con_formato_invalido_no_se_busca(string key) => Assert.Null(ApiKeyFormat.PublicIdOf(key));

    [Fact]
    public void Carga_del_webhook_es_json_con_id_de_entrega()
    {
        var id = Guid.NewGuid();
        var body = WebhookPayload.Serialize(id, "Alta", "Venta", "create", null, null, 5, new DateTime(2026, 10, 23, 18, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, object?> { ["Folio"] = "V-1" });
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(id.ToString(), doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("create", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("V-1", doc.RootElement.GetProperty("data").GetProperty("Folio").GetString());
        Assert.Equal("2026-10-23T18:00:00Z", doc.RootElement.GetProperty("occurredAt").GetString());
    }

    // ---------------- Validación de webhooks ----------------

    private static List<Operation> Base() =>
    [
        new CreateEntityOp { Name = "Venta", Label = "Venta", LabelPlural = "Ventas", Fields =
        [
            new FieldDef { Name = "Folio", Label = "Folio", Type = FieldTypes.Autonumber },
            new FieldDef { Name = "Monto", Label = "Monto", Type = FieldTypes.Decimal },
            new FieldDef { Name = "Margen", Label = "Margen", Type = FieldTypes.Decimal },
            new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "VentaPartida" },
        ] },
        new CreateEntityOp { Name = "VentaPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
        [
            new FieldDef { Name = "Venta", Label = "Venta", Type = FieldTypes.Parent, ReferenceEntity = "Venta" },
            new FieldDef { Name = "Cantidad", Label = "Cantidad", Type = FieldTypes.Decimal },
        ] },
        new SetWorkflowOp { Entity = "Venta", Workflow = new Workflow
        {
            Initial = "Borrador",
            States = [new WorkflowState { Name = "Borrador", Label = "Borrador" }, new WorkflowState { Name = "Cerrada", Label = "Cerrada" }],
            Transitions = [new WorkflowTransition { Name = "Cerrar", Label = "Cerrar", From = ["Borrador"], To = "Cerrada" }],
        } },
        new SetSecurityOp { Entity = "Venta", Security = new SecurityPolicy { Fields = [new FieldSecurity { Field = "Margen", Read = ["Gerente"] }] } },
    ];

    private static CompiledPlan Compile(params Operation[] extra) =>
        PlanCompiler.Compile(new ChangePlan { Operations = [.. Base(), .. extra] }, new Dictionary<string, EntityDef>(), Roles);

    private static string Errors(CompiledPlan c) => string.Join(" | ", c.Preview.Errors.Select(e => e.Message));

    private static WebhookDef Hook(string on = "create", string? state = null, string? transition = null, string role = "Integracion",
        IReadOnlyList<string>? fields = null, string? when = null, string name = "Aviso") =>
        new() { Name = name, Label = "Aviso", Role = role, Fields = fields, When = when, Events = [new WebhookEvent { On = on, State = state, Transition = transition }] };

    [Fact]
    public void Webhook_valido_se_guarda_en_el_catalogo()
    {
        var c = Compile(new SetWebhooksOp { Entity = "Venta", Webhooks =
            [Hook(), Hook("enterState", state: "Cerrada", name: "Cerrada"), Hook("transition", transition: "Cerrar", name: "AlCerrar", fields: ["Folio", "Monto", "Partidas"], when: "Monto > 0")] });
        Assert.True(c.Preview.Valid, Errors(c));
        Assert.Equal(3, c.ResultingModel["Venta"].Webhooks!.Count);
        Assert.Contains("Integraciones", c.Preview.Steps.Last().Description); // la vista previa aclara dónde va el destino
    }

    [Theory]
    [InlineData("enterState", "NoExiste", null, "estado")]
    [InlineData("transition", null, "NoExiste", "transición")]
    [InlineData("create", "Cerrada", null, "no admite")]
    [InlineData("borrar", null, null, "no existe")]
    public void Eventos_invalidos_se_rechazan(string on, string? state, string? transition, string message)
    {
        var c = Compile(new SetWebhooksOp { Entity = "Venta", Webhooks = [Hook(on, state, transition)] });
        Assert.False(c.Preview.Valid);
        Assert.Contains(message, Errors(c));
    }

    [Fact]
    public void Rol_inexistente_campo_inexistente_y_condicion_invalida_se_rechazan()
    {
        Assert.Contains("rol 'Nadie' no existe", Errors(Compile(new SetWebhooksOp { Entity = "Venta", Webhooks = [Hook(role: "Nadie")] })));
        Assert.Contains("'Precio' no es un campo", Errors(Compile(new SetWebhooksOp { Entity = "Venta", Webhooks = [Hook(fields: ["Precio"])] })));
        Assert.Contains("when", Errors(Compile(new SetWebhooksOp { Entity = "Venta", Webhooks = [Hook(when: "NoExiste > 1")] })));
        Assert.Contains("repetido", Errors(Compile(new SetWebhooksOp { Entity = "Venta", Webhooks = [Hook(), Hook()] })));
        Assert.Contains("partida", Errors(Compile(new SetWebhooksOp { Entity = "VentaPartida", Webhooks = [Hook()] })));
    }

    [Fact]
    public void Un_webhook_no_puede_pedir_un_campo_que_su_rol_no_ve()
    {
        // Margen solo lo ve «Gerente» (006): pedirlo para «Integracion» sería una fuga.
        var c = Compile(new SetWebhooksOp { Entity = "Venta", Webhooks = [Hook(fields: ["Folio", "Margen"])] });
        Assert.Contains("no puede ver 'Margen'", Errors(c));
        Assert.True(Compile(new SetWebhooksOp { Entity = "Venta", Webhooks = [Hook(role: "Gerente", fields: ["Margen"])] }).Preview.Valid);
    }

    [Fact]
    public void Archivar_un_campo_o_cambiar_la_seguridad_no_rompe_el_webhook_en_silencio()
    {
        var hooks = new SetWebhooksOp { Entity = "Venta", Webhooks = [Hook(fields: ["Monto"])] };
        Assert.Contains("'Monto' no es un campo", Errors(Compile(hooks, new ArchiveFieldOp { Entity = "Venta", Field = "Monto" })));
        var hide = new SetSecurityOp { Entity = "Venta", Security = new SecurityPolicy { Fields = [new FieldSecurity { Field = "Monto", Read = ["Gerente"] }] } };
        Assert.Contains("no puede ver 'Monto'", Errors(Compile(hooks, hide)));
    }

    [Fact]
    public void El_evento_coincide_por_estado_o_transicion()
    {
        var h = new WebhookDef { Name = "A", Label = "A", Role = "R", Events = [new WebhookEvent { On = "enterState", State = "Cerrada" }, new WebhookEvent { On = "update" }] };
        Assert.True(WebhookRules.Matches(h, "enterState", "cerrada"));
        Assert.False(WebhookRules.Matches(h, "enterState", "Borrador"));
        Assert.True(WebhookRules.Matches(h, "update", null));
        Assert.False(WebhookRules.Matches(h, "create", null));
    }

    [Fact]
    public void Compras_trae_su_webhook_sin_destino_ni_secreto()
    {
        var raw = File.ReadAllText(Path.Combine(ModulesFolder(), "compras.json"));
        var p = JsonSerializer.Deserialize<ModulePackage>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var upgrade = p.Upgrades!.Single(u => u.Version == "1.5.0");
        Assert.Contains(upgrade.Roles!, r => r.Name == "Integración de almacén" && r.Permissions.All(x => !x.CanCreate && !x.CanUpdate && !x.CanDelete));
        Assert.Contains(p.AllRoles, r => r.Name == "Integración de almacén");
        // Nada de URLs ni secretos en un paquete que vive en un repositorio público.
        Assert.DoesNotContain("\"url\"", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("whsec_", raw);
    }

    private static string ModulesFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "modules", "compras.json"))) return Path.Combine(d.FullName, "modules");
        throw new DirectoryNotFoundException("modules");
    }

    // ---------------- Lectores de importación ----------------

    [Fact]
    public void Csv_con_comillas_saltos_de_linea_bom_y_punto_y_coma()
    {
        var t = ImportReader.Read([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("Nombre;Notas;Monto\r\n\"Pérez; Juan\";\"línea 1\nlínea \"\"2\"\"\";12.5\r\n;;\r\nAna;;3\r\n")]);
        Assert.Equal("csv", t.Format);
        Assert.Equal(["Nombre", "Notas", "Monto"], t.Headers);
        Assert.Equal(2, t.Rows.Count); // la fila vacía se omite
        Assert.Equal("Pérez; Juan", t.Rows[0].Cells[0]);
        Assert.Equal("línea 1\nlínea \"2\"", t.Rows[0].Cells[1]);
        Assert.Equal(2, t.Rows[0].Row);
        Assert.Equal(5, t.Rows[1].Row); // número de fila como en la hoja (la celda multilínea ocupa dos)
    }

    [Fact]
    public void Csv_latin1_de_excel_se_lee_con_acentos()
    {
        var t = ImportReader.Read(Encoding.Latin1.GetBytes("Descripción\nCañón\n"));
        Assert.Equal("Descripción", t.Headers[0]);
        Assert.Equal("Cañón", t.Rows[0].Cells[0]);
    }

    [Fact]
    public void Csv_invalido_o_demasiado_grande_se_rechaza()
    {
        Assert.Throws<AppException>(() => ImportReader.Read(Encoding.UTF8.GetBytes("A\n\"sin cerrar\n")));
        Assert.Throws<AppException>(() => ImportReader.Read(Encoding.UTF8.GetBytes("A\n" + string.Concat(Enumerable.Repeat("x\n", ImportReader.MaxRows + 1)))));
        Assert.Throws<AppException>(() => ImportReader.Read(new byte[ImportReader.MaxBytes + 1]));
        Assert.Throws<AppException>(() => ImportReader.Read([]));
    }

    [Fact]
    public void Xlsx_exportado_por_el_erp_se_vuelve_a_leer()
    {
        var r = new ReportResult("Venta", "listado", "Ventas", null, null,
            [new ReportColumn("Nombre", "Nombre", "field", "text"), new ReportColumn("Monto", "Monto", "field", "number")],
            [new() { ["Nombre"] = "Ñandú & <Cía>", ["Monto"] = 12.5m }, new() { ["Nombre"] = "=1+1", ["Monto"] = null }], [], false);
        var t = ImportReader.Read(ReportExport.Xlsx(r));
        Assert.Equal("xlsx", t.Format);
        Assert.Equal(["Nombre", "Monto"], t.Headers);
        Assert.Equal("Ñandú & <Cía>", t.Rows[0].Cells[0]);
        Assert.Equal("12.5", t.Rows[0].Cells[1]);
        Assert.Equal("=1+1", t.Rows[1].Cells[0]); // texto, no fórmula
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(content);
        return ms.ToArray();
    }

    [Fact]
    public void Xlsx_con_dtd_no_se_procesa()
    {
        // XXE: una entidad externa intentaría leer un archivo del servidor.
        var xxe = Zip(("xl/worksheets/sheet1.xml", """
            <?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///c:/windows/win.ini">]>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>&e;</t></is></c></row></sheetData></worksheet>
            """));
        var ex = Assert.Throws<AppException>(() => ImportReader.Read(xxe));
        Assert.Contains("Excel", ex.Message);
    }

    [Fact]
    public void Xlsx_con_demasiadas_entradas_o_corrupto_se_rechaza()
    {
        Assert.Throws<AppException>(() => ImportReader.Read(Zip(Enumerable.Range(0, ImportReader.MaxZipEntries + 1).Select(i => ($"x{i}.xml", "<a/>")).ToArray())));
        Assert.Throws<AppException>(() => ImportReader.Read([0x50, 0x4B, 0x03, 0x04, 1, 2, 3]));
        Assert.Throws<AppException>(() => ImportReader.Read(Zip(("xl/otra.xml", "<a/>"))));
    }

    // ---------------- Conversión de celdas y columnas ----------------

    private static FieldDef F(string type, params string[] options) =>
        new() { Name = "X", Label = "Campo", Type = type, Options = options.Length > 0 ? options : null, Scale = 2 };

    [Theory]
    [InlineData("sí", true)]
    [InlineData("SI", true)]
    [InlineData("x", true)]
    [InlineData("1", true)]
    [InlineData("No", false)]
    [InlineData("falso", false)]
    public void Si_no_en_espanol(string raw, bool expected) =>
        Assert.Equal(expected, RecordService.CellValue(F(FieldTypes.Boolean), raw).Node!.GetValue<bool>());

    [Theory]
    [InlineData("2026-10-23", "2026-10-23")]
    [InlineData("23/10/2026", "2026-10-23")]
    [InlineData("46318", "2026-10-23")] // fecha de Excel
    public void Fechas_en_varios_formatos(string raw, string expected) =>
        Assert.Equal(expected, RecordService.CellValue(F(FieldTypes.Date), raw).Node!.GetValue<string>());

    [Theory]
    [InlineData("1,234.50", "1234.50")]
    [InlineData("$ 99.9", "99.9")]
    [InlineData("-3", "-3")]
    public void Numeros_formato_mexicano(string raw, string expected) =>
        Assert.Equal(expected, RecordService.CellValue(F(FieldTypes.Decimal), raw).Node!.GetValue<string>());

    [Fact]
    public void Valores_invalidos_dan_mensaje_por_celda()
    {
        Assert.Contains("entero", RecordService.CellValue(F(FieldTypes.Integer), "2.5").Error);
        Assert.Contains("fecha", RecordService.CellValue(F(FieldTypes.Date), "mañana").Error);
        Assert.Contains("opción", RecordService.CellValue(F(FieldTypes.Choice, "Alta", "Baja"), "Media").Error);
        Assert.Equal("Alta", RecordService.CellValue(F(FieldTypes.Choice, "Alta", "Baja"), "alta").Node!.GetValue<string>());
        Assert.NotNull(RecordService.CellValue(F(FieldTypes.Boolean), "quizá").Error);
    }

    [Fact]
    public void El_apostrofo_de_la_exportacion_se_quita_al_importar()
    {
        var exported = ReportExport.CsvCell("=1+1");
        Assert.Equal("'=1+1", exported);
        Assert.Equal("=1+1", RecordService.CellValue(F(FieldTypes.Text), exported).Node!.GetValue<string>());
        Assert.Equal("@SUM(A1)", RecordService.CellValue(F(FieldTypes.Text), ReportExport.CsvCell("@SUM(A1)")).Node!.GetValue<string>());
        // Un apóstrofo normal se conserva.
        Assert.Equal("'hola", RecordService.CellValue(F(FieldTypes.Text), "'hola").Node!.GetValue<string>());
    }

    [Fact]
    public void Columnas_por_nombre_o_etiqueta_con_seguridad()
    {
        var c = Compile();
        var e = c.ResultingModel["Venta"];
        var user = new UserContext { Id = 1, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = ["Vendedor"] };
        var (cols, map) = RecordService.MapColumns(e, ["MONTO", "Folio", "Margen", "Desconocida", "Partidas", "monto", ""],
            f => FieldAccess.CanSee(e, f, user), f => FieldAccess.CanEdit(e, f, user, "Borrador"), [(2, ["1", "", "", "", "", "", ""])]);
        Assert.Equal("ok", cols[0].Status);                       // por nombre, sin importar mayúsculas
        Assert.Equal("ignored", cols[1].Status);                  // folio: lo asigna el sistema
        Assert.Equal("error", cols[2].Status);                    // oculto para el rol: como si no existiera
        Assert.Equal(cols[3].Message, cols[2].Message);           // mismo mensaje: no revela que existe
        Assert.Equal("error", cols[4].Status);                    // partidas
        Assert.Contains("repetida", cols[5].Message);
        Assert.Equal("ignored", cols[6].Status);                  // sin encabezado ni datos
        Assert.Equal(["Monto"], map.Values.Select(f => f.Name));
    }

    [Fact]
    public void Etiquetas_con_acentos_se_reconocen() =>
        Assert.Equal(RecordService.Normalize("Fecha de  entrega"), RecordService.Normalize(" FECHA DE ENTREGA "));
}
