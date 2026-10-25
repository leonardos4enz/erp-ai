using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Api.Environments;
using Erp.Api.Metadata;

namespace Erp.Tests;

/// <summary>Spec 026 (H2): origen de cada elemento reproduciendo las cadenas de los paquetes instalados.</summary>
public class ProvenanceTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlySet<string> System = new HashSet<string>(["Administrador", "Usuario"], Ci);

    private static string ModulesFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "modules", "compras.json"))) return Path.Combine(d.FullName, "modules");
        throw new DirectoryNotFoundException("modules");
    }

    private static readonly IReadOnlyList<ModulePackage> Packages = Directory.EnumerateFiles(ModulesFolder(), "*.json")
        .Select(f => JsonSerializer.Deserialize<ModulePackage>(File.ReadAllText(f), PackageJson.Read)!).ToList();

    private static Dictionary<string, string> AllInstalled() => Packages.ToDictionary(p => p.Id, p => p.Version, Ci);

    /// <summary>El modelo que dejan los paquetes instalados, como quedaría en una base real (todas las versiones).</summary>
    private static IReadOnlyDictionary<string, EntityDef> InstalledModel(IReadOnlyDictionary<string, string> installed)
    {
        IReadOnlyDictionary<string, EntityDef> model = new Dictionary<string, EntityDef>(Ci);
        var roles = System.Union(Packages.SelectMany(p => p.AllRoles.Select(r => r.Name))).ToHashSet(Ci);
        foreach (var p in Provenance.InstallOrder(Packages, installed))
            foreach (var (label, plan) in p.FullChain)
            {
                if (label != "base" && Version.Parse(label.TrimStart('v')) > Version.Parse(installed[p.Id])) continue;
                var c = PlanCompiler.Compile(plan, model, roles);
                Assert.True(c.Preview.Valid, string.Join(" | ", c.Preview.Errors.Select(e => e.Message)));
                model = c.ResultingModel;
            }
        return model;
    }

    private static Provenance.ElementOrigin Origin(IReadOnlyList<Provenance.EntityOrigin> all, string entity, string kind, string name) =>
        all.Single(e => e.Entity == entity).Elements.Single(x => x.Kind == kind && x.Name == name);

    [Fact]
    public void Spec027_H6_las_plantillas_de_Compras_y_Contabilidad_compilan_y_dicen_su_paquete()
    {
        var installed = AllInstalled();
        var model = InstalledModel(installed); // cada paso de cada cadena debe ser válido
        Assert.Equal(["Proveedor", "Interna"], model["OrdenCompra"].PrintTemplates!.Select(t => t.Name));
        Assert.Equal("Poliza", Assert.Single(model["Poliza"].PrintTemplates!).Name);
        var all = Provenance.Compute(model, Provenance.Reproduce(Packages, installed, System));
        var o = Origin(all, "OrdenCompra", Provenance.Kinds.PrintTemplate, "Proveedor");
        Assert.Equal(("compras", "1.10.0", false), (o.Package, o.Step, o.Modified));
        Assert.Equal("contabilidad", Origin(all, "Poliza", Provenance.Kinds.PrintTemplate, "Poliza").Package);
    }

    [Fact]
    public void Los_paquetes_se_ordenan_por_dependencias()
    {
        var order = Provenance.InstallOrder(Packages, AllInstalled()).Select(p => p.Id).ToList();
        Assert.True(order.IndexOf("compras") < order.IndexOf("inventario"));
        Assert.True(order.IndexOf("contabilidad") < order.IndexOf("costeo"));
        Assert.True(order.IndexOf("inventario") < order.IndexOf("costeo"));
    }

    [Fact]
    public void Cada_campo_lleva_el_paquete_que_lo_agrego_y_nada_esta_modificado()
    {
        var installed = AllInstalled();
        var model = InstalledModel(installed);
        var rep = Provenance.Reproduce(Packages, installed, System);
        var all = Provenance.Compute(model, rep);

        var almacen = all.Single(e => e.Entity == "Almacen");
        Assert.Equal("compras", almacen.InstalledBy!.Id);
        Assert.Equal("1.10.0", almacen.InstalledBy.Version);
        Assert.Contains(almacen.Packages, p => p.Id == "costeo");
        Assert.Equal("compras", Origin(all, "Almacen", Provenance.Kinds.Field, "Nombre").Package);
        var cuenta = Origin(all, "Almacen", Provenance.Kinds.Field, "CuentaInventario");
        Assert.Equal(("costeo", "base"), (cuenta.Package, cuenta.Step));
        // Una actualización posterior sella el paso que dejó el elemento como está.
        Assert.NotEqual("base", Origin(all, "Requisicion", Provenance.Kinds.Field, "Almacen").Step);

        Assert.DoesNotContain(all.SelectMany(e => e.Elements), x => x.Modified);
        Assert.DoesNotContain(all.SelectMany(e => e.Elements), x => x.Package is null);
    }

    [Fact]
    public void Lo_cambiado_en_el_entorno_es_modificado_y_lo_nuevo_es_local()
    {
        var installed = AllInstalled();
        var model = new Dictionary<string, EntityDef>(InstalledModel(installed), Ci);
        var oc = model["OrdenCompra"];
        var first = oc.Rules![0];
        model["OrdenCompra"] = oc with
        {
            Rules = [first with { Message = "Mensaje local" }, .. oc.Rules.Skip(1), new BusinessRule { Name = "Local", Assert = "TRUE", Message = "x" }],
            Fields = oc.Fields.Select(f => f with { DisplayOrder = f.DisplayOrder + 7 }).ToList(), // reordenar no es modificar
        };
        var all = Provenance.Compute(model, Provenance.Reproduce(Packages, installed, System));

        Assert.True(Origin(all, "OrdenCompra", Provenance.Kinds.Rule, first.Name).Modified);
        var local = Origin(all, "OrdenCompra", Provenance.Kinds.Rule, "Local");
        Assert.Null(local.Package);
        Assert.False(local.Modified);
        Assert.DoesNotContain(all.Single(e => e.Entity == "OrdenCompra").Elements, x => x.Kind == Provenance.Kinds.Field && x.Modified);
    }

    [Fact]
    public void Un_catalogo_creado_en_el_entorno_no_tiene_paquete()
    {
        var installed = new Dictionary<string, string>(Ci) { ["compras"] = "1.9.0" };
        var model = new Dictionary<string, EntityDef>(InstalledModel(installed), Ci)
        {
            ["Empaque"] = new() { Name = "Empaque", Label = "Empaque", LabelPlural = "Empaques", Fields = [new() { Name = "Clave", Label = "Clave", Type = FieldTypes.Text }] },
        };
        var all = Provenance.Compute(model, Provenance.Reproduce(Packages, installed, System));
        var e = all.Single(x => x.Entity == "Empaque");
        Assert.Null(e.InstalledBy);
        Assert.Empty(e.Packages);
        // Solo lo instalado se reproduce: Inventario no está instalado, así que no sella nada.
        Assert.DoesNotContain(all.SelectMany(x => x.Packages), p => p.Id == "inventario");
    }

    [Fact]
    public void Una_version_instalada_anterior_no_reproduce_las_siguientes()
    {
        var installed = new Dictionary<string, string>(Ci) { ["compras"] = "1.1.0" };
        var rep = Provenance.Reproduce(Packages, installed, System);
        var model = InstalledModel(installed);
        Assert.DoesNotContain(Provenance.Compute(model, rep).SelectMany(e => e.Elements), x => x.Modified || x.Package is null);
        Assert.All(Provenance.Compute(model, rep).SelectMany(e => e.Elements), x => Assert.True(x.Step is "base" or "1.1.0"));
    }

    [Fact]
    public void Los_choques_de_una_actualizacion_listan_lo_modificado_y_lo_local_que_se_pierde()
    {
        var installed = new Dictionary<string, string>(Ci) { ["compras"] = "1.6.0" };
        var compras = Packages.Single(p => p.Id == "compras");
        var pending = compras.PendingAfter("1.6.0");
        // La 1.7.0 reemplaza las reglas de OrdenCompraPartida sin merge (spec 012): lo local de esa lista se perdería.
        var target = pending.SelectMany(u => u.Plan.Operations).OfType<SetRulesOp>().First(o => o.Merge != true).Entity;
        var model = new Dictionary<string, EntityDef>(InstalledModel(installed), Ci);
        var e = model[target];
        var rules = e.Rules ?? [];
        model[target] = e with
        {
            Rules = [.. rules.Select((r, i) => i == 0 ? r with { Message = "Cambiada aquí" } : r), new BusinessRule { Name = "MiRegla", Assert = "TRUE", Message = "x" }],
        };
        var roles = System.Union(compras.AllRoles.Select(r => r.Name)).ToHashSet(Ci);
        var conflicts = Provenance.Conflicts(model, Provenance.Reproduce(Packages, installed, System), pending.Select(u => u.Plan), roles);

        Assert.Contains(conflicts, c => c.Entity == target && c.Name == "MiRegla" && c.Reason == "local" && c.Effect == "removed");
        if (rules.Count > 0)
            Assert.Contains(conflicts, c => c.Entity == target && c.Name == rules[0].Name && c.Reason == "modified");
        // Lo que no se tocó localmente no es un choque.
        Assert.DoesNotContain(conflicts, c => c.Entity != target && c.Reason == "local");
    }

    [Fact]
    public void Sin_cambios_locales_una_actualizacion_no_tiene_choques()
    {
        var installed = new Dictionary<string, string>(Ci) { ["compras"] = "1.6.0" };
        var compras = Packages.Single(p => p.Id == "compras");
        var roles = System.Union(compras.AllRoles.Select(r => r.Name)).ToHashSet(Ci);
        var conflicts = Provenance.Conflicts(InstalledModel(installed), Provenance.Reproduce(Packages, installed, System),
            compras.PendingAfter("1.6.0").Select(u => u.Plan), roles);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void La_forma_canonica_ignora_nulos_listas_vacias_falsos_y_orden_de_presentacion()
    {
        var a = new FieldDef { Name = "X", Label = "X", Type = FieldTypes.Text, Options = [], Required = false, DisplayOrder = 3 };
        var b = new FieldDef { Name = "X", Label = "X", Type = FieldTypes.Text, Options = null, DisplayOrder = 9 };
        EntityDef E(FieldDef f) => new() { Name = "T", Label = "T", LabelPlural = "T", Fields = [f] };
        var ka = Provenance.Elements(E(a))[(Provenance.Kinds.Field, "X")];
        var kb = Provenance.Elements(E(b))[(Provenance.Kinds.Field, "X")];
        Assert.True(JsonNode.DeepEquals(ka, kb));
        var kc = Provenance.Elements(E(b with { Required = true }))[(Provenance.Kinds.Field, "X")];
        Assert.False(JsonNode.DeepEquals(ka, kc));
    }
}
