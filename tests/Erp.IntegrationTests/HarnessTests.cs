using Erp.IntegrationTests.Harness;
using Microsoft.Data.SqlClient;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>Spec 030 (H1): el propio arnés. Si esto falla, las demás pruebas de integración no prueban lo que dicen.</summary>
public class HarnessTests(ErpHost host) : IClassFixture<ErpHost>
{
    [Theory]
    [InlineData("ErpAi")]
    [InlineData("erpai")]
    [InlineData("ErpAi_Datos")]
    [InlineData("ErpAi_IT];DROP DATABASE ErpAi;--_IT")]
    [InlineData("ErpAi_IT_x")]
    public void H1_CA1_solo_acepta_bases_desechables(string name)
    {
        Assert.Throws<InvalidOperationException>(() => ErpHost.ValidateDatabase(name));
    }

    [Fact]
    public async Task H1_CA1_la_base_es_propia_de_la_clase_y_la_configuracion_llega_al_registrar_los_servicios()
    {
        Assert.Matches("^ErpAi_ErpHost_[0-9a-f]{8}_IT$", host.Database);
        Assert.Equal(host.Database, await host.ScalarAsync<string>("SELECT DB_NAME()"));
        // EnvironmentOptions.From(config) se lee en AddErp, antes de Build: UseSetting debe llegar ahí.
        using var admin = await host.AdminAsync();
        var info = await admin.GetAsync("/api/admin/environments");
        Assert.Equal("Integración", info["name"]!.GetValue<string>());
        Assert.True(info["directChangesOverride"]!.GetValue<bool>());

        var prod = await host.SiblingAsync(s => { s["Environments:AllowDirectChanges"] = "false"; s["Environments:Name"] = "Producción IT"; });
        using var other = await prod.LoginAsync("admin", host.AdminPassword);
        var prodInfo = await other.GetAsync("/api/admin/environments");
        Assert.Equal("Producción IT", prodInfo["name"]!.GetValue<string>());
        Assert.False(prodInfo["directChangesOverride"]!.GetValue<bool>());
    }

    [Fact]
    public async Task H1_CA2_Concurrently_lanza_todas_a_la_vez()
    {
        var inside = 0;
        var peak = 0;
        using var all = new Barrier(10);
        await Concurrently(10, async _ =>
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref peak, now);
            await Task.Run(() => all.SignalAndWait(TimeSpan.FromSeconds(10))); // con una sola a la vez nunca se completaría
            Interlocked.Decrement(ref inside);
            return 0;
        });
        Assert.Equal(10, peak);
    }

    [Fact]
    public async Task H1_CA3_una_falla_inyectada_se_deshace_aunque_la_prueba_falle()
    {
        // Regresión (revisión adversarial 030, F-A): en una tabla fuera de dbo (como meta.AutomationRun) la restricción no se
        // quitaba: OBJECT_ID con el nombre solo la buscaba en dbo.
        await host.ExecAsync("IF SCHEMA_ID('it') IS NULL EXEC('CREATE SCHEMA it'); IF OBJECT_ID('it.ItFalla') IS NULL CREATE TABLE it.ItFalla (Valor int)");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var _ = await host.FailWhenAsync("it.ItFalla", "Valor = 13");
            await Assert.ThrowsAsync<SqlException>(() => host.ExecAsync("INSERT it.ItFalla VALUES (13)"));
            throw new InvalidOperationException("la prueba falló a mitad");
        });
        Assert.Equal(0, await host.ScalarAsync<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('it.ItFalla')"));
        await host.ExecAsync("INSERT it.ItFalla VALUES (13)"); // la restricción ya no está
        Assert.Equal(1, await host.ScalarAsync<int>("SELECT COUNT(*) FROM it.ItFalla WHERE Valor = 13"));

        await using (await host.HoldAsync("INSERT it.ItFalla VALUES (7)"))
            Assert.Equal(0, await host.ScalarAsync<int>("SELECT COUNT(*) FROM it.ItFalla WITH (READPAST) WHERE Valor = 7"));
        Assert.Equal(0, await host.ScalarAsync<int>("SELECT COUNT(*) FROM it.ItFalla WHERE Valor = 7")); // se deshizo
    }

    [Fact]
    public void H1_CA2_una_asercion_negativa_sobre_un_nodo_ausente_falla()
    {
        // Regresión (revisión adversarial 030, F-B): Json() de un nodo ausente daba «null» y DoesNotContain pasaba con un 403/404.
        Assert.Throws<InvalidOperationException>(() => ((System.Text.Json.Nodes.JsonNode?)null).Json());
        Assert.Equal("{\"a\":\"Cliente de otra empresa · ñ\"}", System.Text.Json.Nodes.JsonNode.Parse("{\"a\":\"Cliente de otra empresa · ñ\"}").Json());
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
