using System.Reflection;
using System.Text.RegularExpressions;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Erp.Api.Studio;
using Microsoft.AspNetCore.Http;

namespace Erp.Tests;

/// <summary>
/// Spec 034 (auditoría 6): la bitácora deniega lo que no está clasificado (A-07), el despachador decide si una ruta escribe
/// por su efecto declarado (A-04) y la regla de las credenciales de integración es una sola función (A-03).
/// </summary>
public class Hardening5Tests
{
    private static IEnumerable<string> Actions() => typeof(AuditActions).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string) && f.Name != nameof(AuditActions.WorkflowPrefix))
        .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Toda_accion_de_auditoria_esta_clasificada()
    {
        var actions = Actions().ToList();
        Assert.True(actions.Count > 40);
        // En exactamente una lista: una acción nueva sin clasificar no la vería ningún auditor que no sea superusuario.
        var lists = new[] { AuditFilter.RecordActions, AuditFilter.ReferenceActions, AuditFilter.EntityActions, AuditFilter.OtherActions };
        var wrong = actions.Where(a => lists.Count(l => l.Contains(a)) != 1).ToList();
        Assert.True(wrong.Count == 0, "Acciones sin clasificar o en dos listas: " + string.Join(", ", wrong));
        Assert.True(AuditFilter.IsClassified(AuditActions.WorkflowPrefix + "aprobar"));
        // Las tres que veía cualquier auditor (A-07, R-032-6).
        Assert.Contains(AuditActions.PrintPdf, AuditFilter.ReferenceActions);
        Assert.Contains(AuditActions.ScheduleChange, AuditFilter.EntityActions);
        Assert.Contains(AuditActions.AutomationRetry, AuditFilter.EntityActions);
    }

    /// <summary>Una acción escrita como texto suelto escaparía de la guardia de arriba.</summary>
    [Fact]
    public void Ninguna_escritura_de_bitacora_usa_un_texto_suelto()
    {
        var src = Path.Combine(RouteInventoryTests.RepoRoot(), "src", "Erp.Api");
        var calls = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"Audit\.WriteAsync\(\s*[^,]+,\s*[^,]+,\s*[^,]+,\s*(?<action>[^,)]+)")
                .Select(m => (File: Path.GetFileName(f), Action: m.Groups["action"].Value.Trim())))
            .ToList();
        Assert.True(calls.Count > 50, $"Solo {calls.Count} escrituras: ¿cambió la firma de Audit.WriteAsync?");
        // `auditAction` es el parámetro de InsertDraftAsync, que sus llamadas pasan como AuditActions.Create o AutomationCreate.
        var loose = calls.Where(c => !c.Action.StartsWith("AuditActions.", StringComparison.Ordinal) && c.Action != "auditAction").ToList();
        Assert.True(loose.Count == 0, "Acciones de bitácora sin constante: " + string.Join(", ", loose.Select(c => $"{c.File}: {c.Action}")));
    }

    private static Endpoint Route(EffectMetadata? effect) =>
        new(null, effect is null ? EndpointMetadataCollection.Empty : new EndpointMetadataCollection(effect), "prueba");

    [Fact]
    public void Una_ruta_escribe_segun_su_efecto_declarado_tambien_un_GET()
    {
        // A-04: imprimir y exportar son GET que escriben en la bitácora.
        Assert.True(RouteDispatcher.Writes(Route(new(Effect.AuditTrail, null)), "GET"));
        Assert.False(RouteDispatcher.Writes(Route(null), "GET"));
        Assert.False(RouteDispatcher.Writes(Route(new(Effect.None, null)), "GET"));
        Assert.False(RouteDispatcher.Writes(Route(new(Effect.None, null)), "POST"));
        Assert.True(RouteDispatcher.Writes(Route(null), "POST"));
        Assert.True(RouteDispatcher.Writes(Route(new(Effect.Data, null)), "PUT"));
    }

    /// <summary>
    /// Spec 034 (H6): prueba del alcance de R-008-4, que sigue abierto. La importación lee números en formato es-MX (punto
    /// decimal, coma de miles): «1,5» entra como 15 sin aviso. Si se decide avisar o rechazar, esta prueba cambia con la decisión.
    /// </summary>
    [Fact]
    public void R_008_4_una_coma_decimal_se_toma_como_separador_de_miles()
    {
        var f = new Erp.Api.Metadata.FieldDef { Name = "Cantidad", Label = "Cantidad", Type = Erp.Api.Metadata.FieldTypes.Decimal, Scale = 2 };
        Assert.Equal("15", Erp.Api.Data.RecordService.CellValue(f, "1,5").Node!.GetValue<string>());
        Assert.Equal("1.5", Erp.Api.Data.RecordService.CellValue(f, "1.5").Node!.GetValue<string>());
        Assert.Equal("1500.25", Erp.Api.Data.RecordService.CellValue(f, "$1,500.25").Node!.GetValue<string>());
    }

    [Fact]
    public void BearerScope_niega_lo_mismo_que_el_middleware()
    {
        var token = (string scopes) => new BearerCredential(Erp.Api.Integrations.OAuthTokenHandler.SchemeName, scopes);
        var key = new BearerCredential(Erp.Api.Integrations.ApiKeyHandler.SchemeName);
        // La sesión de la persona no tiene alcances.
        Assert.Null(BearerScope.Check(null, "/api/model/entities", "GET"));
        Assert.Null(BearerScope.Check(token("data.read"), "/api/data/Producto", "GET"));
        var write = BearerScope.Check(token("data.read"), "/api/data/Producto", "POST");
        Assert.Equal("El token no tiene el alcance data.write.", write!.Title);
        Assert.Contains("scope=\"data.write\"", write.WwwAuthenticate);
        Assert.Equal("Un token OAuth solo puede usar la API de datos, reportes y OData.", BearerScope.Check(token("data.read data.write"), "/api/model/entities", "GET")!.Title);
        Assert.Null(BearerScope.Check(key, "/api/data/Producto/1", "PUT"));
        Assert.Equal("Una clave de API solo puede usar la API de datos, reportes y OData.", BearerScope.Check(key, "/api/explorer/entities/Producto", "GET")!.Title);
    }
}
