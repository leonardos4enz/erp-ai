using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Api.Security;
using Erp.Api.Studio;
using Erp.IntegrationTests.Harness;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 034 · Endurecimiento 5 (auditoría externa 6). Nadie actúa sobre quien tiene más que él (A-01), un entorno con
/// aprobación no arranca sin clave de firma (R-009-1), el catálogo de Studio con los permisos exactos de la persona (A-02,
/// A-04, R-033-5; A-03 en <c>Hardening034Tests.Credential.cs</c>) y la bitácora que deniega lo no clasificado (A-07).
/// </summary>
public partial class Hardening034Tests(Hardening034Tests.Host host) : IClassFixture<Hardening034Tests.Host>
{
    private const string Pwd = "frase larga de endurecimiento 034";
    private const string Group = "Pruebas 034";
    private const string Doc = "It34Doc";

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Ids = [];
        /// <summary>Instancia con aprobación de seguridad (como Hardening021Tests y Hardening031Tests).</summary>
        public Factory Sec = null!;

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            async Task<int> Role(string name, bool model = false, bool users = false, bool audit = false)
            {
                var r = await admin.PostAsync("/api/admin/roles", new { name, description = "Spec 034", canManageModel = model, canManageUsers = users,
                    canViewAudit = audit, permissions = Array.Empty<object>() });
                Assert.True(r.Status == 200, r.Text);
                return r.Body!.Id();
            }
            Ids["gestor"] = await Role("IT34 Gestor", users: true);
            Ids["modelador"] = await Role("IT34 Modelador", model: true);
            Ids["auditor"] = await Role("IT34 Auditor", audit: true);
            await admin.ApplyPlanAsync(new { description = "Spec 034", operations = new object[] {
                new { op = "createEntity", group = Group, name = Doc, label = "Documento 034", labelPlural = "Documentos 034", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                new { op = "createEntity", group = Group, name = "It34Privado", label = "Privado 034", labelPlural = "Privados 034", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                new { op = "setSecurity", entity = Doc, security = new { records = new[] { new { roles = new[] { "IT34 Auditor" }, scope = "own" } } } },
                new { op = "setPrintTemplates", entity = Doc, templates = new[] { new { name = "Ficha", label = "Ficha", @default = true, blocks = new object[] {
                    new { type = "text", text = "Documento {{ Nombre }}" } } } } },
            } });
            Assert.Equal(200, (await admin.PutAsync($"/api/admin/roles/{Ids["auditor"]}", new { name = "IT34 Auditor", description = "Spec 034", canManageModel = false,
                canManageUsers = false, canViewAudit = true, permissions = new[] { new { entity = Doc, canRead = true, canCreate = true, canUpdate = true, canDelete = false } } })).Status);
            async Task User(string username, params int[] roleIds)
            {
                var r = await admin.PostAsync("/api/admin/users", new { username, displayName = username, password = Pwd, roleIds });
                Assert.True(r.Status == 201, r.Text);
                Ids[username] = r.Body!.Id();
            }
            await User("it34-gestor", Ids["gestor"]);
            await User("it34-gestor2", Ids["gestor"]);
            await User("it34-modelador", Ids["modelador"]);
            await User("it34-simple", await admin.RoleIdAsync("Usuario"));
            await User("it34-auditor", Ids["auditor"]);
            await User("it34-admin2", await admin.RoleIdAsync("Administrador"));
            Ids["admin"] = await ScalarAsync<int>("SELECT Id FROM meta.[User] WHERE Username = N'admin'");
            Assert.True((await admin.PostAsync("/api/admin/units", new { name = "IT34 Norte" })).Status is 200 or 201);
            Ids["doc"] = (await admin.CreateAsync(Doc, new { Nombre = "Del administrador" })).Id();

            Sec = await SiblingAsync(s =>
            {
                s["Environments:Name"] = "Producción IT 034";
                s["Environments:RequireSecurityApproval"] = "true";
            });
        }
    }

    private async Task<JsonNode> UserAsync(Client api, int id) =>
        (await api.GetAsync("/api/admin/users")).Array.Nodes().First(u => u.Id() == id);

    private static object Put(JsonNode u, object? changes = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["displayName"] = u.Str("displayName"), ["isActive"] = u["isActive"]!.GetValue<bool>(),
            ["roleIds"] = u.Arr("roleIds").Nodes().Select(x => x.GetValue<int>()).ToArray(), ["unit"] = u.Str("unit"),
        };
        foreach (var p in changes?.GetType().GetProperties() ?? []) body[p.Name] = p.GetValue(changes);
        return body;
    }

    // ---------------- H1: nadie actúa sobre quien tiene más (A-01) ----------------

    [Fact]
    public async Task Un_gestor_de_usuarios_no_cambia_la_contrasena_ni_desactiva_a_quien_tiene_mas_capacidades()
    {
        using var gestor = await host.LoginAsync("it34-gestor", Pwd);
        var admin = await UserAsync(gestor, host.Ids["admin"]);
        // Regresión (auditoría 6 A-01): con los mismos roles, la contraseña nueva del administrador respondía 200 y el gestor
        // entraba como él.
        var takeover = await gestor.PutAsync($"/api/admin/users/{admin.Id()}", Put(admin, new { password = "contraseña tomada por el gestor 034" }));
        Assert.True(takeover.Status == 403, takeover.Text);
        Assert.Contains("administrador del sistema", takeover["title"]!.GetValue<string>());
        var off = await gestor.PutAsync($"/api/admin/users/{host.Ids["it34-admin2"]}", Put(await UserAsync(gestor, host.Ids["it34-admin2"]), new { isActive = false }));
        Assert.True(off.Status == 403, off.Text);
        Assert.Equal(403, (await gestor.PutAsync($"/api/admin/users/{admin.Id()}", Put(admin, new { unit = "IT34 Norte" }))).Status);
        // Revisión adversarial 034 (F-01): agregar o quitar un rol sin capacidades renovaba el sello y cerraba sus sesiones.
        var usuario = await gestor.RoleIdAsync("Usuario");
        var roles = await gestor.PutAsync($"/api/admin/users/{admin.Id()}", Put(admin, new { roleIds = new[] { 1, usuario } }));
        Assert.True(roles.Status == 403, roles.Text);
        // Con otra capacidad administrativa (modelar), igual.
        var modelador = await UserAsync(gestor, host.Ids["it34-modelador"]);
        var m = await gestor.PutAsync($"/api/admin/users/{modelador.Id()}", Put(modelador, new { password = "contraseña tomada por el gestor 034" }));
        Assert.True(m.Status == 403, m.Text);
        Assert.Contains("administrar el modelo", m["title"]!.GetValue<string>());
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.SecurityChange WHERE RequestedBy = {host.Ids["it34-gestor"]}")); // ni siquiera queda solicitud

        // Nada cambió: el administrador entra con su contraseña y sigue activo.
        using (var still = await host.AdminAsync()) Assert.Equal(200, (await still.GetAsync("/api/auth/me")).Status);
        Assert.True((await UserAsync(gestor, host.Ids["it34-admin2"]))["isActive"]!.GetValue<bool>());

        // CA3: el nombre visible no da ni quita acceso; sobre quien no tiene más capacidades, sí actúa; el superusuario, sobre todos.
        var renamed = await gestor.PutAsync($"/api/admin/users/{modelador.Id()}", Put(modelador, new { displayName = "Modelador 034" }));
        Assert.True(renamed.Status == 200, renamed.Text);
        var simple = await UserAsync(gestor, host.Ids["it34-simple"]);
        Assert.Equal(200, (await gestor.PutAsync($"/api/admin/users/{simple.Id()}", Put(simple, new { password = "contraseña nueva del simple 034", unit = "IT34 Norte" }))).Status);
        var par = await UserAsync(gestor, host.Ids["it34-gestor2"]);
        Assert.Equal(200, (await gestor.PutAsync($"/api/admin/users/{par.Id()}", Put(par, new { isActive = false }))).Status);
        using var root = await host.AdminAsync();
        Assert.Equal(200, (await root.PutAsync($"/api/admin/users/{par.Id()}", Put(await UserAsync(root, par.Id()), new { isActive = true }))).Status);
        Assert.Equal(200, (await root.PutAsync($"/api/admin/users/{modelador.Id()}", Put(await UserAsync(root, modelador.Id()), new { password = Pwd + " otra" }))).Status);
    }

    [Fact]
    public async Task H1_CA2_la_aprobacion_aplica_la_misma_regla_con_quien_aprueba()
    {
        using var root = await host.Sec.LoginAsync("admin", host.AdminPassword);
        using var gestor = await host.Sec.LoginAsync("it34-gestor2", Pwd);
        using var admin2 = await host.Sec.LoginAsync("it34-admin2", Pwd);
        // El superusuario pide poner contraseña al modelador (poner contraseña a otro espera aprobación, 015).
        var target = await UserAsync(root, host.Ids["it34-modelador"]);
        var req = await root.PutAsync($"/api/admin/users/{target.Id()}", Put(target, new { password = "contraseña pedida por el admin 034" }));
        Assert.True(req.Status == 202, req.Text);
        var id = req["securityChange"]!.Id();
        var change = (await gestor.GetAsync("/api/admin/security-changes")).Items.Nodes().First(c => c.Id() == id);
        // Regresión (A-01): el gestor la aprobaba y la contraseña que él había leído en la solicitud quedaba puesta.
        Assert.False(change["canApprove"]!.GetValue<bool>(), change.Json());
        var denied = await gestor.PostAsync($"/api/admin/security-changes/{id}/approve", new { hash = change.Str("hash"), comment = "IT 034" });
        Assert.True(denied.Status == 403, denied.Text);
        Assert.Contains("administrar el modelo", denied.Text);
        var ok = await admin2.PostAsync($"/api/admin/security-changes/{id}/approve", new { hash = change.Str("hash"), comment = "IT 034" });
        Assert.True(ok.Status == 200, ok.Text);
    }

    // ---------------- H2: con aprobación, clave de firma (R-009-1) ----------------

    [Fact]
    public async Task H2_CA1_un_entorno_con_aprobacion_sin_clave_no_arranca()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => host.SiblingAsync(s => s["Environments:SigningKey"] = ""));
        Assert.Contains("Environments:SigningKey", ex.ToString());
        Assert.Contains("sin firmar", ex.ToString());
    }

    // ---------------- H3: el catálogo de Studio con los permisos exactos ----------------

    private async Task<UserContext> ContextAsync(string username) => await Task.Run(async () =>
    {
        var id = await host.ScalarAsync<int>($"SELECT Id FROM meta.[User] WHERE Username = N'{username}'");
        var stamp = await host.ScalarAsync<Guid>($"SELECT SecurityStamp FROM meta.[User] WHERE Id = {id}");
        return (await host.Services.GetRequiredService<UserContextLoader>().LoadAsync(id, stamp))!;
    });

    [Fact]
    public Task H3_CA1_sin_modelador_una_herramienta_aislada_se_niega() => Task.Run(async () =>
    {
        var runner = host.Services.GetRequiredService<StudioToolRunner>();
        var simple = await ContextAsync("it34-simple");
        Assert.False(simple.CanManageModel);
        var created = 0;
        var ctx = new ToolContext
        {
            User = simple, Scopes = new HashSet<string> { StudioScopes.Model, StudioScopes.Sandbox }, Services = host.Services,
            SandboxAsync = _ => { Interlocked.Increment(ref created); throw new InvalidOperationException("La copia no debe prepararse."); },
        };
        // Regresión (auditoría 6 A-02): el runner preparaba la copia y corría como su administrador sin mirar a la persona.
        foreach (var (tool, input) in new (string, object)[] { ("aislado_listar", new { entity = Doc }), ("aislado_aplicar_plan", new { plan = new { operations = Array.Empty<object>() } }),
                     ("aislado_procesar_asincronas", new { }), ("aislado_actualizar", new { entity = Doc, id = 1, values = new { } }) })
        {
            var r = await runner.RunAsync(ctx, tool, JsonSerializer.SerializeToElement(input), CancellationToken.None);
            Assert.True(r.Status == "denied", $"{tool}: {r.Status} {r.Summary}");
        }
        Assert.Equal(0, created);
        // La capacidad que se publica es la que se exige.
        var info = StudioToolRunner.Describe(host.Services.GetRequiredService<RouteDispatcher>()).Single(t => t.Name == "aislado_listar");
        Assert.Equal("modelador", info.Capability);
        // En el entorno real, la de la ruta: «registros» la tiene cualquiera.
        var withData = new ToolContext { User = simple, Scopes = new HashSet<string> { StudioScopes.Model, StudioScopes.Data }, Services = host.Services, SandboxAsync = ctx.SandboxAsync };
        var real = await runner.RunAsync(withData, "leer_registros",
            JsonSerializer.SerializeToElement(new { entity = Doc }), CancellationToken.None);
        Assert.Equal("denied", real.Status); // sin permiso sobre el catálogo: el 403 de la API
    });

    /// <summary>
    /// A-04: recorre como administrador todas las GET de la aplicación y compara lo que deja en la bitácora con el efecto que
    /// declara. Una GET que escribe sin declararlo la trataría como lectura el despachador de Studio y del MCP.
    /// </summary>
    [Fact]
    public async Task H3_CA3_cada_GET_que_deja_rastro_declara_su_efecto()
    {
        using var admin = await host.AdminAsync();
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Contains("GET") && !(e.RoutePattern.RawText ?? "").Contains('*'))
            .ToList();
        Assert.True(routes.Count > 60, $"Solo {routes.Count} GET");
        var undeclared = new List<string>();
        var declared = new List<(string Route, bool Wrote)>();
        foreach (var e in routes)
        {
            var pattern = RouteDispatcher.Pattern(e);
            var url = pattern.Replace("{entity}", Doc).Replace("{id:long}", host.Ids["doc"].ToString()).Replace("{id:int}", "1")
                .Replace("{segment}", Doc).Replace("{token}", "x")
                .Replace("{name}", pattern.StartsWith("/api/explorer") || pattern.StartsWith("/api/model") ? Doc : "NoExiste");
            Assert.False(url.Contains('{'), $"La prueba no sabe llenar {pattern}: agrega el parámetro.");
            var before = await host.ScalarAsync<long>("SELECT ISNULL(MAX(Id), 0) FROM meta.AuditLog");
            var res = await admin.GetAsync(url);
            var wrote = await host.ScalarAsync<long>("SELECT ISNULL(MAX(Id), 0) FROM meta.AuditLog") > before;
            if (wrote && !RouteDispatcher.Writes(e, "GET"))
            {
                // Revisión adversarial 034 (F-06): otra vez, por si escribió un trabajo en segundo plano y no la ruta.
                before = await host.ScalarAsync<long>("SELECT ISNULL(MAX(Id), 0) FROM meta.AuditLog");
                await admin.GetAsync(url);
                wrote = await host.ScalarAsync<long>("SELECT ISNULL(MAX(Id), 0) FROM meta.AuditLog") > before;
            }
            if (RouteDispatcher.Writes(e, "GET")) declared.Add((pattern, wrote && res.Status < 300));
            else if (wrote) undeclared.Add($"{pattern} ({res.Status})");
        }
        // Regresión (auditoría 6 A-04): imprimir y exportar escribían en la bitácora y pasaban como lectura.
        Assert.True(undeclared.Count == 0, "GET que dejan rastro sin declararlo: " + string.Join(", ", undeclared));
        Assert.Contains(declared, d => d.Route == "/api/data/{entity}/{id:long}/pdf");
        Assert.Contains(declared, d => d.Route == "/api/data/{entity}/export");
        // Y lo declarado se ejerció: cada una respondió y dejó su rastro (si no, el recorrido no probaría nada).
        Assert.All(declared, d => Assert.True(d.Wrote, $"{d.Route} declara «bitácora» y no dejó rastro"));
    }

    [Fact]
    public Task H3_CA4_las_rutas_despachadas_tienen_su_limite() => Task.Run(async () =>
    {
        var limited = await host.SiblingAsync(s => s["Security:BulkPerMinute"] = "3");
        var dispatcher = limited.Services.GetRequiredService<RouteDispatcher>();
        var admin = await ContextAsync("admin");
        var call = new RouteCall("GET", "/api/data/{entity}/export", new Dictionary<string, string?> { ["entity"] = Doc });
        // Regresión (R-033-5): sin los limitadores de ASP.NET, una ruta despachada no tenía tope.
        var results = await Concurrently(10, _ => dispatcher.SendAsync(limited.Services, admin, call, allowWrites: true, CancellationToken.None));
        Assert.Equal(3, results.Count(r => r.Status == 200));
        Assert.All(results.Where(r => r.Status != 200), r => Assert.Equal(429, r.Status));
        Assert.StartsWith("Demasiadas solicitudes", results.First(r => r.Status == 429).Error);
        // El tope es por persona: otra no lo comparte.
        var other = await dispatcher.SendAsync(limited.Services, await ContextAsync("it34-admin2"), call, allowWrites: true, CancellationToken.None);
        Assert.Equal(200, other.Status);
    });

    // ---------------- H4: la bitácora deniega lo no clasificado (A-07, R-032-6) ----------------

    [Fact]
    public async Task H4_CA1_la_bitacora_no_muestra_impresiones_ni_tareas_fuera_del_alcance()
    {
        using var auditor = await host.LoginAsync("it34-auditor", Pwd);
        var mine = (await auditor.CreateAsync(Doc, new { Nombre = "Del auditor" })).Id();
        // Los eventos se preparan por SQL: lo que se prueba es quién los ve, no quién los escribe.
        string Row(string action, string entity, string recordId) =>
            $"INSERT meta.AuditLog (UserId, Username, Action, EntityName, RecordId, AfterJson) VALUES ({host.Ids["admin"]}, N'admin', N'{action}', N'{entity}', N'{recordId}', N'{{\"template\":\"Ficha\"}}');";
        await host.ExecAsync(Row("data.print", Doc, host.Ids["doc"].ToString()) + Row("data.print", Doc, mine.ToString())
            + Row("automation.schedule", "It34Privado", "Vencer") + Row("automation.retry", "It34Privado", "Avisar")
            + Row("automation.schedule", Doc, "Revisar") + Row("accion.sin.clasificar", Doc, mine.ToString()));
        var items = (await auditor.GetAsync("/api/audit", new { pageSize = 200 })).Items.Nodes()
            .Select(x => (Action: x.Str("action"), Entity: x.Str("entityName"), Record: x.Str("recordId"), After: x["after"])).ToList();
        // Regresión (auditoría 6 A-07): cualquier auditor veía qué se imprimió y qué tareas corrían en catálogos que no lee.
        Assert.DoesNotContain(items, i => i.Action == "data.print" && i.Record == host.Ids["doc"].ToString());
        Assert.DoesNotContain(items, i => i.Entity == "It34Privado");
        Assert.DoesNotContain(items, i => i.Action == "accion.sin.clasificar");
        // Lo suyo sí, sin recortar el nombre de la plantilla (no es un campo del registro).
        var print = Assert.Single(items, i => i.Action == "data.print" && i.Record == mine.ToString());
        Assert.Equal("Ficha", print.After!.Str("template"));
        Assert.Contains(items, i => i.Action == "automation.schedule" && i.Entity == Doc);
        Assert.Contains(items, i => i.Action == "login"); // lo que no es de datos lo sigue viendo todo auditor
        using var admin = await host.AdminAsync();
        Assert.Contains((await admin.GetAsync("/api/audit", new { pageSize = 50 })).Items.Nodes(), x => x.Str("action") == "accion.sin.clasificar");
        // Revisión adversarial 034 (F-05): la impresión guarda el nombre del catálogo del modelo, no el de la URL.
        Assert.Equal(200, (await admin.GetAsync($"/api/data/{Doc.ToLowerInvariant()}/{mine}/pdf")).Status);
        Assert.Equal(Doc, await host.ScalarAsync<string>($"SELECT TOP 1 EntityName FROM meta.AuditLog WHERE Action = 'data.print' AND RecordId = N'{mine}' AND UserId = {host.Ids["admin"]} ORDER BY Id DESC"));
    }
}
