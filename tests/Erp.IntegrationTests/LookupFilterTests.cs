using System.Text;
using System.Text.Json.Nodes;
using Erp.IntegrationTests.Harness;
using Microsoft.Data.SqlClient;
using static Erp.IntegrationTests.Harness.Concurrency;

namespace Erp.IntegrationTests;

/// <summary>
/// Spec 052 · filtros de búsqueda compuestos sobre SQL real: el buscador con el borrador (constantes, «contiene», «es uno de»,
/// ruta de tres saltos con el documento en memoria, origen vacío, alcance, campo oculto) y la misma regla al guardar por POST, PUT,
/// importación, transición y automatizaciones; conservar lo que tenía, cambiar el origen y el elegido bloqueado mientras otra
/// conexión lo desactiva. Ejemplos inventados.
/// </summary>
public class LookupFilterTests(LookupFilterTests.Host host) : IClassFixture<LookupFilterTests.Host>
{
    private const string Pwd = "frase larga de filtros 052";
    private const string Capturista = "it52-capturista";
    private const string Miron = "it52-miron";
    private const string Role = "Lf Capturista";
    private const string ReadOnlyRole = "Lf Solo lectura";

    public sealed class Host : ErpHost
    {
        public readonly Dictionary<string, int> Ids = [];

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            using var admin = await AdminAsync();
            foreach (var r in new[] { Role, ReadOnlyRole })
                Assert.Equal(200, (await admin.PostAsync("/api/admin/roles", new { name = r, description = "Spec 052", canManageModel = false,
                    canManageUsers = false, canViewAudit = false, permissions = Array.Empty<object>() })).Status);
            await admin.ApplyPlanAsync(new
            {
                description = "IT 052: filtros de búsqueda compuestos",
                operations = new object[]
                {
                    new { op = "createEntity", name = "LfCategoria", label = "Categoría 052", labelPlural = "Categorías 052", displayField = "Nombre",
                        fields = new object[] { new { name = "Nombre", label = "Nombre", type = "text", required = true } } },
                    new { op = "createEntity", name = "LfProveedor", label = "Proveedor 052", labelPlural = "Proveedores 052", displayField = "Nombre",
                        fields = new object[]
                        {
                            new { name = "Nombre", label = "Nombre", type = "text", required = true },
                            new { name = "Activo", label = "Activo", type = "boolean" },
                            new { name = "Region", label = "Región", type = "text" },
                            new { name = "Calificacion", label = "Calificación", type = "integer" },
                            new { name = "Categorias", label = "Categorías", type = "multireference", referenceEntity = "LfCategoria" },
                        } },
                    new { op = "createEntity", name = "LfProducto", label = "Producto 052", labelPlural = "Productos 052", displayField = "Nombre",
                        fields = new object[]
                        {
                            new { name = "Nombre", label = "Nombre", type = "text", required = true },
                            new { name = "Activo", label = "Activo", type = "boolean" },
                            new { name = "Categoria", label = "Categoría", type = "reference", referenceEntity = "LfCategoria" },
                        } },
                    new { op = "createEntity", name = "LfOrden", label = "Orden 052", labelPlural = "Órdenes 052", displayField = "Nota",
                        fields = new object[]
                        {
                            new { name = "Nota", label = "Nota", type = "text" },
                            new { name = "Categoria", label = "Categoría", type = "reference", referenceEntity = "LfCategoria" },
                            new { name = "Proveedor", label = "Proveedor", type = "reference", referenceEntity = "LfProveedor",
                                lookupFilter = new { conditions = new object[]
                                {
                                    new { field = "Activo", op = "eq", value = true },
                                    new { field = "Categorias", op = "contains", source = "Categoria" },
                                } } },
                            new { name = "Respaldo", label = "Respaldo", type = "reference", referenceEntity = "LfProveedor" },
                            new { name = "Nivel", label = "Nivel", type = "integer" },
                            // El origen (Nivel, que controla quien captura) contra un campo que su rol no ve: no se usa con él.
                            new { name = "Alterno", label = "Alterno", type = "reference", referenceEntity = "LfProveedor",
                                lookupFilter = new { conditions = new object[] { new { field = "Calificacion", op = "eq", source = "Nivel" } } } },
                        } },
                    new { op = "createEntity", name = "LfOrdenPartida", label = "Partida 052", labelPlural = "Partidas 052",
                        fields = new object[]
                        {
                            new { name = "Orden", label = "Orden", type = "parent", referenceEntity = "LfOrden" },
                            new { name = "Producto", label = "Producto", type = "reference", referenceEntity = "LfProducto",
                                lookupFilter = new { conditions = new object[]
                                {
                                    new { field = "Activo", op = "eq", value = true },
                                    new { field = "Categoria", op = "in", source = "Orden.Proveedor.Categorias" },
                                } } },
                            new { name = "Cantidad", label = "Cantidad", type = "decimal", precision = 18, scale = 2 },
                        } },
                    new { op = "addField", entity = "LfOrden", field = new { name = "Partidas", label = "Partidas", type = "lines", childEntity = "LfOrdenPartida" } },
                    new { op = "setWorkflow", entity = "LfOrden", workflow = new
                    {
                        initial = "Borrador",
                        states = new object[] { new { name = "Borrador", label = "Borrador", editable = true } },
                        transitions = new object[]
                        {
                            new { name = "usarRespaldo", label = "Usar respaldo", from = new[] { "Borrador" }, to = "Borrador",
                                set = new[] { new { field = "Proveedor", value = "Respaldo" } } },
                        },
                    } },
                    // Una automatización que asigna la referencia (update) y otra que crea una orden (create).
                    new { op = "createEntity", name = "LfEvento", label = "Evento 052", labelPlural = "Eventos 052",
                        fields = new object[]
                        {
                            new { name = "Orden", label = "Orden", type = "reference", referenceEntity = "LfOrden" },
                            new { name = "Proveedor", label = "Proveedor", type = "reference", referenceEntity = "LfProveedor" },
                            new { name = "Categoria", label = "Categoría", type = "reference", referenceEntity = "LfCategoria" },
                        } },
                    new { op = "setAutomations", entity = "LfEvento", automations = new object[]
                    {
                        new { name = "Asignar", label = "Asignar proveedor", on = "create", actions = new object[]
                        {
                            new { type = "update", target = "Orden", when = "NOT ISBLANK(Orden) AND NOT ISBLANK(Proveedor)", set = new[] { new { field = "Proveedor", value = "Proveedor" } } },
                            // Revisión adversarial 052 (F-06): cambia un origen (la categoría) sin asignar la referencia.
                            new { type = "update", target = "Orden", when = "NOT ISBLANK(Orden) AND ISBLANK(Proveedor)", set = new[] { new { field = "Categoria", value = "Categoria" } } },
                            new { type = "create", entity = "LfOrden", when = "ISBLANK(Orden)", set = new object[]
                            {
                                new { field = "Nota", value = "'Creada por un evento'" },
                                new { field = "Categoria", value = "Categoria" },
                                new { field = "Proveedor", value = "Proveedor" },
                            } },
                        } },
                    } },
                    new { op = "setSecurity", entity = "LfProveedor", security = new
                    {
                        fields = new[] { new { field = "Calificacion", read = new[] { "Administrador" } } },
                        records = new[] { new { roles = new[] { Role }, where = "Region = 'Norte'", access = "read" } },
                    } },
                },
            });
            foreach (var (r, perms) in new[]
            {
                (Role, new object[]
                {
                    new { entity = "LfOrden", canRead = true, canCreate = true, canUpdate = true, canDelete = false },
                    new { entity = "LfProveedor", canRead = true, canCreate = false, canUpdate = false, canDelete = false },
                    new { entity = "LfProducto", canRead = true, canCreate = false, canUpdate = false, canDelete = false },
                    new { entity = "LfCategoria", canRead = true, canCreate = false, canUpdate = false, canDelete = false },
                }),
                (ReadOnlyRole, new object[] { new { entity = "LfOrden", canRead = true, canCreate = false, canUpdate = false, canDelete = false } }),
            })
                Assert.Equal(200, (await admin.PutAsync($"/api/admin/roles/{await admin.RoleIdAsync(r)}", new { name = r, description = "Spec 052",
                    canManageModel = false, canManageUsers = false, canViewAudit = false, permissions = perms })).Status);
            foreach (var (name, r) in new[] { (Capturista, Role), (Miron, ReadOnlyRole) })
            {
                var u = await admin.PostAsync("/api/admin/users", new { username = name, displayName = $"Persona {name}", password = Pwd, roleIds = new[] { await admin.RoleIdAsync(r) } });
                Assert.True(u.Status == 201, u.Text);
            }
            Ids["papel"] = (await admin.CreateAsync("LfCategoria", new { Nombre = "Papelería" })).Id();
            Ids["limpieza"] = (await admin.CreateAsync("LfCategoria", new { Nombre = "Limpieza" })).Id();
            Ids["norte"] = (await admin.CreateAsync("LfProveedor", new { Nombre = "Papeles del Norte", Activo = true, Region = "Norte", Calificacion = 3, Categorias = new[] { Ids["papel"] } })).Id();
            Ids["mixto"] = (await admin.CreateAsync("LfProveedor", new { Nombre = "Mixto del Norte", Activo = true, Region = "Norte", Calificacion = 5, Categorias = new[] { Ids["papel"], Ids["limpieza"] } })).Id();
            Ids["inactivo"] = (await admin.CreateAsync("LfProveedor", new { Nombre = "Papeles Inactivos", Activo = false, Region = "Norte", Categorias = new[] { Ids["papel"] } })).Id();
            Ids["sur"] = (await admin.CreateAsync("LfProveedor", new { Nombre = "Papeles del Sur", Activo = true, Region = "Sur", Categorias = new[] { Ids["papel"] } })).Id();
            Ids["hojas"] = (await admin.CreateAsync("LfProducto", new { Nombre = "Hojas", Activo = true, Categoria = Ids["papel"] })).Id();
            Ids["cloro"] = (await admin.CreateAsync("LfProducto", new { Nombre = "Cloro", Activo = true, Categoria = Ids["limpieza"] })).Id();
            Ids["lapices"] = (await admin.CreateAsync("LfProducto", new { Nombre = "Lápices", Activo = true, Categoria = Ids["papel"] })).Id();
            Ids["sur2"] = (await admin.CreateAsync("LfProveedor", new { Nombre = "Todo del Sur", Activo = true, Region = "Sur", Categorias = new[] { Ids["papel"], Ids["limpieza"] } })).Id();
            Ids["sobres"] = (await admin.CreateAsync("LfProducto", new { Nombre = "Sobres viejos", Activo = false, Categoria = Ids["papel"] })).Id();
        }
    }

    private int Id(string key) => host.Ids[key];

    private static List<string?> Labels(Res r)
    {
        Assert.True(r.Status == 200, r.Text);
        return r.Array.Nodes().Select(n => n.Str("label")).ToList();
    }

    private async Task<int> CountAsync(string table, string where) => await host.ScalarAsync<int>($"SELECT COUNT(*) FROM app.[{table}] WHERE {where}");

    [Fact]
    public async Task El_buscador_resuelve_constantes_y_contiene_con_el_borrador()
    {
        using var cap = await host.LoginAsync(Capturista, Pwd);
        var papel = Labels(await cap.PostAsync("/api/data/LfOrden/lookup/Proveedor", new { values = new { Categoria = Id("papel") } }));
        // Activos, que surten Papelería y a su alcance (el del Sur no lo ve; el inactivo no cumple).
        Assert.Equal(["Mixto del Norte", "Papeles del Norte"], papel);
        Assert.Equal(["Mixto del Norte"], Labels(await cap.PostAsync("/api/data/LfOrden/lookup/Proveedor", new { values = new { Categoria = Id("limpieza") } })));
        Assert.Equal(["Mixto del Norte"], Labels(await cap.PostAsync("/api/data/LfOrden/lookup/Proveedor", new { search = "Mix", values = new { Categoria = Id("papel") } })));
        // Sin el origen no se ofrece nada (spec 005).
        Assert.Empty(Labels(await cap.PostAsync("/api/data/LfOrden/lookup/Proveedor", new { values = new { } })));
        // El administrador (sin permisos por registro) sí ve el del Sur, pero tampoco el inactivo.
        using var admin = await host.AdminAsync();
        var all = Labels(await admin.PostAsync("/api/data/LfOrden/lookup/Proveedor", new { values = new { Categoria = Id("papel") } }));
        Assert.Contains("Papeles del Sur", all);
        Assert.DoesNotContain("Papeles Inactivos", all);
        // Un campo sin filtro responde como el buscador de siempre.
        Assert.Contains("Papeles Inactivos", Labels(await admin.PostAsync("/api/data/LfOrden/lookup/Respaldo", new { values = new { } })));
    }

    [Fact]
    public async Task En_una_partida_la_ruta_de_tres_saltos_lee_el_documento_en_memoria()
    {
        using var cap = await host.LoginAsync(Capturista, Pwd);
        var body = new { values = new { Categoria = Id("papel"), Proveedor = Id("norte"), Partidas = new object[] { new { Cantidad = 1 } } }, line = 0 };
        Assert.Equal(["Hojas", "Lápices"], Labels(await cap.PostAsync("/api/data/LfOrden/lookup/Partidas.Producto", body)));
        var mixto = new { values = new { Categoria = Id("papel"), Proveedor = Id("mixto"), Partidas = new object[] { new { Cantidad = 1 } } }, line = 0 };
        Assert.Equal(["Cloro", "Hojas", "Lápices"], Labels(await cap.PostAsync("/api/data/LfOrden/lookup/Partidas.Producto", mixto)));
        // Sin proveedor en el documento: nada. Una partida que no existe: 400.
        Assert.Empty(Labels(await cap.PostAsync("/api/data/LfOrden/lookup/Partidas.Producto", new { values = new { Partidas = new object[] { new { } } }, line = 0 })));
        Assert.Equal(400, (await cap.PostAsync("/api/data/LfOrden/lookup/Partidas.Producto", new { values = new { Partidas = Array.Empty<object>() }, line = 0 })).Status);
        Assert.Equal(400, (await cap.PostAsync("/api/data/LfOrden/lookup/Nota", new { values = new { } })).Status);
        Assert.Equal(400, (await cap.PostAsync("/api/data/LfOrden/lookup/Partidas.Nada", new { values = new { } })).Status);
    }

    [Fact]
    public async Task Una_ruta_por_un_registro_fuera_de_alcance_no_ofrece_nada()
    {
        using var admin = await host.AdminAsync();
        var orden = await admin.CreateAsync("LfOrden", new { Nota = "Del sur", Categoria = Id("papel"), Proveedor = Id("sur") });
        using var cap = await host.LoginAsync(Capturista, Pwd);
        // Lo que manda el borrador no lo cambia: elegir al del Sur no se acepta (006) y queda el guardado, que no ve.
        Assert.Empty(Labels(await cap.PostAsync("/api/data/LfOrden/lookup/Partidas.Producto",
            new { id = orden.Id(), values = new { Proveedor = Id("sur"), Partidas = new object[] { new { } } }, line = 0 })));
        // Y al guardar una partida nueva con su alcance tampoco se acepta.
        var row = await admin.RecordAsync("LfOrden", orden.Id());
        var put = await cap.PutAsync($"/api/data/LfOrden/{orden.Id()}", new { Partidas = new object[] { new { Producto = Id("hojas"), Cantidad = 1 } }, _rowVersion = row.Str("_rowVersion") });
        Assert.True(put.Status is 400 or 404, put.Text);
        Assert.Equal(0, await CountAsync("LfOrdenPartida", $"Orden = {orden.Id()}"));
    }

    [Fact]
    public async Task Un_origen_o_destino_oculto_no_es_oraculo()
    {
        using var cap = await host.LoginAsync(Capturista, Pwd);
        var hidden = await cap.PostAsync("/api/data/LfOrden/lookup/Alterno", new { values = new { Nivel = 3 } });
        Assert.True(hidden.Status == 400, hidden.Text);
        Assert.Contains("usa un campo que tu rol no ve", hidden.Text);
        // Al guardar tampoco: aceptar o rechazar diría la calificación.
        var save = await cap.PostAsync("/api/data/LfOrden", new { Nota = "Oráculo", Nivel = 3, Alterno = Id("norte") });
        Assert.True(save.Status == 400, save.Text);
        Assert.Contains("usa un campo que tu rol no ve", save.Text);
        Assert.Equal(0, await CountAsync("LfOrden", "Nota = N'Oráculo'"));
        // Quien sí ve el campo lo usa.
        using var admin = await host.AdminAsync();
        Assert.Equal(["Papeles del Norte"], Labels(await admin.PostAsync("/api/data/LfOrden/lookup/Alterno", new { values = new { Nivel = 3 } })));
        Assert.Equal(201, (await admin.PostAsync("/api/data/LfOrden", new { Nota = "Con calificación", Nivel = 3, Alterno = Id("norte") })).Status);
        var bad = await admin.PostAsync("/api/data/LfOrden", new { Nota = "Calificación distinta", Nivel = 4, Alterno = Id("norte") });
        Assert.True(bad.Status == 400, bad.Text);
    }

    [Fact]
    public async Task Sin_permiso_de_capturar_el_catalogo_el_buscador_responde_403()
    {
        using var miron = await host.LoginAsync(Miron, Pwd);
        Assert.Equal(403, (await miron.PostAsync("/api/data/LfOrden/lookup/Proveedor", new { values = new { Categoria = Id("papel") } })).Status);
        using var admin = await host.AdminAsync();
        var orden = await admin.CreateAsync("LfOrden", new { Nota = "Para editar" });
        Assert.Equal(403, (await miron.PostAsync("/api/data/LfOrden/lookup/Proveedor", new { id = orden.Id(), values = new { } })).Status);
    }

    [Fact]
    public async Task Guardar_por_POST_PUT_e_importacion_rechaza_lo_que_no_cumple()
    {
        using var cap = await host.LoginAsync(Capturista, Pwd);
        var inactive = await cap.PostAsync("/api/data/LfOrden", new { Nota = "Inactivo", Categoria = Id("papel"), Proveedor = Id("inactivo") });
        Assert.True(inactive.Status == 400, inactive.Text);
        Assert.Contains("no cumple el filtro del buscador", inactive["errors"]!["Proveedor"].Json());
        var otherCategory = await cap.PostAsync("/api/data/LfOrden", new { Nota = "Otra categoría", Categoria = Id("limpieza"), Proveedor = Id("norte") });
        Assert.True(otherCategory.Status == 400, otherCategory.Text);
        // La partida: el producto inactivo y el de una categoría que el proveedor no surte.
        var lines = await cap.PostAsync("/api/data/LfOrden", new { Nota = "Partidas", Categoria = Id("papel"), Proveedor = Id("norte"),
            Partidas = new object[] { new { Producto = Id("hojas"), Cantidad = 1 }, new { Producto = Id("sobres"), Cantidad = 1 }, new { Producto = Id("cloro"), Cantidad = 1 } } });
        Assert.True(lines.Status == 400, lines.Text);
        var errors = lines["errors"]!.AsObject();
        Assert.False(errors.ContainsKey("Partidas[0].Producto"), lines.Text);
        Assert.True(errors.ContainsKey("Partidas[1].Producto") && errors.ContainsKey("Partidas[2].Producto"), lines.Text);
        Assert.Equal(0, await CountAsync("LfOrden", "Nota IN (N'Inactivo', N'Otra categoría', N'Partidas')"));

        var ok = await cap.PostAsync("/api/data/LfOrden", new { Nota = "Correcta", Categoria = Id("papel"), Proveedor = Id("norte"),
            Partidas = new object[] { new { Producto = Id("hojas"), Cantidad = 2 } } });
        Assert.True(ok.Status == 201, ok.Text);
        var put = await cap.PutAsync($"/api/data/LfOrden/{ok.Body!.Id()}", new { Proveedor = Id("inactivo"), _rowVersion = ok.Body!.Str("_rowVersion") });
        Assert.True(put.Status == 400, put.Text);
        Assert.Equal(Id("norte"), await host.ScalarAsync<int>($"SELECT CAST(Proveedor AS int) FROM app.LfOrden WHERE Id = {ok.Body!.Id()}"));

        using var admin = await host.AdminAsync();
        var csv = Encoding.UTF8.GetBytes("Nota;Categoria;Proveedor\nImportada buena;Papelería;Papeles del Norte\nImportada mala;Papelería;Papeles Inactivos\n");
        var dry = await admin.PostBytesAsync("/api/data/LfOrden/import?dryRun=true", csv);
        Assert.True(dry.Status == 200, dry.Text);
        Assert.False(dry["valid"]!.GetValue<bool>(), dry.Text);
        Assert.Contains("no cumple el filtro del buscador", dry.Text);
        await admin.PostBytesAsync("/api/data/LfOrden/import?dryRun=false", csv);
        Assert.Equal(0, await CountAsync("LfOrden", "Nota = N'Importada mala'"));
    }

    [Fact]
    public async Task Conservar_la_referencia_que_ya_tenia_aunque_ya_no_cumpla_y_cambiar_el_origen_vuelve_a_revisar()
    {
        using var admin = await host.AdminAsync();
        var prov = (await admin.CreateAsync("LfProveedor", new { Nombre = "Temporal 052", Activo = true, Region = "Norte", Categorias = new[] { Id("papel") } })).Id();
        var orden = await admin.CreateAsync("LfOrden", new { Nota = "Conserva", Categoria = Id("papel"), Proveedor = prov,
            Partidas = new object[] { new { Producto = Id("hojas"), Cantidad = 1 } } });
        await host.ExecAsync($"UPDATE app.LfProveedor SET Activo = 0 WHERE Id = {prov}");
        using var cap = await host.LoginAsync(Capturista, Pwd);
        var row = await cap.RecordAsync("LfOrden", orden.Id());
        // Sin tocar el proveedor ni su origen, se conserva (como la 006).
        var kept = await cap.PutAsync($"/api/data/LfOrden/{orden.Id()}", new { Nota = "Conserva y cambia la nota", _rowVersion = row.Str("_rowVersion") });
        Assert.True(kept.Status == 200, kept.Text);
        // Cambiar la categoría (su origen) lo vuelve a revisar.
        var changed = await cap.PutAsync($"/api/data/LfOrden/{orden.Id()}", new { Categoria = Id("limpieza"), _rowVersion = kept.Body!.Str("_rowVersion") });
        Assert.True(changed.Status == 400, changed.Text);
        Assert.Contains("Proveedor", changed["errors"]!.Json());

        // En la partida: cambiar el proveedor del documento vuelve a revisar los productos que ya tenía.
        await host.ExecAsync($"UPDATE app.LfProveedor SET Activo = 1 WHERE Id = {prov}");
        var limpio = (await admin.CreateAsync("LfProveedor", new { Nombre = "Solo limpieza 052", Activo = true, Region = "Norte", Categorias = new[] { Id("limpieza") } })).Id();
        var o2 = await admin.CreateAsync("LfOrden", new { Nota = "Cambia proveedor", Categoria = Id("limpieza"), Proveedor = Id("mixto"),
            Partidas = new object[] { new { Producto = Id("hojas"), Cantidad = 1 } } });
        var swap = await cap.PutAsync($"/api/data/LfOrden/{o2.Id()}", new { Proveedor = limpio, _rowVersion = o2.Str("_rowVersion") });
        Assert.True(swap.Status == 400, swap.Text);
        Assert.True(swap["errors"]!.AsObject().ContainsKey("Partidas[0].Producto"), swap.Text);
    }

    [Fact]
    public async Task Transicion_y_automatizaciones_rechazan_una_referencia_que_no_cumple()
    {
        using var admin = await host.AdminAsync();
        var orden = await admin.CreateAsync("LfOrden", new { Nota = "Con respaldo", Categoria = Id("papel"), Proveedor = Id("norte"), Respaldo = Id("inactivo") });
        var t = await admin.TransitionAsync("LfOrden", orden, "usarRespaldo");
        Assert.True(t.Status == 400, t.Text);
        Assert.Contains("no cumple el filtro del buscador", t.Text);
        Assert.Equal(Id("norte"), await host.ScalarAsync<int>($"SELECT CAST(Proveedor AS int) FROM app.LfOrden WHERE Id = {orden.Id()}"));
        var row = await admin.RecordAsync("LfOrden", orden.Id());
        Assert.Equal(200, (await admin.PutAsync($"/api/data/LfOrden/{orden.Id()}", new { Respaldo = Id("mixto"), _rowVersion = row.Str("_rowVersion") })).Status);
        Assert.Equal(200, (await admin.TransitionAsync("LfOrden", await admin.RecordAsync("LfOrden", orden.Id()), "usarRespaldo")).Status);

        // update de una automatización: el evento no se crea y la orden no cambia.
        var bad = await admin.PostAsync("/api/data/LfEvento", new { Orden = orden.Id(), Proveedor = Id("inactivo") });
        Assert.True(bad.Status == 400, bad.Text);
        Assert.Contains("no cumple el filtro del buscador", bad.Text);
        Assert.Equal(0, await CountAsync("LfEvento", $"Orden = {orden.Id()}"));
        Assert.Equal(Id("mixto"), await host.ScalarAsync<int>($"SELECT CAST(Proveedor AS int) FROM app.LfOrden WHERE Id = {orden.Id()}"));
        Assert.Equal(201, (await admin.PostAsync("/api/data/LfEvento", new { Orden = orden.Id(), Proveedor = Id("norte") })).Status);

        // create de una automatización: tampoco crea la orden.
        var created = await admin.PostAsync("/api/data/LfEvento", new { Categoria = Id("limpieza"), Proveedor = Id("norte") });
        Assert.True(created.Status == 400, created.Text);
        Assert.Equal(0, await CountAsync("LfOrden", $"Nota = N'Creada por un evento' AND Categoria = {Id("limpieza")}"));
        Assert.Equal(201, (await admin.PostAsync("/api/data/LfEvento", new { Categoria = Id("limpieza"), Proveedor = Id("mixto") })).Status);
    }

    /// <summary>Otra conexión desactiva el proveedor sin confirmar; se confirma cuando las altas ya esperan su bloqueo.</summary>
    private async Task<(SqlConnection Cn, SqlTransaction Tx, int Spid)> DeactivateAsync(int proveedor) => await Task.Run(() =>
    {
        var cn = new SqlConnection(host.ConnectionString);
        cn.Open();
        var tx = cn.BeginTransaction();
        using (var cmd = new SqlCommand($"UPDATE app.LfProveedor SET Activo = 0 WHERE Id = {proveedor}", cn, tx)) cmd.ExecuteNonQuery();
        using var spid = new SqlCommand("SELECT @@SPID", cn, tx);
        return (cn, tx, Convert.ToInt32(spid.ExecuteScalar()));
    });

    [Fact]
    public async Task Si_otro_desactiva_el_elegido_mientras_se_guarda_se_espera_y_se_rechaza()
    {
        using var admin = await host.AdminAsync();
        var prov = (await admin.CreateAsync("LfProveedor", new { Nombre = "Concurrente 052", Activo = true, Region = "Norte", Categorias = new[] { Id("papel") } })).Id();
        using var cap = await host.LoginAsync(Capturista, Pwd);
        var (cn, tx, spid) = await DeactivateAsync(prov);
        Task<Res> save;
        try
        {
            save = cap.PostAsync("/api/data/LfOrden", new { Nota = "Concurrente", Categoria = Id("papel"), Proveedor = prov });
            await WaitForAsync(() => host.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = {spid}"),
                n => n > 0, "el guardado detenido por el proveedor bloqueado");
            await Task.Run(tx.Commit);
        }
        finally { tx.Dispose(); cn.Dispose(); }
        var res = await save;
        Assert.True(res.Status == 400, res.Text);
        Assert.Equal(0, await CountAsync("LfOrden", "Nota = N'Concurrente'"));
    }

    [Fact]
    public async Task Diez_altas_a_la_vez_mientras_se_desactiva_el_elegido()
    {
        using var admin = await host.AdminAsync();
        var prov = (await admin.CreateAsync("LfProveedor", new { Nombre = "Diez 052", Activo = true, Region = "Norte", Categorias = new[] { Id("papel") } })).Id();
        using var cap = await host.LoginAsync(Capturista, Pwd);
        var (cn, tx, spid) = await DeactivateAsync(prov);
        Task<Res[]> saves;
        try
        {
            saves = Concurrently(10, i => cap.PostAsync("/api/data/LfOrden", new { Nota = $"Diez {i}", Categoria = Id("papel"), Proveedor = prov }));
            await WaitForAsync(() => host.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = {spid}"),
                n => n > 0, "las altas detenidas por el proveedor bloqueado");
            await Task.Run(tx.Commit);
        }
        finally { tx.Dispose(); cn.Dispose(); }
        var results = await saves;
        // Ninguna queda guardada con un proveedor que ya no cumplía al confirmar; ninguna falla con 500.
        Assert.All(results, r => Assert.True(r.Status == 400, r.Text));
        Assert.Equal(0, await CountAsync("LfOrden", $"Proveedor = {prov}"));
    }

    [Fact]
    public async Task F01_dos_altas_que_eligen_los_mismos_registros_en_otro_orden_no_se_interbloquean()
    {
        using var admin = await host.AdminAsync();
        // Regresión (revisión adversarial 052, F-01): con UPDLOCK por referencia revisada (en el orden de los campos), Proveedor = A
        // y Alterno = B contra Proveedor = B y Alterno = A, a la vez, se interbloqueaban (1205 → 500). Sin partidas: altas
        // concurrentes con partidas tienen su propio interbloqueo al leerlas (R-052-5, ajeno a la spec).
        var results = await Concurrently(20, i => admin.PostAsync("/api/data/LfOrden", i % 2 == 0
            ? new { Nota = $"Sin interbloqueo {i}", Categoria = Id("papel"), Proveedor = Id("mixto"), Nivel = 3, Alterno = Id("norte") }
            : new { Nota = $"Sin interbloqueo {i}", Categoria = Id("papel"), Proveedor = Id("norte"), Nivel = 5, Alterno = Id("mixto") }));
        Assert.All(results, r => Assert.True(r.Status == 201, r.Text));
    }

    [Fact]
    public async Task F02_una_referencia_conservada_fuera_de_alcance_se_rechaza_igual_cumpla_o_no()
    {
        using var admin = await host.AdminAsync();
        // Las dos órdenes tienen un proveedor del Sur (fuera del alcance de quien captura); con Limpieza, una cumpliría y otra no.
        var noCumple = await admin.CreateAsync("LfOrden", new { Nota = "Sur solo papel", Categoria = Id("papel"), Proveedor = Id("sur") });
        var cumple = await admin.CreateAsync("LfOrden", new { Nota = "Sur todo", Categoria = Id("papel"), Proveedor = Id("sur2") });
        using var cap = await host.LoginAsync(Capturista, Pwd);
        var a = await cap.PutAsync($"/api/data/LfOrden/{noCumple.Id()}", new { Categoria = Id("limpieza"), _rowVersion = noCumple.Str("_rowVersion") });
        var b = await cap.PutAsync($"/api/data/LfOrden/{cumple.Id()}", new { Categoria = Id("limpieza"), _rowVersion = cumple.Str("_rowVersion") });
        // Regresión: antes la segunda daba 200 y la primera 400: un bit sobre las categorías de un proveedor que no ve.
        Assert.True(a.Status == 400 && b.Status == 400, $"{a} | {b}");
        Assert.Equal(a["errors"].Json(), b["errors"].Json());
        Assert.Contains("ya no está a tu alcance", b.Text);
    }

    [Fact]
    public async Task F05_una_partida_que_no_es_objeto_no_corre_el_indice()
    {
        // Revisión adversarial 052 (F-05, no se reproduce): ParseIntoAsync rechaza la partida inválida también en modo tolerante.
        using var cap = await host.LoginAsync(Capturista, Pwd);
        var res = await cap.PostAsync("/api/data/LfOrden/lookup/Partidas.Producto",
            new { values = new { Proveedor = Id("mixto"), Partidas = new object[] { 5, new { }, new { } } }, line = 1 });
        Assert.True(res.Status == 400, res.Text);
    }

    [Fact]
    public async Task F06_una_automatizacion_que_cambia_un_origen_no_revisa_la_referencia_que_no_asigna()
    {
        using var admin = await host.AdminAsync();
        var orden = await admin.CreateAsync("LfOrden", new { Nota = "Cambia categoría por evento", Categoria = Id("papel"), Proveedor = Id("norte") });
        var ev = await admin.PostAsync("/api/data/LfEvento", new { Orden = orden.Id(), Categoria = Id("limpieza") });
        Assert.True(ev.Status == 201, ev.Text);
        Assert.Equal(Id("limpieza"), await host.ScalarAsync<int>($"SELECT CAST(Categoria AS int) FROM app.LfOrden WHERE Id = {orden.Id()}"));
        Assert.Equal(Id("norte"), await host.ScalarAsync<int>($"SELECT CAST(Proveedor AS int) FROM app.LfOrden WHERE Id = {orden.Id()}"));
    }
}
