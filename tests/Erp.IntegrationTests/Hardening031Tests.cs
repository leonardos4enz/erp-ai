using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erp.IntegrationTests.Harness;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 031 · Endurecimiento 4 (auditoría externa 5 sobre la 029). Lo que se le dice a una persona se evalúa con su alcance
/// (A-01, A-04, A-05), los costos no salen por una credencial de terceros (A-03), quien aprueba no gana al mover a alguien a
/// su unidad (A-02) y las rutas reales son las del inventario. Las de H1–H5 fallan con el código anterior (<c>7fce1d6</c>):
/// <c>npm --prefix web run it:regresion -- 7fce1d6 Hardening031Tests</c>; la de H6 es una guardia (pasa también con él).
/// </summary>
public class Hardening031Tests(Hardening031Tests.Host host) : IClassFixture<Hardening031Tests.Host>
{
    private const string Pwd = "frase larga de endurecimiento 031";
    private const string Group = "Pruebas 031";
    private const string Role = "IT31 Lector";
    private const string Lector = "it31-lector";
    private const string Aviso = "Aviso 031: el dato secreto está vacío.";
    private const string AvisoOculto = "Aviso 031: el campo oculto tiene valor.";
    private const decimal Capa = 1234.5m; // 10 piezas: costo unitario 123.45

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Ids = [];
        /// <summary>Instancia con aprobación de seguridad (como Hardening021Tests).</summary>
        public Factory Sec = null!;

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            var role = await admin.PostAsync("/api/admin/roles", new { name = Role, description = "Spec 031", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = Array.Empty<object>() });
            Assert.True(role.Status == 200, role.Text);
            Ids["rol"] = role.Body!.Id();
            await admin.ApplyPlanAsync(new { description = "Spec 031 · avisos e impresión con el alcance de quien los recibe", operations = new object[] {
                new { op = "createEntity", group = Group, name = "It31Secreto", label = "Secreto 031", labelPlural = "Secretos 031", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true },
                    new { name = "Dato", label = "Dato", type = "text" } } },
                // Catálogo de saldo de UNITCOST con alcance por registro (029 F-C).
                new { op = "createEntity", group = Group, name = "It31Saldo", label = "Saldo 031", labelPlural = "Saldos 031", displayField = "Clave", fields = new object[] {
                    new { name = "Clave", label = "Clave", type = "text", required = true } } },
                new { op = "createEntity", group = Group, name = "It31Puente", label = "Puente 031", labelPlural = "Puentes 031", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true },
                    new { name = "Secreto", label = "Secreto", type = "reference", referenceEntity = "It31Secreto" } } },
                new { op = "createEntity", group = Group, name = "It31Doc", label = "Documento 031", labelPlural = "Documentos 031", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true },
                    new { name = "Puente", label = "Puente", type = "reference", referenceEntity = "It31Puente" },
                    new { name = "Oculto", label = "Oculto", type = "text" },
                    new { name = "Clave", label = "Clave", type = "text" } } },
                new { op = "setSecurity", entity = "It31Secreto", security = new { records = new[] { new { roles = new[] { Role }, scope = "own" } } } },
                new { op = "setSecurity", entity = "It31Saldo", security = new { records = new[] { new { roles = new[] { Role }, scope = "own" } } } },
                new { op = "setSecurity", entity = "It31Doc", security = new { fields = new[] { new { field = "Oculto", read = new[] { "Administrador" } } } } },
                new { op = "setRules", entity = "It31Doc", rules = new object[] {
                    new { name = "SecretoVacio", field = "Nombre", severity = "warning", assert = "NOT ISBLANK(Puente.Secreto.Dato)", message = Aviso },
                    new { name = "OcultoConValor", field = "Nombre", severity = "warning", assert = "ISBLANK(Oculto)", message = AvisoOculto } } },
                new { op = "setWorkflow", entity = "It31Doc", workflow = new {
                    initial = "Abierto",
                    states = new object[] { new { name = "Abierto", label = "Abierto" }, new { name = "Revisado", label = "Revisado" }, new { name = "Bloqueado", label = "Bloqueado" } },
                    transitions = new object[] {
                        new { name = "revisar", label = "Revisar", from = new[] { "Abierto" }, to = "Revisado", onWarning = "confirm" },
                        new { name = "bloquear", label = "Bloquear", from = new[] { "Abierto" }, to = "Bloqueado", onWarning = "block" } } } },
                // A-04: una automatización síncrona ejecuta la transición con «block» cuando la persona guarda.
                new { op = "setAutomations", entity = "It31Doc", automations = new[] { new { name = "Bloquear", label = "Bloquear al pedirlo", on = "update",
                    when = "Nombre = 'Bloquear'", actions = new[] { new { type = "transition", transition = "bloquear" } } } } },
                new { op = "setPrintTemplates", entity = "It31Doc", templates = new[] { new { name = "Costo", label = "Costo", @default = true, blocks = new object[] {
                    new { type = "text", text = "Documento {{ Nombre }}" },
                    new { type = "text", text = "Costo: {{ UNITCOST('It31Saldo', 1, Clave) }} fin" } } } } },
            } });
            var perms = new object[] {
                new { entity = "It31Doc", canRead = true, canCreate = true, canUpdate = true, canDelete = false },
                new { entity = "It31Puente", canRead = true, canCreate = false, canUpdate = false, canDelete = false },
                new { entity = "It31Secreto", canRead = true, canCreate = false, canUpdate = false, canDelete = false },
                new { entity = "It31Saldo", canRead = true, canCreate = false, canUpdate = false, canDelete = false },
            };
            Assert.Equal(200, (await admin.PutAsync($"/api/admin/roles/{Ids["rol"]}", new { name = Role, description = "Spec 031", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = perms })).Status);
            var u = await admin.PostAsync("/api/admin/users", new { username = Lector, displayName = "Lector 031", password = Pwd, roleIds = new[] { Ids["rol"] } });
            Assert.True(u.Status == 201, u.Text);

            // El secreto lo crea el administrador: el lector (alcance «propios») no lo ve, pero sí el puente que lo apunta.
            Ids["secreto"] = (await admin.CreateAsync("It31Secreto", new { Nombre = "Secreto", Dato = "valor que el lector no ve" })).Id();
            Ids["puente"] = (await admin.CreateAsync("It31Puente", new { Nombre = "Puente visible", Secreto = Ids["secreto"] })).Id();
            Ids["saldo"] = (await admin.CreateAsync("It31Saldo", new { Clave = "A" })).Id();
            // Una capa abierta de la llave «A» (preparación por SQL: la escriben las automatizaciones de costeo).
            await ExecAsync($"""
                INSERT meta.CostLayer (Book, KeyHash, CompanyId, SourceEntity, SourceId, Quantity, Value)
                VALUES ('It31Saldo', '{KeyHash("It31Saldo", "A")}', NULL, 'It31Saldo', {Ids["saldo"]}, 10, {Capa.ToString(System.Globalization.CultureInfo.InvariantCulture)})
                """);

            Sec = await SiblingAsync(s =>
            {
                s["Environments:Name"] = "Producción IT 031";
                s["Environments:RequireSecurityApproval"] = "true";
                s["Automations:PollSeconds"] = "300";
            });
        }

        /// <summary>La llave de una capa (<c>CostMath.KeyHash</c>): SHA-256 de «erp-rollup:libro:VALOR|empresa:-».</summary>
        private static string KeyHash(string book, string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"erp-rollup:{book}:{value.Trim().ToUpperInvariant()}|empresa:-")));
    }

    private Task<Client> LectorAsync() => host.LoginAsync(Lector, Pwd);

    private static string Messages(Res r) => r.Body?["errors"]?.Json() ?? r.Text;

    /// <summary>Un documento del administrador que apunta al puente visible (el secreto, para el lector, «vacío»).</summary>
    private async Task<JsonNode> DocAsync(string nombre, string? oculto = null)
    {
        using var admin = await host.AdminAsync();
        return await admin.CreateAsync("It31Doc", new { Nombre = nombre, Puente = host.Ids["puente"], Oculto = oculto, Clave = "A" });
    }

    // ---------------- H1: la vista previa de la importación dice lo que /compute (A-01) ----------------

    [Fact]
    public async Task H1_CA1_la_vista_previa_de_la_importacion_da_los_mismos_avisos_que_compute()
    {
        using var lector = await LectorAsync();
        var compute = await lector.PostAsync("/api/data/It31Doc/compute", new { values = new { Nombre = "Importado", Puente = host.Ids["puente"] } });
        Assert.True(compute.Status == 200, compute.Text);
        // Para quien no ve el secreto, el dato está vacío: /compute (029) lo dice.
        Assert.Contains(Aviso, compute["warnings"].Json());

        var csv = Encoding.UTF8.GetBytes("Nombre;Puente\nImportado;Puente visible\n");
        var import = await lector.PostBytesAsync("/api/data/It31Doc/import?dryRun=true", csv);
        Assert.True(import.Status == 200, import.Text);
        Assert.True(import["valid"]!.GetValue<bool>(), import.Text);
        // Regresión (auditoría 5 A-01): la importación evaluaba con la autoridad del motor y el aviso no salía (el secreto
        // tiene valor): un bit por fila sobre un registro fuera de alcance.
        var messages = import.Body!.Arr("warnings").Nodes().Select(w => w.Str("message")).ToList();
        Assert.Equal([Aviso], messages);
        // El administrador, que sí ve el secreto, no recibe el aviso en ninguno de los dos caminos.
        using var admin = await host.AdminAsync();
        Assert.Empty((await admin.PostBytesAsync("/api/data/It31Doc/import?dryRun=true", csv)).Body!.Arr("warnings"));
    }

    // ---------------- H1: lo que pregunta o dice una transición (A-01) ----------------

    [Fact]
    public async Task H1_CA2_confirmar_pregunta_por_los_avisos_que_la_persona_ve_aunque_el_motor_no_los_tenga()
    {
        var doc = await DocAsync("Para revisar");
        using var lector = await LectorAsync();
        var row = await lector.RecordAsync("It31Doc", doc.Id());
        var res = await lector.TransitionAsync("It31Doc", row, "revisar");
        // Regresión: el motor no tiene avisos (lee el secreto) y la transición pasaba sin preguntar: la persona sabía así que el
        // secreto no estaba vacío. Ahora se le pregunta por lo mismo que ve en el formulario.
        Assert.True(res.Status == 428, res.Text);
        Assert.Contains(Aviso, res.Text);
        var ok = await lector.PostAsync($"/api/data/It31Doc/{doc.Id()}/transition",
            new { transition = "revisar", _rowVersion = row.Str("_rowVersion"), acceptWarnings = new[] { Aviso } });
        Assert.True(ok.Status == 200, ok.Text);
        Assert.Equal("Revisado", ok.Body!["_workflow"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task H1_CA3_un_bloqueo_dice_lo_que_la_persona_ve_nunca_el_aviso_que_lee_un_oculto()
    {
        var doc = await DocAsync("Para bloquear", oculto: "con valor");
        using var lector = await LectorAsync();
        var row = await lector.RecordAsync("It31Doc", doc.Id());
        var res = await lector.TransitionAsync("It31Doc", row, "bloquear");
        // El motor bloquea por el aviso del campo oculto; a la persona se le dice lo que /compute le muestra.
        Assert.True(res.Status == 400, res.Text);
        Assert.DoesNotContain(AvisoOculto, res.Text);
        Assert.Contains(Aviso, Messages(res));
        Assert.Equal("Abierto", (await lector.RecordAsync("It31Doc", doc.Id()))["_workflow"]!["state"]!.GetValue<string>());
    }

    // ---------------- H4: el error de una automatización síncrona (A-04) ----------------

    [Fact]
    public async Task H4_CA1_la_automatizacion_que_bloquea_por_avisos_no_le_dice_a_quien_guarda_el_que_lee_un_oculto()
    {
        var doc = await DocAsync("Documento automático", oculto: "con valor");
        using var lector = await LectorAsync();
        var row = await lector.RecordAsync("It31Doc", doc.Id());
        var res = await lector.PutAsync($"/api/data/It31Doc/{doc.Id()}", new { Nombre = "Bloquear", _rowVersion = row.Str("_rowVersion") });
        Assert.True(res.Status == 400, res.Text);
        Assert.Contains("Bloquear al pedirlo", res.Text);
        // Regresión (auditoría 5 A-04): el 400 de la automatización unía todos los mensajes, también el que lee el oculto.
        Assert.DoesNotContain(AvisoOculto, res.Text);
        Assert.Equal("Documento automático", (await lector.RecordAsync("It31Doc", doc.Id())).Str("Nombre"));
        // Al administrador (que ve el campo) sí se le dice.
        using var admin = await host.AdminAsync();
        var mine = await admin.RecordAsync("It31Doc", doc.Id());
        var own = await admin.PutAsync($"/api/data/It31Doc/{doc.Id()}", new { Nombre = "Bloquear", _rowVersion = mine.Str("_rowVersion") });
        Assert.True(own.Status == 400, own.Text);
        Assert.Contains(AvisoOculto, own.Text);
    }

    // ---------------- H5: UNITCOST en el PDF (A-05) ----------------

    [Fact]
    public async Task H5_CA1_el_pdf_no_imprime_el_costo_de_un_catalogo_de_saldo_restringido()
    {
        var doc = await DocAsync("Con costo");
        using var admin = await host.AdminAsync();
        var own = await PdfTextAsync(admin, doc.Id());
        // La extracción funciona y el costo existe para quien no tiene restringido el saldo.
        Assert.Contains("Documento", own);
        Assert.Matches(@"Costo:\s*\$?\s*123[.,]45", own);
        using var lector = await LectorAsync();
        var text = await PdfTextAsync(lector, doc.Id());
        Assert.Contains("Documento", text);
        // Regresión (auditoría 5 A-05): el PDF creaba el cargador sin usuario y UNITCOST leía las capas (en /compute, 029 F-C).
        Assert.DoesNotContain("123", text);
        Assert.Matches(@"Costo:\s*fin", text);
    }

    private static async Task<string> PdfTextAsync(Client api, int id)
    {
        using var res = await api.Http.GetAsync($"/api/data/It31Doc/{id}/pdf");
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.True(res.IsSuccessStatusCode, Encoding.UTF8.GetString(bytes));
        return PdfText(bytes);
    }

    /// <summary>
    /// Texto de un PDF de PDFsharp (como <c>pdfText</c> de canario.spec.ts): cadenas literales de Tj/TJ en los flujos Flate,
    /// unidas sin espacios.
    /// </summary>
    private static string PdfText(byte[] pdf)
    {
        var raw = Encoding.Latin1.GetString(pdf);
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(raw, @"<<([\s\S]*?)>>\s*stream\r?\n"))
        {
            var length = int.TryParse(Regex.Match(m.Groups[1].Value, @"/Length (\d+)").Groups[1].Value, out var l) ? l : 0;
            var start = m.Index + m.Length;
            if (start + length > pdf.Length) continue;
            var data = pdf.AsSpan(start, length).ToArray();
            string content;
            if (m.Groups[1].Value.Contains("FlateDecode"))
            {
                using var output = new MemoryStream();
                try
                {
                    using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                    var buffer = new byte[4096];
                    int n;
                    while ((n = z.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
                }
                catch (InvalidDataException) { /* PDFsharp no cierra el flujo con su suma: vale lo leído */ }
                content = Encoding.Latin1.GetString(output.ToArray());
            }
            else content = Encoding.Latin1.GetString(data);
            if (!Regex.IsMatch(content, @"\bBT\b") || !Regex.IsMatch(content, @"\bT[Jj]\b")) continue;
            foreach (Match lit in Regex.Matches(content, @"\(((?:\\.|[^\\)])*)\)"))
                sb.Append(Regex.Replace(lit.Groups[1].Value, @"\\([()\\])", "$1"));
        }
        return sb.ToString();
    }

    // ---------------- H3: quien aprueba no gana al mover a alguien a su unidad (A-02) ----------------

    [Fact]
    public async Task H3_CA1_quien_aprueba_no_mueve_a_un_usuario_ni_a_un_equipo_a_su_unidad()
    {
        using var admin = await host.AdminAsync();
        async Task<int> Unit(string name, int? parentId = null)
        {
            var r = await admin.PostAsync("/api/admin/units", new { name, parentId });
            Assert.True(r.Status is 200 or 201, r.Text);
            return r.Body!.Id();
        }
        var norte = await Unit("IT31 Norte");
        await Unit("IT31 Norte Ventas", norte);
        await Unit("IT31 Sur");
        var usuarios = await admin.PostAsync("/api/admin/roles", new { name = "IT31 Usuarios", description = "Spec 031", canManageModel = false,
            canManageUsers = true, canViewAudit = false, permissions = Array.Empty<object>() });
        Assert.True(usuarios.Status == 200, usuarios.Text);
        async Task<int> User(string username, string? unit, int roleId)
        {
            var r = await admin.PostAsync("/api/admin/users", new { username, displayName = username, password = Pwd, roleIds = new[] { roleId }, unit });
            Assert.True(r.Status == 201, r.Text);
            return r.Body!.Id();
        }
        await User("it31-solicita", null, usuarios.Body!.Id());
        await User("it31-revisa-norte", "IT31 Norte", usuarios.Body!.Id());
        await User("it31-revisa-sur", "IT31 Sur", usuarios.Body!.Id());
        var duena = await User("it31-duena", "IT31 Sur", host.Ids["rol"]);
        var team = await admin.PostAsync("/api/admin/teams", new { name = "IT31 Equipo", unit = "IT31 Sur", isActive = true, memberIds = Array.Empty<int>() });
        Assert.True(team.Status is 200 or 201, team.Text);

        using var solicita = await host.Sec.LoginAsync("it31-solicita", Pwd);
        using var norteApi = await host.Sec.LoginAsync("it31-revisa-norte", Pwd);
        using var surApi = await host.Sec.LoginAsync("it31-revisa-sur", Pwd);
        async Task<JsonNode> Pending(Res r)
        {
            Assert.True(r.Status == 202, r.Text);
            var id = r["securityChange"]!.Id();
            return (await norteApi.GetAsync("/api/admin/security-changes")).Items.Nodes().First(c => c.Id() == id);
        }
        Task<Res> Approve(Client api, JsonNode change) =>
            api.PostAsync($"/api/admin/security-changes/{change.Id()}/approve", new { hash = change.Str("hash"), comment = "IT 031" });

        // Mover a la dueña de registros de «Sur» a «Norte Ventas» (bajo la unidad de quien revisa en el norte).
        var user = await Pending(await solicita.PutAsync($"/api/admin/users/{duena}",
            new { displayName = "it31-duena", isActive = true, roleIds = new[] { host.Ids["rol"] }, unit = "IT31 Norte Ventas" }));
        // Regresión (auditoría 5 A-02): cualquier revisor lo aprobaba; el del norte ganaba ver los registros de la dueña.
        Assert.False(user["canApprove"]!.GetValue<bool>(), user.Json());
        var denied = await Approve(norteApi, user);
        Assert.True(denied.Status == 403, denied.Text);
        Assert.Contains("tu unidad", denied.Text);
        Assert.True((await Approve(surApi, user)).Status == 200); // a quien no gana nada sí

        var teamChange = await Pending(await solicita.PutAsync($"/api/admin/teams/{team.Body!.Id()}",
            new { name = "IT31 Equipo", unit = "IT31 Norte", isActive = true, memberIds = Array.Empty<int>() }));
        Assert.False(teamChange["canApprove"]!.GetValue<bool>(), teamChange.Json());
        Assert.Equal(403, (await Approve(norteApi, teamChange)).Status);
        Assert.Equal(200, (await Approve(surApi, teamChange)).Status);
    }

    // ---------------- H2: los costos no salen por la cuenta de servicio (A-03) ----------------

    [Fact]
    public async Task H2_CA1_la_integracion_de_tienda_lee_existencias_sin_costos_por_la_api_y_por_odata()
    {
        using var admin = await host.AdminAsync();
        foreach (var m in new[] { "compras", "inventario", "contabilidad", "costeo" }) await admin.InstallModuleAsync(m);
        var unidad = (await admin.GetAsync("/api/data/UnidadMedida", new { search = "PZA" })).Items.Nodes().First().Id();
        var almacen = (await admin.GetAsync("/api/data/Almacen/lookup", new { search = "Almacén general" })).Array.Nodes().First().Id();
        var producto = (await admin.CreateAsync("Producto", new { Clave = "P31", Descripcion = "Producto 031", Unidad = unidad })).Id();
        await admin.CreateAsync("MovimientoInventario", new { Tipo = "Entrada", Almacen = almacen, Producto = producto, Cantidad = 4, CostoUnitario = 12.5 });

        Existencia(await admin.GetAsync("/api/data/Existencia", new { pageSize = 200 }), producto, costs: true);

        var tienda = await admin.RoleIdAsync("Integración de tienda");
        var key = await admin.PostAsync("/api/admin/api-keys", new { name = "Tienda 031", roleIds = new[] { tienda } });
        Assert.True(key.Status == 200, key.Text);
        using var bearer = host.Anonymous(csrfHeader: false);
        bearer.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Body!.Str("secret"));
        // Regresión (auditoría 5 A-03): con la clave de la tienda salían Valor, CantidadCosteada y CostoPromedio.
        Existencia(await bearer.GetAsync("/api/data/Existencia", new { pageSize = 200 }), producto, costs: false);
        var odata = await bearer.GetAsync("/odata/Existencia");
        Assert.True(odata.Status == 200, odata.Text);
        var rows = odata.Body!.Arr("value").Nodes().ToList();
        Assert.NotEmpty(rows);
        foreach (var f in new[] { "Valor", "CantidadCosteada", "CostoPromedio", "CosteoIncompleto" })
            Assert.All(rows, r => Assert.Null(r[f]));
        Assert.Contains(rows, r => r["Cantidad"] is not null);
        var metadata = await bearer.GetAsync("/odata/$metadata");
        Assert.DoesNotContain("CostoPromedio", metadata.Text);

        // Quien registra y supervisa el inventario los sigue viendo.
        var supervisor = await admin.RoleIdAsync("Supervisor de inventario");
        Assert.Equal(201, (await admin.PostAsync("/api/admin/users", new { username = "it31-supervisor", displayName = "Supervisor 031", password = Pwd, roleIds = new[] { supervisor } })).Status);
        using var sup = await host.LoginAsync("it31-supervisor", Pwd);
        Existencia(await sup.GetAsync("/api/data/Existencia", new { pageSize = 200 }), producto, costs: true);
        var movs = (await sup.GetAsync("/api/data/MovimientoInventario", new { pageSize = 200 })).Items.Nodes().Where(x => x["Producto"]?.GetValue<int>() == producto).ToList();
        Assert.Contains(movs, x => x.Dec("CostoTotal") == 50);
    }

    private static void Existencia(Res res, int producto, bool costs)
    {
        Assert.True(res.Status == 200, res.Text);
        var row = res.Items.Nodes().FirstOrDefault(x => x["Producto"]?.GetValue<int>() == producto);
        Assert.True(row is not null, res.Text);
        Assert.Equal(4, row!.Dec("Cantidad"));
        if (costs)
        {
            Assert.Equal(50, row.Dec("Valor"));
            Assert.Equal(12.5m, row.Dec("CostoPromedio"));
        }
        else
            foreach (var f in new[] { "Valor", "CantidadCosteada", "CostoPromedio" }) Assert.Null(row[f]);
    }

    // ---------------- H6: las rutas reales son las del inventario ----------------

    [Fact]
    public void H6_CA3_cada_ruta_de_Program_esta_en_el_inventario()
    {
        // RouteInventoryTests construye las rutas con MapErpRoutes; aquí se toman las de la aplicación real (Program), donde
        // una ruta mapeada aparte no saldría en rutas.md (auditoría 5). La SPA no se sirve en integración.
        var md = File.ReadAllLines(Path.Combine(ErpHost.RepoRoot(), "docs", "sdd", "rutas.md"));
        var inventory = md.Where(l => l.StartsWith("| ", StringComparison.Ordinal) && l.Contains('`'))
            .Select(l => l.Split('|').Select(c => c.Trim()).ToArray())
            .Select(c => $"{c[1]} {c[2].Trim('`')}").ToHashSet(StringComparer.Ordinal);
        Assert.True(inventory.Count > 100);
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(m => $"{m} /{(e.RoutePattern.RawText ?? "").TrimStart('/')}"))
            .ToList();
        Assert.True(routes.Count > 100);
        var missing = routes.Where(r => !inventory.Contains(r)).ToList();
        Assert.True(missing.Count == 0, "Rutas de Program fuera de docs/sdd/rutas.md: " + string.Join(", ", missing));
    }
}
