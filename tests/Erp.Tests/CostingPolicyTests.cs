using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Environments;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;

namespace Erp.Tests;

/// <summary>Spec 022: políticas de valuación (opciones de cost), capture, setRules con merge y los módulos que las ejercen.</summary>
public class CostingPolicyTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlyDictionary<string, EntityDef> Empty = new Dictionary<string, EntityDef>(Ci);

    private static CostLayerState L(long id, decimal q, decimal v) => new(id, q, v);

    // ---------------- CostMath (H2, H5, H6, H1 CA4) ----------------

    [Fact]
    public void Entrada_sin_costo_y_sin_capas_sigue_la_politica()
    {
        // average (la 020): a cero, aunque haya respaldo.
        Assert.Equal(0m, CostMath.Apply([], CostMethods.Average, 4m, null, 12.5m, 2).Cost);
        Assert.Equal(0m, CostMath.Apply([], CostMethods.Average, 4m, null, 12.5m, 2, new(CostPolicies.Average)).Cost);
        // fallback: al respaldo; sin respaldo (o en cero) se rechaza.
        var conRespaldo = CostMath.Apply([], CostMethods.Fifo, 4m, null, 12.5m, 2, new(CostPolicies.Fallback));
        Assert.Equal((50m, 12.5m), (conRespaldo.Cost, conRespaldo.UnitCost));
        Assert.Contains("ni costo de respaldo", Assert.Throws<AppException>(() =>
            CostMath.Apply([], CostMethods.Fifo, 4m, null, null, 2, new(CostPolicies.Fallback))).Message);
        Assert.Throws<AppException>(() => CostMath.Apply([], CostMethods.Fifo, 4m, null, 0m, 2, new(CostPolicies.Fallback)));
        // require: se rechaza aunque haya respaldo.
        Assert.Contains("captura el costo unitario", Assert.Throws<AppException>(() =>
            CostMath.Apply([], CostMethods.Fifo, 4m, null, 12.5m, 2, new(CostPolicies.Require))).Message);
        // Con capas, las tres van al promedio vigente; con costo capturado, ninguna interviene.
        foreach (var policy in CostPolicies.UncostedEntry)
        {
            Assert.Equal(46m, CostMath.Apply([L(1, 4m, 46m)], CostMethods.Average, 4m, null, 99m, 2, new(policy)).Cost);
            Assert.Equal(36m, CostMath.Apply([], CostMethods.Average, 4m, 9m, null, 2, new(policy)).Cost);
        }
    }

    [Fact]
    public void Promedio_con_varias_capas_las_funde_o_las_conserva_segun_la_politica()
    {
        IReadOnlyList<CostLayerState> fifo = [L(1, 10m, 100m), L(2, 10m, 200m)];
        // keep (la 020): la salida «promedio» consume en orden, como FIFO.
        Assert.Equal(-50m, CostMath.Apply(fifo, CostMethods.Average, -5m, null, null, 2).Cost);
        // merge: funde y sale al promedio de ambas; las capas anteriores quedan en cero y la fundida guarda el resto.
        var merged = CostMath.Apply(fifo, CostMethods.Average, -5m, null, null, 2, new(MergeAverage: true));
        Assert.Equal((-75m, 15m), (merged.Cost, merged.UnitCost));
        Assert.All(merged.Layers.Where(l => l.Id is not null), l => Assert.Equal((0m, 0m), (l.Quantity, l.Value)));
        Assert.Equal(new CostLayerState(null, 15m, 225m), merged.Layers.Single(l => l.Id is null));
        Assert.Equal((225m, 15m), (merged.Remaining, merged.RemainingQuantity));
        // FIFO no se funde aunque se pida.
        Assert.Equal(-50m, CostMath.Apply(fifo, CostMethods.Fifo, -5m, null, null, 2, new(MergeAverage: true)).Cost);
    }

    [Fact]
    public void Un_costo_total_exacto_no_pasa_por_un_unitario_redondeado()
    {
        // 1234.57 entre 1000 = 1.23457; a 4 decimales (el campo de costo unitario) y por 1000 daría 1234.60.
        var r = CostMath.Apply([], CostMethods.Fifo, 1000m, 1.2346m, null, 2, new(TotalCost: 1234.57m));
        Assert.Equal(1234.57m, r.Cost);
        Assert.Equal(1234.57m, r.Remaining);
        // Gana sobre la política de una entrada sin costo (un traspaso de algo que salió a cero entra a cero).
        Assert.Equal(0m, CostMath.Apply([], CostMethods.Fifo, 3m, null, null, 2, new(CostPolicies.Require, TotalCost: 0m)).Cost);
        Assert.Throws<AppException>(() => CostMath.Apply([], CostMethods.Fifo, 3m, null, null, 2, new(TotalCost: -1m)));
    }

    [Fact]
    public void Cantidad_costeada_es_la_que_queda_en_las_capas()
    {
        var r = CostMath.Apply([L(1, 3m, 52.11m)], CostMethods.Fifo, -4m, null, 10m, 2);
        Assert.Equal(-62.11m, r.Cost); // 3 con capas + 1 al respaldo
        Assert.Equal((0m, 0m), (r.Remaining, r.RemainingQuantity));
    }

    // ---------------- Módulos (H8) ----------------

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

    /// <summary>Instala los módulos en orden; <paramref name="upTo"/> limita la versión de cada uno (por id).</summary>
    private static Installed Install(IReadOnlyDictionary<string, string>? upTo, params string[] files)
    {
        IReadOnlyDictionary<string, EntityDef> model = Empty;
        IReadOnlyDictionary<string, DashboardDef> boards = new Dictionary<string, DashboardDef>(Ci);
        var roles = new HashSet<string>(["Administrador"], Ci);
        foreach (var file in files)
        {
            var p = Package(file);
            roles.UnionWith(p.AllRoles.Select(r => r.Name));
            var max = upTo?.GetValueOrDefault(p.Id) is { } v ? Version.Parse(v) : null;
            foreach (var (label, plan) in p.FullChain)
            {
                if (max is not null && label != "base" && Version.Parse(label[1..]) > max) continue;
                var c = PlanCompiler.Compile(plan, model, roles, boards);
                Assert.True(c.Preview.Valid, $"{file} {label}: {Errors(c)}");
                (model, boards) = (c.ResultingModel, c.Dashboards!);
            }
        }
        return new(model, boards, roles);
    }

    private static readonly string[] Files = ["compras.json", "inventario.json", "contabilidad.json", "costeo.json"];
    private static Installed All() => Install(null, Files);

    [Fact]
    public void Inventario_1_6_y_Costeo_1_1_llegan_como_actualizacion_sobre_lo_instalado()
    {
        var before = Install(new Dictionary<string, string> { ["inventario"] = "1.5.0", ["costeo"] = "1.0.0" }, Files);
        Assert.Null(before.Model["Existencia"].FindField("CantidadCosteada"));
        var model = before.Model;
        foreach (var (file, from) in new[] { ("costeo.json", "1.0.0"), ("inventario.json", "1.5.0") }) // el orden no importa
        {
            var up = Assert.Single(Package(file).PendingAfter(from));
            var c = PlanCompiler.Compile(up.Plan, model, before.Roles, before.Boards);
            Assert.True(c.Preview.Valid, $"{file}: {Errors(c)}");
            model = c.ResultingModel;
        }
        Assert.Equal(["1.6.0", "1.1.0"], new[] { Package("inventario.json").Version, Package("costeo.json").Version });

        // Las reglas de Compras se conservan (setRules con merge) y se agregan las políticas.
        Assert.Equal(["CantidadPositiva", "PartidaDeLaOrden", "ProductoDeLaPartida", "NoExcederPendiente", "CostoCalculado"],
            model["RecepcionPartida"].Rules!.Select(r => r.Name));
        Assert.Equal(["PolizaConCuentas", "TipoCambioDeLaRecepcion"], model["Recepcion"].Rules!.Select(r => r.Name).TakeLast(2).Order());
        Assert.Contains("IF(BASECURRENCY() = 'MXN', PartidaOrden.CostoMXN, NULL)", model["RecepcionPartida"].FindField("CostoUnitario")!.Formula);
        Assert.Equal("Recepcion.Fecha", model["Recepcion"].Automations!.Single(a => a.Name == "EntradaAlmacen").Actions[0].Set!.Single(s => s.Field == "Fecha").Value);

        var valuar = model["MovimientoInventario"].Automations!.Single(a => a.Name == "Valuar").Actions.Single();
        Assert.Equal((CostPolicies.Fallback, CostPolicies.Merge, "CostoTotal", "CantidadCosteada"),
            (valuar.UncostedEntry, valuar.MethodChange, valuar.TotalCost, valuar.QuantityField));
        // El traspaso sale primero y guarda el costo real; la entrada entra a ese costo exacto.
        var mover = model["Traspaso"].Automations!.Single(a => a.Name == "MoverMercancia").Actions;
        Assert.Equal("'Traspaso salida'", mover[0].Set!.Single(s => s.Field == "Tipo").Value);
        Assert.Equal(new FieldAssignment("CostoOrigen", "-CostoTotal"), Assert.Single(mover[0].Capture!));
        Assert.Equal("CostoOrigen", mover[1].Set!.Single(s => s.Field == "CostoTotal").Value);
        Assert.DoesNotContain(mover, a => a.Set!.Any(s => s.Value.Contains("UNITCOST")));
        Assert.Contains("CantidadCosteada", model["Existencia"].FindField("CostoPromedio")!.Formula);
        Assert.Null(AutomationRules.FindCycle(model));

        // Lo mismo que una instalación nueva.
        var fresh = All().Model;
        Assert.Equal(fresh["Recepcion"].Rules!.Select(r => r.Name).Order(), model["Recepcion"].Rules!.Select(r => r.Name).Order());
        Assert.Equal(JsonSerializer.Serialize(fresh["Traspaso"].Automations), JsonSerializer.Serialize(model["Traspaso"].Automations));
    }

    // ---------------- Validación (H1 CA4, H2 CA1, H5 CA1, H6 CA1, H7 CA4) ----------------

    private static CompiledPlan Compile(Installed i, params Operation[] ops) =>
        PlanCompiler.Compile(new ChangePlan { Description = "prueba", Operations = ops }, i.Model, i.Roles, i.Boards);

    private static SetAutomationsOp With(Installed all, string entity, string name, Func<IReadOnlyList<AutomationAction>, IReadOnlyList<AutomationAction>> change)
    {
        var auto = all.Model[entity].Automations!.Single(a => a.Name == name);
        return new SetAutomationsOp { Entity = entity, Merge = true, Automations = [auto with { Actions = change(auto.Actions) }] };
    }

    [Theory]
    [InlineData("uncostedEntry", "uncostedEntry 'zero' no válido")]
    [InlineData("methodChange", "methodChange 'fundir' no válido")]
    [InlineData("quantityField", "recibe la cantidad costeada")]
    [InlineData("sameField", "quantityField y field deben ser campos distintos")]
    [InlineData("totalCost", "(costo total)")]
    [InlineData("onCreate", "solo se declaran en una acción cost")]
    public void Una_politica_de_valuacion_mal_declarada_se_rechaza(string what, string expected)
    {
        var all = All();
        var op = what == "onCreate"
            ? With(all, "Traspaso", "MoverMercancia", a => [a[0] with { UncostedEntry = CostPolicies.Fallback }, a[1]])
            : With(all, "MovimientoInventario", "Valuar", a => [what switch
            {
                "uncostedEntry" => a[0] with { UncostedEntry = "zero" },
                "methodChange" => a[0] with { MethodChange = "fundir" },
                "quantityField" => a[0] with { QuantityField = "CosteoIncompleto" },
                "sameField" => a[0] with { QuantityField = "Valor" },
                _ => a[0] with { TotalCost = "Inexistente * 2" },
            }]);
        var c = Compile(all, op);
        Assert.False(c.Preview.Valid);
        Assert.Contains(c.Preview.Errors, e => e.Message.Contains(expected));
    }

    [Theory]
    [InlineData("field", "captura: no se puede asignar 'Inexistente' en TraspasoPartida")]
    [InlineData("readonly", "captura: no se puede asignar 'Traspaso' en TraspasoPartida")]
    [InlineData("scope", "captura CostoOrigen")]   // CostoOrigen no existe en el movimiento creado: se calcula en su ámbito
    [InlineData("notCreate", "capture solo se declara en una acción create")]
    public void Una_captura_mal_declarada_se_rechaza(string what, string expected)
    {
        var all = All();
        var op = what == "notCreate"
            ? With(all, "MovimientoInventario", "Valuar", a => [a[0] with { Capture = [new("Notas", "'x'")] }])
            : With(all, "Traspaso", "MoverMercancia", a => [a[0] with
            {
                Capture = [what switch
                {
                    "field" => new FieldAssignment("Inexistente", "CostoTotal"),
                    "readonly" => new FieldAssignment("Traspaso", "CostoTotal"),
                    _ => new FieldAssignment("CostoOrigen", "CostoOrigen * 2"),
                }],
            }, a[1]]);
        var c = Compile(all, op);
        Assert.False(c.Preview.Valid);
        Assert.Contains(c.Preview.Errors, e => e.Message.Contains(expected));
    }

    [Fact]
    public void Capturar_cuenta_como_edicion_del_registro_que_dispara()
    {
        var m = All().Model;
        var salida = m["Traspaso"].Automations!.Single(a => a.Name == "MoverMercancia").Actions[0];
        var effects = AutomationRules.Effects(m["Traspaso"], salida, m).Select(x => (x.Entity.Name, x.Event)).ToList();
        Assert.Contains(("Traspaso", AutomationEvents.Update), effects);

        // Si el traspaso tuviera una automatización «al editar» que vuelve a crear movimientos, hay ciclo.
        var all = All();
        var loop = new SetAutomationsOp { Entity = "Traspaso", Merge = true, Automations = [new Automation
        {
            Name = "Recrear", Label = "Recrear", On = AutomationEvents.Update,
            Actions = [salida with { }],
        }] };
        var c = Compile(all, loop);
        Assert.False(c.Preview.Valid);
        Assert.Contains(c.Preview.Errors, e => e.Message.Contains("Traspaso"));
    }

    [Fact]
    public void SetRules_con_merge_agrega_o_reemplaza_por_nombre_y_conserva_las_demas()
    {
        var all = All();
        var before = all.Model["RecepcionPartida"].Rules!.Select(r => r.Name).ToList();
        var add = Compile(all, new SetRulesOp { Entity = "RecepcionPartida", Merge = true,
            Rules = [new BusinessRule { Name = "Nueva", Message = "m", Assert = "Cantidad < 1000" }] });
        Assert.True(add.Preview.Valid, Errors(add));
        Assert.Equal([.. before, "Nueva"], add.ResultingModel["RecepcionPartida"].Rules!.Select(r => r.Name));

        var replace = Compile(all, new SetRulesOp { Entity = "RecepcionPartida", Merge = true,
            Rules = [new BusinessRule { Name = "costocalculado", Message = "otro", Assert = "TRUE" }] });
        var rules = replace.ResultingModel["RecepcionPartida"].Rules!;
        Assert.Equal(before.Count, rules.Count);
        Assert.Equal("otro", rules.Single(r => r.Name.Equals("CostoCalculado", StringComparison.OrdinalIgnoreCase)).Message);

        // Sin merge reemplaza todas (como antes).
        var all1 = Compile(all, new SetRulesOp { Entity = "RecepcionPartida", Rules = [new BusinessRule { Name = "Sola", Message = "m", Assert = "TRUE" }] });
        Assert.Equal(["Sola"], all1.ResultingModel["RecepcionPartida"].Rules!.Select(r => r.Name));
    }

    [Fact]
    public void Las_opciones_omitidas_no_se_serializan()
    {
        // Las huellas de automatizaciones y tareas (021) no cambian para lo que no usa las opciones nuevas.
        var json = JsonSerializer.Serialize(new AutomationAction { Type = AutomationActions.Update }, JsonSerializerOptions.Web);
        foreach (var name in new[] { "uncostedEntry", "methodChange", "totalCost", "quantityField", "capture" })
            Assert.DoesNotContain(name, json);
    }
}
