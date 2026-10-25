using Erp.IntegrationTests.Harness;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 030 (R-030-1): con dos instancias sobre la misma base, el modelo en caché de cada una (<c>MetadataStore</c>, 30 s)
/// no ve al instante un cambio hecho en la otra. Lo encontró el arnés al probar una bandeja con dos instancias. Esta prueba
/// documenta el alcance del riesgo (hoy se despliega una sola instancia): si algún día se invalida la caché entre
/// instancias, deja de responder 404 y la prueba se actualiza junto con el riesgo.
/// </summary>
public class MultiInstanceTests(ErpHost host) : IClassFixture<ErpHost>
{
    [Fact]
    public async Task R_030_1_una_segunda_instancia_no_ve_al_instante_un_catalogo_creado_en_la_otra()
    {
        var other = await host.SiblingAsync(s => s["Environments:Name"] = "Segunda instancia");
        using var second = await other.LoginAsync("admin", host.AdminPassword);
        Assert.Equal(200, (await second.GetAsync("/api/model/entities")).Status); // carga su caché

        using var admin = await host.AdminAsync();
        await admin.ApplyPlanAsync(new { description = "Catálogo nuevo", operations = new[] { new { op = "createEntity", name = "MiCatalogo",
            label = "Mi catálogo", labelPlural = "Mis catálogos", fields = new[] { new { name = "Clave", label = "Clave", type = "text" } } } } });
        Assert.Equal(201, (await admin.PostAsync("/api/data/MiCatalogo", new { Clave = "A" })).Status);
        Assert.Equal(404, (await second.PostAsync("/api/data/MiCatalogo", new { Clave = "B" })).Status);
    }
}
