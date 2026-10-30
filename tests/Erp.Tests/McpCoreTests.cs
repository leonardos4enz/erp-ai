using System.Text.Json.Nodes;
using Erp.Api.Infrastructure;
using Erp.Api.Mcp;
using Erp.Api.Studio;
using Microsoft.AspNetCore.Http;

namespace Erp.Tests;

/// <summary>
/// Spec 038: piezas del servidor MCP sin base de datos: PKCE S256, URL de retorno, formatos, alcances, el token MCP fuera de
/// <c>/mcp</c>, el selector de esquema y el catálogo que se publica (solo lectura del entorno real, esquemas precisos).
/// </summary>
public class McpCoreTests
{
    [Fact]
    public void El_reto_S256_coincide_con_el_RFC_7636()
    {
        // RFC 7636, apéndice B.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", McpFormat.Challenge(verifier));
        Assert.True(McpFormat.VerifyS256(verifier, "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        Assert.False(McpFormat.VerifyS256(verifier, "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cN"));
        Assert.False(McpFormat.VerifyS256(verifier[..42], McpFormat.Challenge(verifier[..42])), "un verificador de menos de 43 caracteres no vale");
        Assert.False(McpFormat.VerifyS256(verifier + "!", McpFormat.Challenge(verifier + "!")), "solo caracteres unreserved");
        // El reto «plain» (el propio verificador) no pasa como S256.
        Assert.False(McpFormat.VerifyS256(verifier, verifier));
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", null)]
    [InlineData("http://localhost:33418/callback", null)]
    [InlineData("http://127.0.0.1:5000/cb", null)]
    [InlineData("http://[::1]:5000/cb", null)]
    [InlineData("http://claude.ai/cb", "https")]
    [InlineData("http://localhost.evil.com/cb", "https")]
    [InlineData("https://claude.ai/cb#x", "fragmento")]
    [InlineData("https://user:pass@claude.ai/cb", "usuario")]
    [InlineData("javascript:alert(1)", "absoluta")]
    [InlineData("file:///etc/passwd", "https")]
    [InlineData("/relativa", "absoluta")]
    [InlineData("https://claude.ai/c b", "espacios")]
    [InlineData("", "obligatoria")]
    public void La_URL_de_retorno_se_valida(string uri, string? problem)
    {
        var p = RedirectUris.Problem(uri);
        if (problem is null) Assert.Null(p);
        else Assert.Contains(problem, p ?? "(válida)");
        Assert.NotNull(RedirectUris.Problem("https://claude.ai/" + new string('a', 500)));
    }

    [Fact]
    public void Un_sitio_conocido_es_el_de_la_lista_o_el_propio_equipo()
    {
        string[] hosts = ["claude.ai", "claude.com"];
        Assert.True(RedirectUris.Trusted("https://claude.ai/api/mcp/auth_callback", hosts));
        Assert.True(RedirectUris.Trusted("https://www.claude.com/cb", hosts));
        Assert.True(RedirectUris.Trusted("http://localhost:9999/cb", hosts));
        Assert.False(RedirectUris.Trusted("https://evilclaude.ai/cb", hosts));
        Assert.False(RedirectUris.Trusted("https://claude.ai.evil.com/cb", hosts));
        Assert.Equal("claude.ai", RedirectUris.Host("https://claude.ai/api/mcp/auth_callback"));
        Assert.Equal("localhost:9999", RedirectUris.Host("http://localhost:9999/cb"));
        // Revisión adversarial F-09: un homógrafo (la «с» es cirílica) se muestra en punycode y no es conocido.
        Assert.StartsWith("xn--", RedirectUris.Host("https://сlaude.ai/cb"));
        Assert.False(RedirectUris.Trusted("https://сlaude.ai/cb", hosts));
        Assert.False(RedirectUris.SafeName("Claude‮"));
        Assert.True(RedirectUris.SafeName("Claude (revisión)"));
    }

    [Fact]
    public void Los_formatos_tienen_su_prefijo_y_256_bits()
    {
        Assert.True(McpFormat.IsClientId(McpFormat.NewClientId()));
        foreach (var prefix in new[] { McpFormat.CodePrefix, McpFormat.AccessPrefix, McpFormat.RefreshPrefix })
        {
            var s = McpFormat.New(prefix);
            Assert.True(McpFormat.Is(prefix, s), s);
            Assert.Equal(prefix.Length + 43, s.Length);
        }
        Assert.False(McpFormat.Is(McpFormat.AccessPrefix, McpFormat.New(McpFormat.RefreshPrefix)));
        Assert.NotEqual(McpFormat.New(McpFormat.AccessPrefix), McpFormat.New(McpFormat.AccessPrefix));
    }

    [Fact]
    public void Los_alcances_MCP_son_modelo_y_datos()
    {
        Assert.Equal(["modelo"], McpScopes.Parse(null)!);
        Assert.Equal(["modelo"], McpScopes.Parse("")!);
        Assert.Equal(["modelo", "datos"], McpScopes.Parse("datos")!);
        Assert.Equal(["modelo", "datos"], McpScopes.Parse("datos modelo datos")!);
        // La copia aislada no se ofrece por MCP (CLARIFY 2), ni los alcances de la 018.
        Assert.Null(McpScopes.Parse("aislado"));
        Assert.Null(McpScopes.Parse("modelo data.read"));
    }

    [Fact]
    public void BearerScope_niega_el_token_MCP_fuera_de_mcp()
    {
        var mcp = new BearerCredential(McpTokenHandler.SchemeName, "modelo datos");
        Assert.Null(BearerScope.Check(mcp, "/mcp", "POST"));
        Assert.NotNull(BearerScope.Check(mcp, "/api/data/StTarea", "GET"));
        Assert.NotNull(BearerScope.Check(mcp, "/api/auth/me", "GET"));
        Assert.NotNull(BearerScope.Check(mcp, "/odata/StTarea", "GET"));
        Assert.NotNull(BearerScope.Check(mcp, "/mcp/otra", "POST"));
        Assert.NotNull(BearerScope.Check(mcp, "/api/mcp/connections", "GET"));
    }

    private static HttpContext Request(string path, string? authorization = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        if (authorization is not null) http.Request.Headers.Authorization = authorization;
        http.Request.Headers.Cookie = "erp.auth=algo";
        return http;
    }

    [Fact]
    public void El_selector_manda_mcp_y_el_token_MCP_a_su_manejador()
    {
        // En /mcp la cookie no cuenta (sin CSRF posible); con un token de servicio o una clave de API, tampoco.
        Assert.Equal(McpTokenHandler.SchemeName, ErpApp.SelectScheme(Request("/mcp")));
        Assert.Equal(McpTokenHandler.SchemeName, ErpApp.SelectScheme(Request("/mcp", "Bearer erpat_" + new string('a', 43))));
        Assert.Equal(McpTokenHandler.SchemeName, ErpApp.SelectScheme(Request("/mcp", "Bearer erpk_x")));
        // Fuera de /mcp, el token MCP va a su manejador (y BearerScope lo niega); lo demás, como antes.
        Assert.Equal(McpTokenHandler.SchemeName, ErpApp.SelectScheme(Request("/api/data/X", "Bearer erpmt_" + new string('a', 43))));
        Assert.Equal(Erp.Api.Integrations.OAuthTokenHandler.SchemeName, ErpApp.SelectScheme(Request("/api/data/X", "Bearer erpat_" + new string('a', 43))));
        Assert.Equal(Erp.Api.Integrations.ApiKeyHandler.SchemeName, ErpApp.SelectScheme(Request("/api/data/X", "Bearer erpk_x")));
        Assert.Equal("Cookies", ErpApp.SelectScheme(Request("/api/data/X")));
        Assert.Equal("Cookies", ErpApp.SelectScheme(Request("/mcpx")));
    }

    [Fact]
    public void El_catalogo_MCP_solo_tiene_herramientas_de_lectura_del_entorno_real()
    {
        // Todas las del entorno real, y solo esas: ni la copia aislada ni «entregar» (CLARIFY 2).
        Assert.Equal(StudioTools.All.Where(t => t.Target == ToolTarget.Real).Select(t => t.Name).Order(),
            McpTools.All.Select(t => t.Name).Order());
        foreach (var t in McpTools.All)
        {
            var route = RouteInventoryTests.Routes.Single(r => r.Method == t.Tool.Method && r.Path == t.Tool.Route);
            Assert.False(Erp.Api.Studio.RouteDispatcher.Writes(route.Endpoint, route.Method), $"{t.Name} usa {route.Key}, que escribe.");
            var annotations = McpTools.Describe(t)["annotations"]!;
            Assert.True(annotations["readOnlyHint"]!.GetValue<bool>());
            Assert.True(annotations["idempotentHint"]!.GetValue<bool>());
            Assert.False(annotations["destructiveHint"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void Los_esquemas_de_entrada_y_salida_son_precisos()
    {
        foreach (var t in McpTools.All)
        {
            var input = t.Tool.InputSchema;
            Assert.Equal("object", input["type"]!.GetValue<string>());
            Assert.False(input["additionalProperties"]!.GetValue<bool>(), $"{t.Name}: additionalProperties");
            foreach (var (name, prop) in (JsonObject)input["properties"]!)
            {
                var type = prop!["type"]!.GetValue<string>();
                if (type == "array") Assert.True(prop["items"] is JsonObject { Count: > 0 }, $"{t.Name}.{name}: arreglo sin items");
                if (type == "integer") Assert.Equal(1, prop["minimum"]!.GetValue<int>());
                Assert.False(string.IsNullOrWhiteSpace(prop["description"]?.GetValue<string>()), $"{t.Name}.{name}: sin descripción");
            }
            Assert.Equal("object", t.OutputSchema["type"]!.GetValue<string>());
            Assert.True(t.OutputSchema["required"] is JsonArray { Count: > 0 }, $"{t.Name}: outputSchema sin required");
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
        }
        // Paginación explícita (H5 CA2).
        var page = McpTools.Find("leer_registros")!;
        Assert.NotNull(page.Tool.InputSchema["properties"]!["pageSize"]);
        Assert.Equal(["items", "total", "page", "pageSize", "nextPage"], page.OutputSchema["required"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void Un_arreglo_de_la_API_se_publica_en_items()
    {
        Assert.Equal("[1,2]", McpTools.AsObject(new JsonArray(1, 2))["items"]!.ToJsonString());
        Assert.Equal("{\"a\":1}", McpTools.AsObject(new JsonObject { ["a"] = 1 }).ToJsonString());
    }
}
