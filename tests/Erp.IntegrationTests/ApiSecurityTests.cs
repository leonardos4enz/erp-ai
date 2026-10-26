using Erp.IntegrationTests.Harness;

namespace Erp.IntegrationTests;

/// <summary>
/// Pruebas de seguridad contra la API real (spec 001, plan.md › STRIDE). Antes en <c>web/e2e/api-security.spec.ts</c>
/// (movidas en la 030); cada prueba prepara sus propios datos y no depende del orden. La cabecera <c>Server</c> la quita
/// Kestrel, que TestServer no usa: esa aserción sigue en la suite E2E (<c>seguridad.spec.ts</c>).
/// </summary>
public class ApiSecurityTests(ApiSecurityTests.Host host) : IClassFixture<ApiSecurityTests.Host>
{
    private const string Entity = "ApiEmpaque";
    private const string UserPwd = "una frase larga de prueba 2026";

    private static object Plan(string name) => new { description = "Catálogo para pruebas de API", operations = new[] { new {
        op = "createEntity", name, label = "Empaque API", labelPlural = "Empaques API", icon = "package", fields = new object[] {
            new { name = "Clave", label = "Clave", type = "text", maxLength = 20, unique = true, required = true },
            new { name = "Nombre", label = "Nombre", type = "text", requiredForRoles = new[] { "Administrador" } },
            new { name = "Material", label = "Material", type = "choice", options = new[] { "Cartón", "Plástico", "Madera" } },
            new { name = "Capacidad", label = "Capacidad", type = "decimal", precision = 10, scale = 2 } } } } };

    public sealed class Host : ErpHost
    {
        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            await admin.ApplyPlanAsync(Plan(Entity));
            await admin.CreateAsync(Entity, new { Clave = "CJ-01", Nombre = "Caja chica", Material = "Cartón", Capacidad = 12.5 });
            foreach (var username in new[] { "api-capturista", "api-clave", "api-bloqueo" })
            {
                var res = await admin.PostAsync("/api/admin/users", new { username, password = UserPwd, displayName = username, roleIds = new[] { await admin.RoleIdAsync("Usuario") } });
                Assert.True(res.Status == 201, res.Text);
            }
        }
    }

    // ---------------- Autenticación y sesión ----------------

    [Fact]
    public async Task rechaza_peticiones_que_modifican_estado_sin_la_cabecera_anti_CSRF()
    {
        using var api = host.Anonymous(csrfHeader: false);
        Assert.Equal(403, (await api.PostAsync("/api/auth/login", new { username = "admin", password = host.AdminPassword })).Status);
    }

    [Fact]
    public async Task el_login_fallido_no_revela_si_el_usuario_existe()
    {
        using var api = host.Anonymous();
        var missing = await api.PostAsync("/api/auth/login", new { username = "no-existe", password = "una contraseña cualquiera" });
        var wrong = await api.PostAsync("/api/auth/login", new { username = "admin", password = "una contraseña incorrecta" });
        Assert.Equal(401, missing.Status);
        Assert.Equal(401, wrong.Status);
        Assert.Equal(missing["title"]!.GetValue<string>(), wrong["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task sin_sesion_la_API_responde_401()
    {
        using var api = host.Anonymous();
        Assert.Equal(401, (await api.GetAsync("/api/auth/me")).Status);
    }

    [Fact]
    public async Task la_cookie_de_sesion_es_HttpOnly_Secure_y_SameSite_Strict_con_cabeceras_de_endurecimiento()
    {
        using var api = host.Anonymous();
        var res = await api.PostAsync("/api/auth/login", new { username = "admin", password = host.AdminPassword });
        Assert.Equal(200, res.Status);
        var cookie = res.Headers["Set-Cookie"].ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("frame-ancestors 'none'", res.Headers["Content-Security-Policy"]);
        Assert.Equal("nosniff", res.Headers["X-Content-Type-Options"]);
        Assert.Contains("no-store", res.Headers["Cache-Control"]);
    }

    // ---------------- Planes de cambios ----------------

    [Fact]
    public async Task la_vista_previa_valida_y_aplicar_crea_el_catalogo()
    {
        using var api = await host.AdminAsync();
        var preview = await api.PostAsync("/api/model/plan/preview", Plan("ApiEmpaqueDos"));
        Assert.True(preview["valid"]!.GetValue<bool>());
        Assert.Contains("CREATE TABLE [app].[ApiEmpaqueDos]", preview["steps"]![0]!.Str("sql"));
        await api.ApplyPlanAsync(Plan("ApiEmpaqueDos"));
        Assert.Equal(200, (await api.GetAsync("/api/data/ApiEmpaqueDos")).Status);
    }

    [Fact]
    public async Task rechaza_inyeccion_SQL_en_nombres_de_campo()
    {
        using var api = await host.AdminAsync();
        var res = await api.PostAsync("/api/model/plan/apply", new { description = "x", operations = new[] {
            new { op = "addField", entity = Entity, field = new { name = "x] int; DROP TABLE meta.[User];--", label = "x", type = "text" } } } });
        Assert.Equal(400, res.Status);
    }

    [Fact]
    public async Task rechaza_tipos_de_operacion_desconocidos()
    {
        using var api = await host.AdminAsync();
        Assert.Equal(400, (await api.PostAsync("/api/model/plan/apply", new { description = "x", operations = new[] { new { op = "dropTable", entity = Entity } } })).Status);
    }

    // ---------------- Datos ----------------

    [Fact]
    public async Task aplica_la_regla_obligatorio_para_rol()
    {
        using var api = await host.AdminAsync();
        var res = await api.PostAsync($"/api/data/{Entity}", new { Clave = "CJ-OBL", Material = "Cartón" });
        Assert.Equal(400, res.Status);
        Assert.NotNull(res["errors"]!["Nombre"]);
    }

    [Fact]
    public async Task valida_unicos_opciones_y_campos_desconocidos()
    {
        using var api = await host.AdminAsync();
        var dup = await api.PostAsync($"/api/data/{Entity}", new { Clave = "CJ-01", Nombre = "Otra" });
        Assert.Equal(400, dup.Status);
        Assert.NotNull(dup["errors"]!["Clave"]);

        var bad = await api.PostAsync($"/api/data/{Entity}", new { Clave = "CJ-02", Nombre = "X", Material = "Acero", Hack = 1 });
        Assert.Equal(400, bad.Status);
        Assert.NotNull(bad["errors"]!["Material"]);
        Assert.NotNull(bad["errors"]!["Hack"]);
    }

    [Fact]
    public async Task neutraliza_inyeccion_en_orden_y_busqueda()
    {
        using var api = await host.AdminAsync();
        var evil = await api.GetAsync($"/api/data/{Entity}", new { sort = "Id;DROP TABLE x", search = "' OR 1=1--" });
        Assert.Equal(200, evil.Status);
        Assert.Equal(0, evil["total"]!.GetValue<int>());
        Assert.Equal(1, (await api.GetAsync($"/api/data/{Entity}", new { search = "chica" }))["total"]!.GetValue<int>());
        Assert.Equal(0, (await api.GetAsync($"/api/data/{Entity}", new { search = "%" }))["total"]!.GetValue<int>()); // el % es texto literal
    }

    [Fact]
    public async Task ordena_por_Id_en_los_dos_sentidos()
    {
        // Regresión (revisión exploratoria 022, F-01): sort=Id repetía la columna del desempate y respondía 500.
        using var api = await host.AdminAsync();
        await api.CreateAsync(Entity, new { Clave = "CJ-ORD", Nombre = "Para ordenar" });
        foreach (var dir in new[] { "asc", "desc" })
        {
            foreach (var path in new[] { "", "/export" })
                Assert.True((await api.GetAsync($"/api/data/{Entity}{path}", new { sort = "Id", dir })).Status == 200, $"{(path == "" ? "listado" : path)} {dir}");
            var ids = (await api.GetAsync($"/api/data/{Entity}", new { sort = "Id", dir })).Items.Nodes().Select(r => r.Id()).ToList();
            Assert.True(ids.Count > 1);
            Assert.Equal(dir == "asc" ? ids.Order() : ids.OrderDescending(), ids);
        }
    }

    [Fact]
    public async Task detecta_ediciones_concurrentes_rowversion()
    {
        using var api = await host.AdminAsync();
        var row = await api.CreateAsync(Entity, new { Clave = "CJ-RV", Nombre = "v1" });
        var first = await api.PutAsync($"/api/data/{Entity}/{row.Id()}", new { Nombre = "v2", _rowVersion = row.Str("_rowVersion") });
        var second = await api.PutAsync($"/api/data/{Entity}/{row.Id()}", new { Nombre = "v3", _rowVersion = row.Str("_rowVersion") });
        Assert.Equal(200, first.Status);
        Assert.Equal(409, second.Status);
    }

    [Fact]
    public async Task solo_expone_catalogos_registrados_en_metadatos()
    {
        using var api = await host.AdminAsync();
        Assert.Equal(404, (await api.GetAsync("/api/data/meta.User")).Status);
        Assert.Equal(404, (await api.GetAsync("/api/data/..%2Fmeta")).Status);
    }

    [Fact]
    public async Task el_borrado_es_logico_y_queda_auditado()
    {
        using var api = await host.AdminAsync();
        var created = await api.CreateAsync(Entity, new { Clave = "BORRAR", Nombre = "Temporal" });
        Assert.Equal(204, (await api.DeleteAsync($"/api/data/{Entity}/{created.Id()}")).Status);
        Assert.Equal(404, (await api.GetAsync($"/api/data/{Entity}/{created.Id()}")).Status);
        Assert.Equal(1, await host.ScalarAsync<int>($"SELECT COUNT(*) FROM app.[{Entity}] WHERE Id = {created.Id()} AND IsDeleted = 1"));
        var audit = await api.GetAsync("/api/audit", new { entity = Entity, action = "delete" });
        Assert.Contains(audit.Items.Nodes(), i => i.Str("recordId") == created.Id().ToString());
    }

    // ---------------- Autorización y bloqueo ----------------

    [Fact]
    public async Task un_usuario_sin_permisos_recibe_403_en_datos_modelo_usuarios_y_auditoria()
    {
        using var api = await host.LoginAsync("api-capturista", UserPwd);
        Assert.Equal(403, (await api.GetAsync($"/api/data/{Entity}")).Status);
        Assert.Equal(403, (await api.PostAsync("/api/model/plan/apply", Plan("ApiIntruso"))).Status);
        Assert.Equal(403, (await api.GetAsync("/api/admin/users")).Status);
        Assert.Equal(403, (await api.GetAsync("/api/audit")).Status);
        Assert.Null((await api.GetAsync("/api/auth/me"))["entities"]![Entity]);
    }

    [Fact]
    public async Task cambiar_la_contrasena_revoca_las_sesiones_abiertas()
    {
        using var admin = await host.AdminAsync();
        using var session = await host.LoginAsync("api-clave", UserPwd);
        var u = (await admin.GetAsync("/api/admin/users")).Array.Nodes().First(x => x.Str("username") == "api-clave");
        var res = await admin.PutAsync($"/api/admin/users/{u.Id()}", new { displayName = u.Str("displayName"), isActive = true,
            roleIds = u.Arr("roleIds").Nodes().Select(r => r.GetValue<int>()), password = "otra frase larga de prueba 2026" });
        Assert.Equal(200, res.Status);
        Assert.Equal(401, (await session.GetAsync("/api/auth/me")).Status);
    }

    [Fact]
    public async Task la_politica_rechaza_contrasenas_debiles_o_que_contienen_el_usuario()
    {
        using var admin = await host.AdminAsync();
        foreach (var password in new[] { "corta", "mi-clave-api-debil-2026" })
            Assert.Equal(400, (await admin.PostAsync("/api/admin/users", new { username = "api-debil", displayName = "x", password, roleIds = Array.Empty<int>() })).Status);
    }

    [Fact]
    public async Task tras_5_intentos_fallidos_la_cuenta_se_bloquea_aunque_la_contrasena_sea_correcta()
    {
        using var api = host.Anonymous();
        for (var i = 0; i < 5; i++) await api.PostAsync("/api/auth/login", new { username = "api-bloqueo", password = "incorrecta incorrecta" });
        Assert.Equal(401, (await api.PostAsync("/api/auth/login", new { username = "api-bloqueo", password = UserPwd })).Status);
    }

    // Spec 032 (STRIDE, caminos alternos e I): la pantalla nueva no distingue «bloqueada» porque el servidor tampoco lo hace.
    [Fact]
    public async Task la_cuenta_bloqueada_responde_igual_que_un_usuario_inexistente()
    {
        using var admin = await host.AdminAsync();
        Assert.Equal(201, (await admin.PostAsync("/api/admin/users", new { username = "api-bloqueo-032", password = UserPwd, displayName = "Bloqueo 032", roleIds = new[] { await admin.RoleIdAsync("Usuario") } })).Status);
        using var api = host.Anonymous();
        for (var i = 0; i < 5; i++) await api.PostAsync("/api/auth/login", new { username = "api-bloqueo-032", password = "incorrecta incorrecta" });
        var locked = await api.PostAsync("/api/auth/login", new { username = "api-bloqueo-032", password = UserPwd });
        var missing = await api.PostAsync("/api/auth/login", new { username = "no-existe-032", password = UserPwd });
        Assert.Equal(401, locked.Status);
        Assert.Equal(401, missing.Status);
        Assert.Equal(missing["title"]!.GetValue<string>(), locked["title"]!.GetValue<string>());
        Assert.Equal(missing["detail"]?.ToString(), locked["detail"]?.ToString());
    }

    // Spec 032 (STRIDE, límites): el 429 real del login (la pantalla lo muestra con un mensaje genérico). Hasta ahora solo
    // estaba probado el de otras políticas; la suite sube este límite para no bloquearse a sí misma.
    [Fact]
    public async Task el_login_responde_429_al_rebasar_el_limite_por_direccion_sin_decir_nada_del_usuario()
    {
        var limited = await host.SiblingAsync(s => s["Security:LoginAttemptsPerMinute"] = "3");
        using var api = limited.Anonymous();
        var statuses = new List<int>();
        for (var i = 0; i < 3; i++) statuses.Add((await api.PostAsync("/api/auth/login", new { username = "no-existe-429", password = "incorrecta incorrecta" })).Status);
        Assert.Equal([401, 401, 401], statuses);
        // Con la contraseña correcta también: el límite es por dirección, antes de mirar la cuenta.
        var over = await api.PostAsync("/api/auth/login", new { username = "api-capturista", password = UserPwd });
        Assert.Equal(429, over.Status);
        Assert.DoesNotContain("api-capturista", over.Text);
        Assert.False(over.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task no_permite_dejar_el_sistema_sin_administradores()
    {
        using var admin = await host.AdminAsync();
        var me = await admin.GetAsync("/api/auth/me");
        var res = await admin.PutAsync($"/api/admin/users/{me.Body!.Id()}", new { displayName = "Administrador", isActive = true, roleIds = Array.Empty<int>() });
        Assert.Equal(409, res.Status);
    }
}
