using System.Text.Json.Nodes;
using Erp.IntegrationTests.Harness;
using static Erp.IntegrationTests.Harness.Concurrency;
using static Erp.IntegrationTests.Harness.Invariant;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 051 · «pertenece a» sobre SQL real: un permiso por registro con <c>CURRENTUSER() IN Centro.Responsables</c> en todos los
/// caminos de lectura y de escritura, reglas y condiciones de transición con pertenencia, agregar o quitar un valor desde una
/// transición o una automatización (existencia, equipo, tope, bitácora), diez automatizaciones a la vez sobre la misma lista y
/// una transición que agrega y luego falla.
/// </summary>
public class MembershipTests(MembershipTests.Host host) : IClassFixture<MembershipTests.Host>
{
    private const string Pwd = "frase larga de pertenencia 051";
    private const string Lector = "it51-lector";
    private const string Otro = "it51-otro";
    private const string Role = "Pa Lector";

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Ids = [];

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            Assert.Equal(200, (await admin.PostAsync("/api/admin/roles", new { name = Role, description = "Spec 051", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = Array.Empty<object>() })).Status);
            var team = await admin.PostAsync("/api/admin/teams", new { name = "Equipo 051", unit = (string?)null, isActive = true, memberIds = Array.Empty<int>() });
            Assert.True(team.Status is 200 or 201, team.Text);
            Ids["equipo"] = team.Body!.Id();
            await admin.ApplyPlanAsync(new
            {
                description = "IT 051: pertenece a",
                operations = new object[]
                {
                    new { op = "createEntity", name = "PaCentro", label = "Centro 051", labelPlural = "Centros 051", displayField = "Nombre",
                        fields = new object[]
                        {
                            new { name = "Nombre", label = "Nombre", type = "text", required = true },
                            new { name = "Responsables", label = "Responsables", type = "multiuser" },
                        } },
                    new { op = "createEntity", name = "PaReq", label = "Requisición 051", labelPlural = "Requisiciones 051", displayField = "Folio",
                        fields = new object[]
                        {
                            new { name = "Folio", label = "Folio", type = "autonumber", prefix = "PA-", padding = 4 },
                            new { name = "Nota", label = "Nota", type = "text" },
                            new { name = "Urgente", label = "Urgente", type = "boolean" },
                            new { name = "Centro", label = "Centro", type = "reference", referenceEntity = "PaCentro", required = true },
                            new { name = "Seguidores", label = "Seguidores", type = "multiuser", editable = false },
                        } },
                    new { op = "setRules", entity = "PaReq", rules = new object[]
                    {
                        new { name = "UrgenteResponsable", field = "Urgente", assert = "NOT Urgente OR CURRENTUSER() IN Centro.Responsables",
                            message = "Solo un responsable del centro marca una requisición como urgente." },
                        new { name = "NotaAlEnviar", field = "Nota", when = "Estado = 'Enviada'", assert = "NOT ISBLANK(Nota)", message = "Escribe una nota antes de enviar." },
                    } },
                    new { op = "setWorkflow", entity = "PaReq", workflow = new
                    {
                        initial = "Borrador",
                        states = new object[]
                        {
                            new { name = "Borrador", label = "Borrador", editable = true },
                            new { name = "Enviada", label = "Enviada", editable = true },
                            new { name = "Autorizada", label = "Autorizada", editable = false },
                        },
                        transitions = new object[]
                        {
                            new { name = "enviar", label = "Enviar", from = new[] { "Borrador" }, to = "Enviada",
                                set = new[] { new { field = "Seguidores", value = "CURRENTUSER()", mode = "add" } } },
                            new { name = "seguir", label = "Seguir", from = new[] { "Borrador", "Enviada" }, to = "Enviada", roles = new[] { Role },
                                set = new[] { new { field = "Seguidores", value = "CURRENTUSER()", mode = "add" } } },
                            new { name = "dejar", label = "Dejar de seguir", from = new[] { "Enviada" }, to = "Enviada", roles = new[] { Role },
                                set = new[] { new { field = "Seguidores", value = "CURRENTUSER()", mode = "remove" } } },
                            new { name = "autorizar", label = "Autorizar", from = new[] { "Enviada" }, to = "Autorizada", roles = new[] { Role },
                                when = "CURRENTUSER() IN Centro.Responsables", whenMessage = "Solo un responsable del centro autoriza." },
                            new { name = "fantasma", label = "Fantasma", from = new[] { "Enviada" }, to = "Enviada",
                                set = new[] { new { field = "Seguidores", value = "999999", mode = "add" } } },
                            new { name = "equipo", label = "Equipo", from = new[] { "Enviada" }, to = "Enviada",
                                set = new[] { new { field = "Seguidores", value = $"{Ids["equipo"]}", mode = "add" } } },
                        },
                    } },
                    // La lista que abre el acceso no la ve ni la edita el rol: nadie se agrega solo.
                    new { op = "setSecurity", entity = "PaCentro", security = new { fields = new[] { new { field = "Responsables", read = new[] { "Administrador" } } } } },
                    new { op = "setSecurity", entity = "PaReq", security = new
                    {
                        records = new object[]
                        {
                            new { roles = new[] { Role }, where = "CURRENTUSER() IN Centro.Responsables", access = "read" },
                            new { roles = new[] { Role }, where = "CURRENTUSER() IN Seguidores" },
                        },
                    } },
                    new { op = "setReports", entity = "PaReq", reports = new object[]
                    {
                        new { name = "PorCentro", label = "Por centro", groupBy = new[] { new { field = "Centro.Nombre" } }, measures = new[] { new { fn = "count" } } },
                        new { name = "Sigo", label = "Las que sigo", groupBy = new[] { new { field = "Centro.Nombre" } }, measures = new[] { new { fn = "count" } },
                            filter = "CURRENTUSER() IN Seguidores" },
                    } },
                    // Diez automatizaciones a la vez sobre la misma lista.
                    new { op = "createEntity", name = "PaEtiqueta", label = "Etiqueta 051", labelPlural = "Etiquetas 051", displayField = "Nombre",
                        fields = new object[] { new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                    new { op = "createEntity", name = "PaProyecto", label = "Proyecto 051", labelPlural = "Proyectos 051", displayField = "Nombre",
                        fields = new object[]
                        {
                            new { name = "Nombre", label = "Nombre", type = "text", required = true },
                            new { name = "Etiquetas", label = "Etiquetas", type = "multireference", referenceEntity = "PaEtiqueta" },
                            new { name = "Total", label = "Total", type = "decimal", precision = 18, scale = 2 },
                        } },
                    // Revisión adversarial 051 (F-02): un acumulado con un filtro que no se traduce (LEN) se evalúa en memoria.
                    new { op = "createEntity", name = "PaGasto", label = "Gasto 051", labelPlural = "Gastos 051",
                        fields = new object[]
                        {
                            new { name = "Proyecto", label = "Proyecto", type = "reference", referenceEntity = "PaProyecto", required = true },
                            new { name = "Importe", label = "Importe", type = "decimal", precision = 18, scale = 2 },
                            new { name = "Nota", label = "Nota", type = "text" },
                            new { name = "Etiqueta", label = "Etiqueta", type = "reference", referenceEntity = "PaEtiqueta" },
                            new { name = "Etiquetas", label = "Etiquetas", type = "multireference", referenceEntity = "PaEtiqueta" },
                        } },
                    new { op = "setAutomations", entity = "PaGasto", automations = new object[]
                    {
                        new { name = "Sumar", label = "Sumar al proyecto", on = "create", actions = new object[]
                        {
                            new { type = "rollup", target = "Proyecto", field = "Total", sum = "Importe", filter = "Etiqueta IN Etiquetas AND LEN(Nota) > 0" },
                        } },
                    } },
                    new { op = "createEntity", name = "PaTarea", label = "Tarea 051", labelPlural = "Tareas 051",
                        fields = new object[]
                        {
                            new { name = "Proyecto", label = "Proyecto", type = "reference", referenceEntity = "PaProyecto", required = true },
                            new { name = "Etiqueta", label = "Etiqueta", type = "reference", referenceEntity = "PaEtiqueta" },
                            new { name = "Quitar", label = "Quitar", type = "boolean" },
                        } },
                    new { op = "setAutomations", entity = "PaTarea", automations = new object[]
                    {
                        new { name = "Etiquetar", label = "Etiquetar el proyecto", on = "create", actions = new object[]
                        {
                            new { type = "update", target = "Proyecto", when = "NOT (Quitar = TRUE)", set = new[] { new { field = "Etiquetas", value = "Etiqueta", mode = "add" } } },
                            new { type = "update", target = "Proyecto", when = "Quitar = TRUE", set = new[] { new { field = "Etiquetas", value = "Etiqueta", mode = "remove" } } },
                        } },
                    } },
                    // En vivo: la lista de un registro que no se ve no cuenta.
                    new { op = "createEntity", name = "PaZona", label = "Zona 051", labelPlural = "Zonas 051", displayField = "Nombre",
                        fields = new object[]
                        {
                            new { name = "Nombre", label = "Nombre", type = "text", required = true },
                            new { name = "Encargados", label = "Encargados", type = "multiuser" },
                        } },
                    new { op = "setSecurity", entity = "PaZona", security = new { records = new[] { new { roles = new[] { Role }, scope = "own" } } } },
                    new { op = "createEntity", name = "PaVisita", label = "Visita 051", labelPlural = "Visitas 051",
                        fields = new object[]
                        {
                            new { name = "Nota", label = "Nota", type = "text" },
                            new { name = "Zona", label = "Zona", type = "reference", referenceEntity = "PaZona" },
                        } },
                    // Revisión adversarial 051 (F-01): con NOT, una lista fuera de alcance no puede volverse «verdadero».
                    new { op = "createEntity", name = "PaAviso", label = "Aviso 051", labelPlural = "Avisos 051",
                        fields = new object[]
                        {
                            new { name = "Nota", label = "Nota", type = "text" },
                            new { name = "Zona", label = "Zona", type = "reference", referenceEntity = "PaZona" },
                        } },
                    new { op = "setSecurity", entity = "PaAviso", security = new
                    {
                        deletableWhen = "NOT CURRENTUSER() IN Zona.Encargados", deleteMessage = "Un encargado de la zona no elimina sus avisos.",
                    } },
                    new { op = "setSecurity", entity = "PaVisita", security = new
                    {
                        deletableWhen = "CURRENTUSER() IN Zona.Encargados", deleteMessage = "Solo un encargado de la zona elimina la visita.",
                    } },
                },
            });
            var role = await admin.RoleIdAsync(Role);
            Assert.Equal(200, (await admin.PutAsync($"/api/admin/roles/{role}", new { name = Role, description = "Spec 051", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = new object[]
                {
                    new { entity = "PaCentro", canRead = true, canCreate = false, canUpdate = true, canDelete = false },
                    new { entity = "PaReq", canRead = true, canCreate = true, canUpdate = true, canDelete = true },
                    new { entity = "PaZona", canRead = true, canCreate = false, canUpdate = false, canDelete = false },
                    new { entity = "PaVisita", canRead = true, canCreate = false, canUpdate = false, canDelete = true },
                    new { entity = "PaAviso", canRead = true, canCreate = false, canUpdate = false, canDelete = true },
                } })).Status);
            foreach (var (key, name) in new[] { ("lector", Lector), ("otro", Otro) })
            {
                var u = await admin.PostAsync("/api/admin/users", new { username = name, displayName = $"Persona {name}", password = Pwd, roleIds = new[] { role } });
                Assert.True(u.Status == 201, u.Text);
                Ids[key] = u.Body!.Id();
            }
            Ids["norte"] = (await admin.CreateAsync("PaCentro", new { Nombre = "Norte", Responsables = new[] { Ids["lector"] } })).Id();
            Ids["sur"] = (await admin.CreateAsync("PaCentro", new { Nombre = "Sur", Responsables = new[] { Ids["otro"] } })).Id();
            Ids["reqNorte"] = (await admin.CreateAsync("PaReq", new { Nota = "Del norte", Centro = Ids["norte"] })).Id();
            Ids["reqSur"] = (await admin.CreateAsync("PaReq", new { Nota = "Del sur", Centro = Ids["sur"] })).Id();
        }
    }

    private int Id(string key) => host.Ids[key];

    private async Task<string> Joined(string table, int owner) =>
        await host.ScalarAsync<string>($"SELECT STRING_AGG(CAST(ValueId AS varchar(20)), ',') WITHIN GROUP (ORDER BY ValueId) FROM app.[{table}] WHERE OwnerId = {owner}") ?? "";

    private static string Ids(params int[] ids) => string.Join(",", ids.Order());

    private async Task<string> FolioAsync(int id) =>
        (await host.ScalarAsync<string>($"SELECT Folio FROM app.PaReq WHERE Id = {id}"))!;

    [Fact]
    public async Task Un_permiso_por_pertenencia_limita_lista_registro_buscador_busqueda_reporte_exportacion_OData_e_impresion()
    {
        using var lector = await host.LoginAsync(Lector, Pwd);
        var norte = await FolioAsync(Id("reqNorte"));
        var sur = await FolioAsync(Id("reqSur"));

        var list = await lector.GetAsync("/api/data/PaReq", new { pageSize = 100 });
        Assert.True(list.Status == 200, list.Text);
        var folios = list.Items.Nodes().Select(n => n.Str("Folio")).ToList();
        Assert.Contains(norte, folios);
        Assert.DoesNotContain(sur, folios);

        Assert.Equal(200, (await lector.GetAsync($"/api/data/PaReq/{Id("reqNorte")}")).Status);
        Assert.Equal(404, (await lector.GetAsync($"/api/data/PaReq/{Id("reqSur")}")).Status);
        Assert.Equal(404, (await lector.GetAsync($"/api/data/PaReq/{Id("reqSur")}/history")).Status);
        Assert.Equal(404, (await lector.GetAsync($"/api/data/PaReq/{Id("reqSur")}/print")).Status);

        var lookup = await lector.GetAsync("/api/data/PaReq/lookup", new { search = "PA-" });
        Assert.Contains(lookup.Array.Nodes(), n => n.Str("label") == norte);
        Assert.DoesNotContain(lookup.Array.Nodes(), n => n.Str("label") == sur);

        var search = await lector.GetAsync("/api/search", new { q = sur });
        Assert.True(search.Status == 200, search.Text);
        Assert.DoesNotContain(sur, search.Text);
        Assert.Contains(norte, (await lector.GetAsync("/api/search", new { q = norte })).Text);

        var report = await lector.GetAsync("/api/reports/PaReq/PorCentro");
        Assert.True(report.Status == 200, report.Text);
        Assert.Contains("Norte", report.Text);
        Assert.DoesNotContain("Sur", report.Text);

        var csv = await lector.GetAsync("/api/data/PaReq/export", new { format = "csv", columns = "Folio,Nota" });
        Assert.Contains(norte, csv.Text);
        Assert.DoesNotContain(sur, csv.Text);

        var odata = await lector.GetAsync("/odata/PaReq");
        Assert.True(odata.Status == 200, odata.Text);
        Assert.Contains(norte, odata.Text);
        Assert.DoesNotContain(sur, odata.Text);
        Assert.Equal(404, (await lector.GetAsync($"/odata/PaReq({Id("reqSur")})")).Status);

        // El administrador (sin permisos por registro) ve las dos.
        using var admin = await host.AdminAsync();
        Assert.Contains(sur, (await admin.GetAsync("/api/data/PaReq/export", new { format = "csv", columns = "Folio" })).Text);
    }

    [Fact]
    public async Task Fuera_del_alcance_de_escritura_por_pertenencia_PUT_transicion_y_borrar_no_pasan()
    {
        using var admin = await host.AdminAsync();
        var req = await admin.CreateAsync("PaReq", new { Nota = "Solo lectura", Centro = Id("norte") });
        using var lector = await host.LoginAsync(Lector, Pwd);
        // Responsable del centro: la lee (acceso de solo lectura), pero no sigue la requisición: no la escribe.
        var row = await lector.RecordAsync("PaReq", req.Id());
        var put = await lector.PutAsync($"/api/data/PaReq/{req.Id()}", new { Nota = "Cambio", _rowVersion = row.Str("_rowVersion") });
        Assert.True(put.Status is 403 or 404, put.Text);
        Assert.True((await lector.DeleteAsync($"/api/data/PaReq/{req.Id()}")).Status is 403 or 404);
        var t = await lector.TransitionAsync("PaReq", row, "enviar");
        Assert.True(t.Status is 403 or 404, t.Text);
        Assert.Equal("Solo lectura", await host.ScalarAsync<string>($"SELECT Nota FROM app.PaReq WHERE Id = {req.Id()}"));

        // Quien la sigue sí la escribe.
        var sent = await admin.TransitionAsync("PaReq", await admin.RecordAsync("PaReq", req.Id()), "enviar");
        Assert.True(sent.Status == 200, sent.Text);
        using var otro = await host.LoginAsync(Otro, Pwd);
        Assert.Equal(404, (await otro.GetAsync($"/api/data/PaReq/{req.Id()}")).Status);
    }

    [Fact]
    public async Task Quitar_a_la_persona_de_la_lista_le_quita_el_acceso_en_la_siguiente_peticion()
    {
        using var admin = await host.AdminAsync();
        var centro = await admin.CreateAsync("PaCentro", new { Nombre = "Temporal", Responsables = new[] { Id("otro") } });
        var req = await admin.CreateAsync("PaReq", new { Nota = "Temporal", Centro = centro.Id() });
        using var otro = await host.LoginAsync(Otro, Pwd);
        Assert.Equal(200, (await otro.GetAsync($"/api/data/PaReq/{req.Id()}")).Status);
        var c = await admin.RecordAsync("PaCentro", centro.Id());
        Assert.Equal(200, (await admin.PutAsync($"/api/data/PaCentro/{centro.Id()}", new { Responsables = Array.Empty<int>(), _rowVersion = c.Str("_rowVersion") })).Status);
        Assert.Equal(404, (await otro.GetAsync($"/api/data/PaReq/{req.Id()}")).Status);
        // Un centro borrado (lógico) tampoco da acceso: el dueño de la lista debe estar vigente.
        var c2 = await admin.CreateAsync("PaCentro", new { Nombre = "Borrado", Responsables = new[] { Id("otro") } });
        var r2 = await admin.CreateAsync("PaReq", new { Nota = "De un centro borrado", Centro = c2.Id() });
        Assert.Equal(200, (await otro.GetAsync($"/api/data/PaReq/{r2.Id()}")).Status);
        await host.ExecAsync($"UPDATE app.PaCentro SET IsDeleted = 1 WHERE Id = {c2.Id()}");
        Assert.Equal(404, (await otro.GetAsync($"/api/data/PaReq/{r2.Id()}")).Status);
    }

    [Fact]
    public async Task Una_regla_y_una_condicion_de_transicion_con_pertenencia()
    {
        using var admin = await host.AdminAsync();
        // La regla se evalúa con quien guarda: el administrador no es responsable de Sur.
        var bad = await admin.PostAsync("/api/data/PaReq", new { Nota = "Urgente", Centro = Id("sur"), Urgente = true });
        Assert.True(bad.Status == 400, bad.Text);
        Assert.Contains("Solo un responsable del centro", bad.Text);
        Assert.Equal(201, (await admin.PostAsync("/api/data/PaReq", new { Nota = "Normal", Centro = Id("sur"), Urgente = false })).Status);

        // autorizar: solo un responsable del centro (lector en Norte). Con roles, la transición pide leer el registro (no escribirlo).
        var req = await admin.CreateAsync("PaReq", new { Nota = "Para autorizar", Centro = Id("norte") });
        using var lector = await host.LoginAsync(Lector, Pwd);
        var followed = await lector.TransitionAsync("PaReq", await admin.RecordAsync("PaReq", req.Id()), "seguir");
        Assert.True(followed.Status == 200, followed.Text);
        var denied = await admin.TransitionAsync("PaReq", followed.Body!, "autorizar");
        Assert.True(denied.Status is 400 or 409, denied.Text);
        Assert.Contains("Solo un responsable del centro autoriza", denied.Text);
        var ok = await lector.TransitionAsync("PaReq", followed.Body!, "autorizar");
        Assert.True(ok.Status == 200, ok.Text);
        Assert.Equal("Autorizada", ok.Body!.Str("Estado"));
    }

    [Fact]
    public async Task Agregar_y_quitar_desde_una_transicion_queda_en_la_bitacora()
    {
        using var admin = await host.AdminAsync();
        var req = await admin.CreateAsync("PaReq", new { Nota = "Bitácora", Centro = Id("norte") });
        var sent = await admin.TransitionAsync("PaReq", req, "enviar");
        Assert.True(sent.Status == 200, sent.Text);
        var adminId = await host.ScalarAsync<int>("SELECT Id FROM meta.[User] WHERE Username = 'admin'");
        Assert.Equal(Ids(adminId), await Joined("PaReq__Seguidores", req.Id()));
        Assert.Single(sent.Body!.Arr("Seguidores"));

        using var lector = await host.LoginAsync(Lector, Pwd);
        var followed = await lector.TransitionAsync("PaReq", sent.Body!, "seguir");
        Assert.True(followed.Status == 200, followed.Text);
        Assert.Equal(2, followed.Body!.Arr("Seguidores").Count);
        var left = await lector.TransitionAsync("PaReq", followed.Body!, "dejar");
        Assert.True(left.Status == 200, left.Text);
        Assert.DoesNotContain(Id("lector").ToString(), (await Joined("PaReq__Seguidores", req.Id())).Split(','));

        var audit = await host.ScalarAsync<string>(
            $"SELECT TOP 1 BeforeJson + ' => ' + AfterJson FROM meta.AuditLog WHERE EntityName = 'PaReq' AND RecordId = '{req.Id()}' AND Action LIKE '%seguir' ORDER BY Id DESC");
        Assert.NotNull(audit);
        Assert.Contains("Seguidores", audit);
        Assert.Contains(Id("lector").ToString(), audit!.Split(" => ")[1]);
    }

    [Fact]
    public async Task Agregar_un_valor_que_no_existe_o_un_equipo_400_sin_escribir()
    {
        using var admin = await host.AdminAsync();
        var req = await admin.CreateAsync("PaReq", new { Nota = "Inexistente", Centro = Id("norte") });
        var sent = (await admin.TransitionAsync("PaReq", req, "enviar")).Body!;
        var before = await Joined("PaReq__Seguidores", req.Id());
        foreach (var t in new[] { "fantasma", "equipo" })
        {
            var res = await admin.TransitionAsync("PaReq", sent, t);
            Assert.True(res.Status == 400, $"{t}: {res}");
            Assert.Contains("no existe", res.Text);
        }
        Assert.Equal(before, await Joined("PaReq__Seguidores", req.Id()));
        // La versión no cambió: la siguiente transición con la misma versión pasa.
        Assert.Equal(200, (await admin.TransitionAsync("PaReq", sent, "dejar")).Status);
    }

    [Fact]
    public async Task Una_transicion_que_agrega_y_luego_falla_una_regla_no_deja_la_lista_cambiada()
    {
        using var admin = await host.AdminAsync();
        var req = await admin.CreateAsync("PaReq", new { Centro = Id("norte") }); // sin nota: «NotaAlEnviar» falla al enviar
        var res = await admin.TransitionAsync("PaReq", req, "enviar");
        Assert.True(res.Status == 400, res.Text);
        Assert.Contains("Escribe una nota", res.Text);
        Assert.Equal("", await Joined("PaReq__Seguidores", req.Id()));
        Assert.Equal("Borrador", await host.ScalarAsync<string>($"SELECT Estado FROM app.PaReq WHERE Id = {req.Id()}"));
    }

    [Fact]
    public async Task Dos_transiciones_con_la_misma_version_una_gana_y_la_otra_recibe_409()
    {
        using var admin = await host.AdminAsync();
        using var lector = await host.LoginAsync(Lector, Pwd);
        var req = await admin.CreateAsync("PaReq", new { Nota = "Carrera", Centro = Id("norte") });
        var sent = (await admin.TransitionAsync("PaReq", req, "enviar")).Body!;
        var results = await Concurrently(2, i => (i == 0 ? admin : lector).TransitionAsync("PaReq", sent, i == 0 ? "dejar" : "seguir"));
        Statuses(results, 200, 409);
        var winner = results.Single(r => r.Status == 200).Body!;
        Assert.Equal(Ids(winner.Arr("Seguidores").Nodes().Select(n => n.GetValue<int>()).ToArray()), await Joined("PaReq__Seguidores", req.Id()));
    }

    [Fact]
    public async Task Diez_automatizaciones_a_la_vez_agregan_diez_valores_distintos_a_la_misma_lista()
    {
        using var admin = await host.AdminAsync();
        var proyecto = await admin.CreateAsync("PaProyecto", new { Nombre = "Carrera 051" });
        var etiquetas = new List<int>();
        for (var i = 0; i < 10; i++) etiquetas.Add((await admin.CreateAsync("PaEtiqueta", new { Nombre = $"E{i}" })).Id());
        var results = await Concurrently(10, i => admin.PostAsync("/api/data/PaTarea", new { Proyecto = proyecto.Id(), Etiqueta = etiquetas[i] }));
        Assert.All(results, r => Assert.True(r.Status == 201, r.Text));
        // Ninguna se pierde: cada automatización bloquea el proyecto antes de leer la lista.
        Assert.Equal(Ids([.. etiquetas]), await Joined("PaProyecto__Etiquetas", proyecto.Id()));
        // Quitar también desde una automatización; quitar lo que no está no cambia nada.
        Assert.Equal(201, (await admin.PostAsync("/api/data/PaTarea", new { Proyecto = proyecto.Id(), Etiqueta = etiquetas[0], Quitar = true })).Status);
        Assert.Equal(201, (await admin.PostAsync("/api/data/PaTarea", new { Proyecto = proyecto.Id(), Etiqueta = etiquetas[0], Quitar = true })).Status);
        Assert.Equal(Ids([.. etiquetas.Skip(1)]), await Joined("PaProyecto__Etiquetas", proyecto.Id()));
    }

    [Fact]
    public async Task Agregar_el_valor_101_rechaza_la_operacion_completa()
    {
        using var admin = await host.AdminAsync();
        var proyecto = await admin.CreateAsync("PaProyecto", new { Nombre = "Tope 051" });
        await host.ExecAsync("INSERT INTO app.PaEtiqueta (Nombre, CreatedBy) SELECT CONCAT('T', n), 1 FROM (SELECT TOP 101 ROW_NUMBER() OVER (ORDER BY (SELECT 1)) n FROM sys.all_objects) x");
        var ids = new List<int>();
        var csv = await host.ScalarAsync<string>("SELECT STRING_AGG(CAST(Id AS varchar(20)), ',') FROM app.PaEtiqueta WHERE Nombre LIKE 'T%'");
        ids.AddRange(csv!.Split(',').Select(int.Parse).Order().Take(101));
        var row = await admin.RecordAsync("PaProyecto", proyecto.Id());
        Assert.Equal(200, (await admin.PutAsync($"/api/data/PaProyecto/{proyecto.Id()}", new { Etiquetas = ids.Take(100), _rowVersion = row.Str("_rowVersion") })).Status);
        var tareas = await host.ScalarAsync<int>("SELECT COUNT(*) FROM app.PaTarea");
        var res = await admin.PostAsync("/api/data/PaTarea", new { Proyecto = proyecto.Id(), Etiqueta = ids[100] });
        Assert.True(res.Status == 400, res.Text);
        Assert.Contains("máximo 100", res.Text);
        Assert.Equal(tareas, await host.ScalarAsync<int>("SELECT COUNT(*) FROM app.PaTarea"));
        Assert.Equal(100, (await Joined("PaProyecto__Etiquetas", proyecto.Id())).Split(',').Length);
    }

    [Fact]
    public async Task Un_filtro_de_reporte_con_pertenencia_se_evalua_con_quien_lo_pide()
    {
        using var admin = await host.AdminAsync();
        var req = await admin.CreateAsync("PaReq", new { Nota = "Reporte", Centro = Id("sur") });
        using var otro = await host.LoginAsync(Otro, Pwd);
        var before = await otro.GetAsync("/api/reports/PaReq/Sigo");
        Assert.True(before.Status == 200, before.Text);
        Assert.DoesNotContain("Sur", before.Text);
        Assert.Equal(200, (await otro.TransitionAsync("PaReq", await otro.RecordAsync("PaReq", req.Id()), "seguir")).Status);
        Assert.Contains("Sur", (await otro.GetAsync("/api/reports/PaReq/Sigo")).Text);
    }

    [Fact]
    public async Task En_vivo_la_pertenencia_a_un_registro_fuera_de_alcance_no_cuenta()
    {
        // «deletableWhen» se evalúa con el alcance de quien elimina (045 F-02): la lista de una zona que no ve no cuenta.
        using var admin = await host.AdminAsync();
        using var lector = await host.LoginAsync(Lector, Pwd);
        var ajena = await admin.CreateAsync("PaZona", new { Nombre = "Zona del admin", Encargados = new[] { Id("lector") } });
        var visita = await admin.CreateAsync("PaVisita", new { Nota = "Ajena", Zona = ajena.Id() });
        var res = await lector.DeleteAsync($"/api/data/PaVisita/{visita.Id()}");
        Assert.True(res.Status == 409, res.Text);
        Assert.Contains("Solo un encargado de la zona", res.Text);

        // Control: en una zona que sí ve (propia), la misma condición deja eliminar.
        var propia = await admin.CreateAsync("PaZona", new { Nombre = "Zona del lector", Encargados = new[] { Id("lector") } });
        await host.ExecAsync($"UPDATE app.PaZona SET CreatedBy = {Id("lector")} WHERE Id = {propia.Id()}");
        var visible = await admin.CreateAsync("PaVisita", new { Nota = "Propia", Zona = propia.Id() });
        Assert.Equal(204, (await lector.DeleteAsync($"/api/data/PaVisita/{visible.Id()}")).Status);
        // Y el administrador, que ve la ajena pero no es encargado, tampoco elimina.
        Assert.Equal(409, (await admin.DeleteAsync($"/api/data/PaVisita/{visita.Id()}")).Status);
    }

    [Fact]
    public async Task Quien_no_puede_editar_Responsables_no_se_agrega_a_la_lista()
    {
        using var lector = await host.LoginAsync(Lector, Pwd);
        var sur = await lector.RecordAsync("PaCentro", Id("sur"));
        Assert.Null(sur["Responsables"]); // oculto por rol
        var put = await lector.PutAsync($"/api/data/PaCentro/{Id("sur")}", new { Nombre = "Sur", Responsables = new[] { Id("lector") }, _rowVersion = sur.Str("_rowVersion") });
        Assert.True(put.Status == 200, put.Text);
        Assert.Equal(Ids(Id("otro")), await Joined("PaCentro__Responsables", Id("sur")));
        Assert.Equal(404, (await lector.GetAsync($"/api/data/PaReq/{Id("reqSur")}")).Status);
    }

    [Fact]
    public async Task Una_condicion_con_pertenencia_sobre_un_campo_oculto_se_le_oculta_a_quien_no_lo_ve()
    {
        using var lector = await host.LoginAsync(Lector, Pwd);
        var model = await lector.GetAsync("/api/model/entities/PaReq");
        Assert.True(model.Status == 200, model.Text);
        // La regla y la condición nombran Centro.Responsables, que el lector no ve: llegan sin el texto de la expresión.
        Assert.DoesNotContain("Centro.Responsables", model.Text);
        Assert.Contains("Solo un responsable del centro autoriza", model.Text);
        using var admin = await host.AdminAsync();
        Assert.Contains("CURRENTUSER() IN Centro.Responsables", (await admin.GetAsync("/api/model/entities/PaReq")).Text);
    }

    // Regresión (revisión adversarial 051, F-01): «NOT X IN Lista» con la lista de un registro que no ve daba verdadero y dejaba
    // eliminar; ahora es desconocido y no elimina.
    [Fact]
    public async Task Con_NOT_una_lista_fuera_de_alcance_es_desconocida_y_no_elimina()
    {
        using var admin = await host.AdminAsync();
        using var lector = await host.LoginAsync(Lector, Pwd);
        var ajena = await admin.CreateAsync("PaZona", new { Nombre = "Zona ajena F-01", Encargados = new[] { Id("lector") } });
        var aviso = await admin.CreateAsync("PaAviso", new { Nota = "Aviso ajeno", Zona = ajena.Id() });
        var res = await lector.DeleteAsync($"/api/data/PaAviso/{aviso.Id()}");
        Assert.True(res.Status == 409, res.Text);
        // Control: en una zona que sí ve y donde no es encargado, elimina.
        var propia = await admin.CreateAsync("PaZona", new { Nombre = "Zona propia F-01", Encargados = Array.Empty<int>() });
        await host.ExecAsync($"UPDATE app.PaZona SET CreatedBy = {Id("lector")} WHERE Id = {propia.Id()}");
        var otro = await admin.CreateAsync("PaAviso", new { Nota = "Aviso propio", Zona = propia.Id() });
        Assert.Equal(204, (await lector.DeleteAsync($"/api/data/PaAviso/{otro.Id()}")).Status);
    }

    // Regresión (revisión adversarial 051, F-02): el acumulado en memoria leía las filas sin sus listas y «X IN Lista» daba falso.
    [Fact]
    public async Task Un_acumulado_en_memoria_con_pertenencia_suma_lo_que_cumple()
    {
        using var admin = await host.AdminAsync();
        var proyecto = await admin.CreateAsync("PaProyecto", new { Nombre = "Acumulado 051" });
        var e1 = (await admin.CreateAsync("PaEtiqueta", new { Nombre = "Acum uno" })).Id();
        var e2 = (await admin.CreateAsync("PaEtiqueta", new { Nombre = "Acum dos" })).Id();
        await admin.CreateAsync("PaGasto", new { Proyecto = proyecto.Id(), Importe = 10, Nota = "sí", Etiqueta = e1, Etiquetas = new[] { e1 } });
        await admin.CreateAsync("PaGasto", new { Proyecto = proyecto.Id(), Importe = 5, Nota = "no", Etiqueta = e1, Etiquetas = new[] { e2 } });
        Assert.Equal(10m, (await admin.RecordAsync("PaProyecto", proyecto.Id())).Dec("Total"));
    }
}
