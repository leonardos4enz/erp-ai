using Erp.IntegrationTests.Harness;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 051 · Compras 1.12.0 lo ejerce con metadatos: el responsable de un departamento consulta (sin modificar) las
/// requisiciones de ese departamento, y cada transición agrega a quien la ejecuta a «Quienes intervinieron».
/// </summary>
public class ComprasMembershipTests(ComprasMembershipTests.Host host) : IClassFixture<ComprasMembershipTests.Host>
{
    private const string Pwd = "frase larga de compras 051";

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Ids = [];

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            await admin.InstallModuleAsync("compras");
            var role = await admin.RoleIdAsync("Solicitante");
            foreach (var key in new[] { "responsable", "creador", "ajeno" })
            {
                var u = await admin.PostAsync("/api/admin/users", new { username = $"it51-{key}", displayName = $"Compras 051 {key}", password = Pwd, roleIds = new[] { role } });
                Assert.True(u.Status == 201, u.Text);
                Ids[key] = u.Body!.Id();
            }
            Ids["depto"] = await ScalarAsync<int>("SELECT Id FROM app.Departamento WHERE Nombre = N'Producción'");
            var depto = await admin.RecordAsync("Departamento", Ids["depto"]);
            var put = await admin.PutAsync($"/api/data/Departamento/{Ids["depto"]}", new { Responsables = new[] { Ids["responsable"] }, _rowVersion = depto.Str("_rowVersion") });
            Assert.True(put.Status == 200, put.Text);
            var unidad = await ScalarAsync<int>("SELECT Id FROM app.UnidadMedida WHERE Clave = 'PZA'");
            Ids["almacen"] = await ScalarAsync<int>("SELECT TOP 1 Id FROM app.Almacen ORDER BY Id");
            Ids["producto"] = (await admin.CreateAsync("Producto", new { Clave = "P-051", Descripcion = "Hojas 051", Unidad = unidad })).Id();
        }
    }

    [Fact]
    public async Task El_responsable_del_departamento_consulta_la_requisicion_y_la_transicion_agrega_a_quien_la_ejecuta()
    {
        using var creador = await host.LoginAsync("it51-creador", Pwd);
        var req = await creador.CreateAsync("Requisicion", new
        {
            Departamento = host.Ids["depto"], Almacen = host.Ids["almacen"], Justificacion = "Para la línea 2",
            Partidas = new[] { new { Producto = host.Ids["producto"], Cantidad = 3 } },
        });

        using var responsable = await host.LoginAsync("it51-responsable", Pwd);
        var seen = await responsable.GetAsync($"/api/data/Requisicion/{req.Id()}");
        Assert.True(seen.Status == 200, seen.Text);
        var put = await responsable.PutAsync($"/api/data/Requisicion/{req.Id()}", new { Justificacion = "Cambio", _rowVersion = req.Str("_rowVersion") });
        Assert.True(put.Status is 403 or 404, put.Text); // solo lectura
        using var ajeno = await host.LoginAsync("it51-ajeno", Pwd);
        Assert.Equal(404, (await ajeno.GetAsync($"/api/data/Requisicion/{req.Id()}")).Status);

        var sent = await creador.TransitionAsync("Requisicion", req, "enviar");
        Assert.True(sent.Status == 200, sent.Text);
        Assert.Equal(new[] { host.Ids["creador"] }, sent.Body!.Arr("Participantes").Nodes().Select(n => n.GetValue<int>()));
        Assert.Equal(new[] { "Compras 051 creador" }, sent.Body!.Arr("Participantes__label").Nodes().Select(n => n.GetValue<string>()));
    }
}
