using System.Text;
using System.Text.Json.Nodes;
using Erp.Api.Data;
using Erp.IntegrationTests.Harness;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 045 · Endurecimiento 7 (auditoría 8) sobre SQL real: las partidas detrás de un campo de partidas oculto no se ven en
/// reportes, exploración ni saltos (D-01); la condición para eliminar (D-04); el rastro de un choque de valor único (D-05) y de
/// exportar un reporte (D-06); y el tope de lo compartido (D-07).
/// </summary>
public class Hardening045Tests(Hardening045Tests.Host host) : IClassFixture<Hardening045Tests.Host>
{
    private const string Pwd = "frase larga del endurecimiento 045";
    private const string Role = "IT45 Lector";
    private const string Lector = "it45-lector";
    private const string Dueno = "it45-dueno";
    private const string Doc = "It45Doc";
    private const string Linea = "It45Linea";
    private const string Ref = "It45Ref";
    private const string Secreto = "monto-secreto-045";

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Ids = [];

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            var role = await admin.PostAsync("/api/admin/roles", new { name = Role, description = "Spec 045", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = Array.Empty<object>() });
            Assert.True(role.Status == 200, role.Text);
            Ids["role"] = role.Body!.Id();
            await admin.ApplyPlanAsync(new { description = "Spec 045 · endurecimiento 7", operations = new object[] {
                new { op = "createEntity", group = "Pruebas 045", name = Doc, label = "Documento 045", labelPlural = "Documentos 045", displayField = "Nombre", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true },
                    new { name = "Clave", label = "Clave", type = "text", unique = true },
                    new { name = "Notas", label = "Notas", type = "text" } } },
                new { op = "createEntity", name = Linea, label = "Partida 045", labelPlural = "Partidas 045", fields = new object[] {
                    new { name = "Doc", label = "Documento", type = "parent", referenceEntity = Doc },
                    new { name = "Concepto", label = "Concepto", type = "text", required = true },
                    new { name = "Monto", label = "Monto", type = "decimal", required = true },
                    new { name = "Dato", label = "Dato", type = "text" } } },
                new { op = "addField", entity = Doc, field = new { name = "Partidas", label = "Partidas", type = "lines", childEntity = Linea } },
                new { op = "createEntity", group = "Pruebas 045", name = Ref, label = "Referencia 045", labelPlural = "Referencias 045", fields = new object[] {
                    new { name = "Nombre", label = "Nombre", type = "text", required = true },
                    new { name = "Linea", label = "Partida", type = "reference", referenceEntity = Linea } } },
                // D-01: las partidas solo las ve el administrador; D-04: con notas no se elimina.
                new { op = "setSecurity", entity = Doc, security = new {
                    fields = new[] { new { field = "Partidas", read = new[] { "Administrador" } } },
                    records = new object[] { new { roles = new[] { Role }, scope = "own" } },
                    deletableWhen = "ISBLANK(Notas)", deleteMessage = "Tiene notas: no se elimina." } },
                new { op = "setReports", entity = Linea, reports = new object[] {
                    new { name = "PorConcepto", label = "Por concepto", groupBy = new[] { new { field = "Dato" } }, measures = new object[] { new { fn = "sum", field = "Monto" } } } } },
                new { op = "setReports", entity = Ref, reports = new object[] {
                    new { name = "PorMonto", label = "Por monto de la partida", groupBy = new[] { new { field = "Linea.Dato" } }, measures = new object[] { new { fn = "count" } } } } },
            } });
            var perms = new object[] {
                new { entity = Doc, canRead = true, canCreate = true, canUpdate = true, canDelete = true },
                new { entity = Ref, canRead = true, canCreate = false, canUpdate = false, canDelete = false } };
            Assert.Equal(200, (await admin.PutAsync($"/api/admin/roles/{Ids["role"]}", new { name = Role, description = "Spec 045", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = perms })).Status);
            foreach (var user in new[] { Lector, Dueno })
            {
                var u = await admin.PostAsync("/api/admin/users", new { username = user, displayName = user, password = Pwd, roleIds = new[] { Ids["role"] } });
                Assert.True(u.Status == 201, u.Text);
                Ids[user] = u.Body!.Id();
            }
            var doc = await admin.CreateAsync(Doc, new { Nombre = "Del administrador", Clave = "CLAVE-ADMIN-045",
                Partidas = new[] { new { Concepto = "Nómina", Monto = 1234.5m, Dato = Secreto } } });
            Ids["doc"] = doc.Id();
            Ids["linea"] = doc.Arr("Partidas")[0]!.Id();
            await admin.CreateAsync(Ref, new { Nombre = "Apunta a la partida", Linea = Ids["linea"] });
        }
    }

    private Task<Client> LectorAsync() => host.LoginAsync(Lector, Pwd);

    private static object Def(string entity, string group, string? measure = "Monto") => new
    {
        entity, groupBy = new[] { new { field = group } },
        measures = measure is null ? new object[] { new { fn = "count" } } : new object[] { new { fn = "sum", field = measure } },
    };

    private Task<int> AuditCountAsync(string action, string entity, int userId) =>
        host.ScalarAsync<int>($"SELECT COUNT(*) FROM meta.AuditLog WHERE Action = '{action}' AND EntityName = '{entity}' AND UserId = {userId}");

    // ---------------- H1: las partidas se ven solo si se ve su campo (D-01) ----------------

    [Fact]
    public async Task H1_CA1_una_partida_con_su_campo_oculto_no_se_ofrece_ni_se_explora()
    {
        using var admin = await host.AdminAsync();
        using var lector = await LectorAsync();
        // Control: el administrador la ve y la explora (con el dato que el lector no debe ver).
        Assert.Contains(Linea, (await admin.GetAsync("/api/my-reports/sources")).Text);
        var own = await admin.PostAsync("/api/my-reports/explore", new { definition = Def(Linea, "Dato") });
        Assert.True(own.Status == 200 && own.Text.Contains(Secreto), own.Text);

        // Regresión (auditoría 8, D-01): leer el documento bastaba; ahora también hay que ver su campo de partidas.
        var sources = await lector.GetAsync("/api/my-reports/sources");
        Assert.Contains(Doc, sources.Text);
        Assert.DoesNotContain(Linea, sources.Text);
        Assert.Equal(404, (await lector.GetAsync($"/api/my-reports/sources/{Linea}")).Status);
        foreach (var res in new[] {
            await lector.PostAsync("/api/my-reports/explore", new { definition = Def(Linea, "Dato") }),
            await lector.PostAsync("/api/my-reports/explore/export", new { definition = Def(Linea, "Concepto"), format = "csv" }),
            await lector.PostAsync("/api/my-reports", new { label = "Partidas 045", definition = Def(Linea, "Concepto") }) })
        {
            Assert.True(res.Status == 404 && res.Text.Contains("El catálogo no existe."), res.Text);
            Assert.DoesNotContain(Secreto, res.Text);
        }
        // Igual que un catálogo que no existe (no es un oráculo).
        var missing = await lector.PostAsync("/api/my-reports/explore", new { definition = Def("NoExiste045", "Dato") });
        Assert.True(missing.Status == 404 && missing.Body!.Str("title") == "El catálogo no existe.", missing.Text);
    }

    [Fact]
    public async Task H1_CA2_un_reporte_del_modelo_sobre_la_partida_no_se_lista_ni_corre()
    {
        using var admin = await host.AdminAsync();
        using var lector = await LectorAsync();
        Assert.Contains("PorConcepto", (await admin.GetAsync("/api/reports")).Text);
        Assert.Contains(Secreto, (await admin.GetAsync($"/api/reports/{Linea}/PorConcepto/export", new { format = "csv" })).Text);

        Assert.DoesNotContain("PorConcepto", (await lector.GetAsync("/api/reports")).Text);
        Assert.Equal(404, (await lector.GetAsync($"/api/reports/{Linea}/PorConcepto")).Status);
        var export = await lector.GetAsync($"/api/reports/{Linea}/PorConcepto/export", new { format = "csv" });
        Assert.True(export.Status == 404 && !export.Text.Contains(Secreto), export.Text);

        // Una tarjeta de un tablero personal compartido con el lector corre con él: sin ver las partidas, no corre.
        var board = await admin.PostAsync("/api/my-dashboards", new { label = "Tablero 045", widgets = new object[] {
            new { type = "table", entity = Linea, report = "PorConcepto" }, new { type = "table", entity = Ref, report = "PorMonto" } } });
        Assert.True(board.Status == 201, board.Text);
        var shared = await admin.PutAsync($"/api/my-dashboards/{board.Body!.Id()}/share", new { roles = new[] { Role }, version = board.Body!["version"]!.GetValue<int>() });
        Assert.True(shared.Status == 200, shared.Text);
        Assert.Contains(Secreto, (await admin.GetAsync($"/api/my-dashboards/{board.Body!.Id()}")).Text);
        var seen = await lector.GetAsync($"/api/my-dashboards/{board.Body!.Id()}");
        Assert.True(seen.Status == 200, seen.Text);
        Assert.DoesNotContain(Secreto, seen.Text);
        Assert.DoesNotContain("Por concepto", seen.Text);
    }

    [Fact]
    public async Task H1_CA3_un_salto_a_la_partida_solo_admite_su_campo_para_mostrar()
    {
        using var admin = await host.AdminAsync();
        using var lector = await LectorAsync();
        Assert.Contains(Secreto, (await admin.PostAsync("/api/my-reports/explore", new { definition = Def(Ref, "Linea.Dato", null) })).Text);

        // El campo para mostrar de la partida (lo que ofrece el buscador de la referencia) sí; lo demás, no.
        var display = await lector.PostAsync("/api/my-reports/explore", new { definition = Def(Ref, "Linea.Concepto", null) });
        Assert.True(display.Status == 200, display.Text);
        var hidden = await lector.PostAsync("/api/my-reports/explore", new { definition = Def(Ref, "Linea.Dato", null) });
        Assert.True(hidden.Status == 400 && hidden.Text.Contains("no existe o no puedes verlo") && !hidden.Text.Contains(Secreto), hidden.Text);
        // El reporte del modelo por ese salto no se le ofrece, y la lista de campos no baja por la partida.
        Assert.Equal(404, (await lector.GetAsync($"/api/reports/{Ref}/PorMonto")).Status);
        var fields = await lector.GetAsync($"/api/my-reports/sources/{Ref}");
        Assert.True(fields.Status == 200, fields.Text);
        Assert.DoesNotContain("Linea.Dato", fields.Text);
        Assert.DoesNotContain("Linea.Monto", fields.Text);
    }

    // ---------------- H3: condición para eliminar y valores únicos (D-04, D-05) ----------------

    [Fact]
    public async Task H3_CA1_deletableWhen_impide_eliminar_y_se_valida_al_compilar()
    {
        using var admin = await host.AdminAsync();
        using var lector = await LectorAsync();
        var conNotas = await lector.CreateAsync(Doc, new { Nombre = "Con notas", Notas = "no borrar" });
        var res = await lector.DeleteAsync($"/api/data/{Doc}/{conNotas.Id()}");
        Assert.True(res.Status == 409 && res.Text.Contains("Tiene notas: no se elimina."), res.Text);
        // Vale para todos, también para el superusuario.
        Assert.Equal(409, (await admin.DeleteAsync($"/api/data/{Doc}/{conNotas.Id()}")).Status);
        Assert.Equal(200, (await lector.GetAsync($"/api/data/{Doc}/{conNotas.Id()}")).Status);
        var sinNotas = await lector.CreateAsync(Doc, new { Nombre = "Sin notas" });
        Assert.Equal(204, (await lector.DeleteAsync($"/api/data/{Doc}/{sinNotas.Id()}")).Status);

        // Al compilar: la expresión, el mensaje y que no sea de una partida.
        foreach (var (entity, security, error) in new (string, object, string)[] {
            (Doc, new { deletableWhen = "NoExiste = 1" }, "deletableWhen"),
            (Doc, new { deletableWhen = "ISBLANK(Notas)", deleteMessage = new string('x', 301) }, "deleteMessage"),
            (Doc, new { deleteMessage = "Sin condición" }, "deleteMessage"),
            (Linea, new { deletableWhen = "ISBLANK(Dato)" }, "es una partida") })
        {
            var plan = await admin.PostAsync("/api/model/plan/preview", new { description = "045", operations = new[] { new { op = "setSecurity", entity, security } } });
            Assert.True(plan.Text.Contains(error), plan.Text);
            Assert.False(plan.Body?["valid"]?.GetValue<bool>() ?? false, plan.Text);
        }
    }

    [Fact]
    public async Task H3_CA3_un_choque_de_valor_unico_deja_rastro_sin_el_valor()
    {
        using var lector = await LectorAsync();
        var before = await AuditCountAsync("data.unique", Doc, host.Ids[Lector]);
        // El documento del administrador está fuera del alcance del lector («propios»); la respuesta es inevitable (único por
        // empresa), pero ahora deja rastro.
        var dup = await lector.PostAsync($"/api/data/{Doc}", new { Nombre = "Probando", Clave = "CLAVE-ADMIN-045" });
        Assert.True(dup.Status == 400 && dup.Text.Contains("Ya existe"), dup.Text);
        var csv = Encoding.UTF8.GetBytes("Nombre;Clave\nUno;CLAVE-ADMIN-045\nDos;OTRA-045\n");
        var preview = await lector.PostBytesAsync($"/api/data/{Doc}/import?dryRun=true", csv);
        Assert.True(preview.Status == 200 && preview.Text.Contains("Ya existe"), preview.Text);
        Assert.Equal(before + 2, await AuditCountAsync("data.unique", Doc, host.Ids[Lector]));
        var after = await host.ScalarAsync<string>($"SELECT STRING_AGG(AfterJson, ' ') FROM meta.AuditLog WHERE Action = 'data.unique' AND UserId = {host.Ids[Lector]}");
        Assert.Contains("Clave", after);
        Assert.DoesNotContain("CLAVE-ADMIN", after);
    }

    // ---------------- H4: exportar deja rastro y lo compartido tiene tope (D-06, D-07) ----------------

    [Fact]
    public async Task H4_CA1_exportar_un_reporte_o_una_exploracion_deja_bitacora()
    {
        using var lector = await LectorAsync();
        var before = await AuditCountAsync("data.export", Ref, host.Ids[Lector]);
        var docBefore = await AuditCountAsync("data.export", Doc, host.Ids[Lector]);
        var csv = await lector.PostAsync("/api/my-reports/explore/export", new { definition = Def(Ref, "Nombre", null), format = "csv" });
        Assert.True(csv.Status == 200, csv.Text);
        var docs = await lector.PostAsync("/api/my-reports/explore/export", new { definition = Def(Doc, "Nombre", null), format = "xlsx" });
        Assert.True(docs.Status == 200, docs.Text);
        Assert.Equal(before + 1, await AuditCountAsync("data.export", Ref, host.Ids[Lector]));
        Assert.Equal(docBefore + 1, await AuditCountAsync("data.export", Doc, host.Ids[Lector]));

        // El reporte del modelo, igual (el del administrador sobre la partida va bajo el documento: el catálogo raíz).
        using var admin = await host.AdminAsync();
        var adminId = await host.ScalarAsync<int>("SELECT Id FROM meta.[User] WHERE Username = 'admin'");
        var rootBefore = await AuditCountAsync("data.export", Doc, adminId);
        Assert.Equal(200, (await admin.GetAsync($"/api/reports/{Linea}/PorConcepto/export", new { format = "csv" })).Status);
        Assert.Equal(rootBefore + 1, await AuditCountAsync("data.export", Doc, adminId));
        var trail = await host.ScalarAsync<string>($"SELECT TOP 1 AfterJson FROM meta.AuditLog WHERE Action = 'data.export' AND UserId = {adminId} ORDER BY Id DESC");
        Assert.Contains("PorConcepto", trail);
        Assert.DoesNotContain(Secreto, trail);
    }

    [Fact]
    public async Task H4_CA2_lo_compartido_se_lista_con_tope()
    {
        using var dueno = await host.LoginAsync(Dueno, Pwd);
        using var lector = await LectorAsync();
        var report = await dueno.PostAsync("/api/my-reports", new { label = "Compartido 045 base", definition = Def(Doc, "Nombre", null) });
        Assert.True(report.Status == 201, report.Text);
        var board = await dueno.PostAsync("/api/my-dashboards", new { label = "Compartido 045 base", widgets = new object[] { new { type = "table", personalReport = report.Body!.Id() } } });
        Assert.True(board.Status == 201, board.Text);
        // 120 más de cada uno, compartidos con el rol del lector (por SQL: el tope por persona de la 041 no deja crearlos por la API).
        const int tope = 100; // PersonalSharing.MaxListed (literal: la prueba también corre contra el código anterior)
        var extra = tope + 20;
        await host.ExecAsync($"""
            ;WITH n AS (SELECT TOP ({extra}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects)
            INSERT meta.PersonalReport (UserId, EntityName, Label, DefinitionJson)
            SELECT p.UserId, p.EntityName, CONCAT('Compartido 045 #', n.i), p.DefinitionJson FROM meta.PersonalReport p CROSS JOIN n WHERE p.Id = {report.Body!.Id()};
            ;WITH n AS (SELECT TOP ({extra}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects)
            INSERT meta.PersonalDashboard (UserId, Label, DefinitionJson)
            SELECT d.UserId, CONCAT('Compartido 045 #', n.i), d.DefinitionJson FROM meta.PersonalDashboard d CROSS JOIN n WHERE d.Id = {board.Body!.Id()};
            INSERT meta.PersonalReportShare (ReportId, RoleId) SELECT Id, {host.Ids["role"]} FROM meta.PersonalReport WHERE UserId = {host.Ids[Dueno]};
            INSERT meta.PersonalDashboardShare (DashboardId, RoleId) SELECT Id, {host.Ids["role"]} FROM meta.PersonalDashboard WHERE UserId = {host.Ids[Dueno]};
            """);
        // Regresión (auditoría 8, D-07): se devolvían (y validaban) todos.
        var reports = await lector.GetAsync("/api/my-reports/shared");
        Assert.True(reports.Status == 200, reports.Text);
        Assert.Equal(tope, reports.Array.Count);
        var boards = await lector.GetAsync("/api/my-dashboards");
        Assert.Equal(tope, boards.Body!.Arr("shared").Count);
    }
}
