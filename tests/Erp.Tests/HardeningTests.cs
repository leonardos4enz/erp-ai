using System.Text.Json;
using Erp.Api.Assistant;
using Erp.Api.Environments;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Tests;

/// <summary>Spec 012: endurecimiento. Bitácora con la seguridad del dato, cuatro ojos sin atajos y huella de lo revisado.</summary>
public class HardeningTests
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ventas", "Auditor" };

    private static IReadOnlyDictionary<string, EntityDef> Model()
    {
        var plan = new ChangePlan { Operations =
        [
            new CreateEntityOp { Name = "Pedido", Label = "Pedido", LabelPlural = "Pedidos", DisplayField = "Clave", PerCompany = true, Fields =
            [
                new FieldDef { Name = "Clave", Label = "Clave", Type = FieldTypes.Text },
                new FieldDef { Name = "Margen", Label = "Margen", Type = FieldTypes.Decimal },
                new FieldDef { Name = "Publico", Label = "Público", Type = FieldTypes.Boolean },
            ] },
            new CreateEntityOp { Name = "PedidoPartida", Label = "Partida", LabelPlural = "Partidas", Fields =
            [
                new FieldDef { Name = "Pedido", Label = "Pedido", Type = FieldTypes.Parent, ReferenceEntity = "Pedido" },
                new FieldDef { Name = "Concepto", Label = "Concepto", Type = FieldTypes.Text },
                new FieldDef { Name = "Costo", Label = "Costo", Type = FieldTypes.Decimal },
            ] },
            new AddFieldOp { Entity = "Pedido", Field = new FieldDef { Name = "Partidas", Label = "Partidas", Type = FieldTypes.Lines, ChildEntity = "PedidoPartida" } },
            new SetSecurityOp { Entity = "Pedido", Security = new SecurityPolicy
            {
                Fields = [new FieldSecurity { Field = "Margen", Read = ["Ventas"] }],
                Records = [new RecordGrant { Roles = ["Auditor"], Where = "Publico", Access = "read" }, new RecordGrant { Roles = ["Ventas"] }],
            } },
            new SetSecurityOp { Entity = "PedidoPartida", Security = new SecurityPolicy { Fields = [new FieldSecurity { Field = "Costo", Read = ["Ventas"] }] } },
            new CreateEntityOp { Name = "Privado", Label = "Privado", LabelPlural = "Privados", Fields = [new FieldDef { Name = "Nombre", Label = "Nombre", Type = FieldTypes.Text }] },
        ] };
        var compiled = PlanCompiler.Compile(plan, new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase), Roles);
        Assert.True(compiled.Preview.Valid, string.Join(" | ", compiled.Preview.Errors.Select(e => e.Message)));
        return compiled.ResultingModel;
    }

    private static UserContext Auditor(bool superuser = false) => new()
    {
        Id = 9, Username = "aud", DisplayName = "Aud", SecurityStamp = Guid.Empty, Roles = ["Auditor"], IsSuperuser = superuser,
        CanViewAudit = true, Company = new CompanyInfo(3, "PRINCIPAL", "Principal", "MXN"),
        Entities = new Dictionary<string, EntityAccess>(StringComparer.OrdinalIgnoreCase) { ["Pedido"] = new(true, false, false, false) },
    };

    private static JsonElement Snapshot() => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
    {
        ["Clave"] = "P-1", ["Margen"] = 31.5m, ["Publico"] = true, ["Estado"] = "Abierto", ["CampoViejo"] = "x",
        ["Partidas"] = new[] { new Dictionary<string, object?> { ["Pedido"] = 1, ["Concepto"] = "Tornillos", ["Costo"] = 10m } },
    });

    [Fact]
    public void La_bitacora_quita_los_campos_ocultos_del_encabezado_y_las_partidas()
    {
        var model = Model();
        var redacted = AuditFilter.Redact("update", "Pedido", Snapshot(), Auditor(), model)!.Value.GetRawText();
        Assert.Contains("P-1", redacted);
        Assert.Contains("Tornillos", redacted);
        Assert.Contains("Abierto", redacted); // el estado del proceso no es un campo oculto
        Assert.DoesNotContain("Margen", redacted);
        Assert.DoesNotContain("Costo", redacted);
        Assert.DoesNotContain("CampoViejo", redacted); // un campo que ya no existe: no se sabe quién podía verlo

        // Lo mismo en una transición; el superusuario y los eventos que no son de datos quedan igual.
        Assert.DoesNotContain("Margen", AuditFilter.Redact("workflow.cerrar", "Pedido", Snapshot(), Auditor(), model)!.Value.GetRawText());
        Assert.Contains("Margen", AuditFilter.Redact("update", "Pedido", Snapshot(), Auditor(superuser: true), model)!.Value.GetRawText());
        Assert.Contains("Margen", AuditFilter.Redact("model.change", "Pedido", Snapshot(), Auditor(), model)!.Value.GetRawText());
    }

    [Fact]
    public void La_condicion_de_la_bitacora_usa_catalogo_empresa_y_alcance_por_registro()
    {
        var model = Model();
        Assert.Null(AuditFilter.Build(model, Auditor(superuser: true)));
        var p = AuditFilter.Build(model, Auditor())!;
        // Solo el catálogo que puede leer, con su filtro de empresa y su regla «where» en una subconsulta.
        Assert.Contains("[app].[Pedido]", p.Sql);
        Assert.DoesNotContain("Privado", p.Sql);
        Assert.Contains("[CompanyId]", p.Sql);
        Assert.Contains("[Publico] = 1", p.Sql);
        Assert.Contains("NOT LIKE 'workflow.%'", p.Sql);
        Assert.Contains(p.Parameters, x => Equals(x.Value, 3)); // empresa actual
        Assert.Contains(p.Parameters, x => Equals(x.Value, "Pedido"));
        Assert.DoesNotContain(p.Parameters, x => Equals(x.Value, "Privado"));
        // Valores como parámetros: ningún nombre de catálogo ni de acción va en el texto SQL.
        Assert.DoesNotContain("'Pedido'", p.Sql);
        Assert.DoesNotContain("'create'", p.Sql);
    }

    [Fact]
    public void Con_aprobacion_el_modelo_no_cambia_por_caminos_directos()
    {
        var dev = EnvironmentOptions.Create(null, false, null);
        dev.RequireDirectChange("un plan");
        Assert.True(dev.DirectChanges);

        var prod = EnvironmentOptions.Create("Producción", true, null);
        Assert.False(prod.DirectChanges);
        var ex = Assert.Throws<Conflict>(() => prod.RequireDirectChange("un plan del modelador"));
        Assert.Contains("Entornos", ex.Message);

        var suite = prod with { AllowDirectChanges = true };
        suite.RequireDirectChange("un plan");
        Assert.True(suite.DirectChanges);
    }

    [Fact]
    public void La_huella_de_la_revision_cambia_con_el_plan_los_pasos_o_las_advertencias()
    {
        var preview = new PlanPreview(true, [], [new PlanStep(0, "Reglas de 'Producto'", null)]);
        var a = ReviewHash.Of("{\"operations\":[]}", preview, ["Quita reglas existentes: A"]);
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.Equal(a, ReviewHash.Of("{\"operations\":[]}", preview, ["Quita reglas existentes: A"]));
        Assert.NotEqual(a, ReviewHash.Of("{\"operations\":[]}", preview, ["Quita reglas existentes: A, B"]));
        Assert.NotEqual(a, ReviewHash.Of("{\"operations\":[1]}", preview, ["Quita reglas existentes: A"]));
        Assert.NotEqual(a, ReviewHash.Of("{\"operations\":[]}", preview with { Steps = [new PlanStep(0, "Reglas de 'Producto'", "ALTER")] },
            ["Quita reglas existentes: A"]));
        // Mover texto entre partes no produce la misma huella.
        Assert.NotEqual(ReviewHash.Of("x", new PlanPreview(true, [], []), ["y"]), ReviewHash.Of("xy", new PlanPreview(true, [], []), []));
    }
}
