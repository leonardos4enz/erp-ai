using System.Text.Json;
using Erp.Api.Approvals;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>
/// Spec 053: la parte pura de la matriz de autorización. La declaración en el plan (cada error de H1 CA2, también los atajos desde
/// otro catálogo o una tarea), elegir la regla (prioridad, especificidad, vigencia, rango, condiciones), saltar niveles por el
/// rango del perfil, la huella de condiciones, la forma JSON y quién revisa un cambio a la matriz.
/// </summary>
public class ApprovalTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Administrador", "Capturista" };
    private static readonly Dictionary<string, EntityDef> Empty = new(StringComparer.OrdinalIgnoreCase);

    private static FieldDef F(string name, string type, string? target = null, bool editable = true) =>
        new() { Name = name, Label = name, Type = type, ReferenceEntity = target, Editable = editable };

    private static WorkflowApproval Approval(Func<WorkflowApproval, WorkflowApproval>? change = null)
    {
        var a = new WorkflowApproval
        {
            State = "PorAutorizar", Approve = "autorizada", Reject = "rechazada", Amount = "Monto", Conditions = ["Depto", "Tipo"], Approvers = "Autorizadores",
        };
        return change is null ? a : change(a);
    }

    private static Workflow Flow(WorkflowApproval? a, bool system = true, IReadOnlyList<FieldAssignment>? sendSet = null) => new()
    {
        Initial = "Borrador",
        States =
        [
            new() { Name = "Borrador", Label = "Borrador" }, new() { Name = "PorAutorizar", Label = "Por autorizar", Editable = false },
            new() { Name = "Autorizada", Label = "Autorizada" }, new() { Name = "Rechazada", Label = "Rechazada" },
        ],
        Transitions =
        [
            new() { Name = "enviar", Label = "Enviar", From = ["Borrador"], To = "PorAutorizar", Set = sendSet },
            new() { Name = "autorizada", Label = "Autorizada", From = ["PorAutorizar"], To = "Autorizada", System = system },
            new() { Name = "rechazada", Label = "Rechazada", From = ["PorAutorizar"], To = "Rechazada", System = true },
        ],
        Approval = a,
    };

    private static CompiledPlan Compile(Workflow wf, bool perCompany = true, bool approversEditable = false, params Operation[] more) => PlanCompiler.Compile(new ChangePlan
    {
        Operations =
        [
            new CreateEntityOp { Name = "Depto", Label = "Depto", LabelPlural = "Deptos", Fields = [F("Nombre", FieldTypes.Text)] },
            new CreateEntityOp
            {
                Name = "Solicitud", Label = "Solicitud", LabelPlural = "Solicitudes", PerCompany = perCompany, Fields =
                [
                    F("Concepto", FieldTypes.Text), F("Depto", FieldTypes.Reference, "Depto"), F("Monto", FieldTypes.Decimal),
                    new() { Name = "Tipo", Label = "Tipo", Type = FieldTypes.Choice, Options = ["Bienes", "Servicios"] },
                    F("Fecha", FieldTypes.Date), F("Autorizadores", FieldTypes.MultiUser, editable: approversEditable),
                ],
            },
            new SetWorkflowOp { Entity = "Solicitud", Workflow = wf },
            .. more,
        ],
    }, Empty, Roles);

    private static string Errors(CompiledPlan p) => string.Join(" | ", p.Preview.Errors.Select(e => e.Message));

    [Fact]
    public void Una_declaracion_valida_compila_y_la_vista_previa_la_describe()
    {
        var c = Compile(Flow(Approval()));
        Assert.Empty(Errors(c));
        Assert.Contains(c.Preview.Steps, s => s.Description.Contains("autorización por matriz en «Por autorizar»"));
    }

    [Fact]
    public void Una_declaracion_invalida_es_error_del_plan()
    {
        Assert.Contains("por empresa", Errors(Compile(Flow(Approval()), perCompany: false)));
        Assert.Contains("es el inicial", Errors(Compile(Flow(Approval(a => a with { State = "Borrador" })))));
        Assert.Contains("'Nada' no existe", Errors(Compile(Flow(Approval(a => a with { State = "Nada" })))));
        Assert.Contains("'nada' no existe", Errors(Compile(Flow(Approval(a => a with { Approve = "nada" })))));
        Assert.Contains("debe ser del sistema", Errors(Compile(Flow(Approval(), system: false))));
        Assert.Contains("debe salir solo de", Errors(Compile(Flow(Approval(a => a with { Approve = "enviar" })))));
        Assert.Contains("son transiciones distintas", Errors(Compile(Flow(Approval(a => a with { Reject = "autorizada" })))));
        Assert.Contains("campo numérico", Errors(Compile(Flow(Approval(a => a with { Amount = "Concepto" })))));
        Assert.Contains("tipo referencia, opción, sí/no o persona", Errors(Compile(Flow(Approval(a => a with { Conditions = ["Fecha"] })))));
        Assert.Contains("repetido", Errors(Compile(Flow(Approval(a => a with { Conditions = ["Depto", "depto"] })))));
        Assert.Contains("máximo 5", Errors(Compile(Flow(Approval(a => a with { Conditions = ["Depto", "Tipo", "Depto", "Tipo", "Depto", "Tipo"] })))));
        Assert.Contains("no editable", Errors(Compile(Flow(Approval()), approversEditable: true)));
        Assert.Contains("no editable", Errors(Compile(Flow(Approval(a => a with { Approvers = "Concepto" })))));
        Assert.Contains("asigna 'Autorizadores'", Errors(Compile(Flow(Approval(), sendSet: [new("Autorizadores", "CURRENTUSER()", AssignModes.Add)]))));
        // Revisión adversarial 053 (F-02, F-03, F-04).
        var editable = Flow(Approval()) is var f ? f with { States = [.. f.States.Select(s => s.Name == "PorAutorizar" ? s with { Editable = true } : s)] } : null!;
        Assert.Contains("debe ser no editable", Errors(Compile(editable)));
        var forzar = Flow(Approval()) is var g ? g with { Transitions = [.. g.Transitions, new() { Name = "forzar", Label = "Forzar", From = ["Borrador"], To = "Autorizada" }] } : null!;
        Assert.Contains("solo se llega autorizando", Errors(Compile(forzar)));
        var cuando = Flow(Approval()) is var h ? h with { Transitions = [.. h.Transitions.Select(t => t.Name == "rechazada" ? t with { When = "Monto > 0" } : t)] } : null!;
        Assert.Contains("la decide la matriz", Errors(Compile(cuando)));
    }

    [Fact]
    public void Una_automatizacion_o_una_tarea_de_otro_catalogo_no_es_atajo()
    {
        var ajuste = new CreateEntityOp
        {
            Name = "Ajuste", Label = "Ajuste", LabelPlural = "Ajustes",
            Fields = [F("Solicitud", FieldTypes.Reference, "Solicitud"), F("Nuevo", FieldTypes.Decimal)],
        };
        Operation Autos(params AutomationAction[] actions) => new SetAutomationsOp
        {
            Entity = "Ajuste", Automations = [new Automation { Name = "Atajo", Label = "Atajo", On = AutomationEvents.Create, Actions = actions }],
        };
        Assert.Contains("ejecuta 'autorizada'", Errors(Compile(Flow(Approval()), true, false, ajuste,
            Autos(new AutomationAction { Type = AutomationActions.Transition, Target = "Solicitud", Transition = "autorizada" }))));
        Assert.Contains("asigna 'Autorizadores'", Errors(Compile(Flow(Approval()), true, false, ajuste,
            Autos(new AutomationAction { Type = AutomationActions.Update, Target = "Solicitud", Set = [new("Autorizadores", "CURRENTUSER()", AssignModes.Add)] }))));
        // Asignar otro campo o ejecutar otra transición sí vale.
        Assert.Empty(Errors(Compile(Flow(Approval()), true, false, ajuste,
            Autos(new AutomationAction { Type = AutomationActions.Update, Target = "Solicitud", Set = [new("Monto", "Nuevo")] }))));
        // Una tarea programada del propio catálogo tampoco.
        var task = new SetSchedulesOp
        {
            Entity = "Solicitud", Schedules = [new ScheduleDef
            {
                Name = "Auto", Label = "Auto", Every = ScheduleFrequencies.Day, At = "08:00", Where = "Monto > 0", Role = "Capturista",
                Actions = [new AutomationAction { Type = AutomationActions.Transition, Transition = "rechazada" }],
            }],
        };
        Assert.Contains("la tarea Solicitud.Auto ejecuta 'rechazada'", Errors(Compile(Flow(Approval()), true, false, task)));
    }

    [Fact]
    public void La_declaracion_se_omite_en_nulo_y_viaja_en_el_proceso()
    {
        var plain = JsonSerializer.Serialize(Flow(null), MetadataStore.Json);
        Assert.DoesNotContain("approval", plain);
        var json = JsonSerializer.Serialize(Flow(Approval(a => a with { Amount = null, Conditions = null })), MetadataStore.Json);
        Assert.Contains("\"approval\":{\"state\":\"PorAutorizar\",\"approve\":\"autorizada\",\"reject\":\"rechazada\",\"approvers\":\"Autorizadores\"}", json);
        var back = JsonSerializer.Deserialize<Workflow>(json, MetadataStore.Json)!;
        Assert.Equal("Autorizadores", back.Approval!.Approvers);
        Assert.True(back.Approval.IsExit("AUTORIZADA"));
        Assert.False(back.Approval.IsExit("enviar"));
    }

    // ---------------- Elegir la regla ----------------

    private static ApprovalRuleDef Rule(int id, int priority, Dictionary<string, string[]>? cond = null, decimal? min = null, decimal? max = null,
        DateTime? from = null, DateTime? to = null) =>
        new(id, $"R{id}", priority, from, to, min, max,
            (cond ?? []).ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase), [1]);

    private static readonly DateTime Today = new(2026, 11, 2);

    [Fact]
    public void Gana_la_menor_prioridad_luego_la_mas_especifica_luego_la_mas_antigua()
    {
        var values = new Dictionary<string, string?> { ["Depto"] = "7", ["Tipo"] = "Bienes" };
        var general = Rule(1, 5);
        var depto = Rule(2, 5, new() { ["Depto"] = ["7"] });
        var urgente = Rule(3, 1, new() { ["Tipo"] = ["Servicios"] });
        Assert.Equal(2, ApprovalMatrix.Pick([general, depto, urgente], values, 10m, Today)!.Id);
        Assert.Equal(3, ApprovalMatrix.Pick([general, depto, urgente], new Dictionary<string, string?> { ["Depto"] = "7", ["Tipo"] = "servicios" }, 10m, Today)!.Id);
        Assert.Equal(1, ApprovalMatrix.Pick([Rule(4, 5), general], values, 10m, Today)!.Id);
        // Una condición vacía en la regla es «cualquiera»; un campo vacío en el documento solo cumple «cualquiera».
        Assert.Equal(1, ApprovalMatrix.Pick([general, depto], new Dictionary<string, string?> { ["Depto"] = null }, 10m, Today)!.Id);
        Assert.Null(ApprovalMatrix.Pick([depto], new Dictionary<string, string?> { ["Depto"] = null }, 10m, Today));
    }

    [Fact]
    public void Vigencia_inclusiva_y_monto_vacio_no_entra_en_un_rango()
    {
        var values = new Dictionary<string, string?>();
        Assert.NotNull(ApprovalMatrix.Pick([Rule(1, 1, from: Today, to: Today)], values, 1m, Today.AddHours(15)));
        Assert.Null(ApprovalMatrix.Pick([Rule(1, 1, from: Today.AddDays(1))], values, 1m, Today));
        Assert.Null(ApprovalMatrix.Pick([Rule(1, 1, to: Today.AddDays(-1))], values, 1m, Today));
        Assert.Null(ApprovalMatrix.Pick([Rule(1, 1, min: 0m)], values, null, Today));
        Assert.NotNull(ApprovalMatrix.Pick([Rule(1, 1)], values, null, Today));
        Assert.True(ApprovalMatrix.InRange(100m, 100m, 100m));
        Assert.False(ApprovalMatrix.InRange(99.99m, 100m, null));
        Assert.False(ApprovalMatrix.InRange(null, null, 5m));
    }

    [Fact]
    public void Un_nivel_se_salta_al_empezar_si_el_monto_no_cae_en_el_rango_del_perfil()
    {
        var rule = new ApprovalRuleDef(1, "R", 1, null, null, null, null, new Dictionary<string, IReadOnlyList<string>>(), [10, 20, 30]);
        var profiles = new Dictionary<int, ApprovalProfileDef>
        {
            [10] = new(10, "Jefes", null, null, true), [20] = new(20, "Gerentes", null, 50_000m, true), [30] = new(30, "Directores", 100_000m, null, true),
        };
        var small = ApprovalMatrix.Freeze(rule, profiles, 1_000m, hasAmount: true);
        Assert.Equal([null, null, "range"], small.Levels.Select(l => l.Skipped));
        var big = ApprovalMatrix.Freeze(rule, profiles, 200_000m, hasAmount: true);
        Assert.Equal([null, "range", null], big.Levels.Select(l => l.Skipped));
        // Revisión adversarial 053 (F-10): sin monto no se sabe si cae en el rango: ningún nivel se salta.
        Assert.All(ApprovalMatrix.Freeze(rule, profiles, null, hasAmount: true).Levels, l => Assert.Null(l.Skipped));
        // Sin monto declarado, los rangos no aplican.
        Assert.All(ApprovalMatrix.Freeze(rule, profiles, null, hasAmount: false).Levels, l => Assert.Null(l.Skipped));
        Assert.Equal([1, 2, 3], big.Levels.Select(l => l.Level));
        Assert.Equal("Directores", big.Levels[2].Profile);
    }

    [Fact]
    public void La_huella_de_condiciones_cambia_con_el_monto_o_una_condicion_y_no_con_otro_campo()
    {
        var a = Approval();
        var h = new Dictionary<string, object?> { ["Monto"] = 100m, ["Depto"] = 7L, ["Tipo"] = "Bienes", ["Concepto"] = "x" };
        var same = ApprovalMatrix.ConditionsHash(a, h);
        Assert.Equal(same, ApprovalMatrix.ConditionsHash(a, new Dictionary<string, object?>(h) { ["Concepto"] = "otro", ["Monto"] = 100.00m }));
        Assert.NotEqual(same, ApprovalMatrix.ConditionsHash(a, new Dictionary<string, object?>(h) { ["Monto"] = 101m }));
        Assert.NotEqual(same, ApprovalMatrix.ConditionsHash(a, new Dictionary<string, object?>(h) { ["Depto"] = 8L }));
        Assert.NotEqual(same, ApprovalMatrix.ConditionsHash(a, new Dictionary<string, object?>(h) { ["Tipo"] = null }));
        Assert.Equal("7", ApprovalMatrix.Canon(7L));
        Assert.Equal("true", ApprovalMatrix.Canon(true));
        Assert.Null(ApprovalMatrix.Canon(""));
    }

    // ---------------- Quién revisa un cambio a la matriz (015) ----------------

    private static readonly IReadOnlyDictionary<int, (string Name, AdminCapabilities Caps)> NoRoles = new Dictionary<int, (string, AdminCapabilities)>();

    private static UserContext Reviewer(int id) => new()
    {
        Id = id, Username = $"u{id}", DisplayName = "R", SecurityStamp = Guid.Empty, Roles = [], CanManageUsers = true,
    };

    [Fact]
    public void Quien_entra_o_sale_del_perfil_no_aprueba_el_cambio_y_una_regla_la_aprueba_cualquier_administrador()
    {
        const string before = """{"name":"Jefes","memberIds":[5,6]}""";
        Assert.NotNull(SecurityApproval.WhyNotApprover(Reviewer(7), SecurityChangeKinds.ApprovalProfileUpdate, 1, """{"memberIds":[5,6,7]}""", before, NoRoles));
        Assert.NotNull(SecurityApproval.WhyNotApprover(Reviewer(6), SecurityChangeKinds.ApprovalProfileUpdate, 1, """{"memberIds":[5]}""", before, NoRoles));
        Assert.Null(SecurityApproval.WhyNotApprover(Reviewer(5), SecurityChangeKinds.ApprovalProfileUpdate, 1, """{"memberIds":[5,6,8]}""", before, NoRoles));
        Assert.NotNull(SecurityApproval.WhyNotApprover(Reviewer(9), SecurityChangeKinds.ApprovalProfileCreate, null, """{"memberIds":[9]}""", null, NoRoles));
        Assert.Null(SecurityApproval.WhyNotApprover(Reviewer(9), SecurityChangeKinds.ApprovalRuleUpdate, 1, "{}", "{}", NoRoles));
        // Revisión adversarial 053 (F-08): la matriz de otra empresa la revisa quien tiene acceso a ella.
        var norte = Reviewer(9) is var r ? new UserContext { Id = r.Id, Username = r.Username, DisplayName = r.DisplayName, SecurityStamp = r.SecurityStamp,
            Roles = r.Roles, CanManageUsers = true, Companies = [new CompanyInfo(1, "N", "Norte", "MXN")] } : null!;
        Assert.NotNull(SecurityApproval.WhyNotApprover(norte, SecurityChangeKinds.ApprovalRuleCreate, null, """{"companyId":2}""", null, NoRoles));
        Assert.NotNull(SecurityApproval.WhyNotApprover(norte, SecurityChangeKinds.ApprovalProfileCreate, null, """{"companyId":2,"memberIds":[5]}""", null, NoRoles));
        Assert.Null(SecurityApproval.WhyNotApprover(norte, SecurityChangeKinds.ApprovalRuleCreate, null, """{"companyId":1}""", null, NoRoles));
    }
}
