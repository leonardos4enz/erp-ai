using System.Net;
using Erp.Api.Mcp;
using Erp.IntegrationTests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 040 · Endurecimiento 6 (auditoría 7), H1: el registro dinámico del MCP no se agota. Cuenta solo clientes vivos, barre
/// los que ya no tienen conexión, limita las conexiones por persona (también con canjes simultáneos), agrupa una IPv6 por su
/// /64 y solo un proxy listado cambia la dirección y el esquema. La dirección remota la fija la prueba con una cabecera
/// (<see cref="TestAddress"/>), como lo haría la conexión TCP: TestServer no tiene dirección.
/// </summary>
public class Hardening040Tests(Hardening040Tests.Host host) : IClassFixture<Hardening040Tests.Host>
{
    public const string Pwd = "frase larga del endurecimiento 040";
    private const string Callback = "https://claude.ai/api/mcp/auth_callback";
    private const string Resource = "https://localhost/mcp";

    /// <summary>La dirección remota de la petición sale de <c>X-Prueba-Ip</c> (antes de toda la canalización de la API).</summary>
    private sealed class TestAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, go) =>
            {
                if (http.Request.Headers["X-Prueba-Ip"].ToString() is { Length: > 0 } ip) http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                return go(http);
            });
            next(app);
        };
    }

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Users = [];

        protected override void Configure(IDictionary<string, string?> s)
        {
            s["Mcp:PendingClientsPerAddress"] = "100000";
            s["Security:TokenRequestsPerMinute"] = "100000";
        }

        protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IStartupFilter, TestAddress>();

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            Assert.Equal(200, (await admin.PostAsync("/api/admin/roles", new { name = "H40 Modelador", description = "Spec 040", canManageModel = true,
                canManageUsers = false, canViewAudit = false, permissions = Array.Empty<object>() })).Status);
            var role = await admin.RoleIdAsync("H40 Modelador");
            foreach (var u in new[] { "h40-ana", "h40-beto", "h40-carla", "h40-dora" })
            {
                var res = await admin.PostAsync("/api/admin/users", new { username = u, displayName = u, password = Pwd, roleIds = new[] { role } });
                Assert.True(res.Status == 201, res.Text);
                Users[u] = res.Body!.Id();
            }
        }
    }

    // ---------- Cliente MCP simulado ----------

    private static Client Api(ErpHost.Factory? f, ErpHost host, string? ip = null)
    {
        var api = f?.Anonymous(false) ?? host.Anonymous(false);
        if (ip is not null) api.Http.DefaultRequestHeaders.Add("X-Prueba-Ip", ip);
        return api;
    }

    private async Task<Res> RegisterRawAsync(ErpHost.Factory? f = null, string? ip = null)
    {
        using var api = Api(f, host, ip);
        return await api.PostAsync("/oauth/register", new { client_name = "Claude", redirect_uris = new[] { Callback }, token_endpoint_auth_method = "none" });
    }

    private async Task<string> RegisterAsync(ErpHost.Factory? f = null)
    {
        var res = await RegisterRawAsync(f);
        Assert.True(res.Status == 201, res.Text);
        return res.Body!.Str("client_id")!;
    }

    private static string Query(string clientId, string verifier) =>
        $"response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(Callback)}" +
        $"&code_challenge={McpFormat.Challenge(verifier)}&code_challenge_method=S256&state=e&scope=modelo&resource={Uri.EscapeDataString(Resource)}";

    /// <summary>La persona consiente en Conectar; devuelve el código (o falla si la pantalla lo rechaza).</summary>
    private static async Task<string> CodeAsync(Client person, string clientId, string verifier)
    {
        var query = Query(clientId, verifier);
        var check = await person.GetAsync("/api/mcp/authorize?" + query);
        Assert.True(check.Status == 200, check.Text);
        var res = await person.PostAsync("/api/mcp/authorize", new { query, approve = true, scopes = new[] { "modelo" } });
        Assert.True(res.Status == 200, res.Text);
        return QueryHelpers.ParseQuery(new Uri(res["redirect"]!.GetValue<string>()).Query)["code"].ToString();
    }

    private async Task<Res> ExchangeAsync(string clientId, string code, string verifier, ErpHost.Factory? f = null)
    {
        using var api = Api(f, host);
        return await api.PostFormAsync("/oauth/token", new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Callback, ["client_id"] = clientId, ["code_verifier"] = verifier,
        });
    }

    private async Task<string> ConnectAsync(string user, string? clientId = null, ErpHost.Factory? f = null)
    {
        clientId ??= await RegisterAsync(f);
        var verifier = McpFormat.New("v");
        using var person = f is null ? await host.LoginAsync(user, Pwd) : await f.LoginAsync(user, Pwd);
        var t = await ExchangeAsync(clientId, await CodeAsync(person, clientId, verifier), verifier, f);
        Assert.True(t.Status == 200, t.Text);
        return clientId;
    }

    private Task<int> LiveClientsAsync() => host.ScalarAsync<int>("""
        SELECT COUNT(*) FROM meta.McpClient c
         WHERE NOT EXISTS (SELECT 1 FROM meta.McpGrant g WHERE g.ClientId = c.Id)
            OR EXISTS (SELECT 1 FROM meta.McpGrant g WHERE g.ClientId = c.Id AND g.RevokedAt IS NULL
                       AND EXISTS (SELECT 1 FROM meta.McpToken t WHERE t.GrantId = g.Id AND t.Kind = 'r' AND t.UsedAt IS NULL AND t.ExpiresAt > SYSUTCDATETIME()))
        """);

    // ---------- H1 CA1–CA2 ----------

    [Fact]
    public async Task H1_CA1_mil_clientes_con_la_conexion_revocada_no_impiden_registrar()
    {
        // Regresión (auditoría 7, D-01 b): cada conexión revocada (o vencida) dejaba su cliente para siempre y MaxClients los
        // contaba todos: con 1 000, nadie volvía a registrar un cliente. Hoy (autorizados ahora) no se barren todavía, pero
        // no cuentan.
        var uid = host.Users["h40-dora"];
        await host.ExecAsync($"""
            INSERT meta.McpClient (ClientId, Name, RedirectUris, CreatedIp, LastAuthorizedAt)
            SELECT TOP (1000) CONCAT('erpmcp_r', REPLACE(CONVERT(varchar(36), NEWID()), '-', '')), N'Revocado 040', N'["{Callback}"]', '10.40.0.1', SYSUTCDATETIME()
              FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            INSERT meta.McpGrant (ClientId, UserId, SecurityStamp, Scopes, Resource, RevokedAt, RevokedReason)
            SELECT c.Id, u.Id, u.SecurityStamp, N'modelo', N'{Resource}', SYSUTCDATETIME(), N'person'
              FROM meta.McpClient c CROSS JOIN meta.[User] u WHERE c.Name = N'Revocado 040' AND u.Id = {uid};
            """);
        var live = await LiveClientsAsync();
        var f = await host.SiblingAsync(s => s["Mcp:MaxClients"] = (live + 1).ToString());
        var ok = await RegisterRawAsync(f);
        Assert.True(ok.Status == 201, ok.Text);
        // El tope sigue: el siguiente ya no cabe.
        var full = await RegisterRawAsync(f);
        Assert.Equal(503, full.Status);
        Assert.Equal(1000, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.McpClient WHERE Name = N'Revocado 040'"));
    }

    [Fact]
    public async Task H1_CA2_el_barrido_borra_clientes_sin_conexion_viva_con_sus_tokens()
    {
        // Una conexión que la persona desconectó hace dos días (muerta), otra desconectada hace una hora (se conserva un día) y
        // una viva de hace dos días (se conserva).
        var dead = await ConnectAsync("h40-ana");
        var recent = await ConnectAsync("h40-ana");
        var alive = await ConnectAsync("h40-beto");
        using (var ana = await host.LoginAsync("h40-ana", Pwd))
            foreach (var c in (await ana.GetAsync("/api/mcp/connections")).Body!["connections"]!.AsArray())
                Assert.Equal(204, (await ana.DeleteAsync($"/api/mcp/connections/{c!["id"]}")).Status);
        await host.ExecAsync($"""
            UPDATE meta.McpClient SET CreatedAt = DATEADD(day, -2, SYSUTCDATETIME()), LastAuthorizedAt = DATEADD(day, -2, SYSUTCDATETIME()) WHERE ClientId IN ('{dead}', '{alive}', '{recent}');
            UPDATE g SET RevokedAt = DATEADD(day, -2, SYSUTCDATETIME()) FROM meta.McpGrant g JOIN meta.McpClient c ON c.Id = g.ClientId WHERE c.ClientId = '{dead}';
            UPDATE g SET RevokedAt = DATEADD(hour, -1, SYSUTCDATETIME()) FROM meta.McpGrant g JOIN meta.McpClient c ON c.Id = g.ClientId WHERE c.ClientId = '{recent}';
            """);
        var deadId = await host.ScalarAsync<int>($"SELECT Id FROM meta.McpClient WHERE ClientId = '{dead}'");
        Assert.True(await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpToken t JOIN meta.McpGrant g ON g.Id = t.GrantId WHERE g.ClientId = {deadId}") > 0);

        await RegisterAsync(); // el registro barre
        // Regresión (auditoría 7, D-01): un cliente que tuvo conexión no se barría nunca.
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpClient WHERE ClientId = '{dead}'"));
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpGrant WHERE ClientId = {deadId}"));
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpCode WHERE ClientId = {deadId}"));
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpClient WHERE ClientId = '{recent}'"));
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.McpClient WHERE ClientId = '{alive}'"));
        // La conexión viva sigue sirviendo.
        using var beto = await host.LoginAsync("h40-beto", Pwd);
        Assert.Single((await beto.GetAsync("/api/mcp/connections")).Body!["connections"]!.AsArray());
    }

    // ---------- H1 CA3 ----------

    [Fact]
    public async Task H1_CA3_una_persona_no_pasa_del_tope_de_conexiones()
    {
        var f = await host.SiblingAsync(s => s["Mcp:MaxConnectionsPerUser"] = "2");
        var a = await ConnectAsync("h40-carla", f: f);
        // Dos códigos con una sola conexión viva: la pantalla los deja pasar; el canje es el que manda.
        var c1 = await RegisterAsync(f);
        var c2 = await RegisterAsync(f);
        var (v1, v2) = (McpFormat.New("v"), McpFormat.New("v"));
        string code1, code2;
        using (var carla = await f.LoginAsync("h40-carla", Pwd))
        {
            code1 = await CodeAsync(carla, c1, v1);
            code2 = await CodeAsync(carla, c2, v2);
        }
        Assert.Equal(200, (await ExchangeAsync(c1, code1, v1, f)).Status);
        // Regresión (auditoría 7, D-01 b): sin tope por persona, una cuenta llenaba MaxClients.
        var over = await ExchangeAsync(c2, code2, v2, f);
        Assert.Equal(400, over.Status);
        Assert.Equal("invalid_grant", over.Body!.Str("error"));
        Assert.Contains("Conexiones de Claude", over.Body!.Str("error_description"));

        using var person = await f.LoginAsync("h40-carla", Pwd);
        // La pantalla avisa antes de consentir (409, con el motivo).
        var c3 = await RegisterAsync(f);
        var check = await person.GetAsync("/api/mcp/authorize?" + Query(c3, McpFormat.New("v")));
        Assert.Equal(409, check.Status);
        Assert.Contains("Conexiones de Claude", check.Text);
        Assert.Equal(409, (await person.PostAsync("/api/mcp/authorize", new { query = Query(c3, McpFormat.New("v")), approve = true, scopes = new[] { "modelo" } })).Status);
        // Volver a conectar un cliente que ya tiene no cuenta: reemplaza su conexión.
        await ConnectAsync("h40-carla", clientId: a, f: f);
        Assert.Equal(2, (await person.GetAsync("/api/mcp/connections")).Body!["connections"]!.AsArray().Count);
    }

    [Fact]
    public async Task H1_CA3_diez_canjes_a_la_vez_con_tope_dos_dan_dos()
    {
        var f = await host.SiblingAsync(s => s["Mcp:MaxConnectionsPerUser"] = "2");
        var codes = new List<(string Client, string Code, string Verifier)>();
        using (var dora = await f.LoginAsync("h40-dora", Pwd))
            for (var i = 0; i < 10; i++)
            {
                var c = await RegisterAsync(f);
                var v = McpFormat.New("v");
                codes.Add((c, await CodeAsync(dora, c, v), v));
            }
        var results = await Concurrently(10, i => ExchangeAsync(codes[i].Client, codes[i].Code, codes[i].Verifier, f));
        Assert.True(results.Count(r => r.Status == 200) == 2, string.Join("\n", results.Select(r => r.ToString())));
        Assert.All(results.Where(r => r.Status != 200), r => Assert.Equal("invalid_grant", r.Body!.Str("error")));
        Assert.Equal(2, await host.ScalarAsync<int>($"""
            SELECT COUNT(*) FROM meta.McpGrant g WHERE g.UserId = {host.Users["h40-dora"]} AND g.RevokedAt IS NULL
               AND EXISTS (SELECT 1 FROM meta.McpToken t WHERE t.GrantId = g.Id AND t.Kind = 'r' AND t.UsedAt IS NULL AND t.ExpiresAt > SYSUTCDATETIME())
            """));
    }

    // ---------- H1 CA4–CA5 ----------

    [Fact]
    public async Task H1_CA4_una_red_64_no_pasa_del_tope_de_pendientes()
    {
        var f = await host.SiblingAsync(s => s["Mcp:PendingClientsPerAddress"] = "3");
        foreach (var ip in new[] { "2001:db8:40:1::1", "2001:db8:40:1::2", "2001:db8:40:1:ffff::3" })
            Assert.Equal(201, (await RegisterRawAsync(f, ip)).Status);
        // Regresión (auditoría 7, D-01 a): se contaba por dirección completa; otra dirección de la misma /64 pasaba.
        var full = await RegisterRawAsync(f, "2001:db8:40:1::4");
        Assert.Equal(429, full.Status);
        Assert.Equal("temporarily_unavailable", full.Body!.Str("error"));
        Assert.Equal(201, (await RegisterRawAsync(f, "2001:db8:40:2::1")).Status); // otra red
        Assert.Equal(3, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.McpClient WHERE CreatedIp = '2001:db8:40:1::/64'"));
    }

    [Fact]
    public async Task H1_CA4_el_limite_oauth_agrupa_la_red_64()
    {
        var f = await host.SiblingAsync(s => s["Security:TokenRequestsPerMinute"] = "3");
        async Task<int> TokenAsync(string ip)
        {
            using var api = Api(f, host, ip);
            return (await api.PostFormAsync("/oauth/token", new Dictionary<string, string> { ["grant_type"] = "client_credentials" })).Status;
        }
        foreach (var ip in new[] { "2001:db8:41:1::1", "2001:db8:41:1::2", "2001:db8:41:1::3" })
            Assert.NotEqual(429, await TokenAsync(ip));
        Assert.Equal(429, await TokenAsync("2001:db8:41:1::4"));
        Assert.NotEqual(429, await TokenAsync("2001:db8:41:2::1"));
        // Revisión adversarial 040 (F-05): el prefijo se configura (con /128, cada dirección cuenta aparte).
        var g = await host.SiblingAsync(s => { s["Security:TokenRequestsPerMinute"] = "1"; s["Security:Ipv6PrefixLength"] = "128"; });
        foreach (var ip in new[] { "2001:db8:41:9::1", "2001:db8:41:9::2" })
        {
            using var api = Api(g, host, ip);
            Assert.NotEqual(429, (await api.PostFormAsync("/oauth/token", new Dictionary<string, string> { ["grant_type"] = "client_credentials" })).Status);
        }
    }

    /// <summary>
    /// Revisión adversarial 040 (F-01): contar lo vivo bajo <c>UPDLOCK, HOLDLOCK</c> sobre las conexiones leía los tokens en el
    /// orden contrario al de una renovación (token → conexión): un canje y una renovación de la misma persona se interbloqueaban.
    /// </summary>
    [Fact]
    public async Task F01_canjes_y_renovaciones_simultaneos_de_la_misma_persona_no_se_interbloquean()
    {
        using (var admin = await host.AdminAsync())
        {
            var res = await admin.PostAsync("/api/admin/users", new { username = "h40-f01", displayName = "h40-f01", password = Pwd,
                roleIds = new[] { await admin.RoleIdAsync("H40 Modelador") } });
            Assert.True(res.Status == 201, res.Text);
        }
        var refreshes = new List<(string Client, string Refresh)>();
        var codes = new List<(string Client, string Code, string Verifier)>();
        using (var person = await host.LoginAsync("h40-f01", Pwd))
        {
            for (var i = 0; i < 10; i++)
            {
                var c = await RegisterAsync();
                var v = McpFormat.New("v");
                var t = await ExchangeAsync(c, await CodeAsync(person, c, v), v);
                Assert.True(t.Status == 200, t.Text);
                refreshes.Add((c, t.Body!.Str("refresh_token")!));
            }
            for (var i = 0; i < 10; i++)
            {
                var c = await RegisterAsync();
                var v = McpFormat.New("v");
                codes.Add((c, await CodeAsync(person, c, v), v));
            }
        }
        // Diez renovaciones y diez canjes nuevos de la misma persona, a la vez.
        var results = await Concurrently(20, async i =>
        {
            if (i % 2 == 1) return await ExchangeAsync(codes[i / 2].Client, codes[i / 2].Code, codes[i / 2].Verifier);
            using var api = Api(null, host);
            return await api.PostFormAsync("/oauth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = refreshes[i / 2].Refresh, ["client_id"] = refreshes[i / 2].Client,
            });
        });
        // Un interbloqueo sale como 500; con el tope de 20, los diez canjes caben.
        Assert.True(results.All(r => r.Status == 200), string.Join("\n", results.Select(r => r.ToString())));
    }

    /// <summary>Revisión adversarial 040 (F-03): conectar y desconectar en bucle dejaba filas sin tope (las muertas no contaban).</summary>
    [Fact]
    public async Task F03_las_conexiones_desconectadas_hoy_cuentan_en_el_tope_por_persona()
    {
        var f = await host.SiblingAsync(s => s["Mcp:MaxConnectionsPerUser"] = "2");
        using (var admin = await host.AdminAsync())
        {
            var res = await admin.PostAsync("/api/admin/users", new { username = "h40-f03", displayName = "h40-f03", password = Pwd,
                roleIds = new[] { await admin.RoleIdAsync("H40 Modelador") } });
            Assert.True(res.Status == 201, res.Text);
        }
        for (var i = 0; i < 2; i++)
        {
            await ConnectAsync("h40-f03", f: f);
            using var person = await f.LoginAsync("h40-f03", Pwd);
            foreach (var c in (await person.GetAsync("/api/mcp/connections")).Body!["connections"]!.AsArray())
                Assert.Equal(204, (await person.DeleteAsync($"/api/mcp/connections/{c!["id"]}")).Status);
        }
        // Sin conexiones vivas, pero dos desconectadas hoy: la tercera espera.
        using var again = await f.LoginAsync("h40-f03", Pwd);
        var check = await again.GetAsync("/api/mcp/authorize?" + Query(await RegisterAsync(f), McpFormat.New("v")));
        Assert.Equal(409, check.Status);
        Assert.Contains("24 horas", check.Text);
        // Pasado el día, ya no cuentan.
        await host.ExecAsync("UPDATE meta.McpGrant SET RevokedAt = DATEADD(day, -2, SYSUTCDATETIME()) WHERE UserId = (SELECT Id FROM meta.[User] WHERE Username = 'h40-f03')");
        await ConnectAsync("h40-f03", f: f);
    }

    [Fact]
    public async Task H1_CA5_solo_un_proxy_conocido_cambia_la_direccion_y_el_esquema()
    {
        var f = await host.SiblingAsync(s =>
        {
            s["Security:ForwardedProxies:0"] = "10.42.0.0/24";
            s["Mcp:PendingClientsPerAddress"] = "2";
        });
        async Task<Res> SendAsync(string remote, string? forwardedFor, string? proto, Func<Client, Task<Res>> call)
        {
            using var api = Api(f, host, remote);
            if (forwardedFor is not null) api.Http.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
            if (proto is not null) api.Http.DefaultRequestHeaders.Add("X-Forwarded-Proto", proto);
            return await call(api);
        }
        Task<Res> Register(Client api) => api.PostAsync("/oauth/register", new { redirect_uris = new[] { Callback } });

        // Detrás del proxy, cada cliente cuenta por su propia dirección (antes, todos por la del proxy: R-021-5).
        Assert.Equal(201, (await SendAsync("10.42.0.7", "2001:db8:42:1::1", null, Register)).Status);
        Assert.Equal(201, (await SendAsync("10.42.0.7", "2001:db8:42:1::2", null, Register)).Status);
        Assert.Equal(429, (await SendAsync("10.42.0.7", "2001:db8:42:1::3", null, Register)).Status);
        Assert.Equal(201, (await SendAsync("10.42.0.7", "203.0.113.9", null, Register)).Status);
        Assert.Equal(2, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.McpClient WHERE CreatedIp = '2001:db8:42:1::/64'"));
        // Revisión adversarial 040 (F-06): un salto por omisión. Un cliente de la propia red listada (10.42.0.99) que manda su
        // X-Forwarded-For inventado a través del proxy no elige su dirección: cuenta por la suya.
        Assert.Equal(201, (await SendAsync("10.42.0.7", "2001:db8:42:7::1, 10.42.0.99", null, Register)).Status);
        Assert.Equal(1, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.McpClient WHERE CreatedIp = '10.42.0.99'"));
        Assert.Equal(0, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.McpClient WHERE CreatedIp = '2001:db8:42:7::/64'"));
        // Quien no es un proxy listado no elige su dirección: cuenta por la suya.
        Assert.Equal(201, (await SendAsync("198.51.100.4", "2001:db8:42:9::1", null, Register)).Status);
        Assert.Equal(1, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.McpClient WHERE CreatedIp = '198.51.100.4'"));
        Assert.Equal(0, await host.ScalarAsync<int>("SELECT COUNT(*) FROM meta.McpClient WHERE CreatedIp = '2001:db8:42:9::/64'"));

        // El esquema público (R-018-4): el del proxy listado manda; el de cualquier otro se ignora.
        Task<Res> Meta(Client api) => api.GetAsync("/.well-known/oauth-authorization-server");
        Assert.Equal("http://localhost", (await SendAsync("10.42.0.7", "203.0.113.9", "http", Meta)).Body!.Str("issuer"));
        Assert.Equal("https://localhost", (await SendAsync("198.51.100.4", "203.0.113.9", "http", Meta)).Body!.Str("issuer"));
    }
}
