using System.Text.Json.Nodes;
using Erp.Api.Infrastructure;
using Erp.Api.Mcp;
using Erp.Api.Studio;
using Microsoft.AspNetCore.Http;

namespace Erp.Tests;

/// <summary>
/// Spec 038: piezas del servidor MCP sin base de datos: PKCE S256, URL de retorno, formatos, alcances, el token MCP fuera de
/// <c>/mcp</c>, el selector de esquema y el catálogo que se publica (solo lectura del entorno real, esquemas precisos). Spec 044:
/// alcances de la copia y de las propuestas, escrituras solo en la copia o como propuesta y con clave, huella de la entrada y
/// nombres de las copias por conexión.
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
    public void Los_alcances_MCP_son_modelo_datos_aislado_y_proponer()
    {
        Assert.Equal(["modelo"], McpScopes.Parse(null)!);
        Assert.Equal(["modelo"], McpScopes.Parse("")!);
        Assert.Equal(["modelo", "datos"], McpScopes.Parse("datos")!);
        Assert.Equal(["modelo", "datos"], McpScopes.Parse("datos modelo datos")!);
        // Spec 044 (H1 CA1): la copia aislada y las propuestas, en su orden; los de la 018 y los desconocidos, no.
        Assert.Equal(["modelo", "datos", "aislado", "proponer"], McpScopes.Parse("proponer aislado datos")!);
        Assert.Null(McpScopes.Parse("modelo data.read"));
        Assert.Null(McpScopes.Parse("escribir"));
        Assert.Equal(["aislado", "proponer"], McpScopes.ModelerOnly);
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
    public void El_catalogo_MCP_lee_en_el_entorno_real_y_escribe_solo_en_la_copia_o_como_propuesta()
    {
        // Spec 044: todas las de Studio (las del entorno real, las de la copia y «entregar») más «aislado_descartar».
        Assert.Equal(StudioTools.All.Select(t => t.Name).Append(McpTools.DiscardName).Order(), McpTools.All.Select(t => t.Name).Order());
        // H3 CA1: escriben solo las de la copia o la propuesta, y toda escritura no idempotente pide clave.
        foreach (var t in McpTools.All.Where(t => !t.ReadOnly))
        {
            Assert.True(t.Tool.Target is ToolTarget.Sandbox or ToolTarget.Server, $"{t.Name} escribe fuera de la copia o de una propuesta.");
            var input = t.InputSchema;
            Assert.Equal(t.NeedsKey, input["required"]!.AsArray().Any(x => x!.GetValue<string>() == McpIdempotency.Field));
            Assert.Equal(!t.Idempotent, t.NeedsKey);
        }
        Assert.Equal(["aislado_actualizar", "aislado_aplicar_plan", "aislado_crear", "aislado_procesar_asincronas", "aislado_transicion", "entregar"],
            McpTools.All.Where(t => t.NeedsKey).Select(t => t.Name).Order());
        Assert.Equal("proponer", McpTools.Find("entregar")!.Scope);
        Assert.All(McpTools.All.Where(t => t.Tool.Target != ToolTarget.Real && t.Name != "entregar"), t => Assert.Equal("aislado", t.Scope));
        Assert.True(McpTools.Describe(McpTools.Find(McpTools.DiscardName)!)["annotations"]!["destructiveHint"]!.GetValue<bool>());
        foreach (var t in McpTools.All.Where(t => t.Tool.Target == ToolTarget.Real))
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
            var input = t.InputSchema;
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
    public void La_huella_de_la_entrada_no_depende_del_orden()
    {
        var a = McpIdempotency.Hash("aislado_crear", (JsonObject)JsonNode.Parse("""{"entity":"X","values":{"a":1,"b":[1,{"y":2,"x":1}]}}""")!);
        var b = McpIdempotency.Hash("aislado_crear", (JsonObject)JsonNode.Parse("""{ "values": { "b": [1, {"x":1,"y":2}], "a": 1 }, "entity": "X" }""")!);
        var c = McpIdempotency.Hash("aislado_crear", (JsonObject)JsonNode.Parse("""{"entity":"X","values":{"a":2,"b":[1,{"y":2,"x":1}]}}""")!);
        var d = McpIdempotency.Hash("aislado_actualizar", (JsonObject)JsonNode.Parse("""{"entity":"X","values":{"a":1,"b":[1,{"y":2,"x":1}]}}""")!);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        // H3 CA1: formato de la clave.
        Assert.True(McpIdempotency.Valid("crear-ana:0001"));
        Assert.False(McpIdempotency.Valid("corta"));
        Assert.False(McpIdempotency.Valid("con espacio 123"));
        Assert.False(McpIdempotency.Valid(new string('x', 101)));
        Assert.False(McpIdempotency.Valid(null));
    }

    [Fact]
    public void Las_copias_por_conexion_tienen_su_nombre_y_Studio_no_las_barre()
    {
        var name = SandboxFactory.McpNameFor("ErpAi_E2E", 42);
        Assert.EndsWith("_SM42_SBX", name);
        Assert.Equal(42, SandboxFactory.McpSandboxOf(name, "ErpAi_E2E"));
        Assert.Null(SandboxFactory.SessionOf(name, "ErpAi_E2E")); // el barrido de Studio no la reconoce
        Assert.Null(SandboxFactory.McpSandboxOf(SandboxFactory.NameFor("ErpAi_E2E", 42), "ErpAi_E2E"));
        Assert.Null(SandboxFactory.McpSandboxOf(SandboxFactory.McpNameFor("OtraBase", 42), "ErpAi_E2E"));
    }

    // Regresión (revisión exploratoria 044, F-02): el título de la propuesta mostraba «**Vehículos**».
    [Fact]
    public void El_titulo_de_una_propuesta_es_la_primera_linea_sin_Markdown()
    {
        Assert.Equal("Agrego el catálogo Vehículos y su año", McpProposals.Title("\n## Agrego el catálogo **Vehículos** y su `año`\n\n- detalle"));
        Assert.Equal("Uno", McpProposals.Title("- Uno\n- Dos"));
        Assert.Equal("", McpProposals.Title("   "));
    }

    [Fact]
    public void Un_arreglo_de_la_API_se_publica_en_items()
    {
        Assert.Equal("[1,2]", McpTools.AsObject(new JsonArray(1, 2))["items"]!.ToJsonString());
        Assert.Equal("{\"a\":1}", McpTools.AsObject(new JsonObject { ["a"] = 1 }).ToJsonString());
    }
}
