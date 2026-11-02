using System.Text.Json.Nodes;
using Erp.Api.Infrastructure;
using Erp.Api.Mcp;
using Erp.Api.Security;
using Erp.Api.Studio;
using Erp.IntegrationTests.Harness;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 045 · Endurecimiento 7 (auditoría 8) en el MCP: la clave se consume tras el efecto aunque el resultado no se pueda
/// entregar (D-08), las renovaciones sobreviven a una falla pasajera (D-09), el barrido no borra una clave en curso (D-10) y el
/// barrido del registro no deja a medias a quien acaba de permitir ni tumba el registro (D-11).
/// </summary>
public partial class Mcp2Tests
{
    [Fact]
    public async Task H045_H5_CA1_un_resultado_que_no_cabe_tras_el_efecto_consume_la_clave()
    {
        var c = await ConnectAsync("m2-rita");
        Ok(await CallAsync(c.Access, "aislado_aplicar_plan", new { idempotencyKey = Key(), plan = new
        {
            description = "Spec 045", operations = new object[] {
                new { op = "createEntity", group = "Pruebas 045", name = "M2Grande", label = "Grande", labelPlural = "Grandes", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true },
                    new { name = "Texto", label = "Texto", type = "longtext" } } } },
        } }));
        var key = Key();
        // ~300 KB: cabe en el mensaje (512 KB) pero el registro creado no cabe en el resultado (256 KB).
        var args = new { entity = "M2Grande", values = new { Nombre = "Grande", Texto = new string('x', 300_000) }, idempotencyKey = key };
        var first = ErrorOf(await CallAsync(c.Access, "aislado_crear", args));
        Assert.Equal(413, first["status"]!.GetValue<int>());
        Assert.Contains("La escritura se hizo", first["error"]!.GetValue<string>());
        // Regresión (auditoría 8, D-08): la clave se soltaba y el reintento creaba otro registro.
        var again = ErrorOf(await CallAsync(c.Access, "aislado_crear", args));
        Assert.Equal(first.Json(), again.Json());
        var db = await DbOfAsync(c.Grant);
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM [{db}].[app].[M2Grande]"));
        Assert.Equal("done", await host.ScalarAsync<string>($"SELECT Status FROM meta.McpWrite WHERE GrantId = {c.Grant} AND IdemKey = '{key}'"));
        await Sandboxes.ReleaseAsync(c.Grant, host.Users["m2-rita"], "prueba");
    }

    [Fact]
    public async Task H045_H5_CA2_una_falla_al_renovar_no_termina_la_renovacion_de_la_clave_ni_de_la_copia()
    {
        // Clave: lease de 6 s (renovación cada 2 s) en un servicio con esas opciones, sobre la misma base.
        var writes = new McpWrites(host.Services.GetRequiredService<Db>(), new McpOptions { WriteLeaseSeconds = 6 });
        var begin = await writes.BeginAsync(900_045, Key(), "aislado_crear", McpIdempotency.Hash("aislado_crear", []), CancellationToken.None);
        Assert.Equal(McpWriteStart.Run, begin.Start);
        // Copia: la de la conexión, con el lease de 6 s del anfitrión.
        var juan = host.Users["m2-sol"];
        var c = await ConnectAsync("m2-sol");
        var stamp = await host.ScalarAsync<Guid>($"SELECT SecurityStamp FROM meta.[User] WHERE Id = {juan}");
        var user = (await host.Services.GetRequiredService<UserContextLoader>().LoadAsync(juan, stamp))!;
        await using (var lease = await Sandboxes.AcquireAsync(c.Grant, user, CancellationToken.None))
        await using (writes.Keep(begin.Id, begin.Attempt))
        {
            // La primera renovación (a los 2 s) falla; la siguiente ya no.
            // La restricción nombra la columna que la renovación escribe (SQL Server solo revisa las que el UPDATE toca).
            await using (await host.FailWhenAsync("meta.McpWrite", $"Id = {begin.Id} AND PendingUntil IS NOT NULL"))
            await using (await host.FailWhenAsync("meta.McpSandbox", $"Id = {lease.Id} AND LeaseUntil IS NOT NULL"))
                await Task.Delay(TimeSpan.FromSeconds(3));
            // Pasado el lease inicial (6 s), los dos siguen vigentes: la renovación siguió viva (antes moría con la falla).
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.Equal(1, await host.ScalarAsync<int>($"SELECT CASE WHEN PendingUntil > SYSUTCDATETIME() THEN 1 ELSE 0 END FROM meta.McpWrite WHERE Id = {begin.Id}"));
            Assert.Equal(1, await host.ScalarAsync<int>($"SELECT CASE WHEN LeaseUntil > SYSUTCDATETIME() THEN 1 ELSE 0 END FROM meta.McpSandbox WHERE Id = {lease.Id}"));
        }
        await writes.AbandonAsync(begin.Id, begin.Attempt);
        await Sandboxes.ReleaseAsync(c.Grant, juan, "prueba");
    }

    [Fact]
    public async Task H045_H5_CA3_el_barrido_no_borra_una_clave_en_curso()
    {
        var hash = "0x" + Convert.ToHexString(McpIdempotency.Hash("aislado_crear", []));
        // Las tres más viejas que la retención (24 h): una en curso con su lease vigente, una terminada y una con el lease vencido.
        await host.ExecAsync($"""
            INSERT meta.McpWrite (GrantId, IdemKey, Tool, InputHash, Status, Attempt, PendingUntil, CreatedAt) VALUES
              (900046, 'k45-en-curso', N'aislado_crear', {hash}, 'pending', NEWID(), DATEADD(minute, 5, SYSUTCDATETIME()), DATEADD(day, -3, SYSUTCDATETIME())),
              (900046, 'k45-vencida', N'aislado_crear', {hash}, 'pending', NEWID(), DATEADD(minute, -5, SYSUTCDATETIME()), DATEADD(day, -3, SYSUTCDATETIME()));
            INSERT meta.McpWrite (GrantId, IdemKey, Tool, InputHash, Status, Attempt, PendingUntil, ResultJson, CreatedAt) VALUES
              (900046, 'k45-terminada', N'aislado_crear', {hash}, 'done', NEWID(), NULL, N'{"{}"}', DATEADD(day, -3, SYSUTCDATETIME()));
            """);
        await Sandboxes.SweepAsync(CancellationToken.None);
        // Regresión (auditoría 8, D-10): se borraban por fecha sin mirar el estado.
        var left = await host.ScalarAsync<string>("SELECT STRING_AGG(IdemKey, ',') FROM meta.McpWrite WHERE GrantId = 900046");
        Assert.Equal("k45-en-curso", left);
        await host.ExecAsync("DELETE meta.McpWrite WHERE GrantId = 900046");
    }

    [Fact]
    public async Task H045_H6_CA1_el_barrido_no_borra_el_codigo_de_un_cliente_que_nunca_canjeo()
    {
        using var api = host.Anonymous(false);
        var clientId = await RegisterAsync(api);
        // Registrado hace dos horas y sin conexiones: el barrido lo tomaría por inútil.
        await host.ExecAsync($"UPDATE meta.McpClient SET CreatedAt = DATEADD(hour, -2, SYSUTCDATETIME()) WHERE ClientId = '{clientId}'");
        var verifier = McpFormat.New("v");
        using var person = await host.LoginAsync("m2-tito", Pwd);
        var decided = await person.PostAsync("/api/mcp/authorize", new { query = Query(clientId, verifier, "modelo"), approve = true, scopes = new[] { "modelo" } });
        Assert.True(decided.Status == 200, decided.Text);
        var code = QueryHelpers.ParseQuery(new Uri(decided["redirect"]!.GetValue<string>()).Query)["code"].ToString();
        // Otro registro anónimo barre entre «Permitir» y el canje.
        await RegisterAsync(api, "Otro 045");
        // Regresión (auditoría 8, D-11): el barrido borraba el código vigente y el cliente; el canje fallaba.
        var t = await api.PostFormAsync("/oauth/token", new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Callback, ["client_id"] = clientId,
            ["code_verifier"] = verifier, ["resource"] = Resource,
        });
        Assert.True(t.Status == 200, t.Text);
    }

    [Fact]
    public async Task H045_H6_CA2_una_falla_del_barrido_no_tumba_el_registro()
    {
        using var api = host.Anonymous(false);
        // Una falla inyectada en el barrido (como la llave foránea de un código que cruza sus DELETE).
        await host.ExecAsync("EXEC(N'CREATE TRIGGER meta.IT45_FallaBarrido ON meta.McpClient AFTER DELETE AS THROW 50045, ''falla inyectada 045'', 1;');");
        try
        {
            // Regresión (auditoría 8, D-11): el registro anónimo respondía 500.
            var res = await api.PostAsync("/oauth/register", new { client_name = "Claude 045", redirect_uris = new[] { Callback } });
            Assert.True(res.Status == 201, res.Text);
        }
        finally
        {
            await host.ExecAsync("DROP TRIGGER IF EXISTS meta.IT45_FallaBarrido;");
        }
    }
}
