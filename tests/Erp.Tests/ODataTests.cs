using System.Text;
using Erp.Api.Infrastructure;
using Erp.Api.Integrations;
using Erp.Api.Metadata;
using Erp.Api.OData;
using Erp.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;

namespace Erp.Tests;

/// <summary>Spec 018: formato de credenciales OAuth, alcances por superficie, $filter de OData y documento de descubrimiento.</summary>
public class ODataTests
{
    // ---------------- Credenciales y alcances ----------------

    [Fact]
    public void Credenciales_tienen_formato_y_entropia_y_no_se_confunden_con_claves_de_api()
    {
        var (id, secret, token) = (OAuthFormat.NewClientId(), OAuthFormat.NewSecret(), OAuthFormat.NewToken());
        Assert.True(OAuthFormat.IsClientId(id));
        Assert.Equal(20, id.Length); // cabe en char(20)
        Assert.True(OAuthFormat.IsSecret(secret));
        Assert.True(OAuthFormat.IsToken(token));
        Assert.False(OAuthFormat.IsToken(secret));
        Assert.False(OAuthFormat.IsSecret(token));
        Assert.NotEqual(OAuthFormat.NewToken(), token);
        Assert.Null(ApiKeyFormat.PublicIdOf(token)); // un token no pasa por clave de API
        Assert.True(OAuthFormat.Matches(token, OAuthFormat.Hash(token)));
        Assert.False(OAuthFormat.Matches(token + "x", OAuthFormat.Hash(token)));
        Assert.False(OAuthFormat.Matches(token, null));
    }

    [Theory]
    [InlineData("odata.read", "odata.read")]
    [InlineData("data.write  odata.read", "odata.read data.write")]
    [InlineData("data.read data.read", "data.read")]
    [InlineData("", null)]
    [InlineData("odata.read admin", null)]
    [InlineData("ODATA.READ", null)]
    public void Alcances_se_validan_y_normalizan(string input, string? expected)
    {
        var parsed = OAuthScopes.Parse(input);
        Assert.Equal(expected, parsed is null ? null : string.Join(' ', parsed));
    }

    [Theory]
    [InlineData("/odata/Contacto", "GET", OAuthScopes.ODataRead)]
    [InlineData("/odata/$metadata", "GET", OAuthScopes.ODataRead)]
    [InlineData("/odata/Contacto", "POST", OAuthScopes.ODataRead)] // llega al endpoint, que responde 405
    [InlineData("/api/data/Contacto", "GET", OAuthScopes.DataRead)]
    [InlineData("/api/data/Contacto/5/history", "GET", OAuthScopes.DataRead)]
    [InlineData("/api/data/Contacto/compute", "POST", OAuthScopes.DataRead)]
    [InlineData("/api/reports/Contacto/r", "GET", OAuthScopes.DataRead)]
    [InlineData("/api/data/Contacto", "POST", OAuthScopes.DataWrite)]
    [InlineData("/api/data/Contacto/5", "PUT", OAuthScopes.DataWrite)]
    [InlineData("/api/data/Contacto/5/transition", "POST", OAuthScopes.DataWrite)]
    [InlineData("/api/data/Contacto/import", "POST", OAuthScopes.DataWrite)]
    [InlineData("/api/data/Contacto/5/compute", "POST", OAuthScopes.DataWrite)] // no es la ruta de cálculo
    [InlineData("/api/auth/me", "GET", "")]
    [InlineData("/api/auth/logout", "POST", null)]
    [InlineData("/api/admin/users", "GET", null)]
    [InlineData("/api/model/entities", "GET", null)]
    [InlineData("/api/audit", "GET", null)]
    [InlineData("/api/reports/x", "POST", null)]
    [InlineData("/api/dataset", "GET", null)] // prefijo de texto, no de segmento
    [InlineData("/oauth/token", "POST", null)]
    public void Cada_ruta_exige_su_alcance(string path, string method, string? scope) =>
        Assert.Equal(scope, OAuthScopes.Required(new PathString(path), method));

    [Fact]
    public void Ampliar_un_cliente_es_ganar_roles_o_alcances()
    {
        Assert.True(SecurityGrants.OAuthClient(null, null, [1], ["odata.read"]));
        Assert.True(SecurityGrants.OAuthClient([1], ["odata.read"], [1, 2], ["odata.read"]));
        Assert.True(SecurityGrants.OAuthClient([1], ["odata.read"], [1], ["odata.read", "data.write"]));
        Assert.False(SecurityGrants.OAuthClient([1, 2], ["odata.read", "data.read"], [1], ["odata.read"]));
        Assert.False(SecurityGrants.OAuthClient([1], ["odata.read"], [1], ["odata.read"]));
        Assert.True(SecurityChangeKinds.IsIntegration(SecurityChangeKinds.OAuthClientCreate));
        Assert.True(SecurityChangeKinds.IsIntegration(SecurityChangeKinds.ApiKeyUpdate));
        Assert.False(SecurityChangeKinds.IsIntegration(SecurityChangeKinds.RoleCreate));
    }

    [Theory]
    [InlineData("erpcl_0123456789abcd:erpcs_x", "erpcl_0123456789abcd", "erpcs_x")]
    [InlineData("a%3Ab:c%2Bd", "a:b", "c+d")]
    [InlineData("sin-dos-puntos", null, null)]
    [InlineData(":solo-secreto", null, null)]
    public void Basic_se_decodifica_como_dice_el_rfc(string plain, string? id, string? secret)
    {
        var (i, s) = OAuthTokenService.ParseBasic(Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)));
        Assert.Equal(id, i);
        Assert.Equal(secret, s);
        Assert.Equal((null, null), OAuthTokenService.ParseBasic("%%%no-base64"));
    }

    // ---------------- Modelo de ejemplo ----------------

    private static readonly EntityDef Contacto = new()
    {
        Name = "Contacto", Label = "Contacto", LabelPlural = "Contactos", DisplayField = "Nombre",
        Fields =
        [
            new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text, MaxLength = 120, DisplayOrder = 1 },
            new FieldDef { Name = "Segmento", Label = "Segmento", Type = FieldTypes.Choice, Options = ["Minorista", "Mayorista"], DisplayOrder = 2 },
            new FieldDef { Name = "Credito", Label = "Crédito", Type = FieldTypes.Decimal, Precision = 12, Scale = 2, DisplayOrder = 3 },
            new FieldDef { Name = "Activo", Label = "Activo", Type = FieldTypes.Boolean, DisplayOrder = 4 },
            new FieldDef { Name = "Alta", Label = "Alta", Type = FieldTypes.Date, DisplayOrder = 5 },
            new FieldDef { Name = "Notas", Label = "Notas", Type = FieldTypes.LongText, DisplayOrder = 6 },
            new FieldDef { Name = "Ciudad", Label = "Ciudad", Type = FieldTypes.Reference, ReferenceEntity = "Ciudad", DisplayOrder = 7 },
            new FieldDef { Name = "Visitas", Label = "Visitas", Type = FieldTypes.Integer, DisplayOrder = 8 },
            new FieldDef { Name = "Viejo", Label = "Viejo", Type = FieldTypes.Text, IsArchived = true, DisplayOrder = 9 },
        ],
        Security = new SecurityPolicy { Fields = [new FieldSecurity { Field = "Credito", Read = ["Finanzas"] }] },
    };

    private static readonly EntityDef Ciudad = new()
    {
        Name = "Ciudad", Label = "Ciudad", LabelPlural = "Ciudades", Fields = [new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text }],
    };

    private static readonly EntityDef Secreto = new()
    {
        Name = "Secreto", Label = "Secreto", LabelPlural = "Secretos", Fields = [new FieldDef { Name = "Clave", Label = "Clave", Type = FieldTypes.Text }],
    };

    private static readonly Dictionary<string, EntityDef> Model = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Contacto"] = Contacto, ["Ciudad"] = Ciudad, ["Secreto"] = Secreto,
    };

    private static UserContext Ventas(bool ciudades = true) => new()
    {
        Id = 9, Username = "v", DisplayName = "V", SecurityStamp = Guid.Empty, Roles = ["Ventas"],
        Entities = new Dictionary<string, EntityAccess>(StringComparer.OrdinalIgnoreCase)
        {
            ["Contacto"] = new(true, false, false, false),
            ["Ciudad"] = new(ciudades, false, false, false),
        },
    };

    private static (string Sql, List<(string, object)> Params) Filter(string text, UserContext? user = null)
    {
        var props = ODataModel.Properties(Contacto, user ?? Ventas()).ToDictionary(p => p.Name, StringComparer.Ordinal);
        using var cmd = new SqlCommand();
        var sql = ODataFilter.ToSql(ODataFilter.Parse(text), props.GetValueOrDefault, cmd.Parameters, Contacto);
        return (sql, cmd.Parameters.Cast<SqlParameter>().Select(p => (p.ParameterName, p.Value)).ToList());
    }

    private static string Fails(string text, UserContext? user = null) => Assert.Throws<ODataException>(() => Filter(text, user)).Message;

    // ---------------- $filter ----------------

    [Fact]
    public void Comparaciones_van_parametrizadas_con_lista_blanca()
    {
        var (sql, ps) = Filter("Nombre eq 'O''Brien; DROP TABLE x --' and Visitas gt 3");
        Assert.Equal("(t.[Nombre] = @od0 AND t.[Visitas] > @od1)", sql);
        Assert.Equal("O'Brien; DROP TABLE x --", ps[0].Item2);
        Assert.Equal(3L, ps[1].Item2);
        Assert.DoesNotContain("DROP", sql);
    }

    [Fact]
    public void Ne_y_not_siguen_la_logica_de_dos_valores_de_odata()
    {
        Assert.Equal("(t.[Nombre] <> @od0 OR t.[Nombre] IS NULL)", Filter("Nombre ne 'a'").Sql);
        Assert.Equal("(t.[Nombre] <> @od0 OR t.[Nombre] IS NULL)", Filter("not (Nombre eq 'a')").Sql);
        Assert.Equal("t.[Nombre] = @od0", Filter("not Nombre ne 'a'").Sql);
        Assert.Equal("(t.[Visitas] <= @od0 OR t.[Visitas] IS NULL)", Filter("not (Visitas gt 5)").Sql);
        // De Morgan: not (a and b) = not a or not b.
        Assert.Equal("((t.[Visitas] < @od0 OR t.[Visitas] IS NULL) OR (t.[Nombre] <> @od1 OR t.[Nombre] IS NULL))",
            Filter("not (Visitas ge 1 and Nombre eq 'x')").Sql);
        Assert.Equal("t.[Nombre] IS NULL", Filter("Nombre eq null").Sql);
        Assert.Equal("t.[Nombre] IS NOT NULL", Filter("not (Nombre eq null)").Sql);
        Assert.Equal("t.[Nombre] IS NOT NULL", Filter("null ne Nombre").Sql);
        Assert.Equal("t.[Activo] = @od0", Filter("Activo").Sql);
        Assert.Equal("(t.[Activo] <> @od0 OR t.[Activo] IS NULL)", Filter("not Activo").Sql);
    }

    [Fact]
    public void Literal_a_la_izquierda_invierte_el_operador()
    {
        var (sql, ps) = Filter("5 lt Visitas");
        Assert.Equal("t.[Visitas] > @od0", sql);
        Assert.Equal(5L, ps[0].Item2);
    }

    [Fact]
    public void Funciones_de_texto_escapan_comodines()
    {
        var (sql, ps) = Filter("contains(Nombre,'50%_[x]') or startswith(Notas,'A') or endswith(Nombre,'z')");
        Assert.Equal(@"((t.[Nombre] LIKE @od0 ESCAPE '\' OR t.[Notas] LIKE @od1 ESCAPE '\') OR t.[Nombre] LIKE @od2 ESCAPE '\')", sql);
        Assert.Equal(@"%50\%\_\[x]%", ps[0].Item2);
        Assert.Equal("A%", ps[1].Item2);
        Assert.Equal("%z", ps[2].Item2);
        Assert.Equal(@"(t.[Nombre] NOT LIKE @od0 ESCAPE '\' OR t.[Nombre] IS NULL)", Filter("not contains(Nombre,'a')").Sql);
    }

    [Fact]
    public void In_referencias_fechas_y_decimales()
    {
        Assert.Equal("t.[Segmento] IN (@od0, @od1)", Filter("Segmento in ('Minorista','Mayorista')").Sql);
        Assert.Equal("(t.[Segmento] NOT IN (@od0) OR t.[Segmento] IS NULL)", Filter("not (Segmento in ('Minorista'))").Sql);
        Assert.Equal("t.[Ciudad] = @od0", Filter("_Ciudad_value eq 12").Sql);
        var (date, dp) = Filter("Alta ge 2026-01-31");
        Assert.Equal("t.[Alta] >= @od0", date);
        Assert.Equal(new DateTime(2026, 1, 31), dp[0].Item2);
        var (dto, tp) = Filter("CreatedAt lt 2026-01-31T10:00:00-06:00");
        Assert.Equal("t.[CreatedAt] < @od0", dto);
        Assert.Equal(new DateTime(2026, 1, 31, 16, 0, 0), tp[0].Item2); // se compara en UTC
        Assert.Contains("t.[Id] = @od0", Filter("Id eq 7").Sql);
    }

    [Theory]
    [InlineData("Credito gt 0", "no existe")]                       // oculto: el mismo mensaje que inexistente
    [InlineData("Inventado eq 1", "no existe")]
    [InlineData("Viejo eq 'x'", "no existe")]                       // archivado
    [InlineData("Ciudad eq 1", "no existe")]                        // la navegación no es una propiedad
    [InlineData("Ciudad/Nombre eq 'x'", "rutas")]
    [InlineData("Visitas eq 'tres'", "número entero")]
    [InlineData("Visitas eq 1.5", "número entero")]
    [InlineData("Nombre eq 5", "texto")]
    [InlineData("Alta eq 2026-01-01T00:00:00Z", "fecha aaaa-mm-dd")]
    [InlineData("Activo gt true", "sí/no")]
    [InlineData("Visitas gt null", "solo se usan eq y ne")]
    [InlineData("Segmento eq 'Otro'", "no es una opción")]
    [InlineData("Segmento in ('Minorista', null)", "no admite null")]
    [InlineData("contains(Visitas,'1')", "texto")]
    [InlineData("tolower(Nombre) eq 'a'", "no se admite")]
    [InlineData("Nombre eq Notas", "dos propiedades")]
    [InlineData("Nombre eq 'a' and", "se esperaba un valor")]
    [InlineData("(Nombre eq 'a'", "«)»")]
    [InlineData("Nombre eq 'sin cerrar", "falta cerrar")]
    [InlineData("Nombre eq 'a'; DROP", "carácter no válido")]
    [InlineData("Visitas add 1 eq 2", "no se esperaba")]
    [InlineData("1 eq 1", "necesita una propiedad")]
    public void Filtros_invalidos_dan_un_error_claro(string filter, string message) =>
        Assert.Contains(message, Fails(filter));

    [Fact]
    public void Con_el_rol_que_lo_ve_el_campo_oculto_si_se_filtra()
    {
        var finanzas = new UserContext { Id = 1, Username = "f", DisplayName = "F", SecurityStamp = Guid.Empty, Roles = ["Finanzas"] };
        Assert.Equal("t.[Credito] > @od0", Filter("Credito gt 0", finanzas).Sql);
    }

    [Fact]
    public void Limites_de_tamano()
    {
        Assert.Contains("2000", Fails("Nombre eq '" + new string('a', 2000) + "'"));
        Assert.Contains("comparaciones", Fails(string.Join(" or ", Enumerable.Range(0, 51).Select(i => $"Visitas eq {i}"))));
        Assert.Contains("valores", Fails($"Visitas in ({string.Join(",", Enumerable.Range(0, 51))})"));
        Assert.Contains("niveles", Fails(new string('(', 40) + "Activo" + new string(')', 40)));
        Assert.Contains("400", Fails($"Nombre eq '{new string('a', 401)}'"));
    }

    // ---------------- Opciones de consulta ----------------

    private static ODataQuery Query(string qs) => ODataQuery.Parse(new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(qs)));

    [Fact]
    public void Opciones_admitidas_se_leen()
    {
        var q = Query("?$filter=Activo&$select=Nombre,Visitas&$orderby=Nombre desc,Id&$top=10&$skip=20&$count=true&$expand=Ciudad&otra=1");
        Assert.Equal("Activo", q.Filter);
        Assert.Equal(["Nombre", "Visitas"], q.Select);
        Assert.Equal([new ODataOrder("Nombre", true), new ODataOrder("Id", false)], q.OrderBy);
        Assert.Equal((10, 20, true), (q.Top, q.Skip, q.Count));
        Assert.Equal(["Ciudad"], q.Expand);
        Assert.Null(Query("?$select=*").Select);
    }

    [Theory]
    [InlineData("?$search=x", "no se admite")]
    [InlineData("?$apply=groupby((Nombre))", "no se admite")]
    [InlineData("?$expand=Ciudad($select=Nombre)", "anidadas")]
    [InlineData("?$expand=Ciudad/Pais", "rutas")]
    [InlineData("?$expand=*", "no es un nombre válido")]
    [InlineData("?$orderby=Ciudad/Nombre", "rutas")]
    [InlineData("?$orderby=Nombre sideways", "no es válido")]
    [InlineData("?$orderby=a,b,c,d,e,f", "hasta 5")]
    [InlineData("?$top=-1", "no negativo")]
    [InlineData("?$skip=abc", "no negativo")]
    [InlineData("?$count=yes", "true o false")]
    [InlineData("?$format=xml", "json")]
    [InlineData("?$top=1&$top=2", "repetida")]
    [InlineData("?$select=Nombre,,Id", "vacío")]
    public void Opciones_no_admitidas_responden_400(string qs, string message) =>
        Assert.Contains(message, Assert.Throws<ODataException>(() => Query(qs)).Message);

    // ---------------- Descubrimiento ----------------

    [Fact]
    public void Metadata_se_genera_para_quien_lo_pide()
    {
        var xml = Csdl.Write(Model, Ventas());
        Assert.Contains("<EntitySet Name=\"Contacto\" EntityType=\"Erp.Contacto\"", xml);
        Assert.Contains("<Property Name=\"Nombre\" Type=\"Edm.String\" MaxLength=\"120\"", xml);
        Assert.Contains("<Property Name=\"_Ciudad_value\" Type=\"Edm.Int64\"", xml);
        Assert.Contains("<NavigationProperty Name=\"Ciudad\" Type=\"Erp.Ciudad\"", xml);
        Assert.Contains("<NavigationPropertyBinding Path=\"Ciudad\" Target=\"Ciudad\"", xml);
        Assert.Contains("<Property Name=\"Alta\" Type=\"Edm.Date\"", xml);
        Assert.Contains("<Property Name=\"Activo\" Type=\"Edm.Boolean\"", xml);
        Assert.Contains("<Property Name=\"Visitas\" Type=\"Edm.Int64\"", xml);
        Assert.Contains("<Property Name=\"Id\" Type=\"Edm.Int64\" Nullable=\"false\"", xml);
        Assert.DoesNotContain("Credito", xml); // campo oculto para Ventas
        Assert.DoesNotContain("Viejo", xml);   // archivado
        Assert.DoesNotContain("Secreto", xml); // catálogo que su rol no lee
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);

        // Sin permiso sobre el destino, la referencia queda solo como id.
        var sinCiudades = Csdl.Write(Model, Ventas(ciudades: false));
        Assert.Contains("_Ciudad_value", sinCiudades);
        Assert.DoesNotContain("NavigationProperty Name=\"Ciudad\"", sinCiudades);
        Assert.DoesNotContain("EntitySet Name=\"Ciudad\"", sinCiudades);

        var admin = new UserContext { Id = 1, Username = "a", DisplayName = "A", SecurityStamp = Guid.Empty, Roles = [], IsSuperuser = true };
        var full = Csdl.Write(Model, admin);
        Assert.Contains("<Property Name=\"Credito\" Type=\"Edm.Decimal\" Precision=\"12\" Scale=\"2\"", full);
        Assert.Contains("EntitySet Name=\"Secreto\"", full);
    }

    [Fact]
    public void Partidas_son_navegacion_de_coleccion_sin_conjunto_propio()
    {
        var doc = new EntityDef
        {
            Name = "Orden", Label = "Orden", LabelPlural = "Órdenes",
            Fields = [new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "OrdenPartida" }],
        };
        var line = new EntityDef
        {
            Name = "OrdenPartida", Label = "Partida", LabelPlural = "Partidas",
            Fields =
            [
                new FieldDef { Name = "Orden", Label = "Orden", Type = FieldTypes.Parent, ReferenceEntity = "Orden" },
                new FieldDef { Name = "Producto", Label = "Producto", Type = FieldTypes.Reference, ReferenceEntity = "Ciudad" },
            ],
        };
        var model = new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase) { ["Orden"] = doc, ["OrdenPartida"] = line, ["Ciudad"] = Ciudad };
        var admin = new UserContext { Id = 1, Username = "a", DisplayName = "A", SecurityStamp = Guid.Empty, Roles = [], IsSuperuser = true };
        var xml = Csdl.Write(model, admin);
        Assert.Contains("<NavigationProperty Name=\"Partidas\" Type=\"Collection(Erp.OrdenPartida)\" ContainsTarget=\"true\"", xml);
        Assert.Contains("<EntityType Name=\"OrdenPartida\"", xml);
        Assert.DoesNotContain("EntitySet Name=\"OrdenPartida\"", xml);
        Assert.DoesNotContain("<Property Name=\"Orden\"", xml);              // el padre no se publica
        Assert.Contains("<Property Name=\"_Producto_value\"", xml);           // la partida no navega: un nivel
        Assert.DoesNotContain("NavigationProperty Name=\"Producto\"", xml);
    }
}
