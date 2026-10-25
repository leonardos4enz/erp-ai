using System.Text.Json.Nodes;
using Erp.IntegrationTests.Harness;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 021 · Endurecimiento 2 (las pruebas sin UI de <c>web/e2e/endurecimiento-2.spec.ts</c>, movidas en la 030). H3
/// (trabajo asíncrono, 021-3) falla con el código anterior a la 021 (<c>f6c5445</c>):
/// <c>npm --prefix web run it:regresion -- f6c5445 Hardening021Tests.H3</c>.
/// </summary>
public class Hardening021Tests(Hardening021Tests.Host host) : IClassFixture<Hardening021Tests.Host>
{
    public const string Pwd = "frase larga de endurecimiento 021";
    private const string Group = "Pruebas 021";
    private static readonly Dictionary<string, string> Users = new()
    {
        ["ua"] = "e2e-e21-usuarios", ["ub"] = "e2e-e21-usuarios2", ["su"] = "e2e-e21-super", ["basico"] = "e2e-e21-basico", ["aprobador"] = "e2e-e21-aprobador",
    };

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Ids = [];
        /// <summary>Instancia con aprobación de seguridad (antes otro proceso en el puerto +4).</summary>
        public Factory Sec = null!;

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            async Task<int> Role(string name, bool model = false, bool users = false, bool audit = false)
            {
                var res = await admin.PostAsync("/api/admin/roles", new { name, description = "Spec 021", canManageModel = model, canManageUsers = users,
                    canViewAudit = audit, permissions = Array.Empty<object>() });
                Assert.True(res.Status == 200, res.Text);
                return res.Body!.Id();
            }
            Ids["usuarios"] = await Role("E21 Usuarios", users: true);
            Ids["modelador"] = await Role("E21 Modelador", model: true);
            Ids["auditor"] = await Role("E21 Auditor", audit: true);
            Ids["basicoRol"] = await Role("E21 Básico");
            Ids["superRol"] = await admin.RoleIdAsync("Administrador");
            foreach (var (key, username) in Users)
            {
                int[] roleIds = key is "su" or "aprobador" ? [Ids["superRol"]] : key == "basico" ? [Ids["basicoRol"]] : [Ids["usuarios"]];
                var res = await admin.PostAsync("/api/admin/users", new { username, displayName = $"E21 {key}", password = Pwd, roleIds });
                Assert.True(res.Status == 201, res.Text);
                Ids[key] = res.Body!.Id();
            }
            Sec = await SiblingAsync(s =>
            {
                s["Environments:Name"] = "Producción IT 021";
                s["Environments:RequireSecurityApproval"] = "true";
                s["Automations:PollSeconds"] = "300"; // la bandeja la atiende la instancia principal
            });
        }
    }

    private Task<Client> As(string who) => who == "admin" ? host.AdminAsync() : host.LoginAsync(Users[who], Pwd);
    private Task<Client> AtSec(string who) => who == "admin" ? host.Sec.LoginAsync("admin", host.AdminPassword) : host.Sec.LoginAsync(Users[who], Pwd);
    private int Id(string key) => host.Ids[key];

    private async Task<int[]> RolesOf(int userId)
    {
        using var admin = await As("admin");
        var u = (await admin.GetAsync("/api/admin/users")).Array.Nodes().First(x => x.Id() == userId);
        return u.Arr("roleIds").Nodes().Select(r => r.GetValue<int>()).Order().ToArray();
    }

    private static Task<Res> PutUser(Client api, int id, params int[] roleIds) =>
        api.PutAsync($"/api/admin/users/{id}", new { displayName = $"Usuario {id}", isActive = true, roleIds });

    // ---------------- H1: nadie otorga lo que no tiene ----------------

    [Fact]
    public async Task H1_CA1_quien_solo_administra_usuarios_no_da_ni_quita_superusuario_tampoco_a_si_mismo()
    {
        using var ua = await As("ua");
        var self = await PutUser(ua, Id("ua"), Id("usuarios"), Id("superRol"));
        Assert.Equal(403, self.Status);
        Assert.Contains("Solo un administrador del sistema", self.Text);
        Assert.Equal([Id("usuarios")], await RolesOf(Id("ua")));

        var alta = await ua.PostAsync("/api/admin/users", new { username = "e2e-e21-nuevo-su", displayName = "No debe existir", password = Pwd, roleIds = new[] { Id("superRol") } });
        Assert.Equal(403, alta.Status);
        Assert.Equal(403, (await PutUser(ua, Id("su"), Id("basicoRol"))).Status);
        Assert.Equal([Id("superRol")], await RolesOf(Id("su")));
        // Lo que sí tiene lo reparte: un rol sin capacidades administrativas, o el suyo.
        Assert.Equal(200, (await PutUser(ua, Id("basico"), Id("basicoRol"), Id("usuarios"))).Status);
        Assert.Equal(200, (await PutUser(ua, Id("basico"), Id("basicoRol"))).Status);
    }

    [Fact]
    public async Task H1_CA2_tampoco_reparte_modelador_ni_auditoria_si_no_los_tiene_el_superusuario_si()
    {
        using var ua = await As("ua");
        using var admin = await As("admin");
        foreach (var role in new[] { Id("modelador"), Id("auditor") })
        {
            var res = await PutUser(ua, Id("basico"), Id("basicoRol"), role);
            Assert.True(res.Status == 403, res.Text);
        }
        Assert.Equal([Id("basicoRol")], await RolesOf(Id("basico")));
        Assert.Equal(200, (await PutUser(admin, Id("basico"), Id("basicoRol"), Id("modelador"))).Status);
        // Quitarlo también es repartir.
        Assert.Equal(403, (await PutUser(ua, Id("basico"), Id("basicoRol"))).Status);
        Assert.Equal(200, (await PutUser(admin, Id("basico"), Id("basicoRol"))).Status);
    }

    [Fact]
    public async Task H1_CA3_un_rol_no_gana_capacidades_que_quien_lo_edita_no_tiene_el_rol_superusuario_solo_lo_edita_un_superusuario()
    {
        using var ua = await As("ua");
        object Body(string name, bool model = false, bool audit = false, string description = "Spec 021") =>
            new { name, description, canManageModel = model, canManageUsers = false, canViewAudit = audit, permissions = Array.Empty<object>() };
        Assert.Equal(403, (await ua.PutAsync($"/api/admin/roles/{Id("basicoRol")}", Body("E21 Básico", model: true))).Status);
        Assert.Equal(403, (await ua.PostAsync("/api/admin/roles", Body("E21 Auditor bis", audit: true))).Status);
        Assert.Equal(403, (await ua.PutAsync($"/api/admin/roles/{Id("modelador")}", Body("E21 Modelador"))).Status);
        Assert.Equal(403, (await ua.PutAsync($"/api/admin/roles/{Id("superRol")}", Body("Administrador", description: "cambiado por E21"))).Status);
        Assert.Equal(200, (await ua.PutAsync($"/api/admin/roles/{Id("basicoRol")}", Body("E21 Básico", description: "Descrito por E21"))).Status);
    }

    // ---------------- H1 CA4–CA5 y H8 CA1: instancia con aprobación de seguridad ----------------

    private static async Task<JsonNode> ChangeAsync(Client api, int id) =>
        (await api.GetAsync("/api/admin/security-changes")).Items.Nodes().First(c => c.Id() == id);

    private static Task<Res> ApproveAsync(Client api, JsonNode change) =>
        api.PostAsync($"/api/admin/security-changes/{change.Id()}/approve", new { hash = change.Str("hash"), comment = "IT 021" });

    [Fact]
    public async Task H1_CA4_un_cambio_que_otorga_superusuario_solo_lo_aprueba_un_superusuario_la_solicitud_sigue_pendiente()
    {
        using var admin = await AtSec("admin");
        using var ub = await AtSec("ub");
        using var su = await AtSec("su");
        var res = await PutUser(admin, Id("basico"), Id("basicoRol"), Id("superRol"));
        Assert.True(res.Status == 202, res.Text);
        var id = res["securityChange"]!.Id();
        var seen = await ChangeAsync(ub, id);
        Assert.False(seen["canApprove"]!.GetValue<bool>());
        Assert.Equal(403, (await ApproveAsync(ub, seen)).Status);
        Assert.Equal("pending", (await ChangeAsync(admin, id)).Str("status"));
        Assert.Equal([Id("basicoRol")], await RolesOf(Id("basico")));
        var bySu = await ChangeAsync(su, id);
        Assert.True(bySu["canApprove"]!.GetValue<bool>());
        Assert.Equal(200, (await ApproveAsync(su, bySu)).Status);
        Assert.Equal(200, (await PutUser(admin, Id("basico"), Id("basicoRol"))).Status);
    }

    [Fact]
    public async Task H1_CA5_el_beneficiario_no_aprueba_su_propio_cambio()
    {
        using var admin = await AtSec("admin");
        using var ub = await AtSec("ub");
        using var ua = await AtSec("ua");
        var res = await PutUser(admin, Id("ub"), Id("usuarios"), Id("basicoRol"));
        Assert.True(res.Status == 202, res.Text);
        var id = res["securityChange"]!.Id();
        var mine = await ChangeAsync(ub, id);
        Assert.False(mine["canApprove"]!.GetValue<bool>());
        Assert.Equal(403, (await ApproveAsync(ub, mine)).Status);
        var other = await ChangeAsync(ua, id);
        Assert.True(other["canApprove"]!.GetValue<bool>());
        Assert.Equal(200, (await ApproveAsync(ua, other)).Status);
        Assert.Equal(new[] { Id("usuarios"), Id("basicoRol") }.Order(), await RolesOf(Id("ub")));
        Assert.Equal(200, (await PutUser(admin, Id("ub"), Id("usuarios"))).Status); // quitar no espera
    }

    [Fact]
    public async Task H8_CA1_una_solicitud_atorada_en_applying_se_puede_volver_a_aprobar_despues_de_10_minutos()
    {
        using var admin = await AtSec("admin");
        using var ua = await AtSec("ua");
        var res = await PutUser(admin, Id("basico"), Id("basicoRol"), Id("usuarios"));
        Assert.True(res.Status == 202, res.Text);
        var id = res["securityChange"]!.Id();
        // El proceso «cayó» a mitad de la aprobación hace 20 minutos.
        await host.ExecAsync($"UPDATE meta.SecurityChange SET Status = 'applying', ClaimedAt = DATEADD(minute, -20, SYSUTCDATETIME()) WHERE Id = {id}");
        var seen = await ChangeAsync(ua, id);
        Assert.Equal("pending", seen.Str("status"));
        var ok = await ApproveAsync(ua, seen);
        Assert.True(ok.Status == 200, ok.Text);
        Assert.Equal(new[] { Id("basicoRol"), Id("usuarios") }.Order(), await RolesOf(Id("basico")));
        Assert.Equal(200, (await PutUser(admin, Id("basico"), Id("basicoRol"))).Status);
    }

    // ---------------- H2: cambios de modelo sin pisarse ----------------

    [Fact]
    public async Task H2_CA1_CA2_la_vista_previa_da_la_huella_aplicar_la_exige_y_rechaza_si_el_catalogo_cambio()
    {
        using var admin = await As("admin");
        await admin.ApplyPlanAsync(new { description = "IT 021: documento", operations = new object[] {
            new { op = "createEntity", group = Group, name = "E21Doc", label = "Documento 021", labelPlural = "Documentos 021", fields = new object[] {
                new { name = "Clave", label = "Clave", type = "text", required = true }, new { name = "Notas", label = "Notas", type = "text" } } } } });
        var v1 = (await admin.GetAsync("/api/model/entities/E21Doc"))["version"]!.GetValue<string>();
        Assert.Matches("^[0-9a-f]{64}$", v1);
        object Layout(string name, string label, params string[] fields) =>
            new { op = "setLayout", entity = "E21Doc", layout = new { sections = new[] { new { name, label, fields } } } };
        var mine = new { description = "Mi layout", baseVersions = new Dictionary<string, string> { ["E21Doc"] = v1 }, operations = new[] { Layout("Mia", "Mía", "Clave", "Notas") } };
        var preview = await admin.PostAsync("/api/model/plan/preview", mine);
        Assert.True(preview["valid"]!.GetValue<bool>());
        var baseHash = preview["baseHash"]!.GetValue<string>();
        Assert.Matches("^[0-9a-f]{64}$", baseHash);
        Assert.Equal(400, (await admin.PostAsync("/api/model/plan/apply", mine)).Status); // sin huella no se aplica

        await admin.ApplyPlanAsync(new { description = "Layout ajeno", operations = new[] { Layout("Ajena", "Ajena", "Notas", "Clave") } });
        var late = await admin.PostAsync("/api/model/plan/apply", new { mine.description, mine.baseVersions, mine.operations, baseHash });
        Assert.Equal(409, late.Status);
        Assert.Contains("cambió", late.Text);
        Assert.Equal("Ajena", (await admin.GetAsync("/api/model/entities/E21Doc"))["layout"]!["sections"]![0]!.Str("label"));
        var stale = await admin.PostAsync("/api/model/plan/preview", mine);
        Assert.False(stale["valid"]!.GetValue<bool>());
        Assert.Contains("cambió desde que lo abriste", stale["errors"]!.Json());
    }

    [Fact]
    public async Task H8_CA3_no_se_quita_un_estado_del_proceso_en_el_que_hay_registros()
    {
        using var admin = await As("admin");
        object Workflow(params string[] states) => new { initial = "Abierto", states = states.Select(name => new { name, label = name }),
            transitions = states.Contains("Pausado") ? new object[] { new { name = "pausar", label = "Pausar", from = new[] { "Abierto" }, to = "Pausado" } } : [] };
        await admin.ApplyPlanAsync(new { description = "IT 021: proceso", operations = new object[] {
            new { op = "createEntity", group = Group, name = "E21Proceso", label = "Proceso 021", labelPlural = "Procesos 021", fields = new[] { new { name = "Clave", label = "Clave", type = "text", required = true } } },
            new { op = "setWorkflow", entity = "E21Proceso", workflow = Workflow("Abierto", "Pausado") } } });
        var r = await admin.CreateAsync("E21Proceso", new { Clave = "P1" });
        Assert.Equal(200, (await admin.TransitionAsync("E21Proceso", r, "pausar")).Status);
        var preview = await admin.PostAsync("/api/model/plan/preview", new { description = "Quitar Pausado", operations = new[] { new { op = "setWorkflow", entity = "E21Proceso", workflow = Workflow("Abierto") } } });
        Assert.False(preview["valid"]!.GetValue<bool>());
        Assert.Contains("1 registro(s) están en el estado «Pausado»", preview["errors"]!.Json());
    }

    // ---------------- H3: un trabajo se ejecuta una vez (021-3) ----------------

    private static object Counter(string entity) => new { op = "createEntity", group = Group, name = entity, label = entity, labelPlural = entity, fields = new object[] {
        new { name = "Clave", label = "Clave", type = "text", required = true }, new { name = "Veces", label = "Veces", type = "integer", defaultValue = "0" } } };

    private static object CountAsync(string entity, string name) => new { op = "setAutomations", entity, automations = new[] { new { name, label = $"{name} {entity}",
        on = "create", mode = "async", actions = new[] { new { type = "update", set = new[] { new { field = "Veces", value = "Veces + 1" } } } } } } };

    private static async Task<List<JsonNode>> RunsAsync(Client admin, string name) => (await admin.GetAsync("/api/admin/automations/runs", new { name })).Array.Nodes().ToList();

    [Fact]
    public async Task H3_CA4_un_trabajo_con_el_lease_vencido_y_sin_intentos_no_se_vuelve_a_ejecutar()
    {
        using var admin = await As("admin");
        await admin.ApplyPlanAsync(new { description = "IT 021: bandeja", operations = new[] { Counter("E21Contador"), CountAsync("E21Contador", "Contar") } });
        var rec = await admin.CreateAsync("E21Contador", new { Clave = "C1" });
        await WaitForAsync(async () => (await admin.RecordAsync("E21Contador", rec.Id())).Dec("Veces"), v => v == 1, "primera ejecución");

        // Otra instancia lo reclamó, gastó sus tres intentos y cayó: el lease venció.
        var id = await host.ScalarAsync<int>($"""
            INSERT meta.AutomationRun (Kind, EntityName, Name, RecordId, Event, TriggeredBy, RunAsName, Generation, Status, Attempts, LeaseUntil, StartedAt)
            OUTPUT inserted.Id VALUES ('async', 'E21Contador', 'Contar', {rec.Id()}, 'create', 1, 'admin', 1, 'running', 3, DATEADD(minute, -1, SYSUTCDATETIME()), SYSUTCDATETIME())
            """);
        var run = await WaitForAsync(async () => (await RunsAsync(admin, "Contar")).First(r => r.Id() == id), r => r.Str("status") != "running", "que deje de estar en curso");
        Assert.Equal("failed", run.Str("status"));
        Assert.Matches("(?i)se interrumpió 3 veces", run.Str("lastError"));
        Assert.Equal(1m, (await admin.RecordAsync("E21Contador", rec.Id())).Dec("Veces"));
    }

    [Fact]
    public async Task H3_CA1_los_efectos_y_el_estado_correcto_se_guardan_juntos_o_ninguno()
    {
        using var admin = await As("admin");
        await admin.ApplyPlanAsync(new { description = "IT 021: trabajo que falla al marcar", operations = new[] { Counter("E21Fallo"), CountAsync("E21Fallo", "ContarFallo") } });
        // Marcar el éxito falla: simula que el proceso cae justo después de confirmar los efectos del trabajo.
        JsonNode? run;
        int recId;
        await using (await host.FailWhenAsync("meta.AutomationRun", "EntityName = 'E21Fallo' AND Status = 'succeeded'"))
        {
            recId = (await admin.CreateAsync("E21Fallo", new { Clave = "F1" })).Id();
            run = await WaitForAsync(async () => (await RunsAsync(admin, "ContarFallo")).FirstOrDefault(r => r["recordId"]?.GetValue<int>() == recId),
                r => r is not null && r.Str("status") is not ("pending" or "running"), "que termine");
        }
        Assert.Equal("failed", run!.Str("status"));
        // Regresión (auditoría 021, hallazgo 3): si el trabajo «falló», no dejó efectos (antes: Veces = 1 con el estado
        // fallido, y cada reintento lo volvía a sumar).
        Assert.Equal(0m, (await admin.RecordAsync("E21Fallo", recId)).Dec("Veces"));
    }

    [Fact]
    public async Task H3_CA1_con_dos_instancias_sobre_la_misma_base_cada_trabajo_se_ejecuta_una_vez()
    {
        // Spec 030 (H1 CA2): dos servidores atienden la misma bandeja; 20 altas simultáneas, 20 efectos, ninguno doble.
        // La segunda arranca después del plan: el modelo en caché de cada instancia dura 30 s (R-030-1).
        using var admin = await As("admin");
        await admin.ApplyPlanAsync(new { description = "IT 021: dos bandejas", operations = new[] { Counter("E21Doble"), CountAsync("E21Doble", "ContarDoble") } });
        var other = await host.SiblingAsync(s => s["Environments:Name"] = "Segunda instancia 021");
        using var otherAdmin = await other.LoginAsync("admin", host.AdminPassword);
        var created = await Concurrently(20, i => (i % 2 == 0 ? admin : otherAdmin).CreateAsync("E21Doble", new { Clave = $"D{i}" }));
        var ids = created.Select(c => c.Id()).ToHashSet();
        await WaitForAsync(async () => (await RunsAsync(admin, "ContarDoble")).Count(r => r.Str("status") == "succeeded"), n => n >= 20, "las 20 ejecuciones");
        var runs = await RunsAsync(admin, "ContarDoble");
        foreach (var id in ids)
        {
            Invariant.ExactlyOnce(runs.Count(r => r["recordId"]?.GetValue<int>() == id && r.Str("status") == "succeeded"), $"la ejecución del registro {id}");
            Assert.Equal(1m, (await admin.RecordAsync("E21Doble", id)).Dec("Veces"));
        }
    }

    // ---------------- H4: tareas atadas a lo que se activó ----------------

    private static object Schedule(string where, string label = "Marcar tareas") => new { name = "Marcar", label, every = "day", at = "07:00", where, role = "E21 Servicio",
        batchSize = 5, actions = new[] { new { type = "update", set = new[] { new { field = "Marcas", value = "Marcas + 1" } } } } };

    private static async Task EnsureTaskAsync(Client admin)
    {
        if ((await admin.GetAsync("/api/model/entities/E21Tarea")).Status == 200) return;
        var svc = await admin.PostAsync("/api/admin/roles", new { name = "E21 Servicio", description = "Spec 021", canManageModel = false, canManageUsers = false,
            canViewAudit = false, permissions = Array.Empty<object>() });
        Assert.True(svc.Status == 200, svc.Text);
        await admin.ApplyPlanAsync(new { description = "IT 021: tarea", operations = new object[] {
            new { op = "createEntity", group = Group, name = "E21Tarea", label = "Tarea 021", labelPlural = "Tareas 021", fields = new object[] {
                new { name = "Clave", label = "Clave", type = "text", required = true }, new { name = "Activo", label = "Activo", type = "boolean", defaultValue = "TRUE" },
                new { name = "Marcas", label = "Marcas", type = "integer", defaultValue = "0" } } },
            new { op = "setSchedules", entity = "E21Tarea", schedules = new[] { Schedule("Activo") } } } });
    }

    [Fact]
    public async Task H4_CA1_CA2_cambiar_el_contenido_de_una_tarea_activa_o_volver_a_declararla_la_deja_en_pausa()
    {
        using var admin = await As("admin");
        await EnsureTaskAsync(admin);
        Task Plan(string description, params object[] schedules) =>
            admin.ApplyPlanAsync(new { description, operations = new[] { new { op = "setSchedules", entity = "E21Tarea", schedules } } });
        async Task<bool> Active() => (await admin.GetAsync("/api/admin/automations/schedules")).Array.Nodes().First(s => s.Str("entity") == "E21Tarea")["active"]!.GetValue<bool>();
        async Task Activate() => Assert.Equal(200, (await admin.PutAsync("/api/admin/automations/schedules/E21Tarea/Marcar", new { active = true })).Status);

        await Plan("Contenido inicial", Schedule("Activo"));
        await Activate();
        Assert.True(await Active());
        await Plan("Etiqueta", Schedule("Activo", "Marcar tareas (renombrada)"));
        Assert.True(await Active()); // cambiar solo la etiqueta no la pausa
        await Plan("Consulta", Schedule("NOT Activo"));
        Assert.False(await Active());
        var audit = (await admin.GetAsync("/api/audit", new { action = "automation.schedule", pageSize = 5 })).Items.Nodes().First();
        Assert.Equal("Marcar", audit.Str("recordId"));
        Assert.Equal("pause", audit["after"]!.Str("action"));

        await Activate();
        await Plan("Quitar");
        await Plan("Volver a declarar", Schedule("NOT Activo"));
        Assert.False(await Active());
    }

    [Fact]
    public async Task H4_CA3_una_ocurrencia_encolada_de_una_tarea_que_cambio_se_cancela_al_ejecutarse()
    {
        using var admin = await As("admin");
        await EnsureTaskAsync(admin);
        Assert.Equal(200, (await admin.PutAsync("/api/admin/automations/schedules/E21Tarea/Marcar", new { active = true })).Status);
        // La huella guardada ya no es la del contenido actual (p. ej. un cambio que llegó entre encolar y ejecutar).
        var id = await host.ScalarAsync<int>("""
            UPDATE meta.ScheduleState SET ContentHash = REPLICATE('0', 64) WHERE EntityName = 'E21Tarea' AND ScheduleName = 'Marcar';
            INSERT meta.AutomationRun (Kind, EntityName, Name, RunAsRole, RunAsName, Manual, Status)
            OUTPUT inserted.Id VALUES ('schedule', 'E21Tarea', 'Marcar', 'E21 Servicio', 'Tarea E21', 0, 'pending')
            """);
        var run = await WaitForAsync(async () => (await RunsAsync(admin, "Marcar")).First(r => r.Id() == id), r => r.Str("status") is not ("pending" or "running"), "que termine");
        Assert.Equal("canceled", run.Str("status"));
        Assert.Contains("cambió", run.Str("lastError"));
        Assert.Equal(200, (await admin.PutAsync("/api/admin/automations/schedules/E21Tarea/Marcar", new { active = false })).Status);
    }
}
