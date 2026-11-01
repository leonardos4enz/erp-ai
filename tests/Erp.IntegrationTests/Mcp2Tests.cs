using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Api.Mcp;
using Erp.Api.Security;
using Erp.Api.Studio;
using Erp.IntegrationTests.Harness;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 044 · MCP 2, sobre SQL real y con copias aisladas reales: alcances <c>aislado</c> y <c>proponer</c> (solo para quien
/// modela), una copia por conexión (preparación única, reutilización, lease, inactividad, desconexión, sello, descartar,
/// preparaciones a medias y bases huérfanas, topes), claves de idempotencia (repetir, conflicto, en curso, vencida, por
/// conexión, errores que no consumen) y <c>entregar</c> como propuesta de Studio (exactamente una vez, tope, falla inyectada,
/// revisar y aplicar con huella, sin mensajes).
/// </summary>
public class Mcp2Tests(Mcp2Tests.Host host) : IClassFixture<Mcp2Tests.Host>
{
    public const string Pwd = "frase larga del servidor mcp dos 2026";
    public const string Callback = "https://claude.ai/api/mcp/auth_callback";
    public const string Resource = "https://localhost/mcp";
    public const string All = "modelo datos aislado proponer";

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Users = [];

        protected override void Configure(IDictionary<string, string?> s)
        {
            s["Mcp:Burst"] = "100000";
            s["Mcp:CallsPerMinute"] = "100000";
            s["Mcp:PendingClientsPerAddress"] = "100000";
            s["Mcp:SweepSeconds"] = "3600"; // las pruebas barren a mano (McpSandboxes.SweepAsync)
            s["Mcp:SandboxLeaseSeconds"] = "6"; // renovación cada 2 s
            s["Mcp:MaxSandboxes"] = "100";
            s["Security:TokenRequestsPerMinute"] = "100000";
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            foreach (var (role, model) in new[] { ("M2 Modelador", true), ("M2 Lector", false) })
                Assert.Equal(200, (await admin.PostAsync("/api/admin/roles", new { name = role, description = "Spec 044", canManageModel = model,
                    canManageUsers = false, canViewAudit = false, permissions = Array.Empty<object>() })).Status);
            await admin.ApplyPlanAsync(new { description = "Spec 044 · catálogo", operations = new object[] {
                new { op = "createEntity", group = "Pruebas 044", name = "M2Nota", label = "Nota 044", labelPlural = "Notas 044", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
            } });
            var modeler = await admin.RoleIdAsync("M2 Modelador");
            var reader = await admin.RoleIdAsync("M2 Lector");
            foreach (var u in new[] { "m2-ana", "m2-beto", "m2-carla", "m2-dora", "m2-eli", "m2-fer", "m2-gil", "m2-hugo", "m2-ines", "m2-juan", "m2-kim", "m2-lia", "m2-mar", "m2-nico", "m2-olga", "m2-pau", "m2-quim", "m2-lector" })
            {
                var res = await admin.PostAsync("/api/admin/users", new { username = u, displayName = u, password = Pwd, roleIds = new[] { u == "m2-lector" ? reader : modeler } });
                Assert.True(res.Status == 201, res.Text);
                Users[u] = res.Body!.Id();
            }
        }
    }

    // ---------- Cliente MCP simulado ----------

    public sealed record Conn(string ClientId, string Access, string Scope, int Grant);

    private static string Query(string clientId, string verifier, string scope) =>
        $"response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(Callback)}" +
        $"&code_challenge={Uri.EscapeDataString(McpFormat.Challenge(verifier))}&code_challenge_method=S256&state=e1" +
        $"&scope={Uri.EscapeDataString(scope)}&resource={Uri.EscapeDataString(Resource)}";

    private static async Task<string> RegisterAsync(Client api, string name = "Claude")
    {
        var res = await api.PostAsync("/oauth/register", new { client_name = name, redirect_uris = new[] { Callback } });
        Assert.True(res.Status == 201, res.Text);
        return res.Body!.Str("client_id")!;
    }

    private async Task<Conn> ConnectAsync(string user, string scope = All, string[]? chosen = null, ErpHost.Factory? f = null, string name = "Claude")
    {
        using var api = f?.Anonymous(false) ?? host.Anonymous(false);
        var clientId = await RegisterAsync(api, name);
        var verifier = McpFormat.New("v");
        using var person = f is null ? await host.LoginAsync(user, Pwd) : await f.LoginAsync(user, Pwd);
        var query = Query(clientId, verifier, scope);
        var decided = await person.PostAsync("/api/mcp/authorize", new { query, approve = true, scopes = chosen ?? scope.Split(' ') });
        Assert.True(decided.Status == 200, decided.Text);
        var back = QueryHelpers.ParseQuery(new Uri(decided["redirect"]!.GetValue<string>()).Query);
        var t = await api.PostFormAsync("/oauth/token", new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = back["code"].ToString(), ["redirect_uri"] = Callback, ["client_id"] = clientId,
            ["code_verifier"] = verifier, ["resource"] = Resource,
        });
        Assert.True(t.Status == 200, t.Text);
        var grant = await host.ScalarAsync<int>($"SELECT MAX(g.Id) FROM meta.McpGrant g JOIN meta.McpClient c ON c.Id = g.ClientId WHERE c.ClientId = '{clientId}'");
        return new Conn(clientId, t.Body!.Str("access_token")!, t.Body!.Str("scope")!, grant);
    }

    private async Task<Res> RpcAsync(string token, string method, object? @params = null, ErpHost.Factory? f = null, CancellationToken ct = default)
    {
        using var api = f?.Anonymous(false) ?? host.Anonymous(false);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = @params ?? new { } }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await api.Http.SendAsync(req, ct);
        return new Res((int)res.StatusCode, await res.Content.ReadAsStringAsync(), new Dictionary<string, string>());
    }

    private async Task<JsonNode> CallAsync(string token, string tool, object? args = null, ErpHost.Factory? f = null)
    {
        var res = await RpcAsync(token, "tools/call", new { name = tool, arguments = args ?? new { } }, f);
        Assert.True(res.Status == 200 && res["result"] is not null, res.Text);
        return res["result"]!;
    }

    private static JsonNode Ok(JsonNode result)
    {
        Assert.False(result["isError"]!.GetValue<bool>(), result.Json());
        return result["structuredContent"]!;
    }

    private static JsonNode ErrorOf(JsonNode result)
    {
        Assert.True(result["isError"]!.GetValue<bool>(), result.Json());
        return JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
    }

    private async Task<string[]> ToolsAsync(string token) =>
        (await RpcAsync(token, "tools/list"))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order().ToArray();

    private McpSandboxes Sandboxes => host.Services.GetRequiredService<McpSandboxes>();

    private Task<string?> DbOfAsync(int grant) => host.ScalarAsync<string?>($"SELECT DbName FROM meta.McpSandbox WHERE GrantId = {grant}");

    private Task<bool> ExistsAsync(string db) => host.ScalarAsync<int?>($"SELECT DB_ID(N'{db}')").ContinueWith(t => t.Result is not null);

    private static object Plan(string entity) => new
    {
        description = "Spec 044", operations = new object[] {
            new { op = "createEntity", group = "Pruebas 044", name = entity, label = entity, labelPlural = entity, displayField = "Nombre", fields = new object[] {
                new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
        },
    };

    private static string Key() => "k-" + Guid.NewGuid().ToString("N");

    // ---------- H1 ----------

    [Fact]
    public async Task H1_CA2_la_pantalla_ofrece_aislado_y_proponer_solo_a_quien_modela()
    {
        using var api = host.Anonymous(false);
        var clientId = await RegisterAsync(api);
        var query = Query(clientId, McpFormat.New("v"), "modelo aislado");
        using var ana = await host.LoginAsync("m2-ana", Pwd);
        var scopes = (await ana.GetAsync("/api/mcp/authorize?" + query)).Body!["scopes"]!.AsArray().ToDictionary(s => s!.Str("name")!);
        Assert.Equal(["modelo", "datos", "aislado", "proponer"], scopes.Keys);
        Assert.True(scopes["aislado"]!["available"]!.GetValue<bool>());
        Assert.True(scopes["aislado"]!["requested"]!.GetValue<bool>());
        Assert.False(scopes["proponer"]!["requested"]!.GetValue<bool>());
        using var lector = await host.LoginAsync("m2-lector", Pwd);
        var other = (await lector.GetAsync("/api/mcp/authorize?" + query)).Body!["scopes"]!.AsArray().ToDictionary(s => s!.Str("name")!);
        Assert.False(other["aislado"]!["available"]!.GetValue<bool>());
        Assert.Equal("Solo para quien modela.", other["proponer"]!.Str("reason"));
    }

    [Fact]
    public async Task H1_CA3_quien_no_modela_no_recibe_aislado_ni_proponer()
    {
        // Aunque el cliente los pida y el POST los traiga marcados.
        var lector = await ConnectAsync("m2-lector");
        Assert.Equal("modelo datos", lector.Scope);
        Assert.DoesNotContain(await ToolsAsync(lector.Access), t => t.StartsWith("aislado_") || t == "entregar");
        // Y lo pedido manda: sin «proponer» en la petición no se concede aunque se marque.
        var ana = await ConnectAsync("m2-ana", "modelo aislado", chosen: ["modelo", "aislado", "proponer"]);
        Assert.Equal("modelo aislado", ana.Scope);
    }

    [Fact]
    public async Task H1_CA3_sin_copia_en_el_servidor_no_se_concede_aislado()
    {
        var f = await host.SiblingAsync(s => s["Studio:Sandbox"] = "false");
        var conn = await ConnectAsync("m2-kim", f: f);
        Assert.Equal("modelo datos proponer", conn.Scope);
    }

    // ---------- H2 ----------

    [Fact]
    public async Task H2_CA1_con_aislado_tools_list_da_las_de_la_copia_y_sin_el_no()
    {
        var full = await ConnectAsync("m2-beto");
        var tools = await ToolsAsync(full.Access);
        foreach (var t in new[] { "aislado_aplicar_plan", "aislado_crear", "aislado_actualizar", "aislado_transicion", "aislado_leer", "aislado_listar",
                     "aislado_procesar_asincronas", "aislado_descartar", "entregar" })
            Assert.Contains(t, tools);
        var list = (await RpcAsync(full.Access, "tools/list"))["result"]!["tools"]!.AsArray().ToDictionary(t => t!.Str("name")!);
        var crear = list["aislado_crear"]!;
        Assert.False(crear["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.False(crear["annotations"]!["idempotentHint"]!.GetValue<bool>());
        Assert.Contains("idempotencyKey", crear["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(list["aislado_leer"]!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        var read = await ConnectAsync("m2-beto", "modelo datos");
        Assert.DoesNotContain(await ToolsAsync(read.Access), t => t.StartsWith("aislado_") || t == "entregar");
        await Sandboxes.ReleaseAsync(full.Grant, host.Users["m2-beto"], "prueba");
    }

    [Fact]
    public async Task H2_CA1_la_copia_no_toca_la_base_real()
    {
        var c = await ConnectAsync("m2-carla");
        var applied = Ok(await CallAsync(c.Access, "aislado_aplicar_plan", new { plan = Plan("M2SoloCopia"), idempotencyKey = Key() }));
        Assert.True(applied["applied"]!.GetValue<bool>());
        var created = Ok(await CallAsync(c.Access, "aislado_crear", new { entity = "M2SoloCopia", values = new { Nombre = "De prueba" }, idempotencyKey = Key() }));
        Assert.True(created.Id() > 0);
        var listed = Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2SoloCopia" }));
        Assert.Single(listed["items"]!.AsArray());
        // La base real no tiene el catálogo; la copia es una base aparte con el nombre de la conexión.
        using var admin = await host.AdminAsync();
        Assert.DoesNotContain("M2SoloCopia", (await admin.GetAsync("/api/model/entities")).Array.Select(e => e!.Str("name")));
        var db = await DbOfAsync(c.Grant);
        var id = await host.ScalarAsync<int>($"SELECT Id FROM meta.McpSandbox WHERE GrantId = {c.Grant}");
        Assert.Equal(SandboxFactory.McpNameFor(host.Database, id), db);
        Assert.True(await ExistsAsync(db!));
        Assert.NotEqual(host.Database, db);
        // La segunda llamada reutiliza la copia (misma fila, misma base, el catálogo sigue ahí).
        Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2SoloCopia" }));
        Assert.Equal(id, await host.ScalarAsync<int>($"SELECT Id FROM meta.McpSandbox WHERE GrantId = {c.Grant}"));
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.AuditLog WHERE Action = 'mcp.sandbox' AND RecordId = '{c.Grant}' AND AfterJson LIKE '%created%'"));
        await Sandboxes.ReleaseAsync(c.Grant, host.Users["m2-carla"], "prueba");
        Assert.False(await ExistsAsync(db!));
    }

    [Fact]
    public async Task H2_CA2_diez_primeras_llamadas_a_la_vez_dejan_una_copia()
    {
        var c = await ConnectAsync("m2-dora");
        var results = await Concurrently(10, _ => CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }));
        foreach (var r in results.Where(r => r["isError"]!.GetValue<bool>()))
        {
            var e = ErrorOf(r);
            Assert.Equal(409, e["status"]!.GetValue<int>());
            Assert.True(e["retryAfterSeconds"]!.GetValue<int>() >= 1);
        }
        Assert.Contains(results, r => !r["isError"]!.GetValue<bool>());
        Invariant.ExactlyOnce(await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpSandbox WHERE UserId = {host.Users["m2-dora"]}"), "copias de la conexión");
        var prefix = SandboxFactory.Prefix(host.Database) + "M";
        var dbs = await host.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.databases WHERE LEFT(name, {prefix.Length}) = N'{prefix}' AND name = (SELECT DbName FROM meta.McpSandbox WHERE GrantId = {c.Grant})");
        Assert.Equal(1, dbs);
        await Sandboxes.ReleaseAsync(c.Grant, host.Users["m2-dora"], "prueba");
    }

    [Fact]
    public async Task H2_CA3_inactividad_desconectar_y_descartar_borran_la_copia()
    {
        var eli = host.Users["m2-eli"];
        // Inactividad: sin lease vigente y sin uso en SandboxIdleMinutes.
        var a = await ConnectAsync("m2-eli");
        Ok(await CallAsync(a.Access, "aislado_listar", new { entity = "M2Nota" }));
        var dbA = (await DbOfAsync(a.Grant))!;
        await host.ExecAsync($"UPDATE meta.McpSandbox SET LastUsedAt = DATEADD(hour, -2, SYSUTCDATETIME()), LeaseUntil = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE GrantId = {a.Grant}");
        await Sandboxes.SweepAsync(CancellationToken.None);
        Assert.Null(await DbOfAsync(a.Grant));
        Assert.False(await ExistsAsync(dbA));
        // Descartar con la herramienta: la conexión sigue y la siguiente prepara otra.
        Ok(await CallAsync(a.Access, "aislado_listar", new { entity = "M2Nota" }));
        Assert.True(Ok(await CallAsync(a.Access, "aislado_descartar"))["discarded"]!.GetValue<bool>());
        Assert.False(Ok(await CallAsync(a.Access, "aislado_descartar"))["discarded"]!.GetValue<bool>());
        // Desconectar desde la pantalla: en el acto, sin esperar al barrido.
        Ok(await CallAsync(a.Access, "aislado_listar", new { entity = "M2Nota" }));
        var dbB = (await DbOfAsync(a.Grant))!;
        Assert.NotEqual(dbA, dbB);
        using var person = await host.LoginAsync("m2-eli", Pwd);
        Assert.Equal(204, (await person.DeleteAsync($"/api/mcp/connections/{a.Grant}")).Status);
        Assert.Null(await DbOfAsync(a.Grant));
        Assert.False(await ExistsAsync(dbB));
        Assert.True(await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.AuditLog WHERE Action = 'mcp.sandbox' AND UserId = {eli} AND AfterJson LIKE '%dropped%'") >= 3);
    }

    [Fact]
    public async Task H2_CA3_al_cambiar_el_sello_o_morir_la_conexion_el_barrido_borra_la_copia()
    {
        var fer = host.Users["m2-fer"];
        var c = await ConnectAsync("m2-fer");
        Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }));
        var db = (await DbOfAsync(c.Grant))!;
        // Le cambian la contraseña (como al quitarle roles, cambia su sello): el token muere y el barrido borra la copia aunque tenga lease.
        using var admin = await host.AdminAsync();
        Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{fer}", new { displayName = "m2-fer", isActive = true, password = Pwd + " nueva",
            roleIds = new[] { await admin.RoleIdAsync("M2 Modelador") } })).Status);
        Assert.Equal(401, (await RpcAsync(c.Access, "tools/list")).Status);
        await Sandboxes.SweepAsync(CancellationToken.None);
        Assert.Null(await DbOfAsync(c.Grant));
        Assert.False(await ExistsAsync(db));
        // Una conexión revocada por otro camino (reuso, RFC 7009) también se va en el barrido.
        Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{fer}", new { displayName = "m2-fer", isActive = true, password = Pwd,
            roleIds = new[] { await admin.RoleIdAsync("M2 Modelador") } })).Status);
        var d = await ConnectAsync("m2-fer");
        Ok(await CallAsync(d.Access, "aislado_listar", new { entity = "M2Nota" }));
        await host.ExecAsync($"UPDATE meta.McpGrant SET RevokedAt = SYSUTCDATETIME(), RevokedReason = 'reuse' WHERE Id = {d.Grant}");
        await Sandboxes.SweepAsync(CancellationToken.None);
        Assert.Null(await DbOfAsync(d.Grant));
    }

    [Fact]
    public async Task H2_CA3_el_barrido_respeta_el_lease_de_una_llamada_en_curso()
    {
        var gil = host.Users["m2-gil"];
        var c = await ConnectAsync("m2-gil");
        var stamp = await host.ScalarAsync<Guid>($"SELECT SecurityStamp FROM meta.[User] WHERE Id = {gil}");
        var user = (await host.Services.GetRequiredService<UserContextLoader>().LoadAsync(gil, stamp))!;
        await using (var lease = await Sandboxes.AcquireAsync(c.Grant, user, CancellationToken.None))
        {
            // Una llamada larga: pasa más que el lease inicial (6 s) y la renovación lo mantiene vigente.
            await Task.Delay(TimeSpan.FromSeconds(8));
            Assert.True(await host.ScalarAsync<int>($"SELECT CASE WHEN LeaseUntil > SYSUTCDATETIME() THEN 1 ELSE 0 END FROM meta.McpSandbox WHERE Id = {lease.Id}") == 1);
            // Aunque su último uso sea viejo, el barrido no la toca mientras el lease está vigente.
            await host.ExecAsync($"UPDATE meta.McpSandbox SET LastUsedAt = DATEADD(hour, -2, SYSUTCDATETIME()) WHERE Id = {lease.Id}");
            await Sandboxes.SweepAsync(CancellationToken.None);
            Assert.NotNull(await DbOfAsync(c.Grant));
        }
        // Al soltarse anota el último uso: sigue sin ser inactiva.
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT CASE WHEN LastUsedAt > DATEADD(minute, -1, SYSUTCDATETIME()) THEN 1 ELSE 0 END FROM meta.McpSandbox WHERE GrantId = {c.Grant}"));
        await Sandboxes.ReleaseAsync(c.Grant, gil, "prueba");
    }

    [Fact]
    public async Task H2_CA3_una_preparacion_a_medias_y_una_base_huerfana_se_barren()
    {
        var hugo = host.Users["m2-hugo"];
        var c = await ConnectAsync("m2-hugo");
        // Una preparación que cayó: fila «creating» vencida y su base a medio crear.
        var id = await host.ScalarAsync<int>($"""
            INSERT meta.McpSandbox (GrantId, UserId, DbName, Token, Status, LastUsedAt, LeaseUntil)
            OUTPUT inserted.Id VALUES ({c.Grant}, {hugo}, N'', NEWID(), 'creating', SYSUTCDATETIME(), DATEADD(minute, -1, SYSUTCDATETIME()))
            """);
        var stuck = SandboxFactory.McpNameFor(host.Database, id);
        await host.ExecAsync($"UPDATE meta.McpSandbox SET DbName = N'{stuck}' WHERE Id = {id}");
        var orphan = SandboxFactory.McpNameFor(host.Database, 900_000 + id);
        await host.ExecAsync($"USE master; CREATE DATABASE [{stuck}]; CREATE DATABASE [{orphan}];");
        await Sandboxes.SweepAsync(CancellationToken.None);
        Assert.Null(await DbOfAsync(c.Grant));
        Assert.False(await ExistsAsync(stuck));
        Assert.False(await ExistsAsync(orphan));
        // Con la fila a medias y el bloqueo libre, la siguiente llamada la limpia y prepara una buena.
        await host.ExecAsync($"""
            INSERT meta.McpSandbox (GrantId, UserId, DbName, Token, Status, LastUsedAt, LeaseUntil)
            VALUES ({c.Grant}, {hugo}, N'', NEWID(), 'creating', SYSUTCDATETIME(), DATEADD(minute, 10, SYSUTCDATETIME()))
            """);
        Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }));
        Assert.Equal("ready", await host.ScalarAsync<string>($"SELECT Status FROM meta.McpSandbox WHERE GrantId = {c.Grant}"));
        await Sandboxes.ReleaseAsync(c.Grant, hugo, "prueba");
    }

    [Fact]
    public async Task H2_CA4_los_topes_de_copias_se_cumplen_a_la_vez()
    {
        var ines = host.Users["m2-ines"];
        var conns = new List<Conn>();
        for (var i = 0; i < 3; i++) conns.Add(await ConnectAsync("m2-ines", name: $"Claude {i}"));
        var results = await Concurrently(3, i => CallAsync(conns[i].Access, "aislado_listar", new { entity = "M2Nota" }));
        Assert.Equal(2, results.Count(r => !r["isError"]!.GetValue<bool>()));
        var refused = ErrorOf(results.Single(r => r["isError"]!.GetValue<bool>()));
        Assert.Equal(429, refused["status"]!.GetValue<int>());
        Assert.Contains("máximo", refused["error"]!.GetValue<string>());
        Assert.Equal(2, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpSandbox WHERE UserId = {ines}"));
        // H3 CA3: un error que no ejecutó nada no consume la clave.
        var blocked = conns.Single(c => host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpSandbox WHERE GrantId = {c.Grant}").Result == 0);
        var key = Key();
        Assert.Equal(429, ErrorOf(await CallAsync(blocked.Access, "aislado_crear", new { entity = "M2Nota", values = new { Nombre = "x" }, idempotencyKey = key }))["status"]!.GetValue<int>());
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpWrite WHERE GrantId = {blocked.Grant}"));
        // Tope del entorno: otra instancia con MaxSandboxes = 1 no deja preparar una más.
        var f = await host.SiblingAsync(s => s["Mcp:MaxSandboxes"] = "1");
        var juan = await ConnectAsync("m2-juan", f: f);
        var full = ErrorOf(await CallAsync(juan.Access, "aislado_listar", new { entity = "M2Nota" }, f));
        Assert.Equal(429, full["status"]!.GetValue<int>());
        Assert.Contains("servidor", full["error"]!.GetValue<string>());
        foreach (var c in conns) await Sandboxes.ReleaseAsync(c.Grant, ines, "prueba");
        // Liberada una, la clave que no se consumió ejecuta.
        Assert.True(Ok(await CallAsync(blocked.Access, "aislado_crear", new { entity = "M2Nota", values = new { Nombre = "x" }, idempotencyKey = key })).Id() > 0);
        await Sandboxes.ReleaseAsync(blocked.Grant, ines, "prueba");
    }

    // ---------- H3 ----------

    [Fact]
    public async Task H3_CA1_sin_clave_o_con_una_mal_formada_es_error()
    {
        var c = await ConnectAsync("m2-ana", "modelo aislado proponer");
        foreach (var key in new string?[] { null, "corta", "con espacio 1234" })
        {
            var e = ErrorOf(await CallAsync(c.Access, "aislado_crear", key is null
                ? new { entity = "M2Nota", values = new { Nombre = "x" } }
                : (object)new { entity = "M2Nota", values = new { Nombre = "x" }, idempotencyKey = key }));
            Assert.Equal(400, e["status"]!.GetValue<int>());
            Assert.Contains("idempotencyKey", e["error"]!.GetValue<string>());
            Assert.Contains("idempotencyKey", ErrorOf(await CallAsync(c.Access, "entregar", new { summary = "x" }))["error"]!.GetValue<string>());
        }
        // Nada se preparó ni se registró.
        Assert.Null(await DbOfAsync(c.Grant));
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpWrite WHERE GrantId = {c.Grant}"));
    }

    [Fact]
    public async Task H3_CA2_la_misma_clave_repite_el_resultado_sin_volver_a_ejecutar()
    {
        var lia = host.Users["m2-lia"];
        var c = await ConnectAsync("m2-lia");
        var key = Key();
        var first = Ok(await CallAsync(c.Access, "aislado_crear", new { entity = "M2Nota", values = new { Nombre = "Una sola" }, idempotencyKey = key }));
        // El mismo contenido con las propiedades en otro orden es la misma llamada.
        var again = Ok(await CallAsync(c.Access, "aislado_crear", new { values = new { Nombre = "Una sola" }, idempotencyKey = key, entity = "M2Nota" }));
        Assert.Equal(first.Json(), again.Json());
        Assert.Single(Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }))["items"]!.AsArray());
        var conflict = ErrorOf(await CallAsync(c.Access, "aislado_crear", new { entity = "M2Nota", values = new { Nombre = "Otra" }, idempotencyKey = key }));
        Assert.Equal(409, conflict["status"]!.GetValue<int>());
        Assert.Equal(409, ErrorOf(await CallAsync(c.Access, "aislado_actualizar", new { entity = "M2Nota", id = first.Id(), values = new { Nombre = "Una sola" }, idempotencyKey = key }))["status"]!.GetValue<int>());
        // Un error de la ruta (validación) sí ejecutó: consume la clave y se repite igual.
        var bad = Key();
        var invalid = ErrorOf(await CallAsync(c.Access, "aislado_crear", new { entity = "M2Nota", values = new { }, idempotencyKey = bad }));
        Assert.Equal(400, invalid["status"]!.GetValue<int>());
        Assert.Equal("done", await host.ScalarAsync<string>($"SELECT Status FROM meta.McpWrite WHERE GrantId = {c.Grant} AND IdemKey = '{bad}'"));
        Assert.Equal(invalid.Json(), ErrorOf(await CallAsync(c.Access, "aislado_crear", new { entity = "M2Nota", values = new { }, idempotencyKey = bad })).Json());
        // Al borrarse la copia se van sus claves.
        await Sandboxes.ReleaseAsync(c.Grant, lia, "prueba");
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpWrite WHERE GrantId = {c.Grant}"));
    }

    [Fact]
    public async Task H3_CA2_diez_llamadas_con_la_misma_clave_dan_un_efecto()
    {
        var c = await ConnectAsync("m2-beto", "modelo aislado");
        Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" })); // la copia ya está
        var key = Key();
        var results = await Concurrently(10, _ => CallAsync(c.Access, "aislado_crear", new { entity = "M2Nota", values = new { Nombre = "Diez a la vez" }, idempotencyKey = key }));
        var ok = results.Where(r => !r["isError"]!.GetValue<bool>()).Select(r => r["structuredContent"]!.Id()).Distinct().ToList();
        Assert.Single(ok);
        Assert.All(results.Where(r => r["isError"]!.GetValue<bool>()), r => Assert.Equal(409, ErrorOf(r)["status"]!.GetValue<int>()));
        var items = Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota", search = "Diez a la vez" }))["items"]!.AsArray();
        Invariant.ExactlyOnce(items.Count, "registros creados con la misma clave");
        await Sandboxes.ReleaseAsync(c.Grant, host.Users["m2-beto"], "prueba");
    }

    [Fact]
    public async Task H3_CA2_la_clave_es_de_la_conexion_y_una_pendiente_vencida_se_puede_reintentar()
    {
        var carla = host.Users["m2-carla"];
        var a = await ConnectAsync("m2-carla", "modelo aislado", name: "Claude A");
        var b = await ConnectAsync("m2-carla", "modelo aislado", name: "Claude B");
        var key = Key();
        object Args(string n) => new { entity = "M2Nota", values = new { Nombre = n }, idempotencyKey = key };
        var ra = Ok(await CallAsync(a.Access, "aislado_crear", Args("Clave compartida")));
        var rb = Ok(await CallAsync(b.Access, "aislado_crear", Args("Clave compartida")));
        Assert.NotEqual(await DbOfAsync(a.Grant), await DbOfAsync(b.Grant));
        Assert.True(ra.Id() > 0 && rb.Id() > 0); // cada conexión ejecuta la suya, en su copia
        // Una clave «en curso» de un intento que cayó: mientras su lease vive, 409; al vencer, se reintenta y ejecuta.
        var pending = Key();
        var hash = Convert.ToHexString(McpIdempotency.Hash("aislado_crear", (JsonObject)JsonNode.Parse("""{"entity":"M2Nota","values":{"Nombre":"Pendiente"}}""")!));
        await host.ExecAsync($"""
            INSERT meta.McpWrite (GrantId, IdemKey, Tool, InputHash, Status, Attempt, PendingUntil)
            VALUES ({a.Grant}, '{pending}', N'aislado_crear', 0x{hash}, 'pending', NEWID(), DATEADD(minute, 5, SYSUTCDATETIME()))
            """);
        var args = new { entity = "M2Nota", values = new { Nombre = "Pendiente" }, idempotencyKey = pending };
        var busy = ErrorOf(await CallAsync(a.Access, "aislado_crear", args));
        Assert.Equal(409, busy["status"]!.GetValue<int>());
        Assert.True(busy["retryAfterSeconds"]!.GetValue<int>() >= 1);
        await host.ExecAsync($"UPDATE meta.McpWrite SET PendingUntil = DATEADD(second, -1, SYSUTCDATETIME()) WHERE GrantId = {a.Grant} AND IdemKey = '{pending}'");
        Assert.True(Ok(await CallAsync(a.Access, "aislado_crear", args)).Id() > 0);
        Assert.Equal("done", await host.ScalarAsync<string>($"SELECT Status FROM meta.McpWrite WHERE GrantId = {a.Grant} AND IdemKey = '{pending}'"));
        await Sandboxes.ReleaseAsync(a.Grant, carla, "prueba");
        await Sandboxes.ReleaseAsync(b.Grant, carla, "prueba");
    }

    // ---------- H4 y H5 ----------

    private async Task<JsonNode> SessionAsync(Client person, int id)
    {
        var res = await person.GetAsync($"/api/studio/sessions/{id}");
        Assert.True(res.Status == 200, res.Text);
        return res.Body!;
    }

    [Fact]
    public async Task H4_CA1_entregar_crea_una_propuesta_en_Studio_y_H5_se_revisa_y_aplica()
    {
        var c = await ConnectAsync("m2-dora", "modelo proponer");
        var result = Ok(await CallAsync(c.Access, "entregar", new
        {
            summary = "Agrego el catálogo M2Propuesta.", plan = Plan("M2Propuesta"), tested = new[] { new { description = "Compila", ok = true } },
            risks = new[] { "Ninguno" }, idempotencyKey = Key(),
        }));
        var id = result["proposalId"]!.GetValue<int>();
        Assert.Equal("proposed", result.Str("status"));
        Assert.Equal($"https://localhost/admin/studio?s={id}", result.Str("url"));
        using var dora = await host.LoginAsync("m2-dora", Pwd);
        var s = await SessionAsync(dora, id);
        Assert.Equal("mcp", s.Str("origin"));
        Assert.Equal("Claude", s.Str("client"));
        Assert.Equal("proposed", s.Str("planStatus"));
        Assert.False(s["canSend"]!.GetValue<bool>());
        Assert.Contains("llegó por MCP", s.Str("sendBlockedReason"));
        Assert.NotNull(s.Str("reviewHash"));
        Assert.Equal("Compila", s["report"]!["tested"]![0]!.Str("description"));
        Assert.Contains((await dora.GetAsync("/api/studio/sessions")).Array, x => x!["id"]!.GetValue<int>() == id && x.Str("origin") == "mcp");
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.AuditLog WHERE Action = 'mcp.proposal' AND RecordId = '{id}'"));
        // H5 CA2: otra persona no la ve ni la aplica; sin huella no se aplica; con ella, sí.
        using var beto = await host.LoginAsync("m2-beto", Pwd);
        Assert.Equal(404, (await beto.GetAsync($"/api/studio/sessions/{id}")).Status);
        Assert.Equal(404, (await beto.PostAsync($"/api/studio/sessions/{id}/apply", new { reviewHash = s.Str("reviewHash") })).Status);
        Assert.Equal(400, (await dora.PostAsync($"/api/studio/sessions/{id}/apply", new { })).Status);
        var applied = await dora.PostAsync($"/api/studio/sessions/{id}/apply", new { reviewHash = s.Str("reviewHash") });
        Assert.True(applied.Status == 200, applied.Text);
        using var admin = await host.AdminAsync();
        Assert.Contains("M2Propuesta", (await admin.GetAsync("/api/model/entities")).Array.Select(e => e!.Str("name")));
        // H5 CA3: no se le mandan mensajes (la API lo dice, sin clave de Anthropic en este anfitrión).
        var msg = await dora.PostAsync($"/api/studio/sessions/{id}/messages", new { text = "¿Y ahora?" });
        Assert.Equal(409, msg.Status);
        Assert.Contains("llegó por MCP", msg.Text);
        // Las propuestas por MCP no cuentan en los topes de Studio.
        Assert.Equal(0, (await dora.GetAsync("/api/studio")).Body!["sessionsToday"]!.GetValue<int>());
    }

    [Fact]
    public async Task H4_CA1_un_plan_que_no_compila_no_guarda_nada_y_sin_plan_es_una_respuesta()
    {
        var eli = host.Users["m2-eli"];
        var c = await ConnectAsync("m2-eli", "modelo proponer");
        var bad = ErrorOf(await CallAsync(c.Access, "entregar", new
        {
            summary = "Malo", plan = new { operations = new object[] { new { op = "addField", entity = "NoExiste", field = new { name = "X", label = "X", type = "text" } } } },
            idempotencyKey = Key(),
        }));
        Assert.Equal(400, bad["status"]!.GetValue<int>());
        Assert.Contains("no compila", bad["error"]!.GetValue<string>());
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioSession WHERE UserId = {eli}"));
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpWrite WHERE GrantId = {c.Grant}"));
        var answer = Ok(await CallAsync(c.Access, "entregar", new { summary = "No hace falta cambiar nada.", idempotencyKey = Key() }));
        Assert.Equal("answered", answer.Str("status"));
        using var person = await host.LoginAsync("m2-eli", Pwd);
        Assert.Null((await SessionAsync(person, answer["proposalId"]!.GetValue<int>())).Str("planStatus"));
    }

    [Fact]
    public async Task H4_CA2_diez_entregas_con_la_misma_clave_dan_una_propuesta()
    {
        var gil = host.Users["m2-gil"];
        var c = await ConnectAsync("m2-gil", "modelo proponer");
        var key = Key();
        var results = await Concurrently(10, _ => CallAsync(c.Access, "entregar", new { summary = "Diez a la vez", plan = Plan("M2Diez"), idempotencyKey = key }));
        Assert.Single(results.Select(r => Ok(r)["proposalId"]!.GetValue<int>()).Distinct());
        Invariant.ExactlyOnce(await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioSession WHERE UserId = {gil} AND Origin = 'mcp'"), "propuestas con la misma clave");
        // La misma clave con otra entrada: 409.
        Assert.Equal(409, ErrorOf(await CallAsync(c.Access, "entregar", new { summary = "Otra cosa", idempotencyKey = key }))["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task H4_CA2_una_falla_al_guardar_la_propuesta_no_consume_la_clave()
    {
        var hugo = host.Users["m2-hugo"];
        var c = await ConnectAsync("m2-hugo", "modelo proponer");
        var key = Key();
        var args = new { summary = "Con falla", plan = Plan("M2Falla"), idempotencyKey = key };
        await using (await host.FailWhenAsync("meta.AuditLog", "Action = 'mcp.proposal'"))
        {
            var res = await RpcAsync(c.Access, "tools/call", new { name = "entregar", arguments = args });
            Assert.True(res.Status >= 400 || res["result"]?["isError"]?.GetValue<bool>() == true || res["error"] is not null, res.Text);
        }
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.StudioSession WHERE UserId = {hugo}"));
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpWrite WHERE GrantId = {c.Grant}"));
        Assert.Equal("proposed", Ok(await CallAsync(c.Access, "entregar", args)).Str("status"));
    }

    [Fact]
    public async Task H4_CA3_el_tope_de_propuestas_por_dia()
    {
        var f = await host.SiblingAsync(s => s["Mcp:ProposalsPerDay"] = "2");
        var c = await ConnectAsync("m2-kim", "modelo proponer", f: f);
        for (var i = 0; i < 2; i++) Ok(await CallAsync(c.Access, "entregar", new { summary = $"Respuesta {i}", idempotencyKey = Key() }, f));
        var limit = ErrorOf(await CallAsync(c.Access, "entregar", new { summary = "Una más", idempotencyKey = Key() }, f));
        Assert.Equal(429, limit["status"]!.GetValue<int>());
        Assert.Contains("tope", limit["error"]!.GetValue<string>());
    }

    // ---------- H6 ----------

    [Fact]
    public async Task H6_CA1_CA2_las_conexiones_muestran_la_copia_y_solo_su_dueña_la_descarta()
    {
        var c = await ConnectAsync("m2-juan", "modelo aislado");
        using var juan = await host.LoginAsync("m2-juan", Pwd);
        JsonNode Mine(Res r) => r.Body!["connections"]!.AsArray().Single(x => x!["id"]!.GetValue<int>() == c.Grant)!;
        Assert.Null(Mine(await juan.GetAsync("/api/mcp/connections"))["sandbox"]);
        Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }));
        var mine = Mine(await juan.GetAsync("/api/mcp/connections"));
        Assert.Equal(["modelo", "aislado"], mine["scopes"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("ready", mine["sandbox"]!.Str("status"));
        Assert.NotNull(mine["sandbox"]!.Str("lastUsedAt"));
        var first = await DbOfAsync(c.Grant);
        using var lia = await host.LoginAsync("m2-lia", Pwd);
        Assert.Equal(404, (await lia.DeleteAsync($"/api/mcp/connections/{c.Grant}/sandbox")).Status);
        Assert.NotNull(await DbOfAsync(c.Grant));
        Assert.Equal(204, (await juan.DeleteAsync($"/api/mcp/connections/{c.Grant}/sandbox")).Status);
        Assert.Equal(404, (await juan.DeleteAsync($"/api/mcp/connections/{c.Grant}/sandbox")).Status);
        Assert.False(await ExistsAsync(first!));
        // La conexión sigue: la siguiente herramienta prepara otra copia.
        Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }));
        Assert.NotEqual(first, await DbOfAsync(c.Grant));
        await Sandboxes.ReleaseAsync(c.Grant, host.Users["m2-juan"], "prueba");
    }

    // ---------- Revisión adversarial 044 ----------

    private const string Busy = "SELECT TOP 1 1 FROM [{0}].[app].[M2Nota] WITH (TABLOCKX, HOLDLOCK)";

    // Regresión (revisión adversarial 044, A-01): la clave «en curso» tenía un lease fijo; una llamada más larga que él dejaba que
    // un reintento con la misma clave ejecutara otra vez. Y si el cliente se desconectaba, el efecto quedaba sin marca.
    [Fact]
    public async Task Regresion_A01_una_escritura_larga_conserva_su_clave_y_termina_aunque_el_cliente_se_vaya()
    {
        var f = await host.SiblingAsync(s => s["Mcp:WriteLeaseSeconds"] = "5");
        var c = await ConnectAsync("m2-mar", "modelo aislado", f: f);
        Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }, f));
        var db = (await DbOfAsync(c.Grant))!;
        var key = Key();
        object Args(string n) => new { entity = "M2Nota", values = new { Nombre = n }, idempotencyKey = key };
        Task<JsonNode> first;
        await using (var hold = await host.HoldAsync(string.Format(Busy, db)))
        {
            first = CallAsync(c.Access, "aislado_crear", Args("Larga"), f);
            await hold.WaitUntilBlockingAsync();
            await Task.Delay(TimeSpan.FromSeconds(8)); // más que el lease de la clave (5 s)
            var retry = ErrorOf(await CallAsync(c.Access, "aislado_crear", Args("Larga"), f));
            Assert.Equal(409, retry["status"]!.GetValue<int>());
        }
        var id = Ok(await first).Id();
        Assert.Single(Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota", search = "Larga" }, f))["items"]!.AsArray());

        // El cliente se desconecta a mitad: la escritura termina y su resultado queda con la clave (el reintento lo repite).
        var other = Key();
        using var cts = new CancellationTokenSource();
        Task<Res> gone;
        await using (var hold = await host.HoldAsync(string.Format(Busy, db)))
        {
            gone = RpcAsync(c.Access, "tools/call", new { name = "aislado_crear", arguments = new { entity = "M2Nota", values = new { Nombre = "Sin cliente" }, idempotencyKey = other } }, f, cts.Token);
            await hold.WaitUntilBlockingAsync();
            cts.Cancel();
        }
        try { await gone; } catch (OperationCanceledException) { /* el cliente ya no espera la respuesta */ }
        var done = await WaitForAsync(() => host.ScalarAsync<string?>($"SELECT Status FROM meta.McpWrite WHERE GrantId = {c.Grant} AND IdemKey = '{other}'"),
            st => st == "done", "la escritura sin cliente termina con su clave");
        Assert.Equal("done", done);
        var replay = Ok(await CallAsync(c.Access, "aislado_crear", new { entity = "M2Nota", values = new { Nombre = "Sin cliente" }, idempotencyKey = other }, f));
        Assert.Single(Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota", search = "Sin cliente" }, f))["items"]!.AsArray());
        Assert.True(replay.Id() > id);
        await Sandboxes.ReleaseAsync(c.Grant, host.Users["m2-mar"], "prueba");
    }

    // Regresión (revisión adversarial 044, A-02): descartar y volver a preparar en bucle creaba bases sin tope.
    [Fact]
    public async Task Regresion_A02_preparar_copias_tiene_tope_por_hora()
    {
        var f = await host.SiblingAsync(s => s["Mcp:SandboxCreationsPerHour"] = "2");
        var c = await ConnectAsync("m2-nico", "modelo aislado", f: f);
        for (var i = 0; i < 2; i++)
        {
            Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }, f));
            Assert.True(Ok(await CallAsync(c.Access, "aislado_descartar", null, f))["discarded"]!.GetValue<bool>());
        }
        var limit = ErrorOf(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }, f));
        Assert.Equal(429, limit["status"]!.GetValue<int>());
        Assert.Contains("última hora", limit["error"]!.GetValue<string>());
        Assert.Null(await DbOfAsync(c.Grant));
    }

    // Regresión (revisión adversarial 044, A-03): la clave se comparaba sin distinguir mayúsculas.
    [Fact]
    public async Task Regresion_A03_claves_que_solo_difieren_en_mayusculas_son_distintas()
    {
        var c = await ConnectAsync("m2-olga", "modelo aislado");
        var a = Ok(await CallAsync(c.Access, "aislado_crear", new { entity = "M2Nota", values = new { Nombre = "Mayúsculas" }, idempotencyKey = "Clave-Mayus-0001" }));
        var b = Ok(await CallAsync(c.Access, "aislado_crear", new { entity = "M2Nota", values = new { Nombre = "Mayúsculas" }, idempotencyKey = "clave-mayus-0001" }));
        Assert.NotEqual(a.Id(), b.Id());
        await Sandboxes.ReleaseAsync(c.Grant, host.Users["m2-olga"], "prueba");
    }

    // Regresión (revisión adversarial 044, A-04): un error al abrir la copia (la base existe pero no responde) se tomaba como
    // copia perdida y se borraba con sus claves. Otra instancia (sin la copia abierta en memoria) la reabre.
    [Fact]
    public async Task Regresion_A04_una_copia_que_no_responde_no_se_borra()
    {
        var c = await ConnectAsync("m2-pau", "modelo aislado");
        Ok(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }));
        var db = (await DbOfAsync(c.Grant))!;
        var f = await host.SiblingAsync(_ => { });
        await host.ExecAsync($"USE master; ALTER DATABASE [{db}] SET OFFLINE WITH ROLLBACK IMMEDIATE;");
        try
        {
            var e = ErrorOf(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }, f));
            Assert.Equal(503, e["status"]!.GetValue<int>());
            Assert.Equal(db, await DbOfAsync(c.Grant));
        }
        finally { await host.ExecAsync($"USE master; ALTER DATABASE [{db}] SET ONLINE;"); }
        // En línea otra vez, la misma copia vuelve a servir (las conexiones rotas del grupo tardan un momento en reponerse).
        await WaitForAsync(() => CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }, f), r => !r["isError"]!.GetValue<bool>(), "la copia vuelve a responder");
        Assert.Equal(db, await DbOfAsync(c.Grant));
        await Sandboxes.ReleaseAsync(c.Grant, host.Users["m2-pau"], "prueba");
    }

    // Regresión (revisión adversarial 044, A-05): si fallaba marcar la copia como lista, su base (y su proveedor en memoria)
    // quedaban hasta que el barrido venciera la preparación (15 min).
    [Fact]
    public async Task Regresion_A05_si_falla_marcar_la_copia_lista_no_queda_nada()
    {
        var c = await ConnectAsync("m2-quim", "modelo aislado");
        var prefix = SandboxFactory.Prefix(host.Database) + "M";
        var before = await host.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.databases WHERE LEFT(name, {prefix.Length}) = N'{prefix}'");
        await using (await host.FailWhenAsync("meta.AuditLog", "Action = 'mcp.sandbox' AND AfterJson LIKE N'%created%'"))
            Assert.Equal(500, ErrorOf(await CallAsync(c.Access, "aislado_listar", new { entity = "M2Nota" }))["status"]!.GetValue<int>());
        Assert.Null(await DbOfAsync(c.Grant));
        Assert.Equal(before, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.databases WHERE LEFT(name, {prefix.Length}) = N'{prefix}'"));
    }
}
