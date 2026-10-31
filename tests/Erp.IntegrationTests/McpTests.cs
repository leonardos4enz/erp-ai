using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Api.Mcp;
using Erp.IntegrationTests.Harness;
using Microsoft.AspNetCore.WebUtilities;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 038 · servidor MCP por persona, sobre SQL real y como lo usaría un cliente (Claude): registro dinámico, autorización
/// con consentimiento de la persona (su sesión), canje con PKCE, renovación con rotación, <c>/mcp</c> (JSON-RPC) y revocación.
/// Concurrencia (canjes y renovaciones simultáneos, llamadas en paralelo contra la cubeta), falla inyectada al emitir, permisos
/// y canario por las herramientas, y un cliente en <i>code mode</i> que llama herramientas en bucle.
/// </summary>
public class McpTests(McpTests.Host host) : IClassFixture<McpTests.Host>
{
    public const string Pwd = "frase larga del servidor mcp 2026";
    public const string Callback = "https://claude.ai/api/mcp/auth_callback";
    public const string Resource = "https://localhost/mcp";

    public sealed class Host : ErpHost
    {
        public readonly string Canary = "CANARIO-" + Guid.NewGuid().ToString("N");
        public readonly Dictionary<string, int> Rec = [];

        protected override void Configure(IDictionary<string, string?> s)
        {
            s["Mcp:Burst"] = "100000";
            s["Mcp:CallsPerMinute"] = "100000";
            s["Mcp:PendingClientsPerAddress"] = "100000";
            s["Security:TokenRequestsPerMinute"] = "100000"; // registro, token y revocar comparten el límite por dirección (aquí, una sola)
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            foreach (var (role, model) in new[] { ("Mcp Modelador", true), ("Mcp Lector", false) })
                Assert.Equal(200, (await admin.PostAsync("/api/admin/roles", new { name = role, description = "Spec 038", canManageModel = model,
                    canManageUsers = false, canViewAudit = false, permissions = Array.Empty<object>() })).Status);
            await admin.ApplyPlanAsync(new { description = "Spec 038 · catálogos", operations = new object[] {
                new { op = "createEntity", group = "Pruebas 038", name = "McTarea", label = "Tarea 038", labelPlural = "Tareas 038", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                new { op = "createEntity", group = "Pruebas 038", name = "McCanario", label = "Canario 038", labelPlural = "Canarios 038", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true },
                    new { name = "Secreto", label = "Secreto", type = "text" } } },
                new { op = "setSecurity", entity = "McCanario", security = new {
                    fields = new object[] { new { field = "Secreto", read = new[] { "Administrador" } } },
                    records = new object[] { new { roles = new[] { "Mcp Modelador" }, scope = "own" } } } },
                new { op = "createEntity", group = "Pruebas 038", name = "McPrivado", label = "Privado 038", labelPlural = "Privados 038", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
            } });
            static object Perm(string entity, bool write = true) => new { entity, canRead = true, canCreate = write, canUpdate = write, canDelete = false };
            foreach (var (role, model, perms) in new[] { ("Mcp Modelador", true, new[] { Perm("McTarea"), Perm("McCanario") }), ("Mcp Lector", false, new[] { Perm("McTarea", false) }) })
                Assert.Equal(200, (await admin.PutAsync($"/api/admin/roles/{await admin.RoleIdAsync(role)}", new { name = role, description = "Spec 038",
                    canManageModel = model, canManageUsers = false, canViewAudit = false, permissions = perms })).Status);
            var modeler = await admin.RoleIdAsync("Mcp Modelador");
            var reader = await admin.RoleIdAsync("Mcp Lector");
            foreach (var (u, role) in new[] { ("mc-ana", modeler), ("mc-beto", modeler), ("mc-carla", modeler), ("mc-dora", modeler), ("mc-eli", modeler),
                         ("mc-fer", modeler), ("mc-gil", modeler), ("mc-hugo", modeler), ("mc-lector", reader) })
            {
                var res = await admin.PostAsync("/api/admin/users", new { username = u, displayName = u, password = Pwd, roleIds = new[] { role } });
                Assert.True(res.Status == 201, res.Text);
                Rec[u] = res.Body!.Id();
            }
            for (var i = 1; i <= 45; i++) await admin.CreateAsync("McTarea", new { Nombre = $"Tarea {i:00}" });
            Rec["ajeno"] = (await admin.CreateAsync("McCanario", new { Nombre = Canary + "-ajeno", Secreto = Canary + "-secreto-ajeno" })).Id();
            using var ana = await LoginAsync("mc-ana", Pwd);
            Rec["propio"] = (await ana.CreateAsync("McCanario", new { Nombre = "Propio de Ana" })).Id();
            var propio = await admin.RecordAsync("McCanario", Rec["propio"]);
            Assert.Equal(200, (await admin.PutAsync($"/api/data/McCanario/{propio.Id()}", new { Secreto = Canary + "-secreto", _rowVersion = propio["_rowVersion"]!.GetValue<string>() })).Status);
            Rec["privado"] = (await admin.CreateAsync("McPrivado", new { Nombre = "Privado 038" })).Id();
        }
    }

    // ---------- Cliente MCP simulado ----------

    public sealed record Conn(string ClientId, string Redirect, string Access, string Refresh, string Scope);

    private static string Verifier() => McpFormat.New("v");

    private static async Task<Res> RegisterRawAsync(ErpHost.Factory? f, ErpHost host, object body)
    {
        using var api = f?.Anonymous(false) ?? host.Anonymous(false);
        return await api.PostAsync("/oauth/register", body);
    }

    private async Task<string> RegisterAsync(string redirect = Callback, string name = "Claude", ErpHost.Factory? f = null)
    {
        var res = await RegisterRawAsync(f, host, new { client_name = name, redirect_uris = new[] { redirect }, grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" }, token_endpoint_auth_method = "none" });
        Assert.True(res.Status == 201, res.Text);
        return res.Body!.Str("client_id")!;
    }

    private static string Query(string clientId, string redirect, string verifier, string scope = "modelo datos", string state = "estado-1",
        string method = "S256", string? resource = Resource, string? challenge = null) =>
        $"response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}" +
        $"&code_challenge={Uri.EscapeDataString(challenge ?? McpFormat.Challenge(verifier))}&code_challenge_method={method}&state={Uri.EscapeDataString(state)}" +
        $"&scope={Uri.EscapeDataString(scope)}" + (resource is null ? "" : $"&resource={Uri.EscapeDataString(resource)}");

    private static Dictionary<string, string> Params(string redirect)
    {
        var uri = new Uri(redirect);
        return QueryHelpers.ParseQuery(uri.Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
    }

    /// <summary>La persona revisa y decide en la pantalla Conectar (su sesión); devuelve a dónde la manda el ERP.</summary>
    private static async Task<string> DecideAsync(Client person, string query, bool approve = true, string[]? scopes = null)
    {
        var check = await person.GetAsync("/api/mcp/authorize?" + query);
        Assert.True(check.Status == 200, check.Text);
        var res = await person.PostAsync("/api/mcp/authorize", new { query, approve, scopes = scopes ?? ["modelo", "datos"] });
        Assert.True(res.Status == 200, res.Text);
        return res["redirect"]!.GetValue<string>();
    }

    private static Task<Res> TokenAsync(Client api, params (string, string)[] form) =>
        api.PostFormAsync("/oauth/token", form.ToDictionary(p => p.Item1, p => p.Item2));

    private async Task<Conn> ConnectAsync(string user, string scope = "modelo datos", string[]? chosen = null, string? clientId = null, ErpHost.Factory? f = null)
    {
        clientId ??= await RegisterAsync(f: f);
        var verifier = Verifier();
        using var person = f is null ? await host.LoginAsync(user, user == "admin" ? host.AdminPassword : Pwd) : await f.LoginAsync(user, user == "admin" ? host.AdminPassword : Pwd);
        var back = Params(await DecideAsync(person, Query(clientId, Callback, verifier, scope), scopes: chosen));
        Assert.Equal("estado-1", back["state"]);
        Assert.Equal("https://localhost", back["iss"]);
        using var api = f?.Anonymous(false) ?? host.Anonymous(false);
        var t = await TokenAsync(api, ("grant_type", "authorization_code"), ("code", back["code"]), ("redirect_uri", Callback), ("client_id", clientId),
            ("code_verifier", verifier), ("resource", Resource));
        Assert.True(t.Status == 200, t.Text);
        return new Conn(clientId, Callback, t.Body!.Str("access_token")!, t.Body!.Str("refresh_token")!, t.Body!.Str("scope")!);
    }

    private static async Task<Res> RpcAsync(Client api, string? token, object message, Action<HttpRequestMessage>? tweak = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json") };
        req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        tweak?.Invoke(req);
        using var res = await api.Http.SendAsync(req);
        var headers = res.Headers.Concat(res.Content.Headers).GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)), StringComparer.OrdinalIgnoreCase);
        return new Res((int)res.StatusCode, await res.Content.ReadAsStringAsync(), headers);
    }

    private async Task<Res> RpcAsync(string? token, string method, object? @params = null, ErpHost.Factory? f = null)
    {
        using var api = f?.Anonymous(false) ?? host.Anonymous(false);
        return await RpcAsync(api, token, new { jsonrpc = "2.0", id = 1, method, @params = @params ?? new { } });
    }

    private async Task<JsonNode> CallAsync(string token, string tool, object? args = null, ErpHost.Factory? f = null)
    {
        var res = await RpcAsync(token, "tools/call", new { name = tool, arguments = args ?? new { } }, f);
        Assert.True(res.Status == 200, res.Text);
        Assert.True(res["result"] is not null, res.Text);
        return res["result"]!;
    }

    private static JsonNode ErrorOf(JsonNode result)
    {
        Assert.True(result["isError"]!.GetValue<bool>(), result.Json());
        return JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
    }

    private async Task<string[]> ToolsAsync(string token)
    {
        var res = await RpcAsync(token, "tools/list");
        Assert.True(res.Status == 200, res.Text);
        return res["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order().ToArray();
    }

    // ---------- H1 ----------

    [Fact]
    public async Task H1_CA1_sin_token_responde_401_con_los_metadatos()
    {
        using var api = host.Anonymous(false);
        var res = await RpcAsync(api, null, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } });
        Assert.Equal(401, res.Status);
        Assert.Equal("Bearer resource_metadata=\"https://localhost/.well-known/oauth-protected-resource/mcp\"", res.Headers["WWW-Authenticate"]);
        var bad = await RpcAsync(api, McpFormat.New(McpFormat.AccessPrefix), new { jsonrpc = "2.0", id = 1, method = "ping" });
        Assert.Equal(401, bad.Status);
        Assert.Contains("error=\"invalid_token\"", bad.Headers["WWW-Authenticate"]);
    }

    [Fact]
    public async Task H1_CA2_los_metadatos_describen_el_servidor()
    {
        using var api = host.Anonymous(false);
        foreach (var path in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp" })
        {
            var pr = await api.GetAsync(path);
            Assert.Equal(200, pr.Status);
            Assert.Equal(Resource, pr.Body!.Str("resource"));
            Assert.Equal("https://localhost", pr["authorization_servers"]![0]!.GetValue<string>());
            Assert.Equal(["modelo", "datos"], pr["scopes_supported"]!.AsArray().Select(x => x!.GetValue<string>()));
        }
        var asm = (await api.GetAsync("/.well-known/oauth-authorization-server")).Body!;
        Assert.Equal("https://localhost", asm.Str("issuer"));
        Assert.Equal("https://localhost/oauth/authorize", asm.Str("authorization_endpoint"));
        Assert.Equal("https://localhost/oauth/token", asm.Str("token_endpoint"));
        Assert.Equal("https://localhost/oauth/register", asm.Str("registration_endpoint"));
        Assert.Equal("https://localhost/oauth/revoke", asm.Str("revocation_endpoint"));
        Assert.Equal(["S256"], asm["code_challenge_methods_supported"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["none"], asm["token_endpoint_auth_methods_supported"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["authorization_code", "refresh_token"], asm["grant_types_supported"]!.AsArray().Select(x => x!.GetValue<string>()));
        // La autorización del servidor lleva a la pantalla de la SPA con la misma consulta.
        var go = await api.GetAsync("/oauth/authorize?response_type=code&client_id=x");
        Assert.Equal(302, go.Status);
        Assert.Equal("/conectar?response_type=code&client_id=x", go.Headers["Location"]);
    }

    [Fact]
    public async Task H1_CA3_el_registro_rechaza_lo_invalido_y_tiene_tope()
    {
        foreach (var (body, error) in new (object, string)[]
                 {
                     (new { client_name = "Sin retorno" }, "invalid_redirect_uri"),
                     (new { redirect_uris = new[] { "http://evil.example/cb" } }, "invalid_redirect_uri"),
                     (new { redirect_uris = new[] { "https://claude.ai/cb#frag" } }, "invalid_redirect_uri"),
                     (new { redirect_uris = new[] { "javascript:alert(1)" } }, "invalid_redirect_uri"),
                     (new { redirect_uris = Enumerable.Range(0, 6).Select(i => $"https://claude.ai/cb{i}").ToArray() }, "invalid_redirect_uri"),
                     (new { redirect_uris = new[] { Callback }, token_endpoint_auth_method = "client_secret_basic" }, "invalid_client_metadata"),
                     (new { redirect_uris = new[] { Callback }, grant_types = new[] { "client_credentials" } }, "invalid_client_metadata"),
                     (new { redirect_uris = new[] { Callback }, grant_types = new[] { "implicit" } }, "invalid_client_metadata"),
                     (new { redirect_uris = new[] { Callback }, response_types = new[] { "token" } }, "invalid_client_metadata"),
                     (new { redirect_uris = new[] { Callback }, client_name = new string('x', 101) }, "invalid_client_metadata"),
                 })
        {
            var res = await RegisterRawAsync(null, host, body);
            Assert.True(res.Status == 400, $"{JsonSerializer.Serialize(body)} → {res}");
            Assert.Equal(error, res.Body!.Str("error"));
        }
        // Loopback para clientes nativos (Claude Code) y el registro mínimo.
        var loop = await RegisterRawAsync(null, host, new { redirect_uris = new[] { "http://127.0.0.1:33418/callback" } });
        Assert.Equal(201, loop.Status);
        Assert.Equal("none", loop.Body!.Str("token_endpoint_auth_method"));
        Assert.Equal("Cliente MCP", loop.Body!.Str("client_name"));
        Assert.DoesNotContain("client_secret", loop.Text);

        // Tope de clientes: la siguiente instancia admite uno más de los vivos (spec 040: pendientes o con una conexión viva;
        // los que ya no tienen conexión viva no cuentan).
        var count = await host.ScalarAsync<int>("""
            SELECT COUNT(*) FROM meta.McpClient c
             WHERE NOT EXISTS (SELECT 1 FROM meta.McpGrant g WHERE g.ClientId = c.Id)
                OR EXISTS (SELECT 1 FROM meta.McpGrant g WHERE g.ClientId = c.Id AND g.RevokedAt IS NULL
                           AND EXISTS (SELECT 1 FROM meta.McpToken t WHERE t.GrantId = g.Id AND t.Kind = 'r' AND t.UsedAt IS NULL AND t.ExpiresAt > SYSUTCDATETIME()))
            """);
        var f = await host.SiblingAsync(s => s["Mcp:MaxClients"] = (count + 1).ToString());
        Assert.Equal(201, (await RegisterRawAsync(f, host, new { redirect_uris = new[] { Callback } })).Status);
        var full = await RegisterRawAsync(f, host, new { redirect_uris = new[] { Callback } });
        Assert.Equal(503, full.Status);
        Assert.Equal("temporarily_unavailable", full.Body!.Str("error"));
    }

    // ---------- H2 ----------

    [Fact]
    public async Task H2_CA1_la_pantalla_recibe_cliente_sitio_y_alcances()
    {
        var known = await RegisterAsync();
        var unknown = await RegisterAsync("https://claude-ai.example.net/cb", "Claude");
        using var ana = await host.LoginAsync("mc-ana", Pwd);
        var check = (await ana.GetAsync("/api/mcp/authorize?" + Query(known, Callback, Verifier(), "datos"))).Body!;
        Assert.Equal("Claude", check["client"]!.Str("name"));
        Assert.Equal("claude.ai", check["client"]!.Str("redirectHost"));
        Assert.True(check["client"]!["trusted"]!.GetValue<bool>());
        Assert.Equal("mc-ana", check["user"]!.Str("username"));
        var scopes = check["scopes"]!.AsArray().ToDictionary(s => s!.Str("name")!);
        Assert.True(scopes["modelo"]!["required"]!.GetValue<bool>());
        Assert.False(scopes["datos"]!["required"]!.GetValue<bool>());
        Assert.True(scopes["datos"]!["requested"]!.GetValue<bool>());
        var other = (await ana.GetAsync("/api/mcp/authorize?" + Query(unknown, "https://claude-ai.example.net/cb", Verifier()))).Body!;
        Assert.Equal("claude-ai.example.net", other["client"]!.Str("redirectHost"));
        Assert.False(other["client"]!["trusted"]!.GetValue<bool>());
        // Sin sesión no hay consentimiento.
        using var anon = host.Anonymous();
        Assert.Equal(401, (await anon.GetAsync("/api/mcp/authorize?" + Query(known, Callback, Verifier()))).Status);
    }

    [Fact]
    public async Task H2_CA2_un_retorno_no_registrado_no_redirige()
    {
        var client = await RegisterAsync();
        using var ana = await host.LoginAsync("mc-ana", Pwd);
        foreach (var query in new[]
                 {
                     Query(client, "https://evil.example/cb", Verifier()),
                     Query(client, Callback + "/otra", Verifier()),
                     Query("erpmcp_0000000000000000", Callback, Verifier()),
                     Query(client, Callback, Verifier()) + "&redirect_uri=" + Uri.EscapeDataString("https://evil.example/cb"),
                     "response_type=code",
                 })
        {
            var check = await ana.GetAsync("/api/mcp/authorize?" + query);
            Assert.True(check.Status == 400, $"{query} → {check}");
            Assert.Null(check["redirect"]);
            var post = await ana.PostAsync("/api/mcp/authorize", new { query, approve = true, scopes = new[] { "modelo" } });
            Assert.Equal(400, post.Status);
            Assert.DoesNotContain("evil.example", post.Text);
        }
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpCode c JOIN meta.McpClient x ON x.Id = c.ClientId WHERE x.ClientId = '{client}'"));
    }

    [Fact]
    public async Task H2_CA2_los_errores_despues_del_retorno_vuelven_al_cliente()
    {
        var client = await RegisterAsync();
        using var ana = await host.LoginAsync("mc-ana", Pwd);
        var v = Verifier();
        foreach (var (query, error) in new[]
                 {
                     (Query(client, Callback, v, method: "plain", challenge: v), "invalid_request"),
                     (Query(client, Callback, v, challenge: "corto"), "invalid_request"),
                     (Query(client, Callback, v, scope: "modelo aislado"), "invalid_scope"),
                     (Query(client, Callback, v, resource: "https://otro.example/mcp"), "invalid_target"),
                     (Query(client, Callback, v).Replace("response_type=code", "response_type=token"), "unsupported_response_type"),
                     (Query(client, Callback, v) + "&scope=datos", "invalid_request"),
                 })
        {
            var check = await ana.GetAsync("/api/mcp/authorize?" + query);
            Assert.True(check.Status == 200, check.Text);
            var back = Params(check["redirect"]!.GetValue<string>());
            Assert.True(check["redirect"]!.GetValue<string>().StartsWith(Callback + "?"), check.Text);
            Assert.Equal(error, back["error"]);
            Assert.Equal("estado-1", back["state"]);
            Assert.Equal("https://localhost", back["iss"]);
            Assert.False(back.ContainsKey("code"));
        }
    }

    [Fact]
    public async Task H2_CA3_permitir_da_un_codigo_y_cancelar_access_denied()
    {
        var client = await RegisterAsync();
        using var ana = await host.LoginAsync("mc-ana", Pwd);
        var denied = Params(await DecideAsync(ana, Query(client, Callback, Verifier()), approve: false));
        Assert.Equal("access_denied", denied["error"]);
        Assert.False(denied.ContainsKey("code"));
        // Pidió datos y la persona no lo marcó: el acceso queda sin datos.
        var conn = await ConnectAsync("mc-ana", "modelo datos", ["modelo"], client);
        Assert.Equal("modelo", conn.Scope);
        Assert.DoesNotContain("leer_registros", await ToolsAsync(conn.Access));
        // Marcar algo que no pidió no lo agrega.
        var only = await ConnectAsync("mc-beto", "modelo", ["modelo", "datos"]);
        Assert.Equal("modelo", only.Scope);
    }

    [Fact]
    public async Task H2_CA3_permitir_sin_cabecera_anti_CSRF_da_403()
    {
        var client = await RegisterAsync();
        using var ana = await host.LoginAsync("mc-ana", Pwd);
        ana.Http.DefaultRequestHeaders.Remove("X-ERP-Request");
        var res = await ana.PostAsync("/api/mcp/authorize", new { query = Query(client, Callback, Verifier()), approve = true, scopes = new[] { "modelo" } });
        Assert.Equal(403, res.Status);
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpCode c JOIN meta.McpClient x ON x.Id = c.ClientId WHERE x.ClientId = '{client}'"));
    }

    // ---------- H3 ----------

    private async Task<(string Client, string Code, string Verifier)> CodeAsync(string user = "mc-ana", ErpHost.Factory? f = null)
    {
        var client = await RegisterAsync(f: f);
        var verifier = Verifier();
        using var person = f is null ? await host.LoginAsync(user, Pwd) : await f.LoginAsync(user, Pwd);
        var back = Params(await DecideAsync(person, Query(client, Callback, verifier)));
        return (client, back["code"], verifier);
    }

    [Fact]
    public async Task H3_CA1_el_codigo_exige_cliente_retorno_y_verificador()
    {
        using var api = host.Anonymous(false);
        var other = await RegisterAsync();
        foreach (var (name, mutate, status, error) in new (string, Func<(string Client, string Code, string Verifier), (string, string)[]>, int, string)[]
                 {
                     ("otro verificador", c => [("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback), ("client_id", c.Client), ("code_verifier", Verifier())], 400, "invalid_grant"),
                     ("otro retorno", c => [("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback + "x"), ("client_id", c.Client), ("code_verifier", c.Verifier)], 400, "invalid_grant"),
                     ("otro cliente", c => [("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback), ("client_id", other), ("code_verifier", c.Verifier)], 400, "invalid_grant"),
                     ("cliente inexistente", c => [("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback), ("client_id", "erpmcp_0000000000000000"), ("code_verifier", c.Verifier)], 401, "invalid_client"),
                     ("con secreto", c => [("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback), ("client_id", c.Client), ("code_verifier", c.Verifier), ("client_secret", "x")], 401, "invalid_client"),
                     ("otro recurso", c => [("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback), ("client_id", c.Client), ("code_verifier", c.Verifier), ("resource", "https://otro/mcp")], 400, "invalid_target"),
                     ("sin verificador", c => [("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback), ("client_id", c.Client)], 400, "invalid_request"),
                 })
        {
            var c = await CodeAsync();
            var res = await TokenAsync(api, mutate(c));
            Assert.True(res.Status == status, $"{name}: {res}");
            Assert.Equal(error, res.Body!.Str("error"));
            Assert.Null(res["access_token"]);
        }
        // El código vencido no sirve.
        var late = await CodeAsync();
        await host.ExecAsync("UPDATE meta.McpCode SET ExpiresAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE UsedAt IS NULL");
        Assert.Equal("invalid_grant", (await TokenAsync(api, ("grant_type", "authorization_code"), ("code", late.Code), ("redirect_uri", Callback),
            ("client_id", late.Client), ("code_verifier", late.Verifier))).Body!.Str("error"));
        // Uno que falló (verificador equivocado) queda gastado: probar el bueno después tampoco sirve.
        var tried = await CodeAsync();
        await TokenAsync(api, ("grant_type", "authorization_code"), ("code", tried.Code), ("redirect_uri", Callback), ("client_id", tried.Client), ("code_verifier", Verifier()));
        Assert.Equal(400, (await TokenAsync(api, ("grant_type", "authorization_code"), ("code", tried.Code), ("redirect_uri", Callback),
            ("client_id", tried.Client), ("code_verifier", tried.Verifier))).Status);
    }

    [Fact]
    public async Task H3_CA2_un_codigo_reusado_revoca_la_conexion()
    {
        using var api = host.Anonymous(false);
        var c = await CodeAsync("mc-beto");
        (string, string)[] form = [("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback), ("client_id", c.Client), ("code_verifier", c.Verifier)];
        var first = await TokenAsync(api, form);
        Assert.Equal(200, first.Status);
        Assert.Equal(200, (await RpcAsync(first.Body!.Str("access_token"), "ping")).Status);
        var again = await TokenAsync(api, form);
        Assert.Equal(400, again.Status);
        Assert.Equal("invalid_grant", again.Body!.Str("error"));
        Assert.Equal(401, (await RpcAsync(first.Body!.Str("access_token"), "ping")).Status);
        Assert.Equal("invalid_grant", (await TokenAsync(api, ("grant_type", "refresh_token"), ("refresh_token", first.Body!.Str("refresh_token")!),
            ("client_id", c.Client))).Body!.Str("error"));
    }

    [Fact]
    public async Task H3_CA2_diez_canjes_del_mismo_codigo_dan_un_token()
    {
        var c = await CodeAsync("mc-carla");
        var results = await Concurrently(10, async _ =>
        {
            using var api = host.Anonymous(false);
            return await TokenAsync(api, ("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback), ("client_id", c.Client), ("code_verifier", c.Verifier));
        });
        Invariant.ExactlyOnce(results.Count(r => r.Status == 200), "canje del código");
        Assert.All(results.Where(r => r.Status != 200), r => Assert.Equal("invalid_grant", r.Body!.Str("error")));
        // Los otros nueve son reuso: la conexión que creó el primero queda revocada (RFC 6749 §4.1.2).
        var grant = await host.ScalarAsync<int>($"SELECT g.Id FROM meta.McpGrant g JOIN meta.McpClient x ON x.Id = g.ClientId WHERE x.ClientId = '{c.Client}'");
        Assert.Equal("reuse", await host.ScalarAsync<string>($"SELECT RevokedReason FROM meta.McpGrant WHERE Id = {grant}"));
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpGrant g JOIN meta.McpClient x ON x.Id = g.ClientId WHERE x.ClientId = '{c.Client}'"));
    }

    [Fact]
    public async Task H3_CA3_renovar_rota_y_el_reuso_revoca()
    {
        var conn = await ConnectAsync("mc-dora");
        using var api = host.Anonymous(false);
        var next = await TokenAsync(api, ("grant_type", "refresh_token"), ("refresh_token", conn.Refresh), ("client_id", conn.ClientId), ("resource", Resource));
        Assert.True(next.Status == 200, next.Text);
        Assert.NotEqual(conn.Refresh, next.Body!.Str("refresh_token"));
        Assert.Equal("modelo datos", next.Body!.Str("scope"));
        Assert.Equal(200, (await RpcAsync(next.Body!.Str("access_token"), "ping")).Status);
        // De otro cliente no se renueva.
        var other = await RegisterAsync();
        Assert.Equal("invalid_grant", (await TokenAsync(api, ("grant_type", "refresh_token"), ("refresh_token", next.Body!.Str("refresh_token")!), ("client_id", other))).Body!.Str("error"));
        // Reusar el anterior (ya rotado): la conexión se revoca, también el token nuevo.
        var reuse = await TokenAsync(api, ("grant_type", "refresh_token"), ("refresh_token", conn.Refresh), ("client_id", conn.ClientId));
        Assert.Equal("invalid_grant", reuse.Body!.Str("error"));
        Assert.Equal(401, (await RpcAsync(next.Body!.Str("access_token"), "ping")).Status);
        Assert.Equal(400, (await TokenAsync(api, ("grant_type", "refresh_token"), ("refresh_token", next.Body!.Str("refresh_token")!), ("client_id", conn.ClientId))).Status);
    }

    [Fact]
    public async Task H3_CA3_diez_renovaciones_del_mismo_refresh_dan_una()
    {
        var conn = await ConnectAsync("mc-eli");
        var results = await Concurrently(10, async _ =>
        {
            using var api = host.Anonymous(false);
            return await TokenAsync(api, ("grant_type", "refresh_token"), ("refresh_token", conn.Refresh), ("client_id", conn.ClientId));
        });
        Invariant.ExactlyOnce(results.Count(r => r.Status == 200), "renovación");
        // Las otras nueve son reuso del refresh ya rotado: la conexión queda revocada (nadie conserva un token vivo).
        var winner = results.Single(r => r.Status == 200).Body!.Str("access_token");
        Assert.Equal(401, (await RpcAsync(winner, "ping")).Status);
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpToken t JOIN meta.McpGrant g ON g.Id = t.GrantId JOIN meta.McpClient x ON x.Id = g.ClientId WHERE x.ClientId = '{conn.ClientId}' AND t.Kind = 'r' AND t.UsedAt IS NULL"));
    }

    [Fact]
    public async Task H3_CA4_el_token_MCP_no_sirve_fuera_de_mcp()
    {
        var conn = await ConnectAsync("mc-ana");
        using var api = host.Anonymous(false);
        foreach (var (method, path) in new[] { ("GET", "/api/data/McTarea"), ("GET", "/api/auth/me"), ("GET", "/api/model/entities"), ("GET", "/odata/McTarea"),
                     ("GET", "/api/mcp/connections"), ("POST", "/api/data/McTarea") })
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", conn.Access);
            if (method == "POST") req.Content = new StringContent("{\"Nombre\":\"por token MCP\"}", Encoding.UTF8, "application/json");
            using var res = await api.Http.SendAsync(req);
            Assert.True((int)res.StatusCode == 403, $"{method} {path} → {(int)res.StatusCode}");
        }
        Assert.Equal(0, await host.ScalarAsync<int>("SELECT COUNT(*) FROM app.McTarea WHERE Nombre = N'por token MCP'"));
    }

    [Fact]
    public async Task H3_CA4_si_cambia_el_sello_el_token_y_la_renovacion_mueren()
    {
        using var admin = await host.AdminAsync();
        var conn = await ConnectAsync("mc-fer");
        Assert.Equal(200, (await RpcAsync(conn.Access, "ping")).Status);
        // El administrador le cambia la contraseña (cambia su sello).
        Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{host.Rec["mc-fer"]}", new { displayName = "mc-fer", isActive = true, password = Pwd + " nueva",
            roleIds = new[] { await admin.RoleIdAsync("Mcp Modelador") } })).Status);
        Assert.Equal(401, (await RpcAsync(conn.Access, "ping")).Status);
        using var api = host.Anonymous(false);
        var refresh = await TokenAsync(api, ("grant_type", "refresh_token"), ("refresh_token", conn.Refresh), ("client_id", conn.ClientId));
        Assert.Equal("invalid_grant", refresh.Body!.Str("error"));
        Assert.Equal("stamp", await host.ScalarAsync<string>($"SELECT g.RevokedReason FROM meta.McpGrant g JOIN meta.McpClient x ON x.Id = g.ClientId WHERE x.ClientId = '{conn.ClientId}'"));
        Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{host.Rec["mc-fer"]}", new { displayName = "mc-fer", isActive = true, password = Pwd,
            roleIds = new[] { await admin.RoleIdAsync("Mcp Modelador") } })).Status);

        // Un código emitido antes de desactivar a la persona ya no se canjea.
        var c = await CodeAsync("mc-gil");
        Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{host.Rec["mc-gil"]}", new { displayName = "mc-gil", isActive = false,
            roleIds = new[] { await admin.RoleIdAsync("Mcp Modelador") } })).Status);
        var late = await TokenAsync(api, ("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback), ("client_id", c.Client), ("code_verifier", c.Verifier));
        Assert.Equal("invalid_grant", late.Body!.Str("error"));
        Assert.Equal(200, (await admin.PutAsync($"/api/admin/users/{host.Rec["mc-gil"]}", new { displayName = "mc-gil", isActive = true,
            roleIds = new[] { await admin.RoleIdAsync("Mcp Modelador") } })).Status);
    }

    [Fact]
    public async Task Una_falla_al_emitir_no_consume_el_codigo()
    {
        var c = await CodeAsync("mc-ana");
        using var api = host.Anonymous(false);
        (string, string)[] form = [("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback), ("client_id", c.Client), ("code_verifier", c.Verifier)];
        await using (await host.FailWhenAsync("meta.McpToken", "Kind = 'r'"))
        {
            var failed = await TokenAsync(api, form);
            Assert.Equal(500, failed.Status);
            Assert.DoesNotContain("CK_IT", failed.Text);
        }
        // Todo se revirtió: el código sigue sin usar, no quedó conexión sin tokens y ahora sí se canjea.
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpGrant g JOIN meta.McpClient x ON x.Id = g.ClientId WHERE x.ClientId = '{c.Client}'"));
        var ok = await TokenAsync(api, form);
        Assert.True(ok.Status == 200, ok.Text);
        Assert.Equal(200, (await RpcAsync(ok.Body!.Str("access_token"), "ping")).Status);
    }

    // ---------- H4 ----------

    private static readonly string[] ModelTools = ["automatizaciones", "base_fisica", "compilar_plan", "donde_se_usa", "funciones_expresion", "inventario_rutas",
        "listar_catalogos", "relaciones", "riesgos", "seguridad", "validar_expresion", "ver_catalogo"];

    [Fact]
    public async Task H4_CA1_tools_list_da_solo_lo_que_la_persona_puede()
    {
        var ana = await ConnectAsync("mc-ana");
        Assert.Equal(ModelTools.Concat(["leer_registro", "leer_registros"]).Order(), await ToolsAsync(ana.Access));
        // Quien no es modelador: solo lo que su sesión puede (catálogos que ve y, con datos, sus registros).
        var lector = await ConnectAsync("mc-lector");
        Assert.Equal(["leer_registro", "leer_registros", "listar_catalogos"], await ToolsAsync(lector.Access));
        var call = await RpcAsync(lector.Access, "tools/call", new { name = "ver_catalogo", arguments = new { entity = "McTarea" } });
        Assert.Equal(-32602, call["error"]!["code"]!.GetValue<int>());
        // Ni la copia aislada ni entregar existen por MCP.
        foreach (var name in new[] { "aislado_crear", "aislado_aplicar_plan", "entregar" })
            Assert.Equal(-32602, (await RpcAsync(ana.Access, "tools/call", new { name, arguments = new { } }))["error"]!["code"]!.GetValue<int>());
        var catalogs = (await CallAsync(lector.Access, "listar_catalogos"))["structuredContent"]!["items"]!.AsArray().Select(x => x!.Str("name")).ToList();
        Assert.Contains("McTarea", catalogs);
        Assert.DoesNotContain("McPrivado", catalogs);
    }

    [Fact]
    public async Task H4_CA2_una_herramienta_da_el_mismo_403_que_la_API()
    {
        using var lector = await host.LoginAsync("mc-lector", Pwd);
        var api = await lector.GetAsync("/api/data/McPrivado/");
        Assert.Equal(403, api.Status);
        var conn = await ConnectAsync("mc-lector");
        var error = ErrorOf(await CallAsync(conn.Access, "leer_registros", new { entity = "McPrivado" }));
        Assert.Equal(403, error["status"]!.GetValue<int>());
        Assert.Equal(api["title"]!.GetValue<string>(), error["error"]!.GetValue<string>());
        Assert.DoesNotContain("Privado 038", error.Json());
    }

    [Fact]
    public async Task H4_CA2_el_canario_no_sale_por_MCP()
    {
        var ana = await ConnectAsync("mc-ana");
        var seen = new StringBuilder();
        foreach (var (tool, args) in new (string, object)[]
                 {
                     ("leer_registros", new { entity = "McCanario" }), ("leer_registros", new { entity = "McCanario", search = "CANARIO" }),
                     ("leer_registro", new { entity = "McCanario", id = host.Rec["ajeno"] }), ("leer_registro", new { entity = "McCanario", id = host.Rec["propio"] }),
                     ("ver_catalogo", new { entity = "McCanario" }), ("donde_se_usa", new { entity = "McCanario" }), ("seguridad", new { }),
                 })
            seen.Append((await CallAsync(ana.Access, tool, args)).Json());
        Assert.Contains("Propio de Ana", seen.ToString());
        Assert.DoesNotContain(host.Canary, seen.ToString());
        // Control positivo: el administrador sí lo ve por la misma herramienta.
        var admin = await ConnectAsync("admin");
        Assert.Contains(host.Canary + "-secreto-ajeno", (await CallAsync(admin.Access, "leer_registros", new { entity = "McCanario" })).Json());
    }

    [Fact]
    public async Task H4_CA3_sin_datos_no_hay_herramientas_de_registros()
    {
        var conn = await ConnectAsync("mc-beto", "modelo");
        Assert.Equal(ModelTools.Order(), await ToolsAsync(conn.Access));
        var call = await RpcAsync(conn.Access, "tools/call", new { name = "leer_registros", arguments = new { entity = "McTarea" } });
        Assert.Equal(-32602, call["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain("Tarea 01", call.Text);
    }

    [Fact]
    public async Task H4_CA4_mcp_rechaza_lotes_cuerpos_grandes_y_versiones()
    {
        var conn = await ConnectAsync("mc-ana");
        using var api = host.Anonymous(false);
        var init = await RpcAsync(api, conn.Access, new { jsonrpc = "2.0", id = "a", method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "prueba", version = "1" } } });
        Assert.Equal(200, init.Status);
        Assert.StartsWith("application/json", init.Headers["Content-Type"]);
        Assert.Equal("a", init["id"]!.GetValue<string>());
        Assert.Equal("2025-06-18", init["result"]!.Str("protocolVersion"));
        Assert.False(init["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
        var future = await RpcAsync(api, conn.Access, new { jsonrpc = "2.0", id = 2, method = "initialize", @params = new { protocolVersion = "2099-01-01" } });
        Assert.Equal(McpServer.Latest, future["result"]!.Str("protocolVersion"));
        Assert.Equal(202, (await RpcAsync(api, conn.Access, new { jsonrpc = "2.0", method = "notifications/initialized" })).Status);
        Assert.Equal(-32601, (await RpcAsync(api, conn.Access, new { jsonrpc = "2.0", id = 3, method = "resources/list" }))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(200, (await RpcAsync(api, conn.Access, new { jsonrpc = "2.0", id = 4, method = "ping" })).Status);

        var batch = await RpcAsync(api, conn.Access, new object[] { new { jsonrpc = "2.0", id = 1, method = "ping" }, new { jsonrpc = "2.0", id = 2, method = "ping" } });
        Assert.Equal(400, batch.Status);
        Assert.Equal(-32600, batch["error"]!["code"]!.GetValue<int>());
        var version = await RpcAsync(api, conn.Access, new { jsonrpc = "2.0", id = 5, method = "ping" }, r => r.Headers.Add("MCP-Protocol-Version", "1999-01-01"));
        Assert.Equal(400, version.Status);
        var big = await RpcAsync(api, conn.Access, new { jsonrpc = "2.0", id = 6, method = "tools/call",
            @params = new { name = "validar_expresion", arguments = new { entity = "McTarea", expression = new string('1', McpServer.MaxBody) } } });
        Assert.Equal(413, big.Status);
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{no es json", Encoding.UTF8, "application/json") })
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", conn.Access);
            using var res = await api.Http.SendAsync(req);
            Assert.Equal(400, (int)res.StatusCode);
            Assert.Contains("-32700", await res.Content.ReadAsStringAsync());
        }
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete })
        {
            using var req = new HttpRequestMessage(method, "/mcp");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", conn.Access);
            using var res = await api.Http.SendAsync(req);
            Assert.Equal(405, (int)res.StatusCode);
        }
        // Un error inesperado no filtra detalles: la entrada inválida de la herramienta es su mensaje seguro.
        var bad = ErrorOf(await CallAsync(conn.Access, "ver_catalogo", new { entity = 12345 }));
        Assert.DoesNotContain("Exception", bad.Json());
    }

    [Fact]
    public async Task H4_CA4_un_origin_ajeno_da_403()
    {
        var conn = await ConnectAsync("mc-ana");
        using var api = host.Anonymous(false);
        var evil = await RpcAsync(api, conn.Access, new { jsonrpc = "2.0", id = 1, method = "tools/list" }, r => r.Headers.Add("Origin", "https://evil.example"));
        Assert.Equal(403, evil.Status);
        var own = await RpcAsync(api, conn.Access, new { jsonrpc = "2.0", id = 1, method = "ping" }, r => r.Headers.Add("Origin", "https://localhost"));
        Assert.Equal(200, own.Status);
    }

    [Fact]
    public async Task H4_CA4_mcp_no_acepta_cookie_ni_otras_credenciales()
    {
        // La sesión del navegador no llega a /mcp (sin CSRF posible), aunque traiga la cabecera anti-CSRF.
        using var ana = await host.LoginAsync("mc-ana", Pwd);
        var cookie = await RpcAsync(ana, null, new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(401, cookie.Status);
        // Ni una clave de API ni un token de servicio (018): en /mcp solo responde el manejador MCP.
        using var api = host.Anonymous(false);
        foreach (var other in new[] { "erpk_" + new string('a', 40), Erp.Api.Integrations.OAuthFormat.NewToken() })
            Assert.Equal(401, (await RpcAsync(api, other, new { jsonrpc = "2.0", id = 1, method = "tools/list" })).Status);
    }

    // ---------- H5 ----------

    [Fact]
    public async Task H5_CA1_cada_salida_cumple_su_outputSchema()
    {
        var admin = await ConnectAsync("admin");
        var list = (await RpcAsync(admin.Access, "tools/list"))["result"]!["tools"]!.AsArray().ToDictionary(t => t!.Str("name")!, t => t!);
        var samples = new Dictionary<string, object>
        {
            ["listar_catalogos"] = new { }, ["ver_catalogo"] = new { entity = "McTarea" }, ["relaciones"] = new { }, ["base_fisica"] = new { q = "McTarea" },
            ["donde_se_usa"] = new { entity = "McTarea" }, ["automatizaciones"] = new { }, ["seguridad"] = new { }, ["inventario_rutas"] = new { q = "mcp" },
            ["riesgos"] = new { q = "MCP" }, ["funciones_expresion"] = new { }, ["validar_expresion"] = new { entity = "McTarea", expression = "LEN(Nombre) > 0" },
            ["compilar_plan"] = new { plan = new { description = "x", operations = new object[] { new { op = "addField", entity = "McTarea", field = new { name = "Nota", label = "Nota", type = "text" } } } } },
            ["leer_registros"] = new { entity = "McTarea", pageSize = 5 }, ["leer_registro"] = new { entity = "McTarea", id = 1 },
        };
        Assert.Equal(samples.Keys.Order(), list.Keys.Order());
        foreach (var (name, args) in samples)
        {
            var result = await CallAsync(admin.Access, name, args);
            Assert.False(result["isError"]!.GetValue<bool>(), $"{name}: {result.Json()}");
            var structured = result["structuredContent"]!;
            var problems = Schema.Validate(list[name]["outputSchema"]!, structured, name);
            Assert.True(problems.Count == 0, $"{name}: {string.Join("; ", problems)}");
            // El texto es el mismo JSON (clientes sin salida estructurada).
            Assert.Equal(structured.ToJsonString(), JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.ToJsonString());
            var schemaProblems = Schema.Validate(list[name]["inputSchema"]!, JsonSerializer.SerializeToNode(args)!, name + " (entrada)");
            Assert.True(schemaProblems.Count == 0, string.Join("; ", schemaProblems));
        }
    }

    [Fact]
    public async Task H5_CA1_un_parametro_de_mas_es_un_error_de_la_herramienta()
    {
        var conn = await ConnectAsync("mc-ana");
        var error = ErrorOf(await CallAsync(conn.Access, "ver_catalogo", new { entity = "McTarea", entidad = "McTarea" }));
        Assert.Contains("«entidad» no es un parámetro de ver_catalogo", error["error"]!.GetValue<string>());
        var range = ErrorOf(await CallAsync(conn.Access, "leer_registros", new { entity = "McTarea", pageSize = 101 }));
        Assert.Contains("pageSize", range["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task H5_CA2_leer_registros_pagina_con_nextPage()
    {
        var conn = await ConnectAsync("mc-ana");
        var names = new List<string>();
        int? page = 1;
        var pages = 0;
        while (page is { } p && pages++ < 10)
        {
            var s = (await CallAsync(conn.Access, "leer_registros", new { entity = "McTarea", page = p, pageSize = 20, search = "Tarea" }))["structuredContent"]!;
            Assert.Equal(45, s["total"]!.GetValue<int>());
            Assert.Equal(20, s["pageSize"]!.GetValue<int>());
            Assert.Equal(p, s["page"]!.GetValue<int>());
            names.AddRange(s["items"]!.AsArray().Select(x => x!.Str("Nombre")!));
            page = s["nextPage"]?.GetValue<int>();
        }
        Assert.Equal(3, pages);
        Assert.Equal(45, names.Distinct().Count());
    }

    /// <summary>Un cliente en code mode: un bucle que llama herramientas sin pausa, con una que no le toca y otra fuera de su permiso.</summary>
    [Fact]
    public async Task H5_CA3_un_bucle_de_200_llamadas_recibe_lo_que_permiten_los_topes()
    {
        var f = await host.SiblingAsync(s => { s["Mcp:Burst"] = "25"; s["Mcp:CallsPerMinute"] = "1"; });
        var conn = await ConnectAsync("mc-lector", f: f);
        var outcomes = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            var (tool, args) = (i % 4) switch
            {
                0 => ("leer_registros", (object)new { entity = "McTarea", pageSize = 1 }),
                1 => ("leer_registros", new { entity = "McPrivado" }),
                2 => ("ver_catalogo", new { entity = "McTarea" }),
                _ => ("listar_catalogos", new { }),
            };
            var res = await RpcAsync(conn.Access, "tools/call", new { name = tool, arguments = args }, f);
            Assert.Equal(200, res.Status);
            if (res["error"] is { } rpc) { outcomes.Add($"rpc:{rpc["code"]}"); continue; }
            var result = res["result"]!;
            if (!result["isError"]!.GetValue<bool>()) { outcomes.Add("ok:" + tool); continue; }
            var error = ErrorOf(result);
            var status = error["status"]!.GetValue<int>();
            if (status == 429) Assert.True(error["retryAfterSeconds"]!.GetValue<int>() >= 1);
            outcomes.Add($"{status}:{tool}");
            Assert.DoesNotContain("Privado 038", result.Json());
        }
        // Exactamente la cubeta: 25 llamadas pasaron al despachador (las demás esperan); ver_catalogo nunca, porque no es modelador.
        var served = outcomes.Count(o => !o.StartsWith("429") && !o.StartsWith("rpc"));
        Assert.Equal(25, served);
        Assert.Equal(50, outcomes.Count(o => o == "rpc:-32602"));
        Assert.All(outcomes.Where(o => o.EndsWith(":leer_registros") && !o.StartsWith("429") && !o.StartsWith("ok")), o => Assert.Equal("403:leer_registros", o));
        Assert.Contains("403:leer_registros", outcomes);
        Assert.DoesNotContain(outcomes, o => o == "ok:ver_catalogo");
    }

    [Fact]
    public async Task H5_CA3_cuarenta_llamadas_en_paralelo_respetan_la_cubeta()
    {
        var f = await host.SiblingAsync(s => { s["Mcp:Burst"] = "10"; s["Mcp:CallsPerMinute"] = "1"; });
        var conn = await ConnectAsync("mc-carla", f: f);
        var results = await Concurrently(40, i => CallAsync(conn.Access, "listar_catalogos", f: f));
        Assert.Equal(10, results.Count(r => !r["isError"]!.GetValue<bool>()));
        Assert.All(results.Where(r => r["isError"]!.GetValue<bool>()), r => Assert.Equal(429, ErrorOf(r)["status"]!.GetValue<int>()));
        // La cubeta es por persona: otra persona en la misma instancia tiene la suya.
        var other = await ConnectAsync("mc-dora", f: f);
        Assert.False((await CallAsync(other.Access, "listar_catalogos", f: f))["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task H5_CA3_los_limites_del_despachador_y_el_global_se_mantienen()
    {
        // Sin cubeta que estorbe, el tope global por persona (también por HTTP) corta la ráfaga.
        var f = await host.SiblingAsync(s => { s["Security:RequestsPerMinute"] = "12"; });
        var conn = await ConnectAsync("mc-eli", f: f);
        var served = 0;
        var limited = 0;
        for (var i = 0; i < 30; i++)
        {
            var res = await RpcAsync(conn.Access, "tools/call", new { name = "listar_catalogos", arguments = new { } }, f);
            if (res.Status == 429) { limited++; continue; }
            Assert.Equal(200, res.Status);
            if (res["result"]!["isError"]!.GetValue<bool>()) { Assert.Equal(429, ErrorOf(res["result"]!)["status"]!.GetValue<int>()); limited++; }
            else served++;
        }
        Assert.InRange(served, 1, 12);
        Assert.Equal(30 - served, limited);
    }

    [Fact]
    public async Task H5_CA4_un_resultado_grande_es_un_error_que_pide_paginar()
    {
        var f = await host.SiblingAsync(s => s["Mcp:MaxResultBytes"] = "2048");
        var conn = await ConnectAsync("admin", f: f);
        var error = ErrorOf(await CallAsync(conn.Access, "base_fisica", null, f));
        Assert.Equal(413, error["status"]!.GetValue<int>());
        Assert.Contains("filtra", error["error"]!.GetValue<string>());
        Assert.False((await CallAsync(conn.Access, "leer_registros", new { entity = "McTarea", pageSize = 1 }, f))["isError"]!.GetValue<bool>());
    }

    // ---------- H6 ----------

    [Fact]
    public async Task H6_CA1_mis_conexiones_y_desconectar()
    {
        var conn = await ConnectAsync("mc-gil");
        using var gil = await host.LoginAsync("mc-gil", Pwd);
        var list = (await gil.GetAsync("/api/mcp/connections")).Body!;
        Assert.Equal(Resource, list.Str("serverUrl"));
        var mine = list["connections"]!.AsArray().Single()!;
        Assert.Equal("Claude", mine.Str("client"));
        Assert.Equal("claude.ai", mine.Str("redirectHost"));
        Assert.Equal(["modelo", "datos"], mine["scopes"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.EndsWith("Z", mine.Str("createdAt"));
        // Reconectar el mismo cliente reemplaza la conexión (no se acumulan).
        var again = await ConnectAsync("mc-gil", clientId: conn.ClientId);
        Assert.Equal(401, (await RpcAsync(conn.Access, "ping")).Status);
        var current = (await gil.GetAsync("/api/mcp/connections")).Body!["connections"]!.AsArray().Single()!;
        Assert.Equal(204, (await gil.DeleteAsync($"/api/mcp/connections/{current.Id()}")).Status);
        Assert.Equal(401, (await RpcAsync(again.Access, "ping")).Status);
        Assert.Empty((await gil.GetAsync("/api/mcp/connections")).Body!["connections"]!.AsArray());
        Assert.Equal(404, (await gil.DeleteAsync($"/api/mcp/connections/{current.Id()}")).Status);
    }

    [Fact]
    public async Task H6_CA2_otra_persona_no_ve_ni_revoca_mi_conexion()
    {
        var conn = await ConnectAsync("mc-beto");
        var id = await host.ScalarAsync<int>($"SELECT g.Id FROM meta.McpGrant g JOIN meta.McpClient x ON x.Id = g.ClientId WHERE x.ClientId = '{conn.ClientId}' AND g.RevokedAt IS NULL");
        using var carla = await host.LoginAsync("mc-carla", Pwd);
        Assert.DoesNotContain((await carla.GetAsync("/api/mcp/connections")).Body!["connections"]!.AsArray(), c => c!.Id() == id);
        Assert.Equal(404, (await carla.DeleteAsync($"/api/mcp/connections/{id}")).Status);
        // Tampoco el administrador por esta ruta (es de la persona).
        using var admin = await host.AdminAsync();
        Assert.Equal(404, (await admin.DeleteAsync($"/api/mcp/connections/{id}")).Status);
        Assert.Equal(200, (await RpcAsync(conn.Access, "ping")).Status);
    }

    [Fact]
    public async Task H6_CA3_revocar_por_RFC_7009()
    {
        var conn = await ConnectAsync("mc-dora");
        using var api = host.Anonymous(false);
        // Otro cliente no revoca un token ajeno (pero la respuesta es la misma: 200).
        var other = await RegisterAsync();
        Assert.Equal(200, (await api.PostFormAsync("/oauth/revoke", new Dictionary<string, string> { ["token"] = conn.Access, ["client_id"] = other })).Status);
        Assert.Equal(200, (await RpcAsync(conn.Access, "ping")).Status);
        Assert.Equal(200, (await api.PostFormAsync("/oauth/revoke", new Dictionary<string, string> { ["token"] = "basura", ["client_id"] = conn.ClientId })).Status);
        Assert.Equal(400, (await api.PostFormAsync("/oauth/revoke", new Dictionary<string, string> { ["client_id"] = conn.ClientId })).Status);
        Assert.Equal(200, (await api.PostFormAsync("/oauth/revoke", new Dictionary<string, string> { ["token"] = conn.Refresh, ["client_id"] = conn.ClientId })).Status);
        Assert.Equal(401, (await RpcAsync(conn.Access, "ping")).Status);
    }

    [Fact]
    public async Task H6_CA4_la_bitacora_registra_conectar_revocar_y_reuso()
    {
        var conn = await ConnectAsync("mc-hugo");
        using var hugo = await host.LoginAsync("mc-hugo", Pwd);
        using var api = host.Anonymous(false);
        var next = await TokenAsync(api, ("grant_type", "refresh_token"), ("refresh_token", conn.Refresh), ("client_id", conn.ClientId));
        Assert.Equal(200, next.Status);
        await TokenAsync(api, ("grant_type", "refresh_token"), ("refresh_token", conn.Refresh), ("client_id", conn.ClientId)); // reuso
        var second = await ConnectAsync("mc-hugo");
        var id = (await hugo.GetAsync("/api/mcp/connections")).Body!["connections"]!.AsArray().Single()!.Id();
        Assert.Equal(204, (await hugo.DeleteAsync($"/api/mcp/connections/{id}")).Status);

        var uid = host.Rec["mc-hugo"];
        var actions = await host.ScalarAsync<string>($"SELECT STRING_AGG(Action, ',') WITHIN GROUP (ORDER BY Id) FROM meta.AuditLog WHERE UserId = {uid} AND Action LIKE 'mcp.%'");
        Assert.Equal("mcp.connect,mcp.reuse,mcp.connect,mcp.revoke", actions);
        var texts = await host.ScalarAsync<string>($"SELECT STRING_AGG(ISNULL(AfterJson, ''), ' ') FROM meta.AuditLog WHERE UserId = {uid} AND Action LIKE 'mcp.%'");
        foreach (var secret in new[] { conn.Access, conn.Refresh, second.Access, second.Refresh, next.Body!.Str("access_token")! })
            Assert.DoesNotContain(secret, texts);
        Assert.Contains("Claude", texts);
        Assert.Equal(second.ClientId, second.ClientId);
    }

    /// <summary>Regresión (revisión exploratoria 038, F-01): un cuerpo con UTF-8 inválido daba 500 (y su traza al log) en lugar de 400.</summary>
    [Fact]
    public async Task F01_un_cuerpo_con_UTF8_invalido_es_400_y_no_500()
    {
        var conn = await ConnectAsync("mc-ana");
        using var api = host.Anonymous(false);
        // «ó» en Latin-1 (0xF3) dentro de un texto JSON: bytes que no son UTF-8.
        static ByteArrayContent Latin1(string json)
        {
            var c = new ByteArrayContent(Encoding.Latin1.GetBytes(json));
            c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return c;
        }
        var reg = await api.SendAsync(HttpMethod.Post, "/oauth/register", Latin1("{\"client_name\":\"Claude (revisión)\",\"redirect_uris\":[\"" + Callback + "\"]}"));
        Assert.Equal(400, reg.Status);
        Assert.Equal("invalid_client_metadata", reg.Body!.Str("error"));
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Latin1("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"ver_catalogo\",\"arguments\":{\"entity\":\"Catálogo\"}}}") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", conn.Access);
        using var res = await api.Http.SendAsync(req);
        Assert.Equal(400, (int)res.StatusCode);
        Assert.Contains("-32700", await res.Content.ReadAsStringAsync());
    }

    // ---------- Revisión adversarial 038 ----------

    /// <summary>F-04: consentir sin canjear dejaba al cliente fuera del barrido para siempre, y nada limitaba los pendientes por dirección.</summary>
    [Fact]
    public async Task F04_un_cliente_que_nunca_canjeo_se_barre_y_cada_direccion_tiene_tope_de_pendientes()
    {
        var idle = await RegisterAsync();
        using (var ana = await host.LoginAsync("mc-ana", Pwd))
            await DecideAsync(ana, Query(idle, Callback, Verifier())); // consiente, nunca canjea
        await host.ExecAsync($"UPDATE meta.McpClient SET CreatedAt = DATEADD(hour, -2, SYSUTCDATETIME()) WHERE ClientId = '{idle}'");
        await RegisterAsync(); // el registro barre
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpClient WHERE ClientId = '{idle}'"));
        // Uno que sí canjeó no se barre.
        var used = await ConnectAsync("mc-beto");
        await host.ExecAsync($"UPDATE meta.McpClient SET CreatedAt = DATEADD(hour, -2, SYSUTCDATETIME()) WHERE ClientId = '{used.ClientId}'");
        await RegisterAsync();
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpClient WHERE ClientId = '{used.ClientId}'"));

        // Tope de pendientes por dirección (la de la prueba no tiene dirección: cuenta como una sola).
        var pending = await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.McpClient c WHERE c.CreatedIp IS NULL AND NOT EXISTS (SELECT 1 FROM meta.McpGrant g WHERE g.ClientId = c.Id)");
        var f = await host.SiblingAsync(s => s["Mcp:PendingClientsPerAddress"] = (pending + 2).ToString());
        Assert.Equal(201, (await RegisterRawAsync(f, host, new { redirect_uris = new[] { Callback } })).Status);
        Assert.Equal(201, (await RegisterRawAsync(f, host, new { redirect_uris = new[] { Callback } })).Status);
        var full = await RegisterRawAsync(f, host, new { redirect_uris = new[] { Callback } });
        Assert.Equal(429, full.Status);
        Assert.Equal("temporarily_unavailable", full.Body!.Str("error"));
    }

    /// <summary>F-05: revocar aceptaba multipart y formularios sin los límites del servidor de tokens.</summary>
    [Fact]
    public async Task F05_revocar_rechaza_multipart()
    {
        var conn = await ConnectAsync("mc-dora");
        using var api = host.Anonymous(false);
        var multipart = new MultipartFormDataContent { { new StringContent(conn.Access), "token" }, { new StringContent(conn.ClientId), "client_id" } };
        Assert.Equal(400, (await api.SendAsync(HttpMethod.Post, "/oauth/revoke", multipart)).Status);
        Assert.Equal(200, (await RpcAsync(conn.Access, "ping")).Status);
    }

    /// <summary>F-06: dos códigos de la misma persona y el mismo cliente canjeados a la vez dejaban dos conexiones vivas.</summary>
    [Fact]
    public async Task F06_canjes_simultaneos_de_la_misma_persona_y_cliente_dejan_una_conexion()
    {
        var client = await RegisterAsync();
        var codes = new List<(string Code, string Verifier)>();
        using (var eli = await host.LoginAsync("mc-eli", Pwd))
            for (var i = 0; i < 6; i++)
            {
                var v = Verifier();
                codes.Add((Params(await DecideAsync(eli, Query(client, Callback, v)))["code"], v));
            }
        var results = await Concurrently(6, async i =>
        {
            using var api = host.Anonymous(false);
            return await TokenAsync(api, ("grant_type", "authorization_code"), ("code", codes[i].Code), ("redirect_uri", Callback), ("client_id", client), ("code_verifier", codes[i].Verifier));
        });
        Assert.All(results, r => Assert.Equal(200, r.Status));
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpGrant g JOIN meta.McpClient x ON x.Id = g.ClientId WHERE x.ClientId = '{client}' AND g.RevokedAt IS NULL"));
    }

    /// <summary>F-07: los metadatos no llevaban no-store (una caché compartida podía guardar unos envenenados por la cabecera Host).</summary>
    [Fact]
    public async Task F07_los_metadatos_no_se_guardan_en_cache()
    {
        using var api = host.Anonymous(false);
        foreach (var path in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp", "/.well-known/oauth-authorization-server" })
            Assert.Contains("no-store", (await api.GetAsync(path)).Headers["Cache-Control"]);
    }

    /// <summary>F-08: el reuso sobrescribía cuándo se usó el código por primera vez.</summary>
    [Fact]
    public async Task F08_el_reuso_conserva_el_primer_uso()
    {
        var c = await CodeAsync("mc-gil");
        using var api = host.Anonymous(false);
        (string, string)[] form = [("grant_type", "authorization_code"), ("code", c.Code), ("redirect_uri", Callback), ("client_id", c.Client), ("code_verifier", c.Verifier)];
        Assert.Equal(200, (await TokenAsync(api, form)).Status);
        var sql = $"SELECT CONVERT(varchar(30), c.UsedAt, 126) FROM meta.McpCode c JOIN meta.McpClient x ON x.Id = c.ClientId WHERE x.ClientId = '{c.Client}'";
        var first = await host.ScalarAsync<string>(sql);
        await Task.Delay(50);
        Assert.Equal(400, (await TokenAsync(api, form)).Status);
        Assert.Equal(first, await host.ScalarAsync<string>(sql));
    }

    /// <summary>F-09: el nombre admitía caracteres de formato (bidi, ancho cero) con los que se disfraza en el consentimiento.</summary>
    [Fact]
    public async Task F09_el_nombre_del_cliente_no_admite_caracteres_de_formato()
    {
        foreach (var name in new[] { "Claude‮ia.live", "Cla​ude" })
        {
            var res = await RegisterRawAsync(null, host, JsonNode.Parse($"{{\"client_name\":\"{name}\",\"redirect_uris\":[\"{Callback}\"]}}")!);
            Assert.Equal(400, res.Status);
            Assert.Equal("invalid_client_metadata", res.Body!.Str("error"));
        }
    }

    /// <summary>F-10: «/mcp/» (barra final) llega a la misma ruta, pero BearerScope negaba el token MCP ahí.</summary>
    [Fact]
    public async Task F10_mcp_con_barra_final_acepta_el_token()
    {
        var conn = await ConnectAsync("mc-ana");
        using var api = host.Anonymous(false);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp/") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", conn.Access);
        using var res = await api.Http.SendAsync(req);
        Assert.Equal(200, (int)res.StatusCode);
    }

    [Fact]
    public async Task Con_Mcp_deshabilitado_todo_da_404()
    {
        var conn = await ConnectAsync("mc-ana");
        var f = await host.SiblingAsync(s => s["Mcp:Enabled"] = "false");
        using var api = f.Anonymous(false);
        Assert.Equal(404, (await api.GetAsync("/.well-known/oauth-authorization-server")).Status);
        Assert.Equal(404, (await api.GetAsync("/oauth/authorize?x=1")).Status);
        Assert.Equal(404, (await api.PostAsync("/oauth/register", new { redirect_uris = new[] { Callback } })).Status);
        Assert.Equal(404, (await RpcAsync(api, conn.Access, new { jsonrpc = "2.0", id = 1, method = "ping" })).Status);
        Assert.Equal("unsupported_grant_type", (await TokenAsync(api, ("grant_type", "refresh_token"), ("refresh_token", conn.Refresh), ("client_id", conn.ClientId))).Body!.Str("error"));
        using var ana = await f.LoginAsync("mc-ana", Pwd);
        Assert.Equal(404, (await ana.GetAsync("/api/mcp/connections")).Status);
    }
}

/// <summary>Validador mínimo de JSON Schema (el subconjunto que publican las herramientas: type, properties, required, items, additionalProperties, minimum).</summary>
public static class Schema
{
    public static List<string> Validate(JsonNode schema, JsonNode? value, string path)
    {
        var problems = new List<string>();
        Check(schema, value, path, problems);
        return problems;
    }

    private static string KindOf(JsonNode? v) => v switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue x => x.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => x.ToJsonString().Contains('.') ? "number" : "integer",
            _ => "null",
        },
        _ => "null",
    };

    private static void Check(JsonNode schema, JsonNode? value, string path, List<string> problems)
    {
        if (schema["type"] is { } t)
        {
            var allowed = t is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToList() : [t.GetValue<string>()];
            var kind = KindOf(value);
            if (!allowed.Contains(kind) && !(kind == "integer" && allowed.Contains("number")))
            {
                problems.Add($"{path}: se esperaba {string.Join("|", allowed)} y llegó {kind}");
                return;
            }
        }
        if (value is JsonObject o)
        {
            foreach (var r in schema["required"] as JsonArray ?? [])
                if (!o.ContainsKey(r!.GetValue<string>())) problems.Add($"{path}: falta «{r}»");
            var props = schema["properties"] as JsonObject;
            foreach (var (k, v) in o)
            {
                if (props?[k] is { } ps) Check(ps, v, $"{path}.{k}", problems);
                else if (schema["additionalProperties"] is JsonValue ap && !ap.GetValue<bool>()) problems.Add($"{path}: «{k}» no está en el esquema");
            }
        }
        if (value is JsonArray arr && schema["items"] is { } items)
            for (var i = 0; i < Math.Min(arr.Count, 50); i++) Check(items, arr[i], $"{path}[{i}]", problems);
        if (schema["minimum"] is { } min && value is JsonValue n && n.GetValueKind() == JsonValueKind.Number && n.GetValue<decimal>() < min.GetValue<decimal>())
            problems.Add($"{path}: menor que {min}");
    }
}
