using System.Text.Json;
using Erp.Api.Environments;
using Erp.Api.Metadata;

namespace Erp.Tests;

/// <summary>Spec 017: reactivar campos y catálogos archivados, instantáneas y el diff inverso de un paquete aplicado.</summary>
public class RollbackTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlyDictionary<string, EntityDef> Empty = new Dictionary<string, EntityDef>(Ci);
    private static readonly IReadOnlyDictionary<string, DashboardDef> NoBoards = new Dictionary<string, DashboardDef>(Ci);
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(["Administrador", "Usuario"], Ci);

    private static ChangePlan Plan(params Operation[] ops) => new() { Description = "prueba", Operations = ops };
    private static string Errors(IEnumerable<PlanError> errors) => string.Join(" | ", errors.Select(e => $"#{e.Operation} {e.Message}"));

    private static CompiledPlan Compile(IReadOnlyDictionary<string, EntityDef> model, params Operation[] ops) =>
        PlanCompiler.Compile(Plan(ops), model, Roles, NoBoards);

    private static IReadOnlyDictionary<string, EntityDef> Apply(IReadOnlyDictionary<string, EntityDef> model, params Operation[] ops)
    {
        var c = Compile(model, ops);
        Assert.True(c.Preview.Valid, Errors(c.Preview.Errors));
        return c.ResultingModel;
    }

    private static FieldDef Text(string name, bool required = false, bool unique = false) =>
        new() { Name = name, Label = name, Type = "text", Required = required, Unique = unique };

    private static CreateEntityOp Entity(string name, params FieldDef[] fields) =>
        new() { Name = name, Label = name, LabelPlural = name + "s", Fields = [Text("Clave", required: true, unique: true), .. fields] };

    // ---------------- H4: restoreEntity y restoreField ----------------

    [Fact]
    public void Reactivar_un_campo_archivado_lo_devuelve_tal_cual_sin_ddl()
    {
        var model = Apply(Empty, Entity("Producto", Text("Notas"), Text("Peso")));
        var archived = Apply(model, new ArchiveFieldOp { Entity = "Producto", Field = "Notas" });
        var c = Compile(archived, new RestoreFieldOp { Entity = "Producto", Field = "Notas" });
        Assert.True(c.Preview.Valid, Errors(c.Preview.Errors));
        Assert.Empty(c.Sql);
        Assert.False(c.ResultingModel["Producto"].FindField("Notas")!.IsArchived);
        Assert.Contains("Reactivar campo 'Notas'", c.Preview.Steps.Single().Description);
        // Ida y vuelta: archivar y reactivar deja el campo como estaba.
        Assert.Equal(JsonSerializer.Serialize(model["Producto"], MetadataStore.Json), JsonSerializer.Serialize(c.ResultingModel["Producto"], MetadataStore.Json));
    }

    [Theory]
    [InlineData("Producto", "Peso", "no está archivado")]
    [InlineData("Producto", "NoExiste", "no existe")]
    [InlineData("Otro", "Peso", "no existe")]
    public void Reactivar_un_campo_que_no_esta_archivado_es_error(string entity, string field, string message)
    {
        var model = Apply(Empty, Entity("Producto", Text("Peso")));
        var c = Compile(model, new RestoreFieldOp { Entity = entity, Field = field });
        Assert.False(c.Preview.Valid);
        Assert.Contains(c.Preview.Errors, e => e.Message.Contains(message));
    }

    [Fact]
    public void Un_campo_obligatorio_reactivado_avisa_que_los_registros_nuevos_lo_tienen_vacio()
    {
        var model = Apply(Empty, Entity("Producto", Text("Codigo", required: true)));
        var archived = Apply(model, new ArchiveFieldOp { Entity = "Producto", Field = "Codigo" });
        var step = Compile(archived, new RestoreFieldOp { Entity = "Producto", Field = "Codigo" }).Preview.Steps.Single();
        Assert.Contains("lo tienen vacío", step.Description);
    }

    [Fact]
    public void Un_campo_unico_de_un_catalogo_por_empresa_recrea_su_indice_por_empresa()
    {
        var model = Apply(Empty, Entity("Almacen", Text("Codigo", unique: true)));
        var archived = Apply(model, new ArchiveFieldOp { Entity = "Almacen", Field = "Codigo" });
        var perCompany = Apply(archived, new UpdateEntityOp { Entity = "Almacen", PerCompany = true });
        var c = Compile(perCompany, new RestoreFieldOp { Entity = "Almacen", Field = "Codigo" });
        Assert.True(c.Preview.Valid, Errors(c.Preview.Errors));
        Assert.Equal(2, c.Sql.Count);
        Assert.StartsWith("DROP INDEX IF EXISTS [UX_Almacen_Codigo]", c.Sql[0]);
        Assert.Contains("([CompanyId], [Codigo]) WHERE [Codigo] IS NOT NULL", c.Sql[1]);
        // En uno compartido no hace falta tocar el índice.
        Assert.Empty(Compile(archived, new RestoreFieldOp { Entity = "Almacen", Field = "Codigo" }).Sql);
    }

    [Fact]
    public void Reactivar_un_catalogo_archivado_y_errores()
    {
        var model = Apply(Empty, Entity("Marca"));
        var archived = Apply(model, new ArchiveEntityOp { Entity = "Marca" });
        var c = Compile(archived, new RestoreEntityOp { Entity = "Marca" });
        Assert.True(c.Preview.Valid, Errors(c.Preview.Errors));
        Assert.False(c.ResultingModel["Marca"].IsArchived);
        Assert.Empty(c.Sql);
        Assert.Contains(Compile(model, new RestoreEntityOp { Entity = "Marca" }).Preview.Errors, e => e.Message.Contains("no está archivado"));
        Assert.Contains(Compile(model, new RestoreEntityOp { Entity = "Nada" }).Preview.Errors, e => e.Message.Contains("no existe"));
    }

    [Fact]
    public void Lo_reactivado_no_puede_quedar_apuntando_a_algo_archivado()
    {
        // Producto.Marca → Marca. Se archiva el campo y luego el catálogo; reactivar solo el campo deja una referencia rota.
        var model = Apply(Empty, Entity("Marca"), Entity("Producto", new FieldDef { Name = "Marca", Label = "Marca", Type = "reference", ReferenceEntity = "Marca" }));
        var archived = Apply(model, new ArchiveFieldOp { Entity = "Producto", Field = "Marca" }, new ArchiveEntityOp { Entity = "Marca" });
        var broken = Compile(archived, new RestoreFieldOp { Entity = "Producto", Field = "Marca" });
        Assert.Contains(broken.Preview.Errors, e => e.Message.Contains("apunta a 'Marca', que está archivado"));
        // En cualquier orden dentro del mismo plan, sí.
        Assert.True(Compile(archived, new RestoreFieldOp { Entity = "Producto", Field = "Marca" }, new RestoreEntityOp { Entity = "Marca" }).Preview.Valid);
    }

    [Fact]
    public void Una_partida_no_se_reactiva_sin_su_documento_ni_el_documento_sin_sus_partidas()
    {
        var model = Pedido(Empty);
        var archived = Apply(model, new ArchiveFieldOp { Entity = "Pedido", Field = "Partidas" },
            new ArchiveEntityOp { Entity = "PedidoPartida" }, new ArchiveEntityOp { Entity = "Pedido" });
        Assert.Contains(Compile(archived, new RestoreEntityOp { Entity = "PedidoPartida" }).Preview.Errors, e => e.Message.Contains("su documento 'Pedido' está archivado"));
        Assert.Contains(Compile(archived, new RestoreEntityOp { Entity = "Pedido" }, new RestoreFieldOp { Entity = "Pedido", Field = "Partidas" }).Preview.Errors,
            e => e.Message.Contains("partidas 'PedidoPartida' no existe"));
        var all = Compile(archived, new RestoreEntityOp { Entity = "PedidoPartida" }, new RestoreEntityOp { Entity = "Pedido" },
            new RestoreFieldOp { Entity = "Pedido", Field = "Partidas" });
        Assert.True(all.Preview.Valid, Errors(all.Preview.Errors));
    }

    private static IReadOnlyDictionary<string, EntityDef> Pedido(IReadOnlyDictionary<string, EntityDef> model) => Apply(model,
        Entity("Pedido"),
        Entity("PedidoPartida", new FieldDef { Name = "Pedido", Label = "Pedido", Type = "parent", ReferenceEntity = "Pedido" }, Text("Concepto")),
        new AddFieldOp { Entity = "Pedido", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = "lines", ChildEntity = "PedidoPartida" } });

    [Fact]
    public void Un_paquete_reactiva_un_catalogo_archivado_en_el_destino()
    {
        var model = Apply(Empty, Entity("Marca", Text("Pais")));
        var archived = Apply(model, new ArchiveFieldOp { Entity = "Marca", Field = "Pais" }, new ArchiveEntityOp { Entity = "Marca" });
        var diff = ModelDiff.Compute(model, NoBoards, archived, NoBoards, "paquete");
        Assert.Empty(diff.Conflicts);
        Assert.Equal(new Operation[] { new RestoreEntityOp { Entity = "Marca" }, new RestoreFieldOp { Entity = "Marca", Field = "Pais" } }, diff.Plan.Operations);
        var after = Apply(archived, [.. diff.Plan.Operations]);
        Assert.Empty(ModelDiff.Compute(model, NoBoards, after, NoBoards, "otra vez").Plan.Operations);
    }

    [Fact]
    public void Un_campo_archivado_con_otra_estructura_sigue_siendo_conflicto()
    {
        var desired = Apply(Empty, Entity("Marca", new FieldDef { Name = "Pais", Label = "Pais", Type = "text", MaxLength = 80 }));
        var target = Apply(Apply(Empty, Entity("Marca", new FieldDef { Name = "Pais", Label = "Pais", Type = "text", MaxLength = 20 })),
            new ArchiveFieldOp { Entity = "Marca", Field = "Pais" });
        var diff = ModelDiff.Compute(desired, NoBoards, target, NoBoards, "paquete");
        Assert.Contains(diff.Conflicts, c => c.Contains("longitud"));
        Assert.DoesNotContain(diff.Plan.Operations, o => o is RestoreFieldOp);
    }

    // ---------------- H1 y H2: instantáneas y diff inverso ----------------

    private sealed record Applied(IReadOnlyDictionary<string, EntityDef> Model, ModelSnapshot Before, ModelSnapshot After);

    /// <summary>Lo que hace EnvironmentService.ApplyAsync, sin base: diff del paquete, aplicar e instantáneas antes y después.</summary>
    private static Applied ApplyPackage(ChangePlan packagePlan, IReadOnlyDictionary<string, EntityDef> target)
    {
        var p = new ModulePackage("ajustes", "Ajustes", "1.1.0", "package", "", packagePlan, [], []);
        var (desired, desiredBoards, errors, _) = ModelDiff.Desired(p, target, NoBoards, Roles);
        Assert.True(errors.Count == 0, Errors(errors));
        var diff = ModelDiff.Compute(desired, desiredBoards, target, NoBoards, "paquete");
        Assert.Empty(diff.Conflicts);
        var after = diff.Plan.Operations.Count == 0 ? target : Apply(target, [.. diff.Plan.Operations]);
        var (scope, boards) = ModelDiff.Scope(p);
        return new(after, ModelSnapshot.Take(scope, boards, target, NoBoards), ModelSnapshot.Take(scope, boards, after, NoBoards));
    }

    /// <summary>Producto y Notas existían; el paquete cambia Producto (etiqueta, campo nuevo, Notas fuera) y crea Marca.</summary>
    private static (IReadOnlyDictionary<string, EntityDef> Original, Applied Applied) Scenario()
    {
        var original = Apply(Empty, Entity("Producto", Text("Nombre"), Text("Notas")));
        var package = Plan(
            Entity("Marca", Text("Pais")),
            new CreateEntityOp
            {
                Name = "Producto", Label = "Artículo", LabelPlural = "Artículos",
                Fields = [Text("Clave", required: true, unique: true), Text("Nombre"),
                    new FieldDef { Name = "Marca", Label = "Marca", Type = "reference", ReferenceEntity = "Marca" }],
            });
        return (original, ApplyPackage(package, original));
    }

    [Fact]
    public void La_instantanea_es_solo_el_alcance_del_paquete_y_va_y_vuelve_sin_cambios()
    {
        var (original, applied) = Scenario();
        Assert.Equal(["Marca", "Producto"], applied.Before.Entities);
        Assert.Equal(["Producto"], applied.Before.EntityDefs.Select(e => e.Name)); // Marca no existía
        Assert.Equal(["Marca", "Producto"], applied.After.EntityDefs.Select(e => e.Name));

        var json = JsonSerializer.Serialize(applied.After, MetadataStore.Json);
        Assert.DoesNotContain("\"records\"", json);
        var back = JsonSerializer.Deserialize<ModelSnapshot>(json, MetadataStore.Json)!;
        Assert.Equal(json, JsonSerializer.Serialize(back, MetadataStore.Json));
        // Una instantánea leída de la base sirve como estado deseado: contra el modelo de ese momento, diff vacío.
        var diff = ModelDiff.Compute(back.EntityMap(), NoBoards, applied.Model, NoBoards, "x");
        Assert.Empty(diff.Plan.Operations);
        Assert.NotEmpty(original);
    }

    [Fact]
    public void Revertir_archiva_lo_creado_devuelve_lo_cambiado_y_reactiva_lo_archivado()
    {
        var (original, applied) = Scenario();
        Assert.True(applied.Model["Producto"].FindField("Notas")!.IsArchived);

        var revert = ModelDiff.Revert(applied.Before, applied.After, applied.Model, NoBoards, "revertir");
        Assert.Empty(revert.Conflicts);
        Assert.Empty(revert.LocalArchives); // Producto.Marca lo agregó el paquete: no es local
        var ops = revert.Plan.Operations;
        Assert.Contains(ops, o => o is UpdateEntityOp { Entity: "Producto", Label: "Producto" });
        Assert.Contains(ops, o => o is RestoreFieldOp { Entity: "Producto", Field: "Notas" });
        Assert.Contains(ops, o => o is ArchiveFieldOp { Entity: "Producto", Field: "Marca" });
        Assert.Contains(ops, o => o is ArchiveEntityOp { Entity: "Marca" });
        // El campo que apunta a Marca se archiva antes que Marca.
        Assert.True(ops.ToList().FindIndex(o => o is ArchiveFieldOp { Field: "Marca" }) < ops.ToList().FindIndex(o => o is ArchiveEntityOp));
        Assert.Contains(revert.Warnings, w => w.Contains("Se archivan los catálogos que creó el paquete (Marcas)"));
        Assert.DoesNotContain(revert.Warnings, w => w.Contains("cambió en este entorno"));

        var reverted = Apply(applied.Model, [.. ops]);
        Assert.True(reverted["Marca"].IsArchived); // archivado, no borrado
        Assert.Empty(ModelDiff.Compute(original, NoBoards, reverted, NoBoards, "x").Plan.Operations);
    }

    [Fact]
    public void Volver_a_aplicar_el_paquete_despues_de_revertir_reactiva_y_converge()
    {
        var (original, applied) = Scenario();
        var reverted = Apply(applied.Model, [.. ModelDiff.Revert(applied.Before, applied.After, applied.Model, NoBoards, "r").Plan.Operations]);
        var package = Plan(
            Entity("Marca", Text("Pais")),
            new CreateEntityOp
            {
                Name = "Producto", Label = "Artículo", LabelPlural = "Artículos",
                Fields = [Text("Clave", required: true, unique: true), Text("Nombre"),
                    new FieldDef { Name = "Marca", Label = "Marca", Type = "reference", ReferenceEntity = "Marca" }],
            });
        var again = ApplyPackage(package, reverted);
        Assert.Empty(ModelDiff.Compute(applied.After.EntityMap(), NoBoards, again.Model, NoBoards, "x").Plan.Operations);
        Assert.NotNull(original);
    }

    [Fact]
    public void Revertir_un_documento_con_partidas_archiva_en_un_orden_que_compila()
    {
        var original = Apply(Empty, Entity("Cliente"));
        var package = Plan(
            Entity("Pedido", new FieldDef { Name = "Cliente", Label = "Cliente", Type = "reference", ReferenceEntity = "Cliente" }),
            Entity("PedidoPartida", new FieldDef { Name = "Pedido", Label = "Pedido", Type = "parent", ReferenceEntity = "Pedido" }, Text("Concepto")),
            new AddFieldOp { Entity = "Pedido", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = "lines", ChildEntity = "PedidoPartida" } });
        var applied = ApplyPackage(package, original);
        var revert = ModelDiff.Revert(applied.Before, applied.After, applied.Model, NoBoards, "r");
        var c = PlanCompiler.Compile(revert.Plan, applied.Model, Roles, NoBoards);
        Assert.True(c.Preview.Valid, Errors(c.Preview.Errors));
        Assert.Equal(new Operation[]
        {
            new ArchiveFieldOp { Entity = "Pedido", Field = "Partidas" },
            new ArchiveEntityOp { Entity = "PedidoPartida" },
            new ArchiveEntityOp { Entity = "Pedido" },
        }, revert.Plan.Operations);
        Assert.False(c.ResultingModel["Cliente"].IsArchived); // lo que no es del paquete no se toca
    }

    [Fact]
    public void Lo_cambiado_despues_de_aplicar_se_advierte_y_los_campos_nuevos_se_confirman()
    {
        var (_, applied) = Scenario();
        var drifted = Apply(applied.Model,
            new AddFieldOp { Entity = "Producto", Field = Text("Extra") },
            new UpdateFieldOp { Entity = "Producto", Field = "Nombre", Label = "Nombre corto" });
        var revert = ModelDiff.Revert(applied.Before, applied.After, drifted, NoBoards, "r");
        Assert.Contains(revert.Warnings, w => w.Contains("«Artículos» cambió en este entorno después de aplicar el paquete"));
        Assert.Equal(["«Artículo» › Extra se agregó en este entorno después del paquete: se archiva (sus datos se conservan)."], revert.LocalArchives);
        Assert.Contains(revert.Plan.Operations, o => o is UpdateFieldOp { Field: "Nombre", Label: "Nombre" });
    }

    [Fact]
    public void Un_proceso_que_agrego_el_paquete_no_se_puede_quitar_al_revertir()
    {
        var original = Apply(Empty, Entity("Producto"));
        var wf = new Workflow
        {
            Initial = "Borrador",
            States = [new WorkflowState { Name = "Borrador", Label = "Borrador" }, new WorkflowState { Name = "Activo", Label = "Activo", Color = "success" }],
            Transitions = [new WorkflowTransition { Name = "Activar", Label = "Activar", From = ["Borrador"], To = "Activo" }],
        };
        var applied = ApplyPackage(Plan(Entity("Producto"), new SetWorkflowOp { Entity = "Producto", Workflow = wf }), original);
        var revert = ModelDiff.Revert(applied.Before, applied.After, applied.Model, NoBoards, "r");
        Assert.Contains(revert.Conflicts, c => c.Contains("la versión anterior no trae el proceso"));
    }

    [Fact]
    public void Los_tableros_que_creo_el_paquete_se_archivan_y_los_que_cambio_vuelven()
    {
        var oldBoard = new DashboardDef { Name = "Ventas", Label = "Ventas" };
        var newBoard = new DashboardDef { Name = "Nuevo", Label = "Nuevo" };
        var before = new ModelSnapshot([], ["Nuevo", "Ventas"], [], [oldBoard]);
        var after = new ModelSnapshot([], ["Nuevo", "Ventas"], [], [newBoard, oldBoard with { Label = "Ventas 2" }]);
        var current = new Dictionary<string, DashboardDef>(Ci) { ["Nuevo"] = newBoard, ["Ventas"] = oldBoard with { Label = "Ventas 2" } };
        var revert = ModelDiff.Revert(before, after, Empty, current, "r");
        Assert.Contains(revert.Plan.Operations, o => o is ArchiveDashboardOp { Name: "Nuevo" });
        Assert.Contains(revert.Plan.Operations, o => o is SetDashboardOp { Dashboard.Label: "Ventas" });
        Assert.Empty(revert.Warnings);
    }
}
