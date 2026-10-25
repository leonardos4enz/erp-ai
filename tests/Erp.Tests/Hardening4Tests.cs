using Erp.Api.Data;
using Erp.Api.Expressions;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>
/// Spec 031: quien aprueba no gana visibilidad al mover a un usuario o a un equipo a su unidad (H3, auditoría 5 A-02) y
/// <c>UNITCOST</c> respeta la seguridad por campo del saldo (revisión adversarial F-A).
/// </summary>
public class Hardening4Tests
{
    // Norte (/1/) ⊃ Norte-Ventas (/1/2/); Sur (/3/) y Centro (/4/) son de otra rama.
    private static readonly List<SecurityApproval.UnitPath> Units =
        [new(1, "Norte", "/1/"), new(2, "Norte-Ventas", "/1/2/"), new(3, "Sur", "/3/"), new(4, "Centro", "/4/")];

    private static readonly IReadOnlyDictionary<int, (string Name, AdminCapabilities Caps)> NoRoles = new Dictionary<int, (string, AdminCapabilities)>();

    private static UserContext Reviewer(string? unit) => new()
    {
        Id = 7, Username = "revisa", DisplayName = "Revisa", SecurityStamp = Guid.Empty, Roles = [], Unit = unit, CanManageUsers = true,
    };

    private static string? Why(string kind, string? unit, string? from, string? to) =>
        SecurityApproval.WhyNotApprover(Reviewer(unit), kind, 99, SecurityApproval.Json(new { unit = to, roleIds = Array.Empty<int>(), memberIds = Array.Empty<int>() }),
            kind is SecurityChangeKinds.UserCreate or SecurityChangeKinds.TeamCreate ? null
                : SecurityApproval.Json(new { unit = from, roleIds = Array.Empty<int>(), memberIds = Array.Empty<int>() }), NoRoles, Units);

    [Theory]
    [InlineData(SecurityChangeKinds.UserUpdate)]
    [InlineData(SecurityChangeKinds.TeamUpdate)]
    public void Mover_a_un_usuario_o_equipo_a_la_unidad_propia_lo_aprueba_otro(string kind)
    {
        Assert.NotNull(Why(kind, "Norte", "Sur", "Norte"));          // a su unidad
        Assert.NotNull(Why(kind, "Norte", null, "Norte-Ventas"));    // a una que cuelga de la suya
        Assert.Contains("tu unidad", Why(kind, "Norte", "Sur", "Norte-Ventas"));
        Assert.Null(Why(kind, "Norte", "Sur", "Centro"));            // a otra rama: no gana nada
        // Revisión adversarial F-B: con alcance «unit» (exacto) no lo veía; pasarlo a la unidad propia también gana.
        Assert.NotNull(Why(kind, "Norte", "Norte-Ventas", "Norte"));
        Assert.Null(Why(kind, "Norte", "Norte", "Norte-Ventas"));    // de la propia a una hija: no gana nada
        Assert.Null(Why(kind, "Norte", "Norte", null));              // sin unidad: reduce
        Assert.Null(Why(kind, null, "Sur", "Norte"));                // quien revisa no tiene unidad
        Assert.Null(Why(kind, "Norte", "Sur", "Sur"));               // la unidad no cambia
    }

    [Fact]
    public void Reactivar_un_equipo_del_que_se_es_miembro_lo_aprueba_otro()
    {
        // Revisión adversarial F-C: un equipo inactivo no da sus registros a sus miembros (015, «teamOwners»).
        string? Why(bool before, bool after, int[] members) => SecurityApproval.WhyNotApprover(Reviewer(null), SecurityChangeKinds.TeamUpdate, 99,
            SecurityApproval.Json(new { isActive = after, memberIds = members }), SecurityApproval.Json(new { isActive = before, memberIds = members }), NoRoles, Units);
        Assert.NotNull(Why(false, true, [7]));
        Assert.Null(Why(false, true, [8]));   // no es miembro
        Assert.Null(Why(true, true, [7]));    // ya estaba activo
        Assert.Null(Why(true, false, [7]));   // desactivar reduce
    }

    [Theory]
    [InlineData(SecurityChangeKinds.UserCreate)]
    [InlineData(SecurityChangeKinds.TeamCreate)]
    public void Al_crear_aun_no_hay_registros_que_ganar(string kind) =>
        Assert.Null(Why(kind, "Norte", null, "Norte"));

    // ---------------- Revisión adversarial F-A: UNITCOST y la seguridad por campo del saldo ----------------

    /// <summary>Un saldo («Saldo») cuyo valor solo lee «Costos», alimentado por una acción cost de «Mov», y un documento que lo consulta.</summary>
    private static IReadOnlyDictionary<string, EntityDef> CostModel()
    {
        var plan = new ChangePlan
        {
            Operations =
            [
                new CreateEntityOp { Name = "Saldo", Label = "Saldo", LabelPlural = "Saldos", Fields =
                [
                    new FieldDef { Name = "Clave", Label = "Clave", Type = FieldTypes.Text },
                    new FieldDef { Name = "Cantidad", Label = "Cantidad", Type = FieldTypes.Decimal },
                    new FieldDef { Name = "Valor", Label = "Valor", Type = FieldTypes.Decimal },
                ] },
                new CreateEntityOp { Name = "Mov", Label = "Mov", LabelPlural = "Movs", Fields =
                    [new FieldDef { Name = "Clave", Label = "Clave", Type = FieldTypes.Text }] },
                new CreateEntityOp { Name = "Doc", Label = "Doc", LabelPlural = "Docs", Fields =
                [
                    new FieldDef { Name = "Clave", Label = "Clave", Type = FieldTypes.Text },
                    new FieldDef { Name = "Costo", Label = "Costo", Type = FieldTypes.Formula, Formula = "UNITCOST('Saldo', 1, Clave)", ResultType = FieldTypes.Decimal },
                ] },
                new SetSecurityOp { Entity = "Saldo", Security = new SecurityPolicy { Fields = [new FieldSecurity { Field = "Valor", Read = ["Costos"] }] } },
            ],
        };
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Costos", "Lector" };
        var compiled = PlanCompiler.Compile(plan, new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), roles);
        Assert.True(compiled.Preview.Valid, string.Join(" | ", compiled.Preview.Errors.Select(e => e.Message)));
        var model = new Dictionary<string, EntityDef>(compiled.ResultingModel, StringComparer.OrdinalIgnoreCase);
        // La acción cost se agrega directo (su validación completa es de la 020): lo que importa es qué campo del saldo alimenta.
        model["Mov"] = model["Mov"] with { Automations = [new Automation { Name = "Valuar", Label = "Valuar", On = AutomationEvents.Create, Actions =
            [new AutomationAction { Type = AutomationActions.Cost, Entity = "Saldo", Field = "Valor", Quantity = "1" }] }] };
        DerivedAccess.Annotate(model);
        return model;
    }

    private static UserContext Role(string role) => new() { Id = 8, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = [role] };

    [Fact]
    public void UNITCOST_se_oculta_a_quien_no_ve_el_valor_del_saldo()
    {
        var m = CostModel();
        Assert.Equal(["Valor"], DerivedAccess.CostBookFields("Saldo", m).Select(x => x.Field.Name));
        // Antes solo contaba el alcance por registro: quien no ve «Valor» recibía el costo de las capas.
        Assert.False(FieldAccess.CanSee(m["Doc"], m["Doc"].FindField("Costo")!, Role("Lector")));
        Assert.True(FieldAccess.CanSee(m["Doc"], m["Doc"].FindField("Costo")!, Role("Costos")));
        // Un aviso o una condición de layout con UNITCOST tampoco se le muestra.
        var rule = ExprParser.Parse("UNITCOST('Saldo', 1, Clave) > 10");
        Assert.True(RecordService.ReadsHidden(rule, m["Doc"], m, Role("Lector")));
        Assert.False(RecordService.ReadsHidden(rule, m["Doc"], m, Role("Costos")));
        Assert.True(DerivedAccess.CostBookHidden("Saldo", m, Role("Lector")));
    }
}
