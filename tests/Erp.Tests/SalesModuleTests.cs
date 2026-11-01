using System.Text.Json;
using Erp.Api.Environments;
using Erp.Api.Metadata;

namespace Erp.Tests;

/// <summary>Spec 043: <c>createLines[].from</c> (validación y serialización) y el paquete de Ventas sobre los otros módulos.</summary>
public class SalesModuleTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] Base = ["compras.json", "inventario.json", "contabilidad.json", "costeo.json"];

    private static string ModulesFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "modules", "compras.json"))) return Path.Combine(d.FullName, "modules");
        throw new DirectoryNotFoundException("modules");
    }

    private static ModulePackage Package(string file) =>
        JsonSerializer.Deserialize<ModulePackage>(File.ReadAllText(Path.Combine(ModulesFolder(), file)), PackageJson.Read)!;

    private static string Errors(CompiledPlan c) => string.Join(" | ", c.Preview.Errors.Select(e => $"#{e.Operation} {e.Message}"));

    private sealed record Installed(IReadOnlyDictionary<string, EntityDef> Model, IReadOnlyDictionary<string, DashboardDef> Boards, HashSet<string> Roles);

    private static Installed Install(params string[] files)
    {
        IReadOnlyDictionary<string, EntityDef> model = new Dictionary<string, EntityDef>(Ci);
        IReadOnlyDictionary<string, DashboardDef> boards = new Dictionary<string, DashboardDef>(Ci);
        var roles = new HashSet<string>(["Administrador"], Ci);
        foreach (var file in files)
        {
            var p = Package(file);
            roles.UnionWith(p.AllRoles.Select(r => r.Name));
            foreach (var (_, plan) in p.FullChain)
            {
                var c = PlanCompiler.Compile(plan, model, roles, boards);
                Assert.True(c.Preview.Valid, $"{file}: {Errors(c)}");
                (model, boards) = (c.ResultingModel, c.Dashboards!);
            }
        }
        return new Installed(model, boards, roles);
    }

    private static readonly Lazy<Installed> All = new(() => Install([.. Base, "ventas.json"]));

    [Fact]
    public void H6_CA1_Ventas_compila_sobre_Compras_Inventario_Contabilidad_y_Costeo_y_requiere_los_tres()
    {
        var m = All.Value.Model;
        foreach (var e in new[] { "Cliente", "Cotizacion", "Pedido", "Embarque", "Factura", "Cobro" })
        {
            Assert.True(m[e].PerCompany, e); // decisión 2: todo, también el cliente, es de cada empresa
            if (e != "Cliente") Assert.NotNull(m[e].Workflow);
        }
        Assert.NotNull(m["Producto"].FindField("PrecioVenta"));
        // merge: Ventas agrega sus reglas al producto sin quitar las de Compras.
        Assert.Contains(m["Producto"].Rules!, r => r.Name == "CostoNoNegativo");
        Assert.Contains(m["Producto"].Rules!, r => r.Name == "PrecioVentaNoNegativo");
        Assert.Equal(["inventario", "contabilidad", "costeo"], Package("ventas.json").Requires);
    }

    [Fact]
    public void H6_CA1_Ventas_sin_Costeo_no_compila()
    {
        var sin = Install("compras.json", "inventario.json", "contabilidad.json");
        var p = Package("ventas.json");
        var roles = new HashSet<string>(sin.Roles, Ci);
        roles.UnionWith(p.AllRoles.Select(r => r.Name));
        var c = PlanCompiler.Compile(p.Plan, sin.Model, roles, sin.Boards);
        Assert.False(c.Preview.Valid);
        Assert.Contains(c.Preview.Errors, e => e.Message.Contains("CostoTotal") || e.Message.Contains("CuentaInventario"));
    }

    [Fact]
    public void H6_CA2_Ventas_no_da_permisos_a_roles_de_otros_modulos()
    {
        var otros = Base.SelectMany(f => Package(f).AllRoles.Select(r => r.Name)).ToHashSet(Ci);
        Assert.DoesNotContain(Package("ventas.json").AllRoles, r => otros.Contains(r.Name));
    }

    // ---------------- H1: from ----------------

    private static Operation WithLine(Func<AutomationAction, AutomationAction> change)
    {
        var auto = All.Value.Model["Cotizacion"].Automations!.Single(a => a.Name == "CrearPedido");
        return new SetAutomationsOp { Entity = "Cotizacion", Merge = true, Automations = [auto with { Actions = [change(auto.Actions[0])] }] };
    }

    private static CompiledPlan Compile(Operation op) =>
        PlanCompiler.Compile(new ChangePlan { Description = "prueba", Operations = [op] }, All.Value.Model, All.Value.Roles, All.Value.Boards);

    [Theory]
    [InlineData("noLines", "'Cliente' no es un campo de partidas de Cotizacion")]
    [InlineData("conLines", "'from' no se combina con 'lines'")]
    [InlineData("ambito", "partida 1 › Cantidad")]
    [InlineData("when", "partida 1 (cuándo)")]
    public void H1_CA2_un_from_mal_declarado_se_rechaza(string what, string expected)
    {
        var op = WithLine(a =>
        {
            var lt = a.CreateLines![0];
            return what switch
            {
                "noLines" => a with { CreateLines = [lt with { From = "Cliente" }] },
                "conLines" => a with { Lines = "Partidas", Set = [], CreateLines = [lt], Capture = null },
                // En la partida de origen no existe «Vigencia» (es del encabezado): se llega por el campo padre.
                "ambito" => a with { CreateLines = [lt with { Set = [.. lt.Set.Where(s => s.Field != "Cantidad"), new("Cantidad", "IF(Vigencia > TODAY(), Cantidad, 1)")] }] },
                _ => a with { CreateLines = [lt with { When = "Folio <> ''" }] },
            };
        });
        var c = Compile(op);
        Assert.False(c.Preview.Valid);
        Assert.Contains(c.Preview.Errors, e => e.Message.Contains(expected));
    }

    [Fact]
    public void H1_CA1_from_se_calcula_en_la_partida_de_origen_con_rutas_por_el_padre()
    {
        var c = Compile(WithLine(a => a with { CreateLines = [a.CreateLines![0] with {
            When = "Cotizacion.Vigencia >= Cotizacion.Fecha AND Producto.Activo",
            Set = [.. a.CreateLines[0].Set.Where(s => s.Field != "Cantidad"), new("Cantidad", "IF(Cotizacion.Cliente.Activo, Cantidad, 0)")] }] }));
        Assert.True(c.Preview.Valid, Errors(c));
    }

    [Fact]
    public void H1_CA4_sin_from_la_plantilla_se_serializa_igual_que_antes()
    {
        var sin = new LineTemplate { Field = "Partidas", Set = [new("Cargo", "1")] };
        Assert.DoesNotContain("from", JsonSerializer.Serialize(sin, new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.OrdinalIgnoreCase);
        var con = sin with { From = "Partidas" };
        Assert.Contains("\"from\":\"Partidas\"", JsonSerializer.Serialize(con, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        // Y los paquetes de Costeo, que no lo usan, dan el mismo modelo: ninguna plantilla trae from.
        var costeo = Install(Base).Model;
        Assert.All(costeo.Values.SelectMany(e => e.Automations ?? []).SelectMany(a => a.Actions).SelectMany(a => a.CreateLines ?? []), lt => Assert.Null(lt.From));
    }
}
