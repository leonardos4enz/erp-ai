using System.Text;
using System.Text.Json;
using Erp.Api.Environments;
using Erp.Api.Metadata;

namespace Erp.Tests;

/// <summary>Spec 009: diff de modelos, paquetes exportados (ida y vuelta), firma y reglas de seguridad del paquete.</summary>
public class EnvironmentTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlyDictionary<string, EntityDef> Empty = new Dictionary<string, EntityDef>(Ci);
    private static readonly IReadOnlyDictionary<string, DashboardDef> NoBoards = new Dictionary<string, DashboardDef>(Ci);
    private static readonly IReadOnlySet<string> System = new HashSet<string>(["Administrador", "Usuario"], Ci);

    private static string ModulesFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "modules", "compras.json"))) return Path.Combine(d.FullName, "modules");
        throw new DirectoryNotFoundException("modules");
    }

    /// <summary>Compras e Inventario instalados con todas sus actualizaciones, como en una base real.</summary>
    private static (IReadOnlyDictionary<string, EntityDef> Model, IReadOnlyDictionary<string, DashboardDef> Boards, HashSet<string> Roles) Installed()
    {
        IReadOnlyDictionary<string, EntityDef> model = Empty;
        IReadOnlyDictionary<string, DashboardDef> boards = NoBoards;
        var roles = new HashSet<string>(System, Ci);
        foreach (var file in new[] { "compras.json", "inventario.json" })
        {
            var p = JsonSerializer.Deserialize<ModulePackage>(File.ReadAllText(Path.Combine(ModulesFolder(), file)), PackageJson.Read)!;
            roles.UnionWith(p.AllRoles.Select(r => r.Name));
            foreach (var (_, plan) in p.FullChain)
            {
                var c = PlanCompiler.Compile(plan, model, roles, boards);
                Assert.True(c.Preview.Valid, Errors(c.Preview.Errors));
                (model, boards) = (c.ResultingModel, c.Dashboards!);
            }
        }
        return (model, boards, roles);
    }

    private static string Errors(IEnumerable<PlanError> errors) => string.Join(" | ", errors.Select(e => $"#{e.Operation} {e.Message}"));

    private static ModulePackage Package(ChangePlan plan, IReadOnlyList<ModuleRole>? roles = null, string id = "ajustes", string version = "1.0.0") =>
        new(id, "Ajustes", version, "package", "", plan, roles ?? [], []);

    /// <summary>Aplica el plan (compilado) y devuelve el modelo resultante.</summary>
    private static (IReadOnlyDictionary<string, EntityDef>, IReadOnlyDictionary<string, DashboardDef>) Apply(ChangePlan plan,
        IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, DashboardDef> boards, IReadOnlySet<string> roles)
    {
        if (plan.Operations.Count == 0) return (model, boards);
        var c = PlanCompiler.Compile(plan, model, roles, boards);
        Assert.True(c.Preview.Valid, Errors(c.Preview.Errors));
        return (c.ResultingModel, c.Dashboards!);
    }

    private static readonly FieldDef Clave = new() { Name = "Clave", Label = "Clave", Type = "text", Required = true, Unique = true };

    private static ChangePlan Plan(params Operation[] ops) => new() { Description = "prueba", Operations = ops };

    // ---------------- Ida y vuelta ----------------

    [Fact]
    public void Exportar_Compras_e_Inventario_y_aplicar_sobre_el_mismo_modelo_no_cambia_nada()
    {
        var (model, boards, roles) = Installed();
        var roots = model.Values.Where(e => !e.IsChild && !e.IsArchived).Select(e => e.Name).ToHashSet(Ci);
        var all = model.Keys.ToHashSet(Ci);
        var export = ModelDiff.Export(model, boards, all, boards.Keys.ToHashSet(Ci), "todo");
        Assert.Empty(export.Conflicts);
        Assert.True(export.Plan.Operations.Count <= PlanCompiler.MaxOperations, $"{export.Plan.Operations.Count} operaciones");
        Assert.Equal(roots.Count + model.Values.Count(e => e.IsChild), export.Plan.Operations.OfType<CreateEntityOp>().Count());

        // El archivo se escribe y se vuelve a leer como cualquier módulo.
        var file = JsonSerializer.Serialize(Package(export.Plan), PackageJson.File);
        Assert.DoesNotContain("\"readOnly\"", file);
        var p = JsonSerializer.Deserialize<ModulePackage>(file, PackageJson.Read)!;

        // Sobre un entorno vacío: el plan crea todo y compila.
        var (fresh, freshBoards, errors, steps) = ModelDiff.Desired(p, Empty, NoBoards, roles);
        Assert.True(errors.Count == 0, Errors(errors));
        Assert.Equal(all.Count, fresh.Count);
        Assert.NotEmpty(steps);
        Assert.Equal(boards.Count, freshBoards.Count);

        // Sobre el mismo modelo: diff vacío.
        var (desired, desiredBoards, errors2, _) = ModelDiff.Desired(p, model, boards, roles);
        Assert.True(errors2.Count == 0, Errors(errors2));
        var diff = ModelDiff.Compute(desired, desiredBoards, model, boards, "nada");
        Assert.Empty(diff.Conflicts);
        Assert.Empty(diff.Warnings);
        Assert.Empty(diff.Plan.Operations);

        // Y desde vacío el diff converge: aplicarlo deja un modelo cuyo diff ya es vacío.
        var create = ModelDiff.Compute(fresh, freshBoards, Empty, NoBoards, "crear");
        var (after, afterBoards) = Apply(create.Plan, Empty, NoBoards, roles);
        Assert.Empty(ModelDiff.Compute(fresh, freshBoards, after, afterBoards, "otra vez").Plan.Operations);
    }

    [Fact]
    public void Exportar_solo_Inventario_deja_las_referencias_a_Compras_como_dependencias()
    {
        var (model, boards, roles) = Installed();
        var inventario = model.Values.Where(e => e.Group == "Inventario").Select(e => e.Name).ToHashSet(Ci);
        Assert.Contains("Existencia", inventario);
        var export = ModelDiff.Export(model, boards, inventario, new HashSet<string>(Ci), "inventario");
        var p = Package(export.Plan);
        // En un entorno con Compras, el paquete es válido y no cambia nada.
        var (desired, desiredBoards, errors, _) = ModelDiff.Desired(p, model, boards, roles);
        Assert.True(errors.Count == 0, Errors(errors));
        Assert.Empty(ModelDiff.Compute(desired, desiredBoards, model, boards, "x").Plan.Operations);
        // En un entorno vacío falta lo que no viaja (Producto, Almacén…): error claro en la vista previa.
        var (_, _, missing, _) = ModelDiff.Desired(p, Empty, NoBoards, roles);
        Assert.Contains(missing, e => e.Message.Contains("no existe"));
    }

    // ---------------- Cambios ----------------

    private static (IReadOnlyDictionary<string, EntityDef> Model, IReadOnlyDictionary<string, DashboardDef> Boards) Base(params Operation[] ops) =>
        Apply(Plan(ops), Empty, NoBoards, System);

    private static Operation Producto(params FieldDef[] extra) => new CreateEntityOp
    {
        Name = "Producto", Label = "Producto", LabelPlural = "Productos",
        Fields = [Clave, new FieldDef { Name = "Nombre", Label = "Nombre", Type = "text" }, .. extra],
    };

    private static ModelDiffResult Diff(IReadOnlyDictionary<string, EntityDef> desired, IReadOnlyDictionary<string, EntityDef> target, params string[] names) =>
        ModelDiff.Compute(desired.Where(kv => names.Contains(kv.Key, Ci)).ToDictionary(kv => kv.Key, kv => kv.Value, Ci), NoBoards, target, NoBoards, "d");

    [Fact]
    public void Campo_nuevo_campo_cambiado_y_campo_local_archivado()
    {
        var (source, _) = Base(Producto(new FieldDef { Name = "Precio", Label = "Precio", Type = "decimal" }));
        var (target, _) = Base(Producto(new FieldDef { Name = "Notas", Label = "Notas", Type = "longtext" }));
        var (renamed, _) = Apply(Plan(new UpdateFieldOp { Entity = "Producto", Field = "Nombre", Label = "Descripción", Required = true }), source, NoBoards, System);

        var diff = Diff(renamed, target, "Producto");
        Assert.Empty(diff.Conflicts);
        var ops = diff.Plan.Operations;
        Assert.Contains(ops, o => o is AddFieldOp { Field.Name: "Precio" });
        Assert.Contains(ops, o => o is UpdateFieldOp { Field: "Nombre", Label: "Descripción", Required: true });
        Assert.Contains(ops, o => o is ArchiveFieldOp { Field: "Notas" });
        // Spec 014 (H4): el campo local se lista aparte para confirmarlo, no entre las advertencias.
        Assert.Contains(diff.LocalArchives, w => w.Contains("Notas") && w.Contains("se archiva"));
        Assert.DoesNotContain(diff.Warnings, w => w.Contains("Notas"));

        var (after, _) = Apply(diff.Plan, target, NoBoards, System);
        Assert.Empty(Diff(renamed, after, "Producto").Plan.Operations);
        Assert.True(after["Producto"].FindField("Notas")!.IsArchived); // archivado, no borrado
    }

    [Fact]
    public void Cambiar_el_tipo_es_conflicto_y_un_campo_archivado_se_reactiva()
    {
        var (source, _) = Base(Producto(new FieldDef { Name = "Peso", Label = "Peso", Type = "decimal" }));
        var (target, _) = Base(Producto(new FieldDef { Name = "Peso", Label = "Peso", Type = "text" }));
        var diff = Diff(source, target, "Producto");
        Assert.Contains(diff.Conflicts, c => c.Contains("Peso") && c.Contains("tipo"));

        // Spec 017 (H4 CA4): antes era conflicto; ahora el diff lo reactiva con restoreField.
        var (archived, _) = Apply(Plan(new ArchiveFieldOp { Entity = "Producto", Field = "Peso" }), source, NoBoards, System);
        var restore = Diff(source, archived, "Producto");
        Assert.Empty(restore.Conflicts);
        Assert.Equal(new Operation[] { new RestoreFieldOp { Entity = "Producto", Field = "Peso" } }, restore.Plan.Operations);
    }

    [Fact]
    public void Proceso_cambiado_se_redefine_y_quitarlo_es_conflicto()
    {
        var wf = new Workflow
        {
            Initial = "Borrador",
            States = [new WorkflowState { Name = "Borrador", Label = "Borrador" }, new WorkflowState { Name = "Activo", Label = "Activo", Color = "success" }],
            Transitions = [new WorkflowTransition { Name = "Activar", Label = "Activar", From = ["Borrador"], To = "Activo" }],
        };
        var (target, _) = Base(Producto(), new SetWorkflowOp { Entity = "Producto", Workflow = wf });
        var (source, _) = Apply(Plan(new SetWorkflowOp { Entity = "Producto", Workflow = wf with { Transitions = [.. wf.Transitions,
            new WorkflowTransition { Name = "Regresar", Label = "Regresar", From = ["Activo"], To = "Borrador" }] } }), target, NoBoards, System);

        var diff = Diff(source, target, "Producto");
        Assert.Single(diff.Plan.Operations.OfType<SetWorkflowOp>());
        Assert.DoesNotContain(diff.Plan.Operations, o => o is AddFieldOp or ArchiveFieldOp); // el campo Estado lo administra el proceso

        var (plain, _) = Base(Producto());
        Assert.Contains(Diff(plain, target, "Producto").Conflicts, c => c.Contains("proceso"));
    }

    [Fact]
    public void Referencias_en_ciclo_se_crean_y_luego_se_agregan()
    {
        var (source, _) = Base(
            new CreateEntityOp { Name = "Cliente", Label = "Cliente", LabelPlural = "Clientes", Fields = [Clave] },
            new CreateEntityOp { Name = "Vendedor", Label = "Vendedor", LabelPlural = "Vendedores",
                Fields = [Clave, new FieldDef { Name = "Principal", Label = "Cliente principal", Type = "reference", ReferenceEntity = "Cliente" }] },
            new AddFieldOp { Entity = "Cliente", Field = new FieldDef { Name = "Vendedor", Label = "Vendedor", Type = "reference", ReferenceEntity = "Vendedor" } });
        var diff = Diff(source, Empty, "Cliente", "Vendedor");
        Assert.Equal(2, diff.Plan.Operations.OfType<CreateEntityOp>().Count());
        Assert.Single(diff.Plan.Operations.OfType<AddFieldOp>());
        var (after, _) = Apply(diff.Plan, Empty, NoBoards, System);
        Assert.Empty(Diff(source, after, "Cliente", "Vendedor").Plan.Operations);
    }

    [Fact]
    public void Lo_que_no_va_en_el_paquete_no_se_toca()
    {
        var (target, _) = Base(Producto(), new CreateEntityOp { Name = "Local", Label = "Local", LabelPlural = "Locales", Fields = [Clave] });
        var (source, _) = Base(Producto());
        var diff = Diff(source, target, "Producto");
        Assert.Empty(diff.Plan.Operations);
        Assert.DoesNotContain(diff.Warnings, w => w.Contains("Local"));
    }

    // ---------------- Firma y huella ----------------

    [Fact]
    public void Firma_hmac_detecta_cualquier_cambio_y_no_depende_del_formato()
    {
        var (model, boards, _) = Installed();
        var export = ModelDiff.Export(model, boards, new HashSet<string>(["Proveedor"], Ci), new HashSet<string>(Ci), "p");
        var key = new string('k', 40);
        var p = Package(export.Plan, [new ModuleRole("Lector", null, false, false, false, [new ModulePermission("Proveedor", true, false, false, false)])]);
        var canonical = PackageSigner.Canonical(p);
        var signed = p with { Signature = PackageSigner.Sign(canonical, key) };

        // El archivo con sangría se lee y da el mismo contenido canónico.
        var file = JsonSerializer.Serialize(signed, PackageJson.File);
        var read = JsonSerializer.Deserialize<ModulePackage>(file, PackageJson.Read)!;
        Assert.Equal(PackageSigner.Hash(canonical), PackageSigner.Hash(PackageSigner.Canonical(read)));
        Assert.True(PackageSigner.Verify(PackageSigner.Canonical(read), read.Signature, key));

        // Un permiso de más, otra clave, sin firma o con basura: no.
        var tampered = JsonSerializer.Deserialize<ModulePackage>(file.Replace("\"canCreate\": false", "\"canCreate\": true"), PackageJson.Read)!;
        Assert.False(PackageSigner.Verify(PackageSigner.Canonical(tampered), tampered.Signature, key));
        Assert.False(PackageSigner.Verify(canonical, signed.Signature, new string('x', 40)));
        Assert.False(PackageSigner.Verify(canonical, null, key));
        Assert.False(PackageSigner.Verify(canonical, "hmac-sha256:zz", key));
        Assert.False(PackageSigner.Verify(canonical, "sha256:" + signed.Signature![PackageSigner.Prefix.Length..], key));
    }

    [Fact]
    public void La_clave_de_firma_corta_no_se_acepta()
    {
        Assert.Throws<InvalidOperationException>(() => EnvironmentOptions.Create("Producción", true, "corta"));
        var ok = EnvironmentOptions.Create(null, false, "");
        Assert.Equal("Desarrollo", ok.Name);
        Assert.False(ok.RequireApproval);
        Assert.False(ok.Signing);
    }

    /// <summary>Spec 034 (H2, R-009-1): con aprobación y sin clave se aceptarían paquetes sin firma; mejor no arrancar.</summary>
    [Fact]
    public void Con_aprobacion_y_sin_clave_no_arranca()
    {
        foreach (var key in new string?[] { null, "" })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EnvironmentOptions.Create("Producción", true, key));
            Assert.Contains("Environments:SigningKey", ex.Message);
            Assert.Contains("sin firmar", ex.Message);
        }
        Assert.True(EnvironmentOptions.Create("Producción", true, new string('k', EnvironmentOptions.MinKeyLength)).Signing);
    }

    // ---------------- Reglas del paquete (H5) ----------------

    private static readonly IReadOnlySet<string> ServerModules = new HashSet<string>(["compras", "inventario"], Ci);

    private static ModulePackage Valid(IReadOnlyList<ModuleRole>? roles = null) =>
        Package(Plan(Producto()), roles ?? [new ModuleRole("Catalogador", null, false, false, true, [new ModulePermission("Producto", true, true, true, false)])]);

    [Fact]
    public void Un_paquete_valido_pasa_las_reglas()
    {
        Assert.Empty(PackageRules.Validate(Valid(), ServerModules, System));
    }

    [Fact]
    public void Roles_con_administracion_permisos_ajenos_y_roles_del_sistema_se_rechazan()
    {
        Assert.Contains(PackageRules.Validate(Valid([new ModuleRole("Jefe", null, true, false, false, [])]), ServerModules, System),
            e => e.Contains("administra el modelo"));
        Assert.Contains(PackageRules.Validate(Valid([new ModuleRole("Jefe", null, false, true, false, [])]), ServerModules, System),
            e => e.Contains("administra el modelo o los usuarios"));
        Assert.Contains(PackageRules.Validate(Valid([new ModuleRole("Espia", null, false, false, false, [new ModulePermission("Nomina", true, false, false, false)])]),
            ServerModules, System), e => e.Contains("'Nomina', que no va en el paquete"));
        Assert.Contains(PackageRules.Validate(Valid([new ModuleRole("Administrador", null, false, false, false, [])]), ServerModules, System),
            e => e.Contains("rol del sistema"));
        Assert.Contains(PackageRules.Validate(Valid([new ModuleRole("A", null, false, false, false, []), new ModuleRole("a", null, false, false, false, [])]),
            ServerModules, System), e => e.Contains("repetido"));
    }

    [Fact]
    public void Datos_ids_de_modulos_del_servidor_y_versiones_invalidas_se_rechazan()
    {
        var seed = Valid() with { Seed = [new ModuleSeed("Producto", "Clave", [new System.Text.Json.Nodes.JsonObject { ["Clave"] = "X" }])] };
        Assert.Contains(PackageRules.Validate(seed, ServerModules, System), e => e.Contains("no lleva datos"));
        Assert.Contains(PackageRules.Validate(Valid() with { Id = "compras" }, ServerModules, System), e => e.Contains("módulo del servidor"));
        Assert.Contains(PackageRules.Validate(Valid() with { Id = "Mal Id" }, ServerModules, System), e => e.Contains("identificador"));
        Assert.Contains(PackageRules.Validate(Valid() with { Version = "1.0" }, ServerModules, System), e => e.Contains("x.y.z"));
        Assert.Contains(PackageRules.Validate(Package(Plan(new ArchiveDashboardOp { Name = "X" })), ServerModules, System), e => e.Contains("no crea ningún"));
    }

    [Theory]
    [InlineData("1.1.0", null, true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.0", "1.0.0", false)]
    [InlineData("1.0", null, false)]
    public void Versiones(string version, string? installed, bool newer) => Assert.Equal(newer, PackageRules.IsNewer(version, installed));

    [Fact]
    public void Siguiente_version_menor()
    {
        Assert.Equal("1.0.0", PackageRules.NextMinor(null));
        Assert.Equal("1.5.0", PackageRules.NextMinor("1.4.2"));
    }

    [Fact]
    public void El_contenido_canonico_no_lleva_la_firma()
    {
        var p = Valid() with { Signature = "hmac-sha256:00" };
        var text = Encoding.UTF8.GetString(PackageSigner.Canonical(p));
        Assert.DoesNotContain("hmac-sha256", text);
        Assert.Equal(PackageSigner.Hash(PackageSigner.Canonical(p)), PackageSigner.Hash(PackageSigner.Canonical(p with { Signature = null })));
    }
}
