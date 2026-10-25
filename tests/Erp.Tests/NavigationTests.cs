using System.Text.Json;
using Erp.Api.Assistant;
using Erp.Api.Environments;
using Erp.Api.Metadata;

namespace Erp.Tests;

/// <summary>Spec 024: módulos de navegación como metadato (setApp/archiveApp), módulos efectivos y Entornos.</summary>
public class NavigationTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlyDictionary<string, EntityDef> Empty = new Dictionary<string, EntityDef>(Ci);
    private static readonly IReadOnlyDictionary<string, DashboardDef> NoBoards = new Dictionary<string, DashboardDef>(Ci);
    private static readonly IReadOnlyDictionary<string, AppDef> NoApps = new Dictionary<string, AppDef>(Ci);
    private static readonly HashSet<string> Roles = new(["Administrador", "Usuario"], Ci);

    private static ChangePlan Plan(params Operation[] ops) => new() { Description = "prueba", Operations = ops };
    private static string Errors(IEnumerable<PlanError> errors) => string.Join(" | ", errors.Select(e => $"#{e.Operation} {e.Message}"));

    private static string ModulesFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "modules", "compras.json"))) return Path.Combine(d.FullName, "modules");
        throw new DirectoryNotFoundException("modules");
    }

    private static ModulePackage Package(string file) =>
        JsonSerializer.Deserialize<ModulePackage>(File.ReadAllText(Path.Combine(ModulesFolder(), file)), PackageJson.Read)!;

    private static CreateEntityOp Catalog(string name, string? group, bool workflow = false) => new()
    {
        Name = name, Label = name, LabelPlural = name + "s", Group = group,
        Fields = [new FieldDef { Name = "Nombre", Label = "Nombre", Type = "text" }],
    };

    private static CompiledPlan Compile(ChangePlan plan, IReadOnlyDictionary<string, EntityDef>? model = null, IReadOnlyDictionary<string, AppDef>? apps = null) =>
        PlanCompiler.Compile(plan, model ?? Empty, Roles, NoBoards, apps ?? NoApps);

    private static (IReadOnlyDictionary<string, EntityDef> Model, IReadOnlyDictionary<string, AppDef> Apps) Apply(ChangePlan plan,
        IReadOnlyDictionary<string, EntityDef>? model = null, IReadOnlyDictionary<string, AppDef>? apps = null)
    {
        var c = Compile(plan, model, apps);
        Assert.True(c.Preview.Valid, Errors(c.Preview.Errors));
        return (c.ResultingModel, c.Apps!);
    }

    private static SetAppOp App(string name, string? label = null, string? icon = null, int? order = null, string? description = null) =>
        new() { App = new AppDef { Name = name, Label = label ?? name, Icon = icon, Order = order, Description = description } };

    // ---------------- H1 CA4: el metadato ----------------

    [Fact]
    public void H1_CA4_setApp_declara_y_actualiza_conservando_la_capitalizacion()
    {
        var (_, apps) = Apply(Plan(App("Ventas", "Ventas", "badge-dollar-sign", 15, "Clientes, pedidos y facturas.")));
        Assert.Equal(new AppDef { Name = "Ventas", Label = "Ventas", Icon = "badge-dollar-sign", Order = 15, Description = "Clientes, pedidos y facturas." }, apps["ventas"]);
        var c = Compile(Plan(App("VENTAS", "Comercial")), apps: apps);
        Assert.True(c.Preview.Valid, Errors(c.Preview.Errors));
        Assert.Equal("Ventas", c.Apps!["ventas"].Name);
        Assert.Equal("Comercial", c.Apps["ventas"].Label);
        Assert.Contains(c.Preview.Steps, s => s.Description == "Actualizar el módulo 'Comercial'");
        Assert.Equal(["Ventas"], c.TouchedApps!);
        Assert.Empty(c.TouchedEntities);
    }

    [Theory]
    [InlineData("", "Ventas", null, null, "nombre del módulo")]
    [InlineData("Ventas\u0007", "Ventas", null, null, "nombre del módulo")]
    [InlineData("Ventas", "", null, null, "etiqueta")]
    [InlineData("Ventas", "Ventas", "No Válido", null, "")]
    [InlineData("Ventas", "Ventas", null, 10000, "orden")]
    [InlineData("Ventas", "Ventas", null, -1, "orden")]
    public void H1_CA4_setApp_invalido_se_rechaza(string name, string label, string? icon, int? order, string fragment)
    {
        var c = Compile(Plan(App(name, label, icon, order)));
        Assert.False(c.Preview.Valid);
        Assert.Contains(c.Preview.Errors, e => e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(c.TouchedApps!);
    }

    [Fact]
    public void H1_CA4_un_nombre_de_61_caracteres_o_una_descripcion_larga_se_rechazan()
    {
        Assert.False(Compile(Plan(App(new string('a', 61)))).Preview.Valid);
        Assert.False(Compile(Plan(App("Ventas", description: new string('d', 301)))).Preview.Valid);
        Assert.True(Compile(Plan(App("Recursos Humanos", "Recursos humanos"))).Preview.Valid); // como un group: espacios y acentos
    }

    [Fact]
    public void H1_CA4_archiveApp_conserva_la_declaracion_y_rechaza_lo_inexistente()
    {
        var (_, apps) = Apply(Plan(App("Ventas")));
        var (_, archived) = Apply(Plan(new ArchiveAppOp { Name = "ventas" }), apps: apps);
        Assert.True(archived["Ventas"].IsArchived);
        Assert.False(Compile(Plan(new ArchiveAppOp { Name = "Ventas" }), apps: archived).Preview.Valid);
        Assert.False(Compile(Plan(new ArchiveAppOp { Name = "Nada" })).Preview.Valid);
        // Declararlo otra vez lo reactiva.
        var (_, again) = Apply(Plan(App("Ventas")), apps: archived);
        Assert.False(again["Ventas"].IsArchived);
    }

    [Fact]
    public void H1_CA4_setApp_viaja_como_operacion_json()
    {
        var plan = JsonSerializer.Deserialize<ChangePlan>("""
            { "description": "x", "operations": [ { "op": "setApp", "app": { "name": "Ventas", "label": "Ventas", "order": 15 } },
                                                  { "op": "archiveApp", "name": "Ventas" } ] }
            """, MetadataStore.Json)!;
        Assert.IsType<SetAppOp>(plan.Operations[0]);
        Assert.IsType<ArchiveAppOp>(plan.Operations[1]);
    }

    [Fact]
    public void H1_CA4_la_huella_de_la_vista_previa_cubre_el_modulo()
    {
        var (_, apps) = Apply(Plan(App("Ventas")));
        var touched = new HashSet<string>(["Ventas"], Ci);
        var before = ModelVersion.Base(Empty, NoBoards, new HashSet<string>(), null, apps, touched);
        var (_, changed) = Apply(Plan(App("Ventas", "Comercial")), apps: apps);
        Assert.NotEqual(before, ModelVersion.Base(Empty, NoBoards, new HashSet<string>(), null, changed, touched));
        // Sin módulos tocados, la huella es la de siempre (los planes anteriores a la 024 no cambian).
        Assert.Equal(ModelVersion.Base(Empty, NoBoards, new HashSet<string>(), null), ModelVersion.Base(Empty, NoBoards, new HashSet<string>(), null, apps, null));
    }

    // ---------------- H1 CA1 · CA5, H2 CA1: módulos efectivos ----------------

    private static IReadOnlyDictionary<string, EntityDef> Model() => Apply(Plan(
        Catalog("Proveedor", "Compras"), Catalog("Orden", "compras"), Catalog("Almacen", "Inventario"),
        Catalog("Empaque", null), Catalog("Camion", " "), Catalog("Empleado", "Recursos Humanos"))).Model;

    [Fact]
    public void H1_CA1_modulos_efectivos_declarados_implicitos_y_General_al_final()
    {
        var model = Model();
        var (_, apps) = Apply(Plan(App("Compras", "Compras", "shopping-cart", 10, "Órdenes"), App("Inventario", "Inventario", "boxes", 20)));
        var list = Apps.Effective(model, apps, _ => true);
        Assert.Equal(["Compras", "Inventario", "Recursos Humanos", "General"], list.Select(a => a.Name));
        var compras = list[0];
        Assert.True(compras.Declared);
        Assert.Equal(("shopping-cart", "Órdenes", 10), (compras.Icon, compras.Description, compras.Order));
        Assert.Equal(["Orden", "Proveedor"], compras.Entities.Order(Ci)); // el grupo no distingue mayúsculas
        var rh = list[2];
        Assert.False(rh.Declared);
        Assert.Equal(("Recursos Humanos", Apps.ImplicitIcon, (string?)null), (rh.Label, rh.Icon, rh.Description));
        // H2 CA1: sin grupo (o en blanco) → General.
        Assert.Equal(["Camion", "Empaque"], list[3].Entities.Order(Ci));
        Assert.Equal(Apps.DefaultIcon, list[3].Icon);
    }

    [Fact]
    public void H1_CA5_solo_modulos_con_algun_catalogo_visible()
    {
        var model = Model();
        var list = Apps.Effective(model, NoApps, e => e.Name is "Almacen" or "Empaque");
        Assert.Equal(["Inventario", "General"], list.Select(a => a.Name));
        Assert.Equal(["Almacen"], list[0].Entities);
        // Archivados y partidas no cuentan.
        var archived = Apply(Plan(new ArchiveEntityOp { Entity = "Almacen" }), model).Model;
        Assert.DoesNotContain(Apps.Effective(archived, NoApps, _ => true), a => a.Name == "Inventario");
    }

    [Fact]
    public void H1_CA4_un_modulo_archivado_deja_a_sus_catalogos_en_uno_implicito_y_el_modelador_ve_todos()
    {
        var model = Model();
        var (_, apps) = Apply(Plan(App("Compras", "Compras", "shopping-cart", 10), App("Ventas", "Ventas", "store", 15)));
        var (_, archived) = Apply(Plan(new ArchiveAppOp { Name = "Compras" }), apps: apps);
        var compras = Apps.Effective(model, archived, _ => true).Single(a => a.Name == "Compras");
        Assert.False(compras.Declared);
        Assert.Equal(Apps.ImplicitIcon, compras.Icon);
        // El modelador: también el declarado sin catálogos (Ventas) y el archivado.
        var all = Apps.All(model, archived);
        Assert.Contains(all, a => a.Name == "Ventas" && a.Declared && a.Entities.Count == 0 && a.IsArchived == false);
        Assert.Contains(all, a => a.Name == "Compras" && a.Declared && a.IsArchived == true);
        Assert.Contains(all, a => a.Name == "Compras" && !a.Declared && a.Entities.Count == 2);
    }

    // ---------------- H1 CA6: paquetes y Entornos ----------------

    /// <summary>
    /// Regresión (spec 024): cada módulo de <c>modules/</c> que crea catálogos con un grupo declara ese módulo; sin la
    /// declaración, el selector mostraría un módulo implícito sin ícono ni descripción.
    /// </summary>
    [Fact]
    public void H1_CA6_los_paquetes_declaran_el_modulo_de_cada_grupo_que_crean()
    {
        var packages = Directory.EnumerateFiles(ModulesFolder(), "*.json").Select(f => Package(Path.GetFileName(f))).ToList();
        var declared = packages.SelectMany(p => p.FullChain.SelectMany(c => c.Plan.Operations).OfType<SetAppOp>()).Select(o => o.App.Name).ToHashSet(Ci);
        var groups = packages.SelectMany(p => p.FullChain.SelectMany(c => c.Plan.Operations).OfType<CreateEntityOp>())
            .Select(o => o.Group).OfType<string>().ToHashSet(Ci);
        Assert.NotEmpty(groups);
        Assert.DoesNotContain(groups, g => !declared.Contains(g));
        foreach (var o in packages.SelectMany(p => p.FullChain.SelectMany(c => c.Plan.Operations).OfType<SetAppOp>()))
        {
            Assert.False(string.IsNullOrWhiteSpace(o.App.Description), $"{o.App.Name}: falta la descripción de la tarjeta");
            Assert.NotNull(o.App.Order);
        }
    }

    [Fact]
    public void H1_CA6_las_actualizaciones_de_los_paquetes_compilan_y_declaran_su_modulo()
    {
        IReadOnlyDictionary<string, EntityDef> model = Empty;
        IReadOnlyDictionary<string, DashboardDef> boards = NoBoards;
        IReadOnlyDictionary<string, AppDef> apps = NoApps;
        var roles = new HashSet<string>(Roles, Ci);
        foreach (var file in new[] { "compras.json", "inventario.json", "contabilidad.json", "costeo.json" })
        {
            var p = Package(file);
            roles.UnionWith(p.AllRoles.Select(r => r.Name));
            foreach (var (_, plan) in p.FullChain)
            {
                var c = PlanCompiler.Compile(plan, model, roles, boards, apps);
                Assert.True(c.Preview.Valid, $"{file}: {Errors(c.Preview.Errors)}");
                (model, boards, apps) = (c.ResultingModel, c.Dashboards!, c.Apps!);
            }
        }
        var list = Apps.Effective(model, apps, _ => true);
        Assert.Equal(["Compras", "Inventario", "Contabilidad"], list.Select(a => a.Name));
        Assert.All(list, a => Assert.True(a.Declared));
        Assert.Equal(["1.10.0", "1.8.0", "1.3.0"], new[] { Package("compras.json").Version, Package("inventario.json").Version, Package("contabilidad.json").Version });
    }

    [Fact]
    public void H1_CA6_exportar_lleva_el_modulo_y_el_diff_lo_emite_solo_si_difiere()
    {
        var model = Model();
        var (_, apps) = Apply(Plan(App("Compras", "Compras", "shopping-cart", 10), App("Inventario", "Inventario", "boxes", 20)));
        var export = ModelDiff.Export(model, NoBoards, new HashSet<string>(["Proveedor"], Ci), new HashSet<string>(Ci), "p", apps);
        var set = Assert.Single(export.Plan.Operations.OfType<SetAppOp>());
        Assert.Equal("Compras", set.App.Name);

        var p = new ModulePackage("compras-x", "Compras X", "1.0.0", "package", "", export.Plan, [], []);
        Assert.Equal(["Compras"], ModelDiff.ScopeApps(p));
        // Destino sin el módulo: lo declara.
        var target = Apply(Plan(Catalog("Orden", "Compras"))).Model;
        var desired = ModelDiff.DesiredState(p, target, NoBoards, Roles, NoApps);
        Assert.Empty(desired.Errors);
        Assert.Equal(["Compras"], desired.Apps.Keys);
        var diff = ModelDiff.Compute(desired.Entities, desired.Dashboards, target, NoBoards, "d", null, desired.Apps, NoApps);
        Assert.Single(diff.Plan.Operations.OfType<SetAppOp>());
        // Destino con la misma declaración: nada que hacer con el módulo.
        var same = ModelDiff.Compute(desired.Entities, desired.Dashboards, target, NoBoards, "d", null, desired.Apps, apps);
        Assert.Empty(same.Plan.Operations.OfType<SetAppOp>());
        // Destino con otra etiqueta: se actualiza.
        var (_, other) = Apply(Plan(App("Compras", "Abastecimiento", "shopping-cart", 10)), apps: apps);
        var update = ModelDiff.Compute(desired.Entities, desired.Dashboards, target, NoBoards, "d", null, desired.Apps, other);
        Assert.Equal("Compras", Assert.Single(update.Plan.Operations.OfType<SetAppOp>()).App.Label);
    }

    [Fact]
    public void H1_CA6_revertir_archiva_el_modulo_creado_y_devuelve_el_cambiado()
    {
        var target = Apply(Plan(Catalog("Orden", "Compras"))).Model;
        var (_, targetApps) = Apply(Plan(App("Inventario", "Almacén", "boxes", 20)));
        var p = new ModulePackage("x", "X", "1.0.0", "package", "", Plan(App("Compras", "Compras", "shopping-cart", 10), App("Inventario", "Inventario", "boxes", 20)), [], []);
        var desired = ModelDiff.DesiredState(p, target, NoBoards, Roles, targetApps);
        var diff = ModelDiff.Compute(desired.Entities, desired.Dashboards, target, NoBoards, "d", null, desired.Apps, targetApps);
        var (_, applied) = Apply(diff.Plan, target, targetApps);
        Assert.Equal("Inventario", applied["Inventario"].Label);

        var scope = ModelDiff.ScopeApps(p);
        var before = ModelSnapshot.Take(new HashSet<string>(Ci), new HashSet<string>(Ci), target, NoBoards, scope, targetApps);
        var after = ModelSnapshot.Take(new HashSet<string>(Ci), new HashSet<string>(Ci), target, NoBoards, scope, applied);
        var revert = ModelDiff.Revert(before, after, target, NoBoards, "r", applied);
        var (_, reverted) = Apply(revert.Plan, target, applied);
        Assert.True(reverted["Compras"].IsArchived);
        Assert.Equal("Almacén", reverted["Inventario"].Label);
    }

    /// <summary>Las instantáneas guardadas antes de la 024 no traen módulos: se leen y revierten como hasta ahora.</summary>
    [Fact]
    public void H1_CA6_instantanea_anterior_sin_modulos()
    {
        var old = JsonSerializer.Deserialize<ModelSnapshot>("""{ "entities": [], "dashboards": [], "entityDefs": [], "dashboardDefs": [] }""", MetadataStore.Json)!;
        Assert.Null(old.Apps);
        Assert.Empty(old.AppMap());
        var (_, apps) = Apply(Plan(App("Compras")));
        Assert.Empty(ModelDiff.Revert(old, old, Empty, NoBoards, "r", apps).Plan.Operations);
    }

    // ---------------- H1 CA7: asistente ----------------

    [Fact]
    public void H1_CA7_el_asistente_conoce_los_modulos_y_avisa_al_archivar()
    {
        var (_, apps) = Apply(Plan(App("Compras", "Compras", "shopping-cart", 10, "Órdenes")));
        var ctx = AssistantContext.Build(Model(), NoBoards, Roles, 200_000, apps);
        Assert.Contains("\"apps\":[{\"name\":\"Compras\"", ctx.Json);
        Assert.Contains("setApp", AssistantPrompt.Instructions);
        Assert.Contains(PlanWarnings.For(Plan(new ArchiveAppOp { Name = "Compras" }), Model()), w => w.Contains("Archiva el módulo «Compras»"));
    }
}
