using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>Spec 006: validación de la política de seguridad, traducción de permisos a SQL y acceso por campo.</summary>
public class SecurityPolicyTests
{
    private static readonly IReadOnlySet<string> Roles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Solicitante", "Gerente", "Almacenista" };

    /// <summary>Requisición con dueño, departamento, monto, motivo de rechazo, partidas con precio y proceso.</summary>
    private static List<Operation> Base() =>
    [
        new CreateEntityOp { Name = "Req", Label = "Requisición", LabelPlural = "Requisiciones", DisplayField = "Folio", Fields =
        [
            new FieldDef { Name = "Folio", Label = "Folio", Type = FieldTypes.Autonumber },
            new FieldDef { Name = "Solicitante", Label = "Solicitante", Type = FieldTypes.User, DefaultValue = "CURRENTUSER()" },
            new FieldDef { Name = "Departamento", Label = "Departamento", Type = FieldTypes.Text, Required = true },
            new FieldDef { Name = "Monto", Label = "Monto", Type = FieldTypes.Decimal },
            new FieldDef { Name = "Urgente", Label = "Urgente", Type = FieldTypes.Boolean },
            new FieldDef { Name = "Motivo", Label = "Motivo de rechazo", Type = FieldTypes.LongText },
        ] },
        new CreateEntityOp { Name = "ReqPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
        [
            new FieldDef { Name = "Req", Label = "Req", Type = FieldTypes.Parent, ReferenceEntity = "Req" },
            new FieldDef { Name = "Precio", Label = "Precio", Type = FieldTypes.Decimal },
        ] },
        new AddFieldOp { Entity = "Req", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "ReqPartida" } },
        new SetWorkflowOp { Entity = "Req", Workflow = new()
        {
            Initial = "Borrador",
            States = [new() { Name = "Borrador", Label = "Borrador" }, new() { Name = "Rechazada", Label = "Rechazada", Editable = false }],
            Transitions = [new() { Name = "rechazar", Label = "Rechazar", From = ["Borrador"], To = "Rechazada", RequiredFields = ["Motivo"] }],
        } },
    ];

    private static CompiledPlan Compile(params Operation[] extra) =>
        PlanCompiler.Compile(new ChangePlan { Operations = [.. Base(), .. extra] },
            new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);

    private static string Errors(CompiledPlan p) => string.Join(" | ", p.Preview.Errors.Select(e => e.Message));

    private static SetSecurityOp Sec(SecurityPolicy p, string entity = "Req") => new() { Entity = entity, Security = p };

    private static EntityDef Req(SecurityPolicy p)
    {
        var r = Compile(Sec(p));
        Assert.True(r.Preview.Valid, Errors(r));
        return r.ResultingModel["Req"];
    }

    private static UserContext User(params string[] roles) => UserIn("Norte", roles);

    private static UserContext UserIn(string? unit, params string[] roles) => new()
    {
        Id = 7, Username = "u", DisplayName = "U", SecurityStamp = Guid.Empty, Roles = roles, Unit = unit,
    };

    // ---------------- Compilador ----------------

    [Fact]
    public void Valid_policy_compiles_and_is_kept_in_the_model()
    {
        var e = Req(new SecurityPolicy
        {
            Owner = "Solicitante",
            Fields =
            [
                new() { Field = "Monto", Read = ["Gerente"] },
                new() { Field = "Motivo", EditableIn = [] },
                new() { Field = "Urgente", Edit = ["Gerente"], EditableIn = ["Borrador"] },
            ],
            Records =
            [
                new() { Roles = ["Solicitante"], Scope = RecordScopes.Own },
                new() { Roles = ["Gerente"], Where = "Departamento = CURRENTUNIT() OR HASROLE('Gerente')", Access = "read" },
            ],
        });
        Assert.Equal("Solicitante", e.Security!.Owner);
        Assert.Equal(3, e.Security.Fields!.Count);
    }

    [Theory]
    [InlineData("NoExiste", "no existe en Req")]
    [InlineData("Estado", "no admiten seguridad por campo")]
    public void Unknown_or_state_fields_are_rejected(string field, string expected)
    {
        var r = Compile(Sec(new SecurityPolicy { Fields = [new() { Field = field, Read = ["Gerente"] }] }));
        Assert.Contains(expected, Errors(r));
    }

    [Fact]
    public void Unknown_roles_and_states_are_rejected()
    {
        var r = Compile(Sec(new SecurityPolicy
        {
            Fields = [new() { Field = "Monto", Read = ["Fantasma"], EditableIn = ["Aprobada"] }],
            Records = [new() { Roles = ["Otro"] }],
        }));
        var errors = Errors(r);
        Assert.Contains("el rol 'Fantasma' no existe", errors);
        Assert.Contains("el estado 'Aprobada' no existe", errors);
        Assert.Contains("el rol 'Otro' no existe", errors);
    }

    [Fact]
    public void Display_field_cannot_be_hidden()
    {
        var r = Compile(Sec(new SecurityPolicy { Fields = [new() { Field = "Folio", Read = ["Gerente"] }] }));
        Assert.Contains("campo para mostrar", Errors(r));
    }

    [Fact]
    public void Computed_fields_only_admit_read()
    {
        var r = Compile(Sec(new SecurityPolicy { Fields = [new() { Field = "Folio", Edit = ["Gerente"] }] }));
        Assert.Contains("solo admite «read»", Errors(r));
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(false, false, "Rechazada")]
    public void Required_field_without_default_must_be_capturable_on_create(bool restrictRead, bool restrictEdit, string? onlyIn)
    {
        var rule = new FieldSecurity
        {
            Field = "Departamento",
            Read = restrictRead ? ["Gerente"] : null,
            Edit = restrictEdit ? ["Gerente"] : null,
            EditableIn = onlyIn is null ? null : [onlyIn],
        };
        Assert.Contains("es obligatorio y sin valor por defecto", Errors(Compile(Sec(new SecurityPolicy { Fields = [rule] }))));
    }

    [Fact]
    public void Owner_must_be_a_user_field()
    {
        Assert.Contains("debe ser un campo de tipo usuario", Errors(Compile(Sec(new SecurityPolicy { Owner = "Departamento" }))));
    }

    [Fact]
    public void Empty_records_and_bad_scope_or_access_are_rejected()
    {
        Assert.Contains("«records» vacío", Errors(Compile(Sec(new SecurityPolicy { Records = [] }))));
        var errors = Errors(Compile(Sec(new SecurityPolicy { Records = [new() { Scope = "mios", Access = "todo" }] })));
        Assert.Contains("alcance 'mios' inválido", errors);
        Assert.Contains("acceso 'todo' inválido", errors);
    }

    [Theory]
    [InlineData("Solicitante.Nombre = 'Ana'", "solo usa campos del propio catálogo")]
    [InlineData("Monto > SUM(Partidas.Precio)", "no se puede usar en una regla de acceso")]
    [InlineData("Departamento = NULL", "ISBLANK")]
    [InlineData("Monto + 1", "debe ser una condición")]
    [InlineData("HASROLE('Fantasma')", "rol 'Fantasma' no existe")]
    [InlineData("Partidas = 1", "no existe en Req")]
    public void Untranslatable_where_is_rejected(string where, string expected)
    {
        var r = Compile(Sec(new SecurityPolicy { Records = [new() { Where = where }] }));
        Assert.Contains(expected, Errors(r));
    }

    [Fact]
    public void Line_entities_only_admit_field_rules_with_parent_states()
    {
        var ok = Compile(Sec(new SecurityPolicy { Fields = [new() { Field = "Precio", Read = ["Gerente"], EditableIn = ["Borrador"] }] }, "ReqPartida"));
        Assert.True(ok.Preview.Valid, Errors(ok));
        var bad = Compile(Sec(new SecurityPolicy { Records = [new() { Scope = RecordScopes.Own }] }, "ReqPartida"));
        Assert.Contains("es una partida", Errors(bad));
    }

    [Fact]
    public void Archiving_a_field_used_by_the_policy_is_rejected()
    {
        var r = Compile(Sec(new SecurityPolicy { Fields = [new() { Field = "Monto", Read = ["Gerente"] }] }),
            new ArchiveFieldOp { Entity = "Req", Field = "Monto" });
        Assert.Contains("campo 'Monto': no existe", Errors(r));
    }

    [Fact]
    public void Null_security_removes_the_policy()
    {
        var r = Compile(Sec(new SecurityPolicy { SystemManaged = true }), new SetSecurityOp { Entity = "Req", Security = null });
        Assert.True(r.Preview.Valid, Errors(r));
        Assert.Null(r.ResultingModel["Req"].Security);
    }

    [Fact]
    public void Output_only_field_properties_from_a_plan_are_ignored()
    {
        var r = Compile(new AddFieldOp { Entity = "Req", Field = new FieldDef { Name = "Nota", Label = "Nota", Type = FieldTypes.Text, Access = "hidden", EditableIn = ["Borrador"] } });
        var f = r.ResultingModel["Req"].FindField("Nota")!;
        Assert.Null(f.Access);
        Assert.Null(f.EditableIn);
    }

    // ---------------- Traducción a SQL ----------------

    [Fact]
    public void No_records_policy_or_superuser_means_no_filter()
    {
        var e = Req(new SecurityPolicy { Fields = [new() { Field = "Monto", Read = ["Gerente"] }] });
        Assert.Null(RecordFilter.For(e, User("Solicitante"), write: false));
        var restricted = Req(new SecurityPolicy { Records = [new() { Roles = ["Gerente"] }] });
        Assert.Equal("1 = 0", RecordFilter.For(restricted, User(), write: false)!.Sql); // denegar por defecto
        var admin = new UserContext { Id = 1, Username = "a", DisplayName = "A", SecurityStamp = Guid.Empty, Roles = [], IsSuperuser = true };
        Assert.Null(RecordFilter.For(restricted, admin, write: false));
    }

    [Fact]
    public void Scopes_translate_to_parameterized_sql()
    {
        var e = Req(new SecurityPolicy
        {
            Owner = "Solicitante",
            Records =
            [
                new() { Roles = ["Solicitante"], Scope = RecordScopes.Own },
                new() { Roles = ["Almacenista"], Scope = RecordScopes.Unit, Access = "read" },
            ],
        });
        var both = RecordFilter.For(e, User("Solicitante", "Almacenista"), write: false)!;
        Assert.Equal("(t.[Solicitante] = @sec0 OR t.[Solicitante] IN (SELECT u.[Id] FROM meta.[User] u WHERE u.[Unit] = @sec1))", both.Sql);
        Assert.Equal([("@sec0", (object)7), ("@sec1", "Norte")], both.Parameters);

        // Escribir solo con los permisos de escritura: el de unidad es de lectura.
        Assert.Equal("(t.[Solicitante] = @sec0)", RecordFilter.For(e, User("Solicitante", "Almacenista"), write: true)!.Sql);
        // Sin permisos aplicables: ningún registro.
        Assert.Equal("1 = 0", RecordFilter.For(e, User("Gerente"), write: false)!.Sql);
        // Sin unidad, «de mi unidad» se reduce a «propios».
        Assert.Equal("(t.[Solicitante] = @sec0)", RecordFilter.For(e, UserIn(null, "Almacenista"), write: false)!.Sql);
    }

    [Fact]
    public void An_unconditional_all_grant_removes_the_filter()
    {
        var e = Req(new SecurityPolicy { Records = [new() { Roles = ["Solicitante"], Scope = RecordScopes.Own }, new() { Roles = ["Gerente"] }] });
        Assert.Null(RecordFilter.For(e, User("Solicitante", "Gerente"), write: true));
        Assert.Equal("(t.[CreatedBy] = @sec0)", RecordFilter.For(e, User("Solicitante"), write: true)!.Sql);
    }

    [Fact]
    public void Where_values_never_reach_the_sql_text()
    {
        const string evil = "x'; DROP TABLE meta.[User]; --";
        var e = Req(new SecurityPolicy
        {
            Records = [new() { Where = $"(Departamento = '{evil.Replace("'", "")}' OR Departamento IN ('A', 'B')) AND NOT ISBLANK(Motivo) AND Urgente AND Monto >= -5 AND HASROLE('Gerente') OR ISCREATOR()" }],
        });
        var p = RecordFilter.For(e, User("Gerente"), write: false)!;
        Assert.DoesNotContain("DROP", p.Sql);
        Assert.Equal(
            // El permiso completo va entre paréntesis para combinarse con OR con los demás.
            "(((((((t.[Departamento] = @sec0 OR t.[Departamento] IN (@sec1, @sec2)) AND (NOT (t.[Motivo] IS NULL OR LTRIM(RTRIM(t.[Motivo])) = N''))) AND t.[Urgente] = 1) AND t.[Monto] >= @sec3) AND 1 = 1) OR t.[CreatedBy] = @sec4))",
            p.Sql);
        Assert.Equal(-5m, p.Parameters[3].Value);
        // HASROLE se resuelve con los roles de quien consulta.
        Assert.Contains("AND 1 = 0", RecordFilter.For(e, User("Solicitante"), write: false)!.Sql);
    }

    [Fact]
    public void Lines_inherit_the_record_scope_of_their_document()
    {
        var r = Compile(Sec(new SecurityPolicy { Records = [new() { Roles = ["Solicitante"], Scope = RecordScopes.Own }] }));
        Assert.True(r.Preview.Valid, Errors(r));
        var p = RecordFilter.ForRecordOrParent(r.ResultingModel["ReqPartida"], r.ResultingModel, User("Solicitante"))!;
        Assert.StartsWith("t.[Req] IN (SELECT t.[Id] FROM ", p.Sql);
        Assert.EndsWith("t.[IsDeleted] = 0 AND (t.[CreatedBy] = @sec0))", p.Sql);
        // Sin permiso sobre el documento, tampoco sobre sus partidas.
        Assert.EndsWith("AND 1 = 0)", RecordFilter.ForRecordOrParent(r.ResultingModel["ReqPartida"], r.ResultingModel, User("Gerente"))!.Sql);
    }

    // ---------------- Acceso por campo ----------------

    [Fact]
    public void Field_access_by_role_and_state()
    {
        var e = Req(new SecurityPolicy
        {
            Fields =
            [
                new() { Field = "Monto", Read = ["Gerente"] },
                new() { Field = "Urgente", Edit = ["Gerente"] },
                new() { Field = "Motivo", EditableIn = [] },
                new() { Field = "Departamento", EditableIn = ["Borrador"] },
            ],
        });
        FieldDef F(string n) => e.FindField(n)!;
        var solicitante = User("Solicitante");
        var gerente = User("Gerente", "Solicitante"); // unión de roles: lo más permisivo
        Assert.Equal(FieldAccessLevels.Hidden, FieldAccess.ByRole(e, F("Monto"), solicitante));
        Assert.Equal(FieldAccessLevels.Edit, FieldAccess.ByRole(e, F("Monto"), gerente));
        Assert.Equal(FieldAccessLevels.Read, FieldAccess.ByRole(e, F("Urgente"), solicitante));
        Assert.False(FieldAccess.CanEdit(e, F("Motivo"), gerente, "Borrador"));
        Assert.True(FieldAccess.CanEdit(e, F("Departamento"), solicitante, "Borrador"));
        Assert.False(FieldAccess.CanEdit(e, F("Departamento"), solicitante, "Rechazada"));
        Assert.False(FieldAccess.CanEdit(e, F("Folio"), gerente, "Borrador")); // calculado

        // El superusuario ignora los roles, pero no los estados (lógica de proceso).
        var admin = new UserContext { Id = 1, Username = "a", DisplayName = "A", SecurityStamp = Guid.Empty, Roles = [], IsSuperuser = true };
        Assert.True(FieldAccess.CanSee(e, F("Monto"), admin));
        Assert.False(FieldAccess.CanEdit(e, F("Motivo"), admin, "Borrador"));
    }

    [Fact]
    public void Strip_removes_hidden_fields_labels_and_line_values()
    {
        var r = Compile(
            Sec(new SecurityPolicy { Fields = [new() { Field = "Monto", Read = ["Gerente"] }, new() { Field = "Solicitante", Read = ["Gerente"] }] }),
            Sec(new SecurityPolicy { Fields = [new() { Field = "Precio", Read = ["Gerente"] }] }, "ReqPartida"));
        Assert.True(r.Preview.Valid, Errors(r));
        var row = new Dictionary<string, object?>
        {
            ["Folio"] = "R-1", ["Monto"] = 10m, ["Solicitante"] = 3, ["Solicitante__label"] = "Ana",
            ["Partidas"] = new List<Dictionary<string, object?>> { new() { ["Precio"] = 5m, ["id"] = 1L } },
        };
        FieldAccess.Strip(r.ResultingModel["Req"], row, User("Solicitante"), r.ResultingModel);
        Assert.Equal(["Folio", "Partidas"], row.Keys.Order());
        Assert.Equal(["id"], ((List<Dictionary<string, object?>>)row["Partidas"]!)[0].Keys);
    }
}
