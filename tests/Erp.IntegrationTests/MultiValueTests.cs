using System.Text;
using System.Text.Json.Nodes;
using Erp.IntegrationTests.Harness;
using static Erp.IntegrationTests.Harness.Concurrency;
using static Erp.IntegrationTests.Harness.Invariant;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 050 · campos de varios valores sobre SQL real: la tabla de unión que crea el plan, alta y edición con listas de ids,
/// visibilidad al elegir (006), tope, obligatorio, bitácora, diez ediciones a la vez con la misma versión, la falla después de
/// escribir la tabla de unión, el campo oculto por rol, filtros, exportación e importación, y OData con <c>$expand</c>.
/// </summary>
public class MultiValueTests(MultiValueTests.Host host) : IClassFixture<MultiValueTests.Host>
{
    private const string Pwd = "frase larga de varios valores 050";
    private const string Lector = "it50-lector";
    private const string Role = "Mv Lector";

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Ids = [];

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            Assert.Equal(200, (await admin.PostAsync("/api/admin/roles", new { name = Role, description = "Spec 050", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = Array.Empty<object>() })).Status);
            await admin.ApplyPlanAsync(new
            {
                description = "IT 050: campos de varios valores",
                operations = new object[]
                {
                    new { op = "createEntity", name = "MvCategoria", label = "Categoría 050", labelPlural = "Categorías 050", displayField = "Nombre",
                        fields = new object[] { new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                    new { op = "createEntity", name = "MvProveedor", label = "Proveedor 050", labelPlural = "Proveedores 050", displayField = "Nombre",
                        fields = new object[]
                        {
                            new { name = "Nombre", label = "Nombre", type = "text", required = true },
                            new { name = "Dueno", label = "Dueño", type = "user" },
                            new { name = "Categorias", label = "Categorías", type = "multireference", referenceEntity = "MvCategoria" },
                            new { name = "Responsables", label = "Responsables", type = "multiuser" },
                            new { name = "Ocultas", label = "Ocultas", type = "multireference", referenceEntity = "MvCategoria" },
                        } },
                    new { op = "createEntity", name = "MvRequerido", label = "Requerido 050", labelPlural = "Requeridos 050",
                        fields = new object[]
                        {
                            new { name = "Nombre", label = "Nombre", type = "text" },
                            new { name = "Categorias", label = "Categorías", type = "multireference", referenceEntity = "MvCategoria", required = true },
                        } },
                    new { op = "setSecurity", entity = "MvCategoria", security = new { records = new[] { new { roles = new[] { Role }, scope = "own" } } } },
                    new { op = "setSecurity", entity = "MvProveedor", security = new
                    {
                        owner = "Dueno",
                        records = new[] { new { roles = new[] { Role }, scope = "own" } },
                        fields = new[] { new { field = "Ocultas", read = new[] { "Administrador" } } },
                    } },
                },
            });
            var role = await admin.RoleIdAsync(Role);
            Assert.Equal(200, (await admin.PutAsync($"/api/admin/roles/{role}", new { name = Role, description = "Spec 050", canManageModel = false,
                canManageUsers = false, canViewAudit = false, permissions = new object[]
                {
                    new { entity = "MvCategoria", canRead = true, canCreate = true, canUpdate = false, canDelete = false },
                    new { entity = "MvProveedor", canRead = true, canCreate = true, canUpdate = true, canDelete = false },
                } })).Status);
            var u = await admin.PostAsync("/api/admin/users", new { username = Lector, displayName = "Lector 050", password = Pwd, roleIds = new[] { role } });
            Assert.True(u.Status == 201, u.Text);
            Ids["lector"] = u.Body!.Id();
            Ids["admin"] = await ScalarAsync<int>("SELECT Id FROM meta.[User] WHERE Username = 'admin'");
            foreach (var (key, name) in new[] { ("alfa", "Alfa"), ("beta", "Beta"), ("gama", "Gama") })
                Ids[key] = (await admin.CreateAsync("MvCategoria", new { Nombre = name })).Id();
            using var lector = await LoginAsync(Lector, Pwd);
            Ids["propia"] = (await lector.CreateAsync("MvCategoria", new { Nombre = "Propia" })).Id();
            Ids["propia2"] = (await lector.CreateAsync("MvCategoria", new { Nombre = "Propia dos" })).Id();
        }
    }

    private int Id(string key) => host.Ids[key];

    /// <summary>Los ids de la tabla de unión, leídos directo de la base (no de la API que se prueba).</summary>
    private async Task<string> Joined(string table, int owner) =>
        await host.ScalarAsync<string>($"SELECT STRING_AGG(CAST(ValueId AS varchar(20)), ',') WITHIN GROUP (ORDER BY ValueId) FROM app.[{table}] WHERE OwnerId = {owner}") ?? "";

    private static string Ids(params int[] ids) => string.Join(",", ids.Order());

    private static int[] Values(JsonNode row, string field) => row.Arr(field).Nodes().Select(n => n.GetValue<int>()).ToArray();
    private static string?[] Labels(JsonNode row, string field) => row.Arr(field + "__label").Select(n => n?.GetValue<string>()).ToArray();

    [Fact]
    public async Task El_plan_crea_la_tabla_de_union_con_sus_llaves()
    {
        Assert.Equal(1, await host.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'app' AND t.name = 'MvProveedor__Categorias'"));
        Assert.Equal(2, await host.ScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID('app.MvProveedor__Categorias')"));
        // Sin columna en la tabla del catálogo.
        Assert.Null(await host.ScalarAsync<object>("SELECT COL_LENGTH('app.MvProveedor', 'Categorias')"));
        Assert.Equal("int", await host.ScalarAsync<string>("SELECT TYPE_NAME(system_type_id) FROM sys.columns WHERE object_id = OBJECT_ID('app.MvProveedor__Responsables') AND name = 'ValueId'"));
    }

    [Fact]
    public async Task Alta_y_lectura_con_ids_y_etiquetas_null_vacio_y_repetidos()
    {
        using var admin = await host.AdminAsync();
        var row = await admin.CreateAsync("MvProveedor", new { Nombre = "Surte dos", Categorias = new object[] { Id("gama"), Id("alfa"), Id("alfa"), $"{Id("gama")}" },
            Responsables = new[] { Id("lector") } });
        // Ordenados por etiqueta, sin repetidos; ids y etiquetas en la misma posición.
        Assert.Equal(new[] { Id("alfa"), Id("gama") }, Values(row, "Categorias"));
        Assert.Equal(new[] { "Alfa", "Gama" }, Labels(row, "Categorias"));
        Assert.Equal(new[] { "Lector 050" }, Labels(row, "Responsables"));
        Assert.Equal(Ids(Id("alfa"), Id("gama")), await Joined("MvProveedor__Categorias", row.Id()));

        var list = await admin.GetAsync("/api/data/MvProveedor", new { search = "Surte dos" });
        Assert.Equal(new[] { "Alfa", "Gama" }, Labels(list.Items.Nodes().Single(), "Categorias"));

        // null y [] dejan el campo vacío; un campo que no viene no se toca.
        var upd = await admin.PutAsync($"/api/data/MvProveedor/{row.Id()}", new { Nombre = "Surte dos", Categorias = (object?)null, _rowVersion = row.Str("_rowVersion") });
        Assert.True(upd.Status == 200, upd.Text);
        Assert.Empty(Values(upd.Body!, "Categorias"));
        Assert.Equal("", await Joined("MvProveedor__Categorias", row.Id()));
        Assert.Equal(Ids(Id("lector")), await Joined("MvProveedor__Responsables", row.Id()));
        var again = await admin.PutAsync($"/api/data/MvProveedor/{row.Id()}", new { Categorias = Array.Empty<int>(), Responsables = Array.Empty<int>(), _rowVersion = upd.Body!.Str("_rowVersion") });
        Assert.True(again.Status == 200, again.Text);
        Assert.Equal("", await Joined("MvProveedor__Responsables", row.Id()));
    }

    [Fact]
    public async Task Un_id_que_no_es_numero_una_lista_de_mas_de_100_o_un_objeto_400_sin_escribir()
    {
        using var admin = await host.AdminAsync();
        var before = await host.ScalarAsync<int>("SELECT COUNT(*) FROM app.MvProveedor");
        foreach (var bad in new object[] { new { a = 1 }, "3", new[] { "x" }, Enumerable.Range(1, 101).ToArray(), new[] { 999_999 } })
        {
            var res = await admin.PostAsync("/api/data/MvProveedor", new { Nombre = "Malo", Categorias = bad });
            Assert.True(res.Status == 400, res.Text);
            Assert.NotNull(res["errors"]?["Categorias"]);
        }
        Assert.Contains("máximo 100", (await admin.PostAsync("/api/data/MvProveedor", new { Nombre = "Malo", Categorias = Enumerable.Range(1, 101).ToArray() })).Text);
        // Un equipo no es una persona.
        var team = await admin.PostAsync("/api/admin/teams", new { name = "Equipo 050", unit = (string?)null, isActive = true, memberIds = Array.Empty<int>() });
        Assert.True(team.Status is 200 or 201, team.Text);
        Assert.Equal(400, (await admin.PostAsync("/api/data/MvProveedor", new { Nombre = "Malo", Responsables = new[] { team.Body!.Id() } })).Status);
        Assert.Equal(before, await host.ScalarAsync<int>("SELECT COUNT(*) FROM app.MvProveedor"));
    }

    [Fact]
    public async Task Un_campo_obligatorio_exige_al_menos_un_valor()
    {
        using var admin = await host.AdminAsync();
        foreach (var empty in new object?[] { null, Array.Empty<int>() })
        {
            var res = await admin.PostAsync("/api/data/MvRequerido", new { Nombre = "Sin", Categorias = empty });
            Assert.True(res.Status == 400, res.Text);
            Assert.NotNull(res["errors"]?["Categorias"]);
        }
        await admin.CreateAsync("MvRequerido", new { Nombre = "Con", Categorias = new[] { Id("beta") } });
    }

    [Fact]
    public async Task Elegir_un_registro_que_no_ve_400_conservar_uno_que_ya_tenia_si()
    {
        using var lector = await host.LoginAsync(Lector, Pwd);
        using var admin = await host.AdminAsync();
        // Lo que no ve «no existe» (spec 006: enlazar no da lectura).
        var res = await lector.PostAsync("/api/data/MvProveedor", new { Nombre = "Del lector", Dueno = Id("lector"), Categorias = new[] { Id("propia"), Id("alfa") } });
        Assert.True(res.Status == 400, res.Text);
        Assert.Contains("El registro seleccionado no existe.", res.Text);
        var row = await lector.CreateAsync("MvProveedor", new { Nombre = "Del lector", Dueno = Id("lector"), Categorias = new[] { Id("propia") } });

        // El administrador le agrega una que el lector no ve: la ve como «(sin acceso)», con su id (como una referencia).
        var full = await admin.RecordAsync("MvProveedor", row.Id());
        var put = await admin.PutAsync($"/api/data/MvProveedor/{row.Id()}", new { Categorias = new[] { Id("propia"), Id("beta") }, _rowVersion = full.Str("_rowVersion") });
        Assert.True(put.Status == 200, put.Text);
        var seen = await lector.RecordAsync("MvProveedor", row.Id());
        Assert.Contains("(sin acceso)", Labels(seen, "Categorias"));
        Assert.DoesNotContain("Beta", seen.Json());

        // Guardar lo que recibió conserva la que ya tenía; agregar otra visible también.
        var keep = await lector.PutAsync($"/api/data/MvProveedor/{row.Id()}", new { Nombre = "Del lector 2", Categorias = Values(seen, "Categorias").Append(Id("propia2")).ToArray(),
            _rowVersion = seen.Str("_rowVersion") });
        Assert.True(keep.Status == 200, keep.Text);
        Assert.Equal(Ids(Id("propia"), Id("propia2"), Id("beta")), await Joined("MvProveedor__Categorias", row.Id()));
    }

    [Fact]
    public async Task La_bitacora_guarda_la_lista_antes_y_despues()
    {
        using var admin = await host.AdminAsync();
        var row = await admin.CreateAsync("MvProveedor", new { Nombre = "Auditado", Categorias = new[] { Id("alfa") } });
        var put = await admin.PutAsync($"/api/data/MvProveedor/{row.Id()}", new { Categorias = new[] { Id("beta"), Id("gama") }, _rowVersion = row.Str("_rowVersion") });
        Assert.True(put.Status == 200, put.Text);
        var items = (await admin.GetAsync("/api/audit", new { entity = "MvProveedor", action = "update", pageSize = 200 })).Items.Nodes();
        var entry = items.First(i => i.Str("recordId") == row.Id().ToString());
        Assert.Equal(new[] { Id("alfa") }, entry["before"]!.Arr("Categorias").Nodes().Select(n => n.GetValue<int>()));
        Assert.Equal(new[] { Id("beta"), Id("gama") }, entry["after"]!.Arr("Categorias").Nodes().Select(n => n.GetValue<int>()));
    }

    [Fact]
    public async Task Diez_ediciones_a_la_vez_con_la_misma_version_una_gana_y_la_tabla_de_union_tiene_sus_valores()
    {
        using var admin = await host.AdminAsync();
        var row = await admin.CreateAsync("MvProveedor", new { Nombre = "Carrera", Categorias = new[] { Id("alfa") } });
        var options = new[] { new[] { Id("beta") }, new[] { Id("gama") }, new[] { Id("beta"), Id("gama") }, new[] { Id("alfa"), Id("gama") } };
        var results = await Concurrently(10, i => admin.PutAsync($"/api/data/MvProveedor/{row.Id()}",
            new { Nombre = $"Carrera {i}", Categorias = options[i % options.Length], _rowVersion = row.Str("_rowVersion") }));
        Statuses(results, 200, 409, 409, 409, 409, 409, 409, 409, 409, 409);
        var winners = results.Where(r => r.Status == 200).ToList();
        ExactlyOnce(winners.Count, "edición ganadora");
        // La tabla de unión es exactamente la lista ganadora (nunca una mezcla de dos ediciones).
        Assert.Equal(Ids(Values(winners[0].Body!, "Categorias")), await Joined("MvProveedor__Categorias", row.Id()));
        var i = int.Parse(winners[0].Body!.Str("Nombre")!["Carrera ".Length..]);
        Assert.Equal(Ids(options[i % options.Length]), await Joined("MvProveedor__Categorias", row.Id()));
    }

    [Fact]
    public async Task Si_el_registro_queda_fuera_del_alcance_de_escritura_la_tabla_de_union_no_cambia()
    {
        using var lector = await host.LoginAsync(Lector, Pwd);
        var row = await lector.CreateAsync("MvProveedor", new { Nombre = "Se escapa", Dueno = Id("lector"), Categorias = new[] { Id("propia") } });
        // Cambia sus categorías y se lo pasa al administrador: la tabla de unión ya se escribió cuando se descubre el 403.
        var res = await lector.PutAsync($"/api/data/MvProveedor/{row.Id()}", new { Dueno = Id("admin"), Categorias = new[] { Id("propia"), Id("propia2") },
            _rowVersion = row.Str("_rowVersion") });
        Assert.True(res.Status == 403, res.Text);
        Assert.Equal(Ids(Id("propia")), await Joined("MvProveedor__Categorias", row.Id()));
        Assert.Equal(Id("lector"), await host.ScalarAsync<int>($"SELECT Dueno FROM app.MvProveedor WHERE Id = {row.Id()}"));
    }

    [Fact]
    public async Task Un_campo_oculto_por_rol_no_se_lee_no_se_filtra_y_no_se_escribe()
    {
        using var admin = await host.AdminAsync();
        using var lector = await host.LoginAsync(Lector, Pwd);
        var row = await lector.CreateAsync("MvProveedor", new { Nombre = "Con ocultas", Dueno = Id("lector") });
        var full = await admin.RecordAsync("MvProveedor", row.Id());
        Assert.Equal(200, (await admin.PutAsync($"/api/data/MvProveedor/{row.Id()}", new { Ocultas = new[] { Id("gama") }, _rowVersion = full.Str("_rowVersion") })).Status);

        var seen = await lector.RecordAsync("MvProveedor", row.Id());
        Assert.Null(seen["Ocultas"]);
        Assert.Null(seen["Ocultas__label"]);
        var list = await lector.GetAsync("/api/data/MvProveedor", new { search = "Con ocultas" });
        Assert.DoesNotContain("Ocultas", list.Text);
        Assert.Equal(400, (await lector.GetAsync("/api/data/MvProveedor", new { filters = $"[{{\"field\":\"Ocultas\",\"op\":\"contains\",\"value\":{Id("gama")}}}]" })).Status);
        // Escribirlo se ignora (conserva su valor).
        var put = await lector.PutAsync($"/api/data/MvProveedor/{row.Id()}", new { Ocultas = Array.Empty<int>(), Nombre = "Con ocultas 2", _rowVersion = seen.Str("_rowVersion") });
        Assert.True(put.Status == 200, put.Text);
        Assert.Equal(Ids(Id("gama")), await Joined("MvProveedor__Ocultas", row.Id()));
        // Tampoco en la exportación.
        Assert.DoesNotContain("Gama", (await lector.GetAsync("/api/data/MvProveedor/export", new { format = "csv", columns = "Nombre,Ocultas" })).Text);
    }

    [Fact]
    public async Task Los_filtros_contiene_alguno_vacio_y_con_valores()
    {
        using var admin = await host.AdminAsync();
        var a = (await admin.CreateAsync("MvProveedor", new { Nombre = "Filtro A", Categorias = new[] { Id("alfa") }, Responsables = new[] { Id("admin") } })).Id();
        var b = (await admin.CreateAsync("MvProveedor", new { Nombre = "Filtro B", Categorias = new[] { Id("beta"), Id("gama") } })).Id();
        var c = (await admin.CreateAsync("MvProveedor", new { Nombre = "Filtro C" })).Id();
        async Task<int[]> Find(string filters) =>
            (await admin.GetAsync("/api/data/MvProveedor", new { search = "Filtro", pageSize = 50, filters })).Items.Nodes().Select(n => n.Id()).Order().ToArray();
        Assert.Equal(new[] { b }, await Find($"[{{\"field\":\"Categorias\",\"op\":\"contains\",\"value\":{Id("gama")}}}]"));
        Assert.Equal(new[] { a, b }.Order(), await Find($"[{{\"field\":\"Categorias\",\"op\":\"in\",\"value\":[{Id("alfa")},{Id("beta")}]}}]"));
        Assert.Equal(new[] { c }, await Find("[{\"field\":\"Categorias\",\"op\":\"blank\"}]"));
        Assert.Equal(new[] { a, b }.Order(), await Find("[{\"field\":\"Categorias\",\"op\":\"notBlank\"}]"));
        Assert.Equal(new[] { a }, await Find("[{\"field\":\"Responsables\",\"op\":\"contains\",\"value\":\"$me\"}]"));
        Assert.Equal(400, (await admin.GetAsync("/api/data/MvProveedor", new { filters = "[{\"field\":\"Categorias\",\"op\":\"eq\",\"value\":1}]" })).Status);
    }

    [Fact]
    public async Task Exportar_y_volver_a_importar_conserva_los_valores_con_la_misma_visibilidad_que_la_api()
    {
        using var admin = await host.AdminAsync();
        await admin.CreateAsync("MvProveedor", new { Nombre = "Exportado 050", Categorias = new[] { Id("beta"), Id("alfa") } });
        var csv = await admin.GetAsync("/api/data/MvProveedor/export", new { format = "csv", columns = "Nombre,Categorias", search = "Exportado 050" });
        Assert.True(csv.Status == 200, csv.Text);
        Assert.Contains("Alfa; Beta", csv.Text);

        // Lo exportado se vuelve a importar con otro nombre; un valor que no existe se reporta por fila.
        var file = csv.Text.Replace("Exportado 050", "Importado 050") + "Importado mal 050,\"Alfa; Nadie\"\r\n";
        var res = await admin.PostBytesAsync("/api/data/MvProveedor/import?dryRun=false", Encoding.UTF8.GetBytes(file));
        Assert.True(res.Status == 400, res.Text); // todo o nada: ninguna fila se importa
        Assert.Contains("No existe «Nadie»", res.Text);
        Assert.Empty((await admin.GetAsync("/api/data/MvProveedor", new { search = "Importado" })).Items);
        var good = await admin.PostBytesAsync("/api/data/MvProveedor/import?dryRun=false", Encoding.UTF8.GetBytes(csv.Text.Replace("Exportado 050", "Importado 050")));
        Assert.True(good.Status == 200 && good["imported"]!.GetValue<int>() == 1, good.Text);
        var imported = (await admin.GetAsync("/api/data/MvProveedor", new { search = "Importado 050" })).Items.Nodes().Single();
        Assert.Equal(Ids(Id("alfa"), Id("beta")), Ids(Values(imported, "Categorias")));

        // El lector no puede enlazar por importación lo que no ve.
        using var lector = await host.LoginAsync(Lector, Pwd);
        var lfile = "Nombre,Dueno,Categorias\r\nDel lector importado,Lector 050,\"Propia; Beta\"\r\n";
        var lres = await lector.PostBytesAsync("/api/data/MvProveedor/import?dryRun=false", Encoding.UTF8.GetBytes(lfile));
        Assert.True(lres.Status == 400, lres.Text);
        Assert.Contains("No existe «Beta»", lres.Text);
    }

    [Fact]
    public async Task OData_expand_solo_trae_los_registros_que_el_lector_ve_y_no_filtra_por_el_campo()
    {
        using var admin = await host.AdminAsync();
        using var lector = await host.LoginAsync(Lector, Pwd);
        var row = await lector.CreateAsync("MvProveedor", new { Nombre = "OData 050", Dueno = Id("lector"), Categorias = new[] { Id("propia") } });
        var full = await admin.RecordAsync("MvProveedor", row.Id());
        Assert.Equal(200, (await admin.PutAsync($"/api/data/MvProveedor/{row.Id()}", new { Categorias = new[] { Id("propia"), Id("gama") }, _rowVersion = full.Str("_rowVersion") })).Status);

        var res = await lector.GetAsync($"/odata/MvProveedor({row.Id()})?$expand=Categorias");
        Assert.True(res.Status == 200, res.Text);
        Assert.Equal(new[] { "Propia" }, res.Body!.Arr("Categorias").Nodes().Select(n => n.Str("Nombre")));
        Assert.DoesNotContain("Gama", res.Text);
        var adminRes = await admin.GetAsync($"/odata/MvProveedor({row.Id()})?$expand=Categorias");
        Assert.Equal(new[] { "Gama", "Propia" }, adminRes.Body!.Arr("Categorias").Nodes().Select(n => n.Str("Nombre")!).Order());
        // Ni filtro, ni orden, ni $select sobre el campo; las personas no se publican.
        Assert.Equal(400, (await lector.GetAsync($"/odata/MvProveedor?$filter=Categorias eq {Id("propia")}")).Status);
        Assert.Equal(400, (await lector.GetAsync("/odata/MvProveedor?$orderby=Categorias")).Status);
        Assert.Equal(400, (await lector.GetAsync("/odata/MvProveedor?$select=Categorias")).Status);
        Assert.Equal(400, (await lector.GetAsync("/odata/MvProveedor?$expand=Responsables")).Status);
        var metadata = await lector.GetAsync("/odata/$metadata");
        Assert.Contains("NavigationProperty Name=\"Categorias\" Type=\"Collection(Erp.MvCategoria)\"", metadata.Text);
        Assert.DoesNotContain("Name=\"Ocultas\"", metadata.Text);
    }

    // Regresión (revisión adversarial 050, F-01): se podía eliminar un registro que es uno de los valores de otro, y su etiqueta
    // seguía saliendo en la API y no en OData.
    [Fact]
    public async Task No_se_elimina_un_registro_que_es_valor_de_un_campo_de_varios_valores()
    {
        using var admin = await host.AdminAsync();
        var cat = (await admin.CreateAsync("MvCategoria", new { Nombre = "En uso 050" })).Id();
        var row = await admin.CreateAsync("MvProveedor", new { Nombre = "Usa una", Categorias = new[] { cat } });
        var del = await admin.DeleteAsync($"/api/data/MvCategoria/{cat}");
        Assert.True(del.Status == 409, del.Text);
        Assert.Contains("está en uso en Proveedores 050 (Categorías)", del.Text);
        // Sin el registro que la usa (borrado), ya se puede.
        Assert.Equal(204, (await admin.DeleteAsync($"/api/data/MvProveedor/{row.Id()}")).Status);
        Assert.Equal(204, (await admin.DeleteAsync($"/api/data/MvCategoria/{cat}")).Status);
    }

    // Regresión (revisión adversarial 050, F-02): quien captura proveedores pero no lee el catálogo de categorías no podía usar el
    // buscador del campo (con una referencia simple sí).
    [Fact]
    public async Task Quien_captura_el_campo_usa_su_buscador_sin_leer_el_catalogo_destino()
    {
        using var admin = await host.AdminAsync();
        const string name = "Mv Capturista";
        var created = await admin.PostAsync("/api/admin/roles", new { name, description = "Spec 050", canManageModel = false, canManageUsers = false,
            canViewAudit = false, permissions = new[] { new { entity = "MvProveedor", canRead = true, canCreate = true, canUpdate = true, canDelete = false } } });
        Assert.True(created.Status == 200, created.Text);
        var u = await admin.PostAsync("/api/admin/users", new { username = "it50-capturista", displayName = "Capturista 050", password = Pwd,
            roleIds = new[] { await admin.RoleIdAsync(name) } });
        Assert.True(u.Status == 201, u.Text);
        using var cap = await host.LoginAsync("it50-capturista", Pwd);
        Assert.Equal(403, (await cap.GetAsync("/api/data/MvCategoria")).Status);
        var lookup = await cap.GetAsync("/api/data/MvCategoria/lookup", new { search = "Alfa" });
        Assert.True(lookup.Status == 200, lookup.Text);
        // Con su alcance: los permisos por registro de MvCategoria solo dan acceso al rol lector (006), así que no ve ninguna.
        Assert.Empty(lookup.Array);
    }

    [Fact]
    public async Task OData_expand_respeta_el_tope_de_expansion()
    {
        using var admin = await host.AdminAsync();
        await admin.CreateAsync("MvProveedor", new { Nombre = "Tope 050", Categorias = new[] { Id("alfa"), Id("beta") } });
        await using var sibling = await host.SiblingAsync(s => s["OData:MaxExpandedLines"] = "1");
        using var a = await sibling.LoginAsync("admin", host.AdminPassword);
        var res = await a.GetAsync("/odata/MvProveedor?$filter=Nombre eq 'Tope 050'&$expand=Categorias");
        Assert.True(res.Status == 400, res.Text);
        Assert.Contains("más de 1", res.Text);
    }
}
