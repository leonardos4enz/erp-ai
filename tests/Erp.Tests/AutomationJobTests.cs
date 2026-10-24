using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>
/// Spec 016: calendario de las tareas programadas, su validación en el plan, la consulta (condición + alcance del rol, todo
/// como parámetros), <c>ADDDAYS(TODAY(), n)</c> en el traductor, el modo asíncrono y qué errores se reintentan.
/// </summary>
public class AutomationJobTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cobranza", "Gerente" };
    private static readonly TimeZoneInfo Cdmx = TimeZoneInfo.CreateCustomTimeZone("T-6", TimeSpan.FromHours(-6), "T-6", "T-6");

    private static ScheduleDef S(string every, int? interval = null, int? minute = null, string? at = null, int[]? weekdays = null, int? day = null) => new()
    {
        Name = "Vencer", Label = "Vencer pedidos", Every = every, Interval = interval, Minute = minute, At = at, Weekdays = weekdays, Day = day,
        Where = "Estado = 'Abierto' AND Vence < TODAY()", Role = "Cobranza",
        Actions = [new AutomationAction { Type = AutomationActions.Update, Target = "Cliente", Set = [new FieldAssignment("Avisos", "Cliente.Avisos + 1")] }],
    };

    /// <summary>Hora local (UTC-6) → UTC.</summary>
    private static DateTime L(int y, int mo, int d, int h, int mi, int s = 0) => new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc).AddHours(6);

    // ---------------- Calendario ----------------

    [Fact]
    public void Cada_n_minutos_cuenta_desde_la_medianoche_y_siempre_es_posterior()
    {
        Assert.Equal(L(2026, 10, 21, 7, 15), ScheduleCalendar.Next(S("minutes", interval: 15), L(2026, 10, 21, 7, 7, 30), Cdmx));
        Assert.Equal(L(2026, 10, 21, 7, 30), ScheduleCalendar.Next(S("minutes", interval: 15), L(2026, 10, 21, 7, 15), Cdmx));
        Assert.Equal(L(2026, 10, 22, 0, 0), ScheduleCalendar.Next(S("minutes", interval: 30), L(2026, 10, 21, 23, 50), Cdmx));
    }

    [Fact]
    public void Cada_hora_diaria_semanal_y_mensual()
    {
        Assert.Equal(L(2026, 10, 21, 7, 5), ScheduleCalendar.Next(S("hour", minute: 5), L(2026, 10, 21, 7, 4), Cdmx));
        Assert.Equal(L(2026, 10, 21, 8, 5), ScheduleCalendar.Next(S("hour", minute: 5), L(2026, 10, 21, 7, 5), Cdmx));
        Assert.Equal(L(2026, 10, 21, 7, 0), ScheduleCalendar.Next(S("day", at: "07:00"), L(2026, 10, 21, 6, 59, 59), Cdmx));
        Assert.Equal(L(2026, 10, 22, 7, 0), ScheduleCalendar.Next(S("day", at: "07:00"), L(2026, 10, 21, 7, 0), Cdmx));
        // 21/10/2026 es miércoles: lunes y jueves → jueves 22; desde el jueves a las 8, el lunes 26.
        Assert.Equal(L(2026, 10, 22, 7, 0), ScheduleCalendar.Next(S("week", at: "07:00", weekdays: [1, 4]), L(2026, 10, 21, 9, 0), Cdmx));
        Assert.Equal(L(2026, 10, 26, 7, 0), ScheduleCalendar.Next(S("week", at: "07:00", weekdays: [4, 1]), L(2026, 10, 22, 8, 0), Cdmx));
        // Un solo día de la semana ya pasado hoy: la semana siguiente.
        Assert.Equal(L(2026, 10, 28, 7, 0), ScheduleCalendar.Next(S("week", at: "07:00", weekdays: [3]), L(2026, 10, 21, 7, 0), Cdmx));
        // Día 31: en noviembre, el 30; en febrero de 2028 (bisiesto), el 29.
        Assert.Equal(L(2026, 11, 30, 23, 0), ScheduleCalendar.Next(S("month", at: "23:00", day: 31), L(2026, 11, 5, 0, 0), Cdmx));
        Assert.Equal(L(2028, 2, 29, 23, 0), ScheduleCalendar.Next(S("month", at: "23:00", day: 31), L(2028, 2, 1, 0, 0), Cdmx));
        Assert.Equal(L(2026, 12, 1, 6, 0), ScheduleCalendar.Next(S("month", at: "06:00", day: 1), L(2026, 11, 1, 6, 0), Cdmx));
    }

    [Fact]
    public void Las_ocurrencias_perdidas_no_se_acumulan()
    {
        // Diaria a las 7:00 y el servidor estuvo apagado tres días: la siguiente es la próxima a partir de ahora, no tres atrasadas.
        var ahora = L(2026, 10, 24, 12, 0);
        var next = ScheduleCalendar.Next(S("day", at: "07:00"), ahora, Cdmx);
        Assert.Equal(L(2026, 10, 25, 7, 0), next);
        Assert.True(next > ahora);
    }

    [Fact]
    public void Una_hora_inexistente_por_horario_de_verano_se_corre_a_la_primera_valida()
    {
        // Zona con horario de verano: el segundo domingo de marzo las 2:00 pasan a las 3:00 (como en Nueva York).
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday));
        var ny = TimeZoneInfo.CreateCustomTimeZone("T-5", TimeSpan.FromHours(-5), "T-5", "T-5", "T-4", [rule]);
        // 8/3/2026 a las 2:30 no existe: se corre a las 3:00 de horario de verano (UTC-4) = 7:00 UTC.
        var next = ScheduleCalendar.Next(S("day", at: "02:30"), new DateTime(2026, 3, 8, 6, 0, 0, DateTimeKind.Utc), ny);
        Assert.Equal(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void El_calendario_se_describe_en_palabras()
    {
        Assert.Equal("Cada 15 minutos", ScheduleCalendar.Describe(S("minutes", interval: 15)));
        Assert.Equal("Cada hora, al minuto 05", ScheduleCalendar.Describe(S("hour", minute: 5)));
        Assert.Equal("Diaria a las 07:00", ScheduleCalendar.Describe(S("day", at: "07:00")));
        Assert.Equal("Semanal: lunes, miércoles y viernes a las 07:00", ScheduleCalendar.Describe(S("week", at: "07:00", weekdays: [5, 1, 3])));
        Assert.Equal("Mensual: día 31 a las 23:00 (o el último día del mes)", ScheduleCalendar.Describe(S("month", at: "23:00", day: 31)));
        // La clave cambia si cambia el calendario (se reagenda), no por el orden de los días.
        Assert.Equal(ScheduleCalendar.Key(S("week", at: "07:00", weekdays: [1, 4])), ScheduleCalendar.Key(S("week", at: "07:00", weekdays: [4, 1])));
        Assert.NotEqual(ScheduleCalendar.Key(S("day", at: "07:00")), ScheduleCalendar.Key(S("day", at: "08:00")));
    }

    // ---------------- Validación en el plan ----------------

    private static FieldDef F(string name, string type, string? reference = null) => new() { Name = name, Label = name, Type = type, ReferenceEntity = reference };

    private static List<Operation> Base() =>
    [
        new CreateEntityOp { Name = "Cliente", Label = "Cliente", LabelPlural = "Clientes", Fields =
            [F("Nombre", FieldTypes.Text), new FieldDef { Name = "Avisos", Label = "Avisos", Type = FieldTypes.Decimal }] },
        new CreateEntityOp { Name = "Pedido", Label = "Pedido", LabelPlural = "Pedidos", Fields =
            [F("Folio", FieldTypes.Autonumber), F("Vence", FieldTypes.Date), F("Cliente", FieldTypes.Reference, "Cliente"), F("Responsable", FieldTypes.User)] },
        new SetWorkflowOp { Entity = "Pedido", Workflow = new()
        {
            Initial = "Abierto",
            States = [new() { Name = "Abierto", Label = "Abierto" }, new() { Name = "Vencido", Label = "Vencido", Editable = false }],
            Transitions = [new() { Name = "vencer", Label = "Vencer", From = ["Abierto"], To = "Vencido", System = true }],
        } },
        new SetSecurityOp { Entity = "Pedido", Security = new()
        {
            Owner = "Responsable",
            Records = [new() { Roles = ["Cobranza"], Scope = RecordScopes.Own }, new() { Roles = ["Gerente"], Scope = RecordScopes.All }],
        } },
    ];

    /// <summary>Sin target: la transición es del propio registro (lo que hace una tarea de vencimientos).</summary>
    private static readonly AutomationAction Vencer = new() { Type = AutomationActions.Transition, Transition = "vencer" };

    private static CompiledPlan Compile(params Operation[] extra) =>
        PlanCompiler.Compile(new ChangePlan { Operations = [.. Base(), .. extra] }, new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);

    private static string Errors(CompiledPlan p) => string.Join(" | ", p.Preview.Errors.Select(e => e.Message));

    private static SetSchedulesOp Op(ScheduleDef s) => new() { Entity = "Pedido", Schedules = [s] };

    private static readonly ScheduleDef Valid = S("day", at: "07:00") with
    {
        Where = "Estado = 'Abierto' AND Vence < ADDDAYS(TODAY(), -1)",
        Actions = [Vencer, new AutomationAction { Type = AutomationActions.Update, Target = "Cliente", Set = [new FieldAssignment("Avisos", "Cliente.Avisos + 1")] }],
    };

    [Fact]
    public void Una_tarea_valida_se_guarda_en_el_catalogo()
    {
        var c = Compile(Op(Valid));
        Assert.True(c.Preview.Valid, Errors(c));
        Assert.Equal("Vencer", Assert.Single(c.ResultingModel["Pedido"].Schedules!).Name);
        Assert.Contains(c.Preview.Steps, s => s.Description.Contains("tarea(s) programada(s)"));
    }

    [Theory]
    [InlineData("HASROLE('Gerente')", "resolverse en la base")]
    [InlineData("Responsable = CURRENTUSER()", "resolverse en la base")]
    [InlineData("Noexiste = 1", "Noexiste")]
    [InlineData("", "indica qué registros")]
    public void La_consulta_debe_ser_valida_y_traducible_a_sql(string where, string expected)
    {
        var c = Compile(Op(Valid with { Where = where }));
        Assert.False(c.Preview.Valid);
        Assert.Contains(expected, Errors(c));
    }

    [Fact]
    public void El_calendario_se_valida_por_frecuencia()
    {
        Assert.Contains("at debe ser", Errors(Compile(Op(S("day")))));
        Assert.Contains("at debe ser", Errors(Compile(Op(S("day", at: "24:00")))));
        Assert.Contains("interval debe ser", Errors(Compile(Op(S("minutes", interval: 7)))));
        Assert.Contains("interval debe ser", Errors(Compile(Op(S("minutes", interval: 2)))));
        Assert.Contains("'minute' no aplica", Errors(Compile(Op(S("day", at: "07:00", minute: 30)))));
        Assert.Contains("weekdays", Errors(Compile(Op(S("week", at: "07:00", weekdays: [1, 8])))));
        Assert.Contains("weekdays", Errors(Compile(Op(S("week", at: "07:00", weekdays: [2, 2])))));
        Assert.Contains("day va de 1 a 31", Errors(Compile(Op(S("month", at: "07:00", day: 0)))));
        Assert.Contains("frecuencia 'yearly'", Errors(Compile(Op(S("yearly")))));
        Assert.True(Compile(Op(S("minutes", interval: 5))).Preview.Valid);
        Assert.True(Compile(Op(S("hour"))).Preview.Valid);
    }

    [Fact]
    public void Rol_lote_acciones_y_catalogo_se_validan()
    {
        Assert.Contains("el rol 'Nadie' no existe", Errors(Compile(Op(Valid with { Role = "Nadie" }))));
        Assert.Contains("tamaño de lote", Errors(Compile(Op(Valid with { BatchSize = 0 }))));
        Assert.Contains("tamaño de lote", Errors(Compile(Op(Valid with { BatchSize = 1001 }))));
        Assert.True(Compile(Op(Valid with { BatchSize = 1000 })).Preview.Valid);
        Assert.Contains("necesita de 1 a", Errors(Compile(Op(Valid with { Actions = [] }))));
        var bad = new AutomationAction { Type = AutomationActions.Update, Target = "Cliente", Set = [new FieldAssignment("Noexiste", "1")] };
        Assert.Contains("no se puede asignar 'Noexiste'", Errors(Compile(Op(Valid with { Actions = [bad] }))));
        Assert.Contains("está repetido", Errors(Compile(new SetSchedulesOp { Entity = "Pedido", Schedules = [Valid, Valid] })));
        var model = Model();
        var child = model["Pedido"] with { Fields = [.. model["Pedido"].Fields, F("Doc", FieldTypes.Parent, "Cliente")] };
        Assert.Contains("es una partida", string.Join(" ", ScheduleRules.Validate(child, [Valid], model, Roles)));
        // Asignar el propio registro: solo campos capturables (Folio lo asigna el sistema).
        var folio = new AutomationAction { Type = AutomationActions.Update, Set = [new FieldAssignment("Folio", "'X'")] };
        Assert.Contains("no se puede asignar 'Folio'", Errors(Compile(Op(Valid with { Actions = [folio] }))));
    }

    [Fact]
    public void Archivar_un_campo_que_usa_la_tarea_rompe_la_vista_previa()
    {
        // La pasada final revisa las tareas aunque el plan no las toque (como los webhooks).
        var c = Compile(Op(Valid), new ArchiveFieldOp { Entity = "Pedido", Field = "Vence" });
        Assert.False(c.Preview.Valid);
        Assert.Contains("Vence", Errors(c));
    }

    [Fact]
    public void El_modo_de_una_automatizacion_se_valida_y_se_omite_si_es_sincrono()
    {
        Automation A(string? mode) => new() { Name = "Avisar", Label = "Avisar", On = AutomationEvents.Create, Mode = mode,
            Actions = [new AutomationAction { Type = AutomationActions.Update, Target = "Cliente", Set = [new FieldAssignment("Avisos", "1")] }] };
        Assert.True(Compile(new SetAutomationsOp { Entity = "Pedido", Automations = [A("async")] }).Preview.Valid);
        Assert.True(Compile(new SetAutomationsOp { Entity = "Pedido", Automations = [A("sync")] }).Preview.Valid);
        Assert.Contains("modo 'later' no válido", Errors(Compile(new SetAutomationsOp { Entity = "Pedido", Automations = [A("later")] })));
        Assert.True(A("async").IsAsync);
        Assert.False(A(null).IsAsync);
        // Sin modo, el JSON guardado es el de la 005: los paquetes y módulos existentes no cambian.
        Assert.DoesNotContain("mode", JsonSerializer.Serialize(A(null), MetadataStore.Json));
        Assert.Contains("\"mode\":\"async\"", JsonSerializer.Serialize(A("async"), MetadataStore.Json));
    }

    [Fact]
    public void Las_tareas_viajan_en_los_paquetes_de_entorno()
    {
        var none = new Dictionary<string, DashboardDef>(StringComparer.OrdinalIgnoreCase);
        var without = Model();
        var with = Compile(Op(Valid)).ResultingModel;
        var diff = ModelDiff.Compute(new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase) { ["Pedido"] = with["Pedido"] }, none, without, none, "p");
        Assert.Empty(diff.Conflicts);
        Assert.Equal("Vencer", Assert.Single(Assert.Single(diff.Plan.Operations.OfType<SetSchedulesOp>()).Schedules).Name);
        // Iguales: nada que aplicar (si estaba activa o en pausa es del entorno, no del paquete).
        Assert.Empty(ModelDiff.Compute(new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase) { ["Pedido"] = with["Pedido"] }, none, with, none, "p")
            .Plan.Operations.OfType<SetSchedulesOp>());
    }

    // ---------------- Traductor y consulta ----------------

    private static IReadOnlyDictionary<string, EntityDef> Model() => Compile().ResultingModel;

    [Fact]
    public void ADDDAYS_de_TODAY_se_traduce_como_parametro()
    {
        var model = Model();
        var bag = new RecordFilter.ParamBag("@rf");
        var sql = RecordFilter.TranslateRollup("Vence <= ADDDAYS(TODAY(), 3) AND Vence > ADDDAYS(TODAY(), -2)", model["Pedido"], model, bag);
        Assert.DoesNotContain("3", sql.Replace("@rf", ""));
        Assert.Equal(DateTime.Today.AddDays(3), bag.Items[0].Value);
        Assert.Equal(DateTime.Today.AddDays(-2), bag.Items[1].Value);
        // También en las reglas de acceso por registro.
        Assert.Null(RecordFilter.Check("Vence >= ADDDAYS(TODAY(), -30)", model["Pedido"], Roles));
        // Solo con TODAY() y un entero literal.
        Assert.Throws<ExprException>(() => RecordFilter.TranslateRollup("Vence <= ADDDAYS(Vence, 3)", model["Pedido"], model, new RecordFilter.ParamBag("@rf")));
        Assert.Throws<ExprException>(() => RecordFilter.TranslateRollup("Vence <= ADDDAYS(TODAY(), 1.5)", model["Pedido"], model, new RecordFilter.ParamBag("@rf")));
    }

    private static UserContext Service(params string[] roles) => new()
    {
        Id = 42, Username = "tarea:Pedido.Vencer", DisplayName = "Tareas programadas", SecurityStamp = Guid.Empty, Roles = roles,
        Entities = new Dictionary<string, EntityAccess>(), ServiceRoleName = roles.FirstOrDefault(),
    };

    [Fact]
    public void La_consulta_combina_la_condicion_y_el_alcance_del_rol_todo_como_parametros()
    {
        var model = Model();
        var (sql, pars) = RecordService.ScheduleQuery(model["Pedido"], Valid with { Where = "Estado = 'Abierto' AND Cliente.Nombre = 'Ñandú'" },
            model, Service("Cobranza"), 101, lockId: null);
        Assert.StartsWith("SELECT TOP (101) t.[Id] FROM [app].[Pedido] t WHERE t.[IsDeleted] = 0 AND", sql);
        Assert.EndsWith("ORDER BY t.[Id]", sql);
        Assert.DoesNotContain("Abierto", sql);
        Assert.DoesNotContain("Ñandú", sql);
        // «Propios» del rol de servicio: el dueño es la cuenta de la tarea (parámetro), no todo el catálogo.
        Assert.Contains("t.[Responsable] = @sec", sql);
        Assert.Contains(pars, p => p.Name.StartsWith("@sec") && Equals(p.Value, 42));
        Assert.Contains(pars, p => Equals(p.Value, "Abierto"));

        // Con un rol de alcance «todos» no hay filtro de alcance; el bloqueo por registro repite la condición.
        var (all, _) = RecordService.ScheduleQuery(model["Pedido"], Valid, model, Service("Gerente"), 10, lockId: null);
        Assert.DoesNotContain("@sec", all);
        var (locked, lockPars) = RecordService.ScheduleQuery(model["Pedido"], Valid, model, Service("Cobranza"), 1, lockId: 7);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK) WHERE t.[Id] = @id AND t.[IsDeleted] = 0 AND", locked);
        Assert.Contains("@sec", locked);
        Assert.Contains(lockPars, p => p.Name == "@id" && Equals(p.Value, 7L));
        // El tope del lote no se puede rebasar.
        Assert.StartsWith("SELECT TOP (1001)", RecordService.ScheduleQuery(model["Pedido"], Valid, model, Service("Gerente"), 50_000, null).Sql);
    }

    [Fact]
    public void Un_rol_sin_permiso_por_registro_no_selecciona_nada()
    {
        var model = Model();
        var (sql, _) = RecordService.ScheduleQuery(model["Pedido"], Valid, model, Service("Otro"), 10, lockId: null);
        Assert.Contains("1 = 0", sql);
    }

    // ---------------- Reintentos ----------------

    [Fact]
    public void Solo_se_reintenta_lo_transitorio()
    {
        Assert.True(BackgroundErrors.IsTransient(new Conflict("Otra operación está actualizando el mismo registro.")));
        Assert.True(BackgroundErrors.IsTransient(new AutomationFailed("Avisar", "otra operación…", transient: true)));
        Assert.True(BackgroundErrors.IsTransient(new TimeoutException()));
        Assert.False(BackgroundErrors.IsTransient(new AutomationFailed("Avisar", "la regla no se cumple")));
        Assert.False(BackgroundErrors.IsTransient(new ValidationFailed(new Dictionary<string, string[]> { ["x"] = ["mal"] })));
        Assert.False(BackgroundErrors.IsTransient(new AppException(400, "mal")));
        Assert.False(BackgroundErrors.IsTransient(new InvalidOperationException()));
        // El mensaje de la bitácora es el del motor; lo inesperado no filtra detalles.
        Assert.Equal("mal", BackgroundErrors.Message(new ValidationFailed(new Dictionary<string, string[]> { ["x"] = ["mal"] })));
        Assert.Equal("No se pudo completar «Avisar»: la regla", BackgroundErrors.Message(new AutomationFailed("Avisar", "la regla")));
        Assert.DoesNotContain("secreto", BackgroundErrors.Message(new InvalidOperationException("secreto interno")));
    }
}
