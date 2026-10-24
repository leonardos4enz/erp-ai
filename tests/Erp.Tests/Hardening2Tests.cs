using Erp.Api.Assistant;
using Erp.Api.Environments;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.OData;
using Erp.Api.Security;
using Microsoft.AspNetCore.Http;

namespace Erp.Tests;

/// <summary>
/// Spec 021: nadie otorga lo que no tiene, versión de los catálogos y huella de lo revisado, huella del contenido de una
/// tarea, avisos de seguridad de una reversión y tope de <c>$skip</c>.
/// </summary>
public class Hardening2Tests
{
    private static UserContext Actor(bool superuser = false, bool model = false, bool users = false, bool audit = false) => new()
    {
        Id = 7, Username = "actor", DisplayName = "Actor", SecurityStamp = Guid.NewGuid(), Roles = [],
        IsSuperuser = superuser, CanManageModel = superuser || model, CanManageUsers = superuser || users, CanViewAudit = superuser || audit,
    };

    // ---------------- H1 ----------------

    [Fact]
    public void Solo_se_otorga_o_quita_lo_que_se_tiene()
    {
        var usersAdmin = Actor(users: true);
        Assert.Null(AdminCapabilities.None.MissingFor(usersAdmin));
        Assert.Null(new AdminCapabilities(false, false, true, false).MissingFor(usersAdmin));
        Assert.Equal("administrador del sistema", new AdminCapabilities(true, true, true, true).MissingFor(usersAdmin));
        Assert.Equal("administrar el modelo", new AdminCapabilities(false, true, false, false).MissingFor(usersAdmin));
        Assert.Equal("ver la auditoría", new AdminCapabilities(false, false, false, true).MissingFor(usersAdmin));
        // El superusuario cubre todo; quien tiene modelo y usuarios reparte ambos, pero no auditoría.
        Assert.Null(new AdminCapabilities(true, true, true, true).MissingFor(Actor(superuser: true)));
        Assert.Null(new AdminCapabilities(false, true, true, false).MissingFor(Actor(model: true, users: true)));
        Assert.NotNull(new AdminCapabilities(false, true, true, true).MissingFor(Actor(model: true, users: true)));
    }

    [Fact]
    public void Las_capacidades_de_varios_roles_se_suman()
    {
        var sum = new AdminCapabilities(false, true, false, false).Or(new AdminCapabilities(false, false, false, true));
        Assert.Equal(new AdminCapabilities(false, true, false, true), sum);
        Assert.True(sum.Any);
        Assert.False(AdminCapabilities.None.Any);
    }

    // ---------------- H2 ----------------

    private static EntityDef Doc(string? title = null) => new()
    {
        Name = "Doc", Label = "Documento", LabelPlural = "Documentos",
        Fields = [new FieldDef { Name = "Clave", Label = "Clave", Type = FieldTypes.Text }],
        Layout = title is null ? null : new Layout { Sections = [new LayoutSection { Name = "General", Label = title, Fields = [new LayoutItem { Field = "Clave" }] }] },
    };

    [Fact]
    public void La_version_de_un_catalogo_cambia_con_su_definicion_y_no_con_otra_cosa()
    {
        Assert.Matches("^[0-9a-f]{64}$", ModelVersion.Of(Doc()));
        Assert.Equal(ModelVersion.Of(Doc("A")), ModelVersion.Of(Doc("A")));
        Assert.NotEqual(ModelVersion.Of(Doc("A")), ModelVersion.Of(Doc("B")));
    }

    [Fact]
    public void La_huella_base_cubre_solo_lo_que_el_plan_toca()
    {
        var other = Doc() with { Name = "Otro" };
        IReadOnlyDictionary<string, EntityDef> m1 = new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase) { ["Doc"] = Doc("A"), ["Otro"] = other };
        IReadOnlyDictionary<string, EntityDef> m2 = new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase) { ["Doc"] = Doc("A"), ["Otro"] = other with { Label = "Cambiado" } };
        IReadOnlyDictionary<string, EntityDef> m3 = new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase) { ["Doc"] = Doc("B"), ["Otro"] = other };
        var noBoards = new Dictionary<string, DashboardDef>();
        string Base(IReadOnlyDictionary<string, EntityDef> m, params string[] touched) => ModelVersion.Base(m, noBoards, touched.ToHashSet(StringComparer.OrdinalIgnoreCase), null);
        Assert.Equal(Base(m1, "Doc"), Base(m2, "Doc"));
        Assert.NotEqual(Base(m1, "Doc"), Base(m3, "Doc"));
        // Un catálogo que aún no existe también cuenta (otro lo pudo crear entre medio).
        Assert.NotEqual(Base(m1, "Nuevo"), Base(m1, "Doc"));
        Assert.Equal(Base(m1, "Nuevo"), Base(m2, "Nuevo"));
    }

    [Fact]
    public void La_huella_del_asistente_cambia_si_cambia_la_base_aunque_el_plan_compile_igual()
    {
        var preview = new PlanPreview(true, [], [new PlanStep(0, "Actualizar layout", null)]);
        Assert.NotEqual(ReviewHash.Of("{}", preview, [], "a"), ReviewHash.Of("{}", preview, [], "b"));
        Assert.Equal(ReviewHash.Of("{}", preview, [], "a"), ReviewHash.Of("{}", preview, [], "a"));
    }

    // ---------------- H4 ----------------

    private static ScheduleDef Task(string where = "Activo", string label = "Marcar", string role = "Servicio") => new()
    {
        Name = "Marcar", Label = label, Every = "day", At = "07:00", Where = where, Role = role,
        Actions = [new AutomationAction { Type = AutomationActions.Update, Set = [new FieldAssignment("Marcas", "Marcas + 1")] }],
    };

    [Fact]
    public void La_huella_de_una_tarea_cubre_lo_que_hace_y_no_su_nombre_visible()
    {
        var h = ScheduleCalendar.ContentHash(Task());
        Assert.Matches("^[0-9a-f]{64}$", h);
        Assert.Equal(h, ScheduleCalendar.ContentHash(Task(label: "Otra etiqueta")));
        Assert.NotEqual(h, ScheduleCalendar.ContentHash(Task(where: "NOT Activo")));
        Assert.NotEqual(h, ScheduleCalendar.ContentHash(Task(role: "Otro")));
        Assert.NotEqual(h, ScheduleCalendar.ContentHash(Task() with { At = "08:00" }));
        Assert.NotEqual(h, ScheduleCalendar.ContentHash(Task() with { Actions = [] }));
    }

    // ---------------- H5 ----------------

    [Fact]
    public void Los_avisos_de_seguridad_de_un_plan_no_incluyen_lo_demas()
    {
        IReadOnlyDictionary<string, EntityDef> model = new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase) { ["Doc"] = Doc() };
        var plan = new ChangePlan { Operations =
        [
            new SetSecurityOp { Entity = "Doc", Security = null },
            new SetLayoutOp { Entity = "Doc", Layout = null },
            new ArchiveFieldOp { Entity = "Doc", Field = "Clave" },
        ] };
        var w = PlanWarnings.Security(plan, model);
        Assert.Equal(["Quita la seguridad por campo y por registro de «Documento»."], w);
    }

    [Fact]
    public void Aprobar_o_aplicar_una_reversion_que_cambia_seguridad_exige_confirmarlo()
    {
        var none = new PackageDiffDto(true, [], [], [], [], [], "h", false, [], []);
        EnvironmentService.RequireSecurityConfirmed(none, confirmed: false);
        var security = none with { SecurityChanges = ["Quita la seguridad por campo y por registro de «Documento»."] };
        var ex = Assert.Throws<Conflict>(() => EnvironmentService.RequireSecurityConfirmed(security, confirmed: false));
        Assert.Contains("seguridad", ex.Message);
        EnvironmentService.RequireSecurityConfirmed(security, confirmed: true);
    }

    // ---------------- H7 ----------------

    private static ODataQuery Query(string qs) => ODataQuery.Parse(new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(qs)));

    [Fact]
    public void Skip_tiene_tope()
    {
        Assert.Equal(ODataQuery.MaxSkip, Query($"$skip={ODataQuery.MaxSkip}").Skip);
        var ex = Assert.Throws<ODataException>(() => Query($"$skip={ODataQuery.MaxSkip + 1}"));
        Assert.Contains("$skip", ex.Message);
    }
}
