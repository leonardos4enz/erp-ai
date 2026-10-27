using System.Text.Json;
using Erp.Api.Infrastructure;
using Erp.Api.Integrations;
using Erp.Api.Studio;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 034 (A-03): la credencial con la que llega la persona viaja con la herramienta. Va aparte porque usa una API nueva
/// (<see cref="BearerCredential"/>, <c>ToolContext.Credential</c>): contra el código anterior no compila y el resto de la
/// clase se corre con <c>it:regresion -- --sin=Hardening034Tests.Credential.cs</c>.
/// </summary>
public partial class Hardening034Tests
{
    [Fact]
    public Task H3_CA2_el_despachador_aplica_los_alcances_de_la_credencial() => Task.Run(async () =>
    {
        var dispatcher = host.Services.GetRequiredService<RouteDispatcher>();
        var admin = await ContextAsync("admin");
        var read = new BearerCredential(OAuthTokenHandler.SchemeName, OAuthScopes.DataRead);
        var list = new RouteCall("GET", "/api/data/{entity}/", new Dictionary<string, string?> { ["entity"] = Doc });
        Assert.Equal(200, (await dispatcher.SendAsync(host.Services, admin, list, allowWrites: false, CancellationToken.None, read)).Status);
        // Regresión (auditoría 6 A-03): sin la credencial, un token «data.read» llegaba por el MCP a lo que HTTP le niega.
        var create = await dispatcher.SendAsync(host.Services, admin, list with { Method = "POST", Body = new { Nombre = "Por token de lectura" } },
            allowWrites: true, CancellationToken.None, read);
        Assert.Equal(403, create.Status);
        Assert.Equal("El token no tiene el alcance data.write.", create.Error);
        Assert.Equal(0, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM app.[{Doc}] WHERE Nombre = N'Por token de lectura'"));
        var model = await dispatcher.SendAsync(host.Services, admin, new RouteCall("GET", "/api/model/entities"), allowWrites: false, CancellationToken.None, read);
        Assert.Equal(403, model.Status);
        var key = await dispatcher.SendAsync(host.Services, admin, new RouteCall("GET", "/api/explorer/relations"), allowWrites: false, CancellationToken.None,
            new BearerCredential(ApiKeyHandler.SchemeName));
        Assert.Equal("Una clave de API solo puede usar la API de datos, reportes y OData.", key.Error);

        // Por el runner: la credencial del contexto llega a cada ruta, también a las de la copia aislada.
        var runner = host.Services.GetRequiredService<StudioToolRunner>();
        var ctx = new ToolContext
        {
            User = admin, Scopes = new HashSet<string> { StudioScopes.Model, StudioScopes.Data }, Services = host.Services,
            SandboxAsync = _ => throw new InvalidOperationException("sin copia"), Credential = new BearerCredential(OAuthTokenHandler.SchemeName, OAuthScopes.ODataRead),
        };
        var tool = await runner.RunAsync(ctx, "leer_registros", JsonSerializer.SerializeToElement(new { entity = Doc }), CancellationToken.None);
        Assert.Equal("denied", tool.Status);
        Assert.Contains("data.read", tool.Summary);

        // Revisión adversarial 034 (F-02): en la copia, antes de prepararla, también aplicar un plan y procesar las asíncronas.
        var created = 0;
        var sandboxCtx = new ToolContext
        {
            User = admin, Scopes = new HashSet<string> { StudioScopes.Model, StudioScopes.Sandbox }, Services = host.Services,
            SandboxAsync = _ => { Interlocked.Increment(ref created); throw new InvalidOperationException("sin copia"); },
            Credential = new BearerCredential(OAuthTokenHandler.SchemeName, $"{OAuthScopes.DataRead} {OAuthScopes.DataWrite}"),
        };
        foreach (var (name, input) in new (string, object)[] { ("aislado_aplicar_plan", new { plan = new { operations = Array.Empty<object>() } }),
                     ("aislado_procesar_asincronas", new { }) })
        {
            var r = await runner.RunAsync(sandboxCtx, name, JsonSerializer.SerializeToElement(input), CancellationToken.None);
            Assert.True(r.Status == "denied", $"{name}: {r.Status} {r.Summary}");
        }
        Assert.Equal(0, created);
    });
}
