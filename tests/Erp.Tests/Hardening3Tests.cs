using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>Spec 029: capacidades, lo derivado de un campo oculto, aprobaciones sin caso por omisión y límites del PDF.</summary>
public class Hardening3Tests
{
    private static readonly IReadOnlySet<string> Roles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Comprador", "Almacenista", "Lector" };

    /// <summary>
    /// Orden con partidas (precio oculto para quien no compra) y una recepción cuyo costo sale del precio de la orden, como
    /// Compras 1.3.0; un catálogo «Socio» con alcance por registro y otro por empresa al que llega uno compartido.
    /// </summary>
    private static IReadOnlyDictionary<string, EntityDef> Model()
    {
        var plan = new ChangePlan
        {
            Operations =
            [
                new CreateEntityOp { Name = "Socio", Label = "Socio", LabelPlural = "Socios", Fields =
                [
                    new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text },
                    new FieldDef { Name = "Limite", Label = "Límite", Type = FieldTypes.Decimal },
                ] },
                new CreateEntityOp { Name = "Sucursal", Label = "Sucursal", LabelPlural = "Sucursales", PerCompany = true, Fields =
                    [new FieldDef { Name = "Clave", Label = "Clave", Type = FieldTypes.Text }] },
                new CreateEntityOp { Name = "Orden", Label = "Orden", LabelPlural = "Órdenes", Fields =
                [
                    new FieldDef { Name = "Socio", Label = "Socio", Type = FieldTypes.Reference, ReferenceEntity = "Socio" },
                    new FieldDef { Name = "LimiteSocio", Label = "Límite del socio", Type = FieldTypes.Formula, Formula = "Socio.Limite", ResultType = FieldTypes.Decimal },
                    new FieldDef { Name = "Nota", Label = "Nota", Type = FieldTypes.Text },
                ] },
                new CreateEntityOp { Name = "OrdenPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
                [
                    new FieldDef { Name = "Orden", Label = "Orden", Type = FieldTypes.Parent, ReferenceEntity = "Orden" },
                    new FieldDef { Name = "Cantidad", Label = "Cantidad", Type = FieldTypes.Decimal },
                    new FieldDef { Name = "Precio", Label = "Precio", Type = FieldTypes.Decimal },
                    new FieldDef { Name = "Importe", Label = "Importe", Type = FieldTypes.Formula, Formula = "Cantidad * Precio", ResultType = FieldTypes.Decimal },
                    new FieldDef { Name = "Doble", Label = "Doble", Type = FieldTypes.Formula, Formula = "Cantidad * 2", ResultType = FieldTypes.Decimal },
                    new FieldDef { Name = "NotaOrden", Label = "Nota de la orden", Type = FieldTypes.Formula, Formula = "Orden.Nota", ResultType = FieldTypes.Text },
                ] },
                new AddFieldOp { Entity = "Orden", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "OrdenPartida" } },
                new AddFieldOp { Entity = "Orden", Field = new FieldDef { Name = "Total", Label = "Total", Type = FieldTypes.Formula, Formula = "SUM(Partidas.Importe)", ResultType = FieldTypes.Decimal } },
                new AddFieldOp { Entity = "Orden", Field = new FieldDef { Name = "Piezas", Label = "Piezas", Type = FieldTypes.Formula, Formula = "SUM(Partidas.Cantidad)", ResultType = FieldTypes.Decimal } },
                new CreateEntityOp { Name = "Recepcion", Label = "Recepción", LabelPlural = "Recepciones", Fields =
                [
                    new FieldDef { Name = "Partida", Label = "Partida", Type = FieldTypes.Reference, ReferenceEntity = "OrdenPartida" },
                    new FieldDef { Name = "Costo", Label = "Costo", Type = FieldTypes.Formula, Formula = "Partida.Importe", ResultType = FieldTypes.Decimal },
                    new FieldDef { Name = "Sucursal", Label = "Sucursal", Type = FieldTypes.Reference, ReferenceEntity = "Sucursal" },
                    new FieldDef { Name = "ClaveSucursal", Label = "Clave de sucursal", Type = FieldTypes.Formula, Formula = "Sucursal.Clave", ResultType = FieldTypes.Text },
                ] },
                new SetSecurityOp { Entity = "OrdenPartida", Security = new SecurityPolicy { Fields = [new FieldSecurity { Field = "Precio", Read = ["Comprador"] }] } },
                new SetSecurityOp { Entity = "Socio", Security = new SecurityPolicy { Records =
                [
                    new RecordGrant { Roles = ["Lector"], Scope = RecordScopes.Own },
                    new RecordGrant { Roles = ["Comprador", "Almacenista"], Scope = RecordScopes.All },
                ] } },
            ],
        };
        var compiled = PlanCompiler.Compile(plan, new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);
        Assert.True(compiled.Preview.Valid, string.Join(" | ", compiled.Preview.Errors.Select(e => e.Message)));
        var model = compiled.ResultingModel;
        DerivedAccess.Annotate(model);
        return model;
    }

    private static UserContext User(params string[] roles) => new()
    {
        Id = 7, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = roles,
    };

    private static UserContext Superuser() => new() { Id = 1, Username = "admin", DisplayName = "A", SecurityStamp = Guid.Empty, Roles = [], IsSuperuser = true };

    private static bool Sees(IReadOnlyDictionary<string, EntityDef> m, string entity, string field, UserContext u) =>
        FieldAccess.CanSee(m[entity], m[entity].FindField(field)!, u);

    // ---------------- H4 CA1: lo derivado de un campo oculto (D-05) ----------------

    [Fact]
    public void Una_formula_que_lee_un_campo_oculto_se_oculta()
    {
        var m = Model();
        Assert.False(Sees(m, "OrdenPartida", "Importe", User("Almacenista")));
        Assert.True(Sees(m, "OrdenPartida", "Importe", User("Comprador")));
        Assert.True(Sees(m, "OrdenPartida", "Importe", Superuser()));
        // Lo que no lee el precio sigue visible.
        Assert.True(Sees(m, "OrdenPartida", "Doble", User("Almacenista")));
    }

    [Fact]
    public void Se_oculta_a_traves_de_partidas_y_de_otras_formulas()
    {
        var m = Model();
        // SUM(Partidas.Importe) → Importe → Precio (oculto).
        Assert.False(Sees(m, "Orden", "Total", User("Almacenista")));
        Assert.True(Sees(m, "Orden", "Piezas", User("Almacenista")));
        // Regresión (auditoría 4 D-05, Compras 1.3.0): el costo de la recepción sale del importe de la partida de la orden.
        Assert.False(Sees(m, "Recepcion", "Costo", User("Almacenista")));
        Assert.True(Sees(m, "Recepcion", "Costo", User("Comprador")));
    }

    [Fact]
    public void Strip_quita_la_formula_de_las_partidas_aunque_la_partida_no_tenga_reglas_propias()
    {
        var m = Model();
        var row = new Dictionary<string, object?>
        {
            ["Total"] = 10m, ["Piezas"] = 2m,
            ["Partidas"] = new List<Dictionary<string, object?>> { new() { ["Cantidad"] = 1m, ["Precio"] = 5m, ["Importe"] = 5m, ["Doble"] = 2m } },
        };
        FieldAccess.Strip(m["Orden"], row, User("Almacenista"), m);
        Assert.False(row.ContainsKey("Total"));
        var line = ((List<Dictionary<string, object?>>)row["Partidas"]!)[0];
        Assert.False(line.ContainsKey("Precio"));
        Assert.False(line.ContainsKey("Importe"));
        Assert.True(line.ContainsKey("Doble"));
    }

    [Fact]
    public void Ordenar_por_una_formula_que_lee_un_oculto_se_ignora()
    {
        var m = Model();
        var e = m["OrdenPartida"];
        Assert.Equal("t.[Id] DESC", RecordService.ResolveSort(e, "Importe", "asc", f => FieldAccess.CanSee(e, f, User("Almacenista"))));
    }

    // ---------------- H3 CA2: fórmulas guardadas que recorren un catálogo restringido (D-01) ----------------

    [Fact]
    public void Una_formula_que_recorre_un_catalogo_con_alcance_restringido_se_oculta_a_quien_lo_tiene_restringido()
    {
        var m = Model();
        Assert.False(Sees(m, "Orden", "LimiteSocio", User("Lector")));   // solo ve sus socios
        Assert.True(Sees(m, "Orden", "LimiteSocio", User("Almacenista"))); // ve todos
        Assert.True(Sees(m, "Orden", "LimiteSocio", Superuser()));
        // El salto al propio documento (partida → orden) no es otro registro.
        Assert.True(Sees(m, "OrdenPartida", "NotaOrden", User("Lector")));
    }

    [Fact]
    public void De_un_catalogo_compartido_a_uno_por_empresa_se_oculta_a_todos()
    {
        var m = Model();
        Assert.False(Sees(m, "Recepcion", "ClaveSucursal", Superuser()));
        Assert.False(Sees(m, "Recepcion", "ClaveSucursal", User("Comprador")));
    }

    [Fact]
    public void UNITCOST_de_un_catalogo_restringido_cuenta_como_recorrido()
    {
        // Revisión adversarial 029 (F-C): el catálogo de saldo va como texto; DerivedAccess lo trata como un salto.
        var m = new Dictionary<string, EntityDef>(Model(), StringComparer.OrdinalIgnoreCase);
        var orden = m["Orden"];
        m["Orden"] = orden with { Fields = [.. orden.Fields, new FieldDef { Name = "CostoSocio", Label = "Costo", Type = FieldTypes.Formula,
            Formula = "UNITCOST('Socio', 1, Socio)", ResultType = FieldTypes.Decimal }] };
        DerivedAccess.Annotate(m);
        Assert.False(Sees(m, "Orden", "CostoSocio", User("Lector")));
        Assert.True(Sees(m, "Orden", "CostoSocio", User("Almacenista")));
    }

    [Fact]
    public void Sin_anotar_el_modelo_las_reglas_son_las_de_antes()
    {
        var e = Model()["OrdenPartida"] with { };
        var bare = e.Fields.Select(f => f with { }).ToList(); // instancias nuevas, sin anotación
        var copy = e with { Fields = bare };
        Assert.True(FieldAccess.CanSee(copy, copy.FindField("Importe")!, User("Almacenista")));
    }

    // ---------------- H6 CA1: aprobaciones sin caso por omisión (D-03) ----------------

    private static IReadOnlyDictionary<int, (string Name, AdminCapabilities Caps)> NoRoles => new Dictionary<int, (string, AdminCapabilities)>();

    [Fact]
    public void Cada_tipo_de_cambio_de_seguridad_tiene_su_regla()
    {
        var kinds = typeof(SecurityChangeKinds).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.True(kinds.Count >= 11);
        var reviewer = User("Comprador");
        foreach (var kind in kinds)
            Assert.NotEqual(SecurityApproval.NoRule, SecurityApproval.WhyNotApprover(reviewer, kind, 99, "{}", "{}", NoRoles));
        // Uno desconocido no lo aprueba nadie (antes: default → null, aprobable).
        Assert.Equal(SecurityApproval.NoRule, SecurityApproval.WhyNotApprover(Superuser(), "algo.nuevo", 1, "{}", null, NoRoles));
    }

    [Fact]
    public void No_se_aprueba_mover_una_unidad_bajo_la_propia()
    {
        // Norte (/1/) ⊃ Norte-Ventas (/1/2/); Sur (/3/) se mueve.
        var units = new List<SecurityApproval.UnitPath> { new(1, "Norte", "/1/"), new(2, "Norte-Ventas", "/1/2/"), new(3, "Sur", "/3/"), new(4, "Centro", "/4/") };
        var norte = User("Comprador") is var u ? new UserContext { Id = u.Id, Username = u.Username, DisplayName = u.DisplayName, SecurityStamp = u.SecurityStamp, Roles = u.Roles, Unit = "Norte" } : null!;
        string? Why(int? from, int? to) => SecurityApproval.WhyNotApprover(norte, SecurityChangeKinds.UnitMove, 3,
            SecurityApproval.Json(new { parentId = to }), SecurityApproval.Json(new { parentId = from }), NoRoles, units);
        Assert.NotNull(Why(null, 1)); // bajo su unidad
        Assert.NotNull(Why(4, 2));    // bajo una que cuelga de la suya
        Assert.Null(Why(null, 4));    // bajo otra: no gana nada
        Assert.Null(Why(2, 1));       // ya la veía
        Assert.Null(Why(1, null));    // a la raíz: reduce
    }

    // ---------------- H5 CA1: el semáforo del PDF (D-02) ----------------

    [Fact]
    public async Task El_lugar_del_semaforo_se_libera_al_terminar_el_render_no_al_vencer_el_tiempo()
    {
        var gate = new SemaphoreSlim(1, 1);
        var release = new ManualResetEventSlim();
        var slow = PrintLimits.RenderAsync(() => { release.Wait(); return [1]; }, gate, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<AppException>(() => slow);
        Assert.Equal(503, ex.Status);
        // El render sigue: el lugar no se liberó y el siguiente no entra.
        Assert.Equal(0, gate.CurrentCount);
        var next = await Assert.ThrowsAsync<AppException>(() =>
            PrintLimits.RenderAsync(() => [2], gate, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(503, next.Status);
        release.Set();
        for (var i = 0; i < 50 && gate.CurrentCount == 0; i++) await Task.Delay(20);
        Assert.Equal(1, gate.CurrentCount);
        Assert.Equal([3], await PrintLimits.RenderAsync(() => [3], gate, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task Cancelar_el_cliente_tampoco_libera_el_lugar_antes_de_tiempo()
    {
        var gate = new SemaphoreSlim(1, 1);
        var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var slow = PrintLimits.RenderAsync(() => { release.Wait(); return [1]; }, gate, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);
        Assert.Equal(0, gate.CurrentCount);
        release.Set();
        for (var i = 0; i < 50 && gate.CurrentCount == 0; i++) await Task.Delay(20);
        Assert.Equal(1, gate.CurrentCount);
    }

    // ---------------- H1: capacidades ----------------

    [Fact]
    public void Cada_capacidad_se_comprueba_con_su_permiso()
    {
        var plain = User("Lector");
        Assert.True(EndpointCapabilities.Has(Capability.Records, plain));
        Assert.True(EndpointCapabilities.Has(Capability.Authenticated, plain));
        foreach (var c in new[] { Capability.Modeler, Capability.UserAdmin, Capability.Audit, Capability.Superuser })
            Assert.False(EndpointCapabilities.Has(c, plain));
        var modeler = new UserContext { Id = 2, Username = "m", DisplayName = "M", SecurityStamp = Guid.Empty, Roles = [], CanManageModel = true };
        Assert.True(EndpointCapabilities.Has(Capability.Modeler, modeler));
        Assert.False(EndpointCapabilities.Has(Capability.Superuser, modeler));
        Assert.True(EndpointCapabilities.Has(Capability.Superuser, Superuser()));
    }
}
