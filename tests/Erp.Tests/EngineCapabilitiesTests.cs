using System.Text.Json;
using Erp.Api.Assistant;
using Erp.Api.Environments;
using Erp.Api.Metadata;

namespace Erp.Tests;

/// <summary>Spec 023: listas que se fusionan (merge), reglas de aviso y rutas de tres niveles.</summary>
public class EngineCapabilitiesTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlyDictionary<string, EntityDef> Empty = new Dictionary<string, EntityDef>(Ci);
    private static readonly IReadOnlyDictionary<string, DashboardDef> NoBoards = new Dictionary<string, DashboardDef>(Ci);
    private static readonly HashSet<string> SystemRoles = new(["Administrador", "Usuario"], Ci);

    private static string ModulesFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "modules", "compras.json"))) return Path.Combine(d.FullName, "modules");
        throw new DirectoryNotFoundException("modules");
    }

    internal static ModulePackage Package(string file) =>
        JsonSerializer.Deserialize<ModulePackage>(File.ReadAllText(Path.Combine(ModulesFolder(), file)), PackageJson.Read)!;

    private static string Errors(IEnumerable<PlanError> errors) => string.Join(" | ", errors.Select(e => $"#{e.Operation} {e.Message}"));

    /// <summary>Instala los módulos en orden con todas sus actualizaciones (o hasta <paramref name="upTo"/> en el último).</summary>
    internal static (IReadOnlyDictionary<string, EntityDef> Model, IReadOnlyDictionary<string, DashboardDef> Boards, HashSet<string> Roles) Installed(
        params string[] files)
    {
        IReadOnlyDictionary<string, EntityDef> model = Empty;
        IReadOnlyDictionary<string, DashboardDef> boards = NoBoards;
        var roles = new HashSet<string>(SystemRoles, Ci);
        foreach (var file in files)
        {
            var p = Package(file);
            roles.UnionWith(p.AllRoles.Select(r => r.Name));
            foreach (var (_, plan) in p.FullChain)
            {
                var c = PlanCompiler.Compile(plan, model, roles, boards);
                Assert.True(c.Preview.Valid, $"{file}: {Errors(c.Preview.Errors)}");
                (model, boards) = (c.ResultingModel, c.Dashboards!);
            }
        }
        return (model, boards, roles);
    }

    private static ChangePlan Plan(params Operation[] ops) => new() { Description = "prueba", Operations = ops };

    private static (IReadOnlyDictionary<string, EntityDef>, IReadOnlyDictionary<string, DashboardDef>) Apply(ChangePlan plan,
        IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, DashboardDef> boards, IReadOnlySet<string> roles)
    {
        var c = PlanCompiler.Compile(plan, model, roles, boards);
        Assert.True(c.Preview.Valid, Errors(c.Preview.Errors));
        return (c.ResultingModel, c.Dashboards!);
    }

    // ---------------- H1: listas que se fusionan ----------------

    /// <summary>Regresión (spec 023, 1b): con merge la vista previa decía que quitaba las reglas que conserva.</summary>
    [Fact]
    public void H1_CA2_merge_de_reglas_no_se_reporta_como_quitar()
    {
        var (model, _, _) = Installed("compras.json", "inventario.json");
        Assert.NotEmpty(model["Recepcion"].Rules ?? []);
        var plan = Plan(new SetRulesOp
        {
            Entity = "Recepcion", Merge = true,
            Rules = [new BusinessRule { Name = "Nueva", Assert = "TRUE", Message = "m" }],
        });
        Assert.DoesNotContain(PlanWarnings.For(plan, model), w => w.Contains("Quita reglas"));
        // Sin merge sí las quita (y lo dice).
        var replace = Plan(new SetRulesOp { Entity = "Recepcion", Rules = [new BusinessRule { Name = "Nueva", Assert = "TRUE", Message = "m" }] });
        Assert.Contains(PlanWarnings.For(replace, model), w => w.Contains("Quita reglas"));
    }

    /// <summary>
    /// Regresión (spec 023, 1c): un paquete de Entornos que agrega un campo y una regla a un catálogo que no crea perdía esas
    /// operaciones: el estado deseado solo tenía los catálogos creados.
    /// </summary>
    [Fact]
    public void H1_CA3_paquete_que_extiende_un_catalogo_ajeno_llega_al_destino()
    {
        var (model, boards, roles) = Installed("compras.json");
        var p = TallerPackage();
        var (desired, desiredBoards, errors, _) = ModelDiff.Desired(p, model, boards, roles);
        Assert.Empty(errors);
        var diff = ModelDiff.Compute(desired, desiredBoards, model, boards, "taller");
        Assert.Contains(diff.Plan.Operations, o => o is AddFieldOp { Entity: "Proveedor", Field.Name: "Taller" });
        var rules = diff.Plan.Operations.OfType<SetRulesOp>().Single(o => o.Entity == "Proveedor");
        // El estado deseado es completo: conserva las reglas de Compras y agrega la del paquete.
        Assert.Equal(model["Proveedor"].Rules!.Select(r => r.Name).Append("ConTaller"), rules.Rules.Select(r => r.Name));

        var (applied, _) = Apply(diff.Plan, model, boards, roles);
        Assert.NotNull(applied["Proveedor"].FindField("Taller"));
    }

    private static ModulePackage TallerPackage() => new("taller", "Taller", "1.0.0", "package", "", Plan(
        new CreateEntityOp { Name = "Taller", Label = "Taller", LabelPlural = "Talleres", Fields = [new FieldDef { Name = "Nombre", Label = "Nombre", Type = "text" }] },
        new AddFieldOp { Entity = "Proveedor", Field = new FieldDef { Name = "Taller", Label = "Taller", Type = "reference", ReferenceEntity = "Taller" } },
        new SetRulesOp { Entity = "Proveedor", Merge = true, Rules = [new BusinessRule { Name = "ConTaller", Assert = "NOT ISBLANK(Taller)", Message = "Elige el taller." }] }),
        [], []);

    /// <summary>H1 CA3 (017): revertir el paquete devuelve el catálogo extendido a como estaba.</summary>
    [Fact]
    public void H1_CA3_revertir_un_paquete_que_extiende_restaura_el_catalogo()
    {
        var (model, boards, roles) = Installed("compras.json");
        var p = TallerPackage();
        var (desired, desiredBoards, _, _) = ModelDiff.Desired(p, model, boards, roles);
        var (applied, appliedBoards) = Apply(ModelDiff.Compute(desired, desiredBoards, model, boards, "taller").Plan, model, boards, roles);
        var (scope, scopeBoards) = (ModelDiff.SnapshotScope(p).Entities, ModelDiff.SnapshotScope(p).Dashboards);
        Assert.Equal(["Proveedor", "Taller"], scope.Order(Ci));
        var before = ModelSnapshot.Take(scope, scopeBoards, model, boards);
        var after = ModelSnapshot.Take(scope, scopeBoards, applied, appliedBoards);
        var revert = ModelDiff.Revert(before, after, applied, appliedBoards, "revertir");
        Assert.Empty(revert.Conflicts);
        var (reverted, _) = Apply(revert.Plan, applied, appliedBoards, roles);
        Assert.True(reverted["Proveedor"].FindField("Taller")!.IsArchived);
        Assert.Equal(model["Proveedor"].Rules!.Select(r => r.Name), reverted["Proveedor"].Rules!.Select(r => r.Name));
    }

    /// <summary>
    /// Regresión (spec 023, 1d): ningún módulo reemplaza una lista (reglas, automatizaciones, vistas, reportes, webhooks,
    /// tareas) de un catálogo que crea otro módulo. Costeo 1.1.0 reemplazaba las vistas de Recepción (de Compras).
    /// </summary>
    [Fact]
    public void H1_CA4_ningun_modulo_reemplaza_listas_de_catalogos_ajenos()
    {
        var packages = Directory.EnumerateFiles(ModulesFolder(), "*.json").Select(f => Package(Path.GetFileName(f))).ToList();
        var owner = packages.SelectMany(p => p.Plan.Operations.OfType<CreateEntityOp>().Select(o => (o.Name, p.Id)))
            .ToDictionary(x => x.Name, x => x.Id, Ci);
        var offenders = new List<string>();
        foreach (var p in packages)
        foreach (var (label, plan) in p.FullChain)
        foreach (var op in plan.Operations)
        {
            var (entity, merge) = op switch
            {
                SetRulesOp o => (o.Entity, o.Merge),
                SetAutomationsOp o => (o.Entity, o.Merge),
                SetViewsOp o => (o.Entity, MergeOf(o)),
                SetReportsOp o => (o.Entity, MergeOf(o)),
                SetWebhooksOp o => (o.Entity, MergeOf(o)),
                SetSchedulesOp o => (o.Entity, MergeOf(o)),
                _ => (null, true),
            };
            if (entity is not null && !merge && owner.TryGetValue(entity, out var o2) && o2 != p.Id)
                offenders.Add($"{p.Id} {label}: {op.GetType().Name} {entity} (de {o2})");
        }
        Assert.Empty(offenders);
    }

    // Con el código anterior estas operaciones no tenían Merge: se lee por reflexión para que la prueba compile y falle.
    private static bool MergeOf(Operation op) => op.GetType().GetProperty("Merge")?.GetValue(op) is true;

    /// <summary>
    /// CLARIFY (spec 023): las actualizaciones viejas de Compras que reemplazan reglas de RecepcionPartida no pueden correr
    /// después de instalar Inventario, porque su cadena no compila sobre Compras anterior a esas versiones.
    /// </summary>
    [Fact]
    public void H1_inventario_no_se_instala_sobre_compras_anterior()
    {
        var compras = Package("compras.json");
        IReadOnlyDictionary<string, EntityDef> model = Empty;
        IReadOnlyDictionary<string, DashboardDef> boards = NoBoards;
        var roles = new HashSet<string>(SystemRoles, Ci);
        roles.UnionWith(compras.AllRoles.Select(r => r.Name));
        foreach (var (label, plan) in compras.FullChain.TakeWhile(c => c.Label != "v1.2.0"))
            (model, boards) = Apply(plan, model, boards, roles);
        var inventario = Package("inventario.json");
        roles.UnionWith(inventario.AllRoles.Select(r => r.Name));
        var valid = true;
        foreach (var (_, plan) in inventario.FullChain)
        {
            var c = PlanCompiler.Compile(plan, model, roles, boards);
            if (!c.Preview.Valid) { valid = false; break; }
            (model, boards) = (c.ResultingModel, c.Dashboards!);
        }
        Assert.False(valid);
    }
}
