using Erp.Api.Environments;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>Spec 015: unidades en árbol, equipos dueños, herencia de roles y aprobación de cambios de seguridad.</summary>
public class HierarchyTests
{
    private static EntityDef Catalogo(SecurityPolicy security) => new()
    {
        Name = "Caso", Label = "Caso", LabelPlural = "Casos",
        Fields = [new FieldDef { Name = "Titulo", Label = "Título", Type = FieldTypes.Text }, new FieldDef { Name = "Dueno", Label = "Dueño", Type = FieldTypes.User }],
        Security = security,
    };

    private static readonly RecordFilter.Who Ana = new(7, "Norte", ["Agente"]);

    // ---------- H1: unidades en árbol ----------

    [Fact]
    public void UnitTree_filtra_por_la_ruta_de_mi_unidad_con_parametros()
    {
        var e = Catalogo(new SecurityPolicy { Owner = "Dueno", Records = [new RecordGrant { Scope = RecordScopes.UnitTree }] });
        var p = RecordFilter.Build(e, Ana, write: false)!;
        Assert.Equal("(t.[Dueno] IN (SELECT u.[Id] FROM meta.[User] u JOIN meta.[BusinessUnit] b ON b.[Name] = u.[Unit] " +
                     "WHERE b.[Path] LIKE (SELECT m.[Path] FROM meta.[BusinessUnit] m WHERE m.[Name] = @sec0) + N'%'))", p.Sql);
        Assert.Equal("Norte", p.Parameters.Single().Value);
    }

    [Fact]
    public void Sin_unidad_unitTree_y_unit_se_reducen_a_propios()
    {
        foreach (var scope in new[] { RecordScopes.Unit, RecordScopes.UnitTree })
        {
            var e = Catalogo(new SecurityPolicy { Records = [new RecordGrant { Scope = scope }] });
            var p = RecordFilter.Build(e, Ana with { Unit = null }, write: false)!;
            Assert.Equal("(t.[CreatedBy] = @sec0)", p.Sql);
            Assert.Equal(7, p.Parameters.Single().Value);
        }
    }

    [Fact]
    public void La_unidad_con_comillas_o_comodines_va_como_parametro()
    {
        var e = Catalogo(new SecurityPolicy { Records = [new RecordGrant { Scope = RecordScopes.UnitTree }] });
        var p = RecordFilter.Build(e, Ana with { Unit = "N%'; DROP TABLE x --" }, write: false)!;
        Assert.DoesNotContain("DROP", p.Sql);
        Assert.Equal("N%'; DROP TABLE x --", p.Parameters.Single().Value);
    }

    // ---------- H2: equipos ----------

    [Fact]
    public void Propios_con_teamOwners_incluye_equipos_activos_de_los_que_soy_miembro()
    {
        var e = Catalogo(new SecurityPolicy { Owner = "Dueno", TeamOwners = true, Records = [new RecordGrant { Scope = RecordScopes.Own }] });
        var p = RecordFilter.Build(e, Ana, write: true)!;
        Assert.Equal("((t.[Dueno] = @sec0 OR t.[Dueno] IN (SELECT tm.[TeamId] FROM meta.[TeamMember] tm JOIN meta.[User] tu ON tu.[Id] = tm.[TeamId] " +
                     "WHERE tm.[UserId] = @sec0 AND tu.[IsActive] = 1)))", p.Sql);
        Assert.Equal(7, p.Parameters.Single().Value);
    }

    [Fact]
    public void Sin_teamOwners_propios_sigue_igual()
    {
        var e = Catalogo(new SecurityPolicy { Owner = "Dueno", Records = [new RecordGrant { Scope = RecordScopes.Own }] });
        Assert.Equal("(t.[Dueno] = @sec0)", RecordFilter.Build(e, Ana, write: true)!.Sql);
    }

    [Fact]
    public void Solo_el_campo_duenio_con_teamOwners_acepta_equipos()
    {
        var policy = new SecurityPolicy { Owner = "Dueno", TeamOwners = true };
        Assert.True(policy.AcceptsTeams("dueno"));
        Assert.False(policy.AcceptsTeams("Titulo"));
        Assert.False((policy with { TeamOwners = false }).AcceptsTeams("Dueno"));
    }

    [Fact]
    public void La_politica_valida_unitTree_y_que_teamOwners_requiere_duenio()
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Agente" };
        var ok = new SecurityPolicy { Owner = "Dueno", TeamOwners = true, Records = [new RecordGrant { Scope = "unitTree" }] };
        var e = Catalogo(ok);
        Assert.Empty(SecurityRules.Validate(e, ok, new Dictionary<string, EntityDef> { ["Caso"] = e }, roles));
        var bad = new SecurityPolicy { TeamOwners = true, Records = [new RecordGrant { Scope = "arbol" }] };
        var msgs = SecurityRules.Validate(e, bad, new Dictionary<string, EntityDef> { ["Caso"] = e }, roles).ToList();
        Assert.Contains(msgs, m => m.Contains("teamOwners"));
        Assert.Contains(msgs, m => m.Contains("unitTree"));
    }

    [Fact]
    public void TeamOwners_en_falso_no_cambia_la_forma_canonica_de_la_politica()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new SecurityPolicy { Owner = "Dueno" }, PackageJson.Canonical);
        Assert.DoesNotContain("teamOwners", json);
        Assert.Contains("\"teamOwners\":true", System.Text.Json.JsonSerializer.Serialize(new SecurityPolicy { Owner = "Dueno", TeamOwners = true }, PackageJson.Canonical));
    }

    // ---------- H3: herencia de roles ----------

    [Fact]
    public void La_herencia_es_transitiva_y_tolera_diamantes()
    {
        // 4 (Director) hereda de 3 (Gerente) y 2; 3 hereda de 2 (Comprador); 2 hereda de 1 (Consulta).
        var edges = new List<(int, int)> { (4, 3), (4, 2), (3, 2), (2, 1) };
        Assert.Equal([1, 2, 3, 4], RoleGraph.Expand([4], edges).Order());
        Assert.Equal([1, 2], RoleGraph.Expand([2], edges).Order());
        Assert.Equal(3, RoleGraph.Depth(4, edges));
        Assert.Equal(0, RoleGraph.Depth(1, edges));
    }

    [Fact]
    public void Un_ciclo_se_detecta_y_el_cierre_no_se_cuelga()
    {
        var edges = new List<(int, int)> { (1, 2), (2, 3), (3, 1) };
        Assert.Null(RoleGraph.Depth(1, edges));
        Assert.Null(RoleGraph.MaxDepthOf(edges));
        Assert.Equal([1, 2, 3], RoleGraph.Expand([1], edges).Order());
    }

    [Fact]
    public void Los_permisos_heredados_se_unen_con_lo_mas_permisivo()
    {
        var union = RoleGraph.Union([new PermRow("Orden", true, false, false, false), new PermRow("orden", true, true, true, false), new PermRow("Pago", true, false, false, false)]);
        Assert.Equal(new EntityAccess(true, true, true, false), union["ORDEN"]);
        Assert.Equal(new EntityAccess(true, false, false, false), union["Pago"]);
    }

    // ---------- H4: amplía o reduce ----------

    private static readonly UserAccessSnapshot Beto = new("Beto", true, [2], "Norte", [1]);

    [Fact]
    public void Usuario_ampliar_espera_y_reducir_no()
    {
        Assert.True(SecurityGrants.User(null, Beto, false));                                       // alta
        Assert.True(SecurityGrants.User(Beto, Beto with { RoleIds = [2, 3] }, false));             // agrega rol
        Assert.True(SecurityGrants.User(Beto, Beto with { CompanyIds = [1, 2] }, false));          // agrega empresa
        Assert.True(SecurityGrants.User(Beto, Beto with { Unit = "Sur" }, false));                 // cambia de unidad
        Assert.True(SecurityGrants.User(Beto with { IsActive = false }, Beto, false));             // reactiva
        Assert.True(SecurityGrants.User(Beto, Beto, passwordForOther: true));                      // contraseña ajena
        Assert.False(SecurityGrants.User(Beto, Beto with { RoleIds = [] }, false));                // quita rol
        Assert.False(SecurityGrants.User(Beto, Beto with { IsActive = false }, false));            // desactiva
        Assert.False(SecurityGrants.User(Beto, Beto with { Unit = null }, false));                 // sin unidad: solo propios
        Assert.False(SecurityGrants.User(Beto, Beto with { DisplayName = "Beto R." }, false));     // nombre visible
    }

    private static readonly RoleSnapshot Comprador = new("Comprador", false, false, false, [new PermissionSnapshot("Orden", true, true, false, false)], []);

    [Fact]
    public void Rol_ampliar_espera_y_reducir_no()
    {
        Assert.False(SecurityGrants.Role(null, Comprador with { Permissions = [] }));              // alta vacía: no da nada
        Assert.True(SecurityGrants.Role(null, Comprador));
        Assert.True(SecurityGrants.Role(Comprador, Comprador with { Name = "Director" }));        // renombrar
        Assert.True(SecurityGrants.Role(Comprador, Comprador with { CanViewAudit = true }));
        Assert.True(SecurityGrants.Role(Comprador, Comprador with { InheritsRoleIds = [9] }));
        Assert.True(SecurityGrants.Role(Comprador, Comprador with { Permissions = [new PermissionSnapshot("orden", true, true, true, false)] }));
        Assert.True(SecurityGrants.Role(Comprador, Comprador with { Permissions = [.. Comprador.Permissions, new PermissionSnapshot("Pago", true, false, false, false)] }));
        Assert.False(SecurityGrants.Role(Comprador, Comprador with { Permissions = [new PermissionSnapshot("Orden", true, false, false, false)] }));
        Assert.False(SecurityGrants.Role(Comprador with { InheritsRoleIds = [9] }, Comprador));
    }

    [Fact]
    public void Equipo_unidad_y_clave_ampliar_espera_y_reducir_no()
    {
        var t = new TeamSnapshot("Compras Norte", "Norte", true, [1, 2]);
        Assert.False(SecurityGrants.Team(null, t with { MemberIds = [] }));
        Assert.True(SecurityGrants.Team(null, t));
        Assert.True(SecurityGrants.Team(t, t with { MemberIds = [1, 2, 3] }));
        Assert.True(SecurityGrants.Team(t, t with { Unit = "Sur" }));
        Assert.True(SecurityGrants.Team(t with { IsActive = false }, t));
        Assert.False(SecurityGrants.Team(t, t with { MemberIds = [1] }));
        Assert.False(SecurityGrants.Team(t, t with { IsActive = false }));

        Assert.True(SecurityGrants.UnitMove(null, 4));
        Assert.True(SecurityGrants.UnitMove(3, 4));
        Assert.False(SecurityGrants.UnitMove(3, null));                                           // a la raíz: solo reduce

        Assert.True(SecurityGrants.ApiKeyRoles(null, [1]));
        Assert.True(SecurityGrants.ApiKeyRoles([1], [1, 2]));
        Assert.False(SecurityGrants.ApiKeyRoles([1, 2], [2]));
    }

    [Fact]
    public void La_huella_cubre_tipo_destino_carga_y_antes()
    {
        var h = SecurityApproval.Hash("user.update", 5, "{\"a\":1}", "{\"b\":1}");
        Assert.Equal(64, h.Length);
        Assert.NotEqual(h, SecurityApproval.Hash("user.update", 6, "{\"a\":1}", "{\"b\":1}"));
        Assert.NotEqual(h, SecurityApproval.Hash("user.update", 5, "{\"a\":2}", "{\"b\":1}"));
        Assert.NotEqual(h, SecurityApproval.Hash("user.update", 5, "{\"a\":1}", "{\"b\":2}"));
        Assert.NotEqual(h, SecurityApproval.Hash("role.update", 5, "{\"a\":1}", "{\"b\":1}"));
    }

    [Fact]
    public void Aprobar_compara_el_antes_guardado_con_el_actual()
    {
        var approving = new SecurityChangeApproval(1, "user.update", 5, "{}", SecurityApproval.Json(Beto), null);
        approving.Verify(Beto with { RoleIds = [2] });
        Assert.Throws<Erp.Api.Infrastructure.Conflict>(() => approving.Verify(Beto with { RoleIds = [2, 4] }));
    }

    [Fact]
    public void La_aprobacion_de_seguridad_solo_aplica_en_entornos_con_aprobacion()
    {
        var dev = EnvironmentOptions.Create(null, false, null) with { RequireSecurityApproval = true };
        Assert.False(dev.SecurityChangesNeedApproval);
        var prod = EnvironmentOptions.Create("Producción", true, new string('k', EnvironmentOptions.MinKeyLength));
        Assert.False(prod.SecurityChangesNeedApproval);
        Assert.True((prod with { RequireSecurityApproval = true }).SecurityChangesNeedApproval);
        // AllowDirectChanges es del modelo: no reabre los cambios de seguridad.
        Assert.True((prod with { RequireSecurityApproval = true, AllowDirectChanges = true }).SecurityChangesNeedApproval);
    }
}
