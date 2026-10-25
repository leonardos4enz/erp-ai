using System.Security.Claims;
using System.Text;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Erp.Tests;

/// <summary>
/// Spec 029 (H1): inventario de rutas. Recorre las rutas reales de la aplicación (sin base de datos) y falla si una no
/// declara su capacidad, su efecto, su compuerta, su límite o su huella; <c>docs/sdd/rutas.md</c> es su foto versionada.
/// Para regenerarla tras un cambio intencional: <c>ACTUALIZAR_RUTAS=1 dotnet test --filter RouteInventory</c>.
/// </summary>
public class RouteInventoryTests
{
    internal sealed record Route(string Method, string Path, Endpoint Endpoint)
    {
        public string Key => $"{Method} {Path}";
        public CapabilityMetadata? Capability => Endpoint.Metadata.GetMetadata<CapabilityMetadata>();
        public int CapabilityCount => Endpoint.Metadata.GetOrderedMetadata<CapabilityMetadata>().Count;
        public EffectMetadata? Effect => Endpoint.Metadata.GetMetadata<EffectMetadata>();
        public CostlyMetadata? Costly => Endpoint.Metadata.GetMetadata<CostlyMetadata>();
        public ReviewApplyMetadata? Review => Endpoint.Metadata.GetMetadata<ReviewApplyMetadata>();
        public string? RatePolicy => Endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
    }

    private static readonly Lazy<WebApplication> App = new(() =>
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ContentRootPath = Path.Combine(RepoRoot(), "src", "Erp.Api"),
        });
        builder.AddErp();
        var app = builder.Build();
        app.MapErpRoutes();
        return app;
    });

    private static readonly Lazy<IReadOnlyList<Route>> All = new(Load);

    internal static IReadOnlyList<Route> Routes => All.Value;

    private static IReadOnlyList<Route> Load()
    {
        var app = App.Value;
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(m => new Route(m, "/" + (e.RoutePattern.RawText ?? "").TrimStart('/'), e)))
            .OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal).ToList();
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ErpAi.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }

    /// <summary>Lista blanca anónima: lo único que responde sin sesión.</summary>
    private static readonly HashSet<string> Anonymous =
    [
        "GET /api/health", "POST /api/auth/login", "POST /oauth/token",
        "GET /oauth/token", "PUT /oauth/token", "PATCH /oauth/token", "DELETE /oauth/token",
        "* /api/{**path}", "* /oauth/{**path}", "* /odata/{**path}",
    ];

    [Fact]
    public void Hay_rutas_que_revisar()
    {
        Assert.True(Routes.Count > 100, $"Solo se encontraron {Routes.Count} rutas: ¿cambió cómo se registran?");
    }

    [Fact]
    public void Cada_ruta_declara_exactamente_una_capacidad()
    {
        var missing = Routes.Where(r => r.CapabilityCount != 1).Select(r => $"{r.Key} ({r.CapabilityCount})").ToList();
        Assert.True(missing.Count == 0, "Rutas sin capacidad declarada (o con más de una): " + string.Join(", ", missing));
    }

    [Fact]
    public void Solo_la_lista_blanca_es_anonima()
    {
        var anonymous = Routes.Where(r => r.Capability?.AnyOf.Contains(Capability.Anonymous) == true).Select(r => r.Key).ToHashSet();
        Assert.Empty(anonymous.Except(Anonymous));
        // Una ruta anónima no admite otra capacidad alternativa (sería anónima de todas formas).
        Assert.DoesNotContain(Routes, r => r.Capability is { AnyOf.Count: > 1 } c && c.AnyOf.Contains(Capability.Anonymous));
    }

    [Fact]
    public void Lo_que_no_es_GET_declara_su_efecto()
    {
        var missing = Routes.Where(r => r.Method is not ("GET" or "*") && r.Effect is null).Select(r => r.Key).ToList();
        Assert.True(missing.Count == 0, "Rutas que escriben sin declarar qué cambian: " + string.Join(", ", missing));
        Assert.DoesNotContain(Routes, r => r.Method == "GET" && r.Effect is { Effect: not Effect.None });
    }

    [Fact]
    public void Cambiar_el_modelo_o_la_seguridad_pasa_por_su_compuerta()
    {
        var model = new[] { Gates.DirectChange, Gates.PackageApproval };
        var security = new[] { Gates.SecurityApproval, Gates.NoGrant };
        var bad = Routes.Where(r => r.Effect is { Effect: Effect.Model } e && !model.Contains(e.Gate)
                                    || r.Effect is { Effect: Effect.Security } s && !security.Contains(s.Gate))
            .Select(r => $"{r.Key} ({r.Effect!.Gate ?? "sin compuerta"})").ToList();
        Assert.True(bad.Count == 0, "Cambios de modelo o seguridad sin compuerta: " + string.Join(", ", bad));
        // Cambiar el modelo o la seguridad es de administradores, nunca de cualquier sesión.
        Assert.DoesNotContain(Routes, r => r.Effect is { Effect: Effect.Model or Effect.Security }
                                           && r.Capability!.AnyOf.Any(c => c is Capability.Anonymous or Capability.Authenticated or Capability.Records));
    }

    [Fact]
    public void Una_ruta_costosa_tiene_limite_de_frecuencia()
    {
        var bad = Routes.Where(r => r.Costly is { OwnLimit: null } && r.RatePolicy is null).Select(r => r.Key).ToList();
        Assert.True(bad.Count == 0, "Rutas costosas sin límite: " + string.Join(", ", bad));
    }

    [Fact]
    public void Aplicar_lo_revisado_recibe_su_huella()
    {
        // Todo cambio al modelo es «revisar → aplicar» (constitución IV.4).
        var model = Routes.Where(r => r.Effect is { Effect: Effect.Model } && r.Review is null).Select(r => r.Key).ToList();
        Assert.True(model.Count == 0, "Cambios de modelo sin huella de lo revisado: " + string.Join(", ", model));
        // Y cada «vista previa» de algo que cambia modelo o seguridad tiene su «aplicar» con huella.
        var byKey = Routes.ToDictionary(r => r.Key);
        var missing = new List<string>();
        foreach (var preview in Routes.Where(r => r.Method == "POST" && r.Path.EndsWith("/preview", StringComparison.Ordinal)))
        {
            var root = preview.Path[..^"/preview".Length];
            foreach (var apply in new[] { root, root + "/apply", root + "/install" }.Select(p => byKey.GetValueOrDefault("POST " + p)).OfType<Route>())
                if (apply.Effect is { Effect: Effect.Model or Effect.Security } && apply.Review is null) missing.Add(apply.Key);
        }
        Assert.True(missing.Count == 0, "«Aplicar» sin huella de su vista previa: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task Una_ruta_sin_capacidad_declarada_no_responde_a_nadie()
    {
        // Spec 031 (auditoría 5): sin política por omisión, una ruta mapeada fuera de MapErpRoutes (sin .Requires) era anónima
        // y tampoco salía en el inventario.
        var services = App.Value.Services;
        var fallback = services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;
        Assert.NotNull(fallback);
        var superuser = new UserContext { Id = 1, Username = "admin", DisplayName = "A", SecurityStamp = Guid.Empty, Roles = [], IsSuperuser = true, CanManageModel = true, CanManageUsers = true, CanViewAudit = true };
        var http = new DefaultHttpContext { RequestServices = services };
        http.Items[UserContextExtensions.ItemKey] = superuser;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "prueba"));
        var auth = services.GetRequiredService<IAuthorizationService>();
        Assert.False((await auth.AuthorizeAsync(principal, http, fallback!)).Succeeded);
        // Las rutas que declaran capacidad no usan la política por omisión: la suya sí deja pasar al superusuario.
        var policy = Routes.First(r => r.Key == "GET /api/admin/users").Endpoint.Metadata.GetMetadata<AuthorizationPolicy>();
        Assert.True((await auth.AuthorizeAsync(principal, http, policy!)).Succeeded);
    }

    [Fact]
    public void Lo_que_agrega_o_recorre_un_catalogo_es_costoso()
    {
        // Spec 031 (auditoría 5): un tablero corre un reporte por tarjeta; reportes y OData recorren el catálogo.
        foreach (var key in new[] { "GET /api/dashboards/{name}", "GET /api/reports/{entity}/{name}", "GET /odata/{segment}" })
        {
            var r = Routes.Single(x => x.Key == key);
            Assert.True(r.Costly is not null && r.RatePolicy is not null, $"{key} no es costosa o no tiene límite");
        }
    }

    [Fact]
    public void El_inventario_versionado_coincide_con_las_rutas()
    {
        var path = Path.Combine(RepoRoot(), "docs", "sdd", "rutas.md");
        var expected = Snapshot();
        if (Environment.GetEnvironmentVariable("ACTUALIZAR_RUTAS") == "1") File.WriteAllText(path, expected, new UTF8Encoding(false));
        var actual = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
        Assert.True(actual == expected,
            "docs/sdd/rutas.md no coincide con las rutas. Si el cambio es intencional: ACTUALIZAR_RUTAS=1 dotnet test --filter RouteInventory, y revisa el diff.");
    }

    internal static string Snapshot()
    {
        var sb = new StringBuilder();
        sb.Append("# Inventario de rutas\n\n");
        sb.Append("Generado por `tests/Erp.Tests/RouteInventoryTests.cs` de los metadatos de cada ruta (spec 029). **No se edita a mano:**\n");
        sb.Append("`ACTUALIZAR_RUTAS=1 dotnet test --filter RouteInventory` lo regenera y el diff muestra cada ruta nueva o cambiada.\n");
        sb.Append("La prueba falla si una ruta no declara capacidad, si escribe sin declarar su efecto, si cambia el modelo o la seguridad\n");
        sb.Append("sin compuerta, si es costosa sin límite o si aplica lo revisado sin huella. `web/e2e/capacidades.spec.ts` y\n");
        sb.Append("`web/e2e/canario.spec.ts` recorren esta tabla.\n\n");
        sb.Append("Capacidades: **anónima** (lista blanca) · **sesión** (lo propio del usuario) · **registros** (el motor exige permiso del\n");
        sb.Append("catálogo, alcance por registro y seguridad por campo) · **modelador** · **usuarios** · **auditoría** · **superusuario**.\n\n");
        sb.Append($"Total: {Routes.Count} rutas.\n\n");
        sb.Append("| Método | Ruta | Capacidad | Efecto | Compuerta | Costosa | Huella |\n|---|---|---|---|---|---|---|\n");
        foreach (var r in Routes)
            sb.Append($"| {r.Method} | `{r.Path}` | {r.Capability} | {EffectName(r.Effect?.Effect)} | {r.Effect?.Gate ?? ""} | ")
              .Append(r.Costly is { } c ? $"{c.Why} ({r.RatePolicy ?? c.OwnLimit})" : "")
              .Append($" | {r.Review?.Fingerprint ?? ""} |\n");
        return sb.ToString();
    }

    private static string EffectName(Effect? e) => e switch
    {
        null => "",
        Effect.None => "ninguno",
        Effect.Data => "datos",
        Effect.Model => "modelo",
        Effect.Security => "seguridad",
        Effect.Config => "configuración",
        Effect.Session => "sesión",
        _ => e.ToString()!,
    };
}
