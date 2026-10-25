using Erp.IntegrationTests.Harness;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 030 (H2 CA3, R-029-12): la huella de un módulo se compara con el candado del modelo (revisión adversarial 029, F-A).
/// Un plan que crea el mismo catálogo que el módulo y la instalación, a la vez: gana uno y el otro se rechaza sin 500,
/// y el catálogo queda como lo declaró el ganador. Base propia: un módulo se instala una sola vez.
/// </summary>
public class ModuleRaceTests(ErpHost host) : IClassFixture<ErpHost>
{
    [Fact]
    public async Task H2_CA3_un_plan_y_la_instalacion_de_un_modulo_a_la_vez_gana_uno_y_el_otro_se_rechaza()
    {
        using var admin = await host.AdminAsync();
        var plan = new { description = "Proveedor propio", operations = new[] { new { op = "createEntity", name = "Proveedor", label = "Proveedor local",
            labelPlural = "Proveedores locales", fields = new[] { new { name = "Alias", label = "Alias", type = "text", required = true } } } } };
        var planPreview = await admin.PostAsync("/api/model/plan/preview", plan);
        Assert.True(planPreview["valid"]!.GetValue<bool>(), planPreview.Text);
        var modulePreview = await admin.PostAsync("/api/admin/modules/compras/preview");
        Assert.Equal(200, modulePreview.Status);

        var results = await Concurrently(2, i => i == 0
            ? admin.PostAsync("/api/model/plan/apply", new { plan.description, plan.operations, baseHash = planPreview["baseHash"]!.GetValue<string>() })
            : admin.PostAsync("/api/admin/modules/compras/install", new { baseHash = modulePreview["baseHash"]!.GetValue<string>() }));

        Assert.True(results.Count(r => r.Status == 200) == 1, $"plan: {results[0]} | módulo: {results[1]}");
        var loser = results.Single(r => r.Status != 200);
        Assert.True(loser.Status is 400 or 409, $"el que pierde se rechaza con su motivo, no con un error: {loser}");
        var fields = (await admin.GetAsync("/api/model/entities/Proveedor"))["fields"].Json();
        if (results[0].Status == 200) Assert.Contains("Alias", fields);
        else Assert.DoesNotContain("Alias", fields);
    }
}
