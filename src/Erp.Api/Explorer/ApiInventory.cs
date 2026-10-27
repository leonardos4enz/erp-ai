using System.Reflection;
using Erp.Api.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace Erp.Api.Explorer;

/// <summary>
/// Spec 033: el inventario de rutas (el mismo de <c>docs/sdd/rutas.md</c>, leído de los metadatos de cada endpoint en vivo)
/// y el registro de riesgos (<c>docs/sdd/riesgos.md</c>, incrustado al compilar), para el explorador y las herramientas del
/// agente. Solo metadatos del sistema: ningún dato de registros.
/// </summary>
public static class ApiInventory
{
    public const int MaxQuery = 100;

    public sealed record RouteInfo(string Method, string Route, string Capability, string? Effect, string? Gate, string? Costly);

    public sealed record RiskInfo(string Id, string Text, string Severity, string Decision, string State);

    public static IReadOnlyList<RouteInfo> Routes(EndpointDataSource endpoints, string? q) =>
        endpoints.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<CapabilityMetadata>() is not null)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"]).Select(m => (Method: m, Endpoint: e)))
            .Select(x => new RouteInfo(x.Method, "/" + (x.Endpoint.RoutePattern.RawText ?? "").TrimStart('/'),
                x.Endpoint.Metadata.GetMetadata<CapabilityMetadata>()!.ToString(),
                x.Endpoint.Metadata.GetMetadata<EffectMetadata>()?.Effect.ToString(), x.Endpoint.Metadata.GetMetadata<EffectMetadata>()?.Gate,
                x.Endpoint.Metadata.GetMetadata<CostlyMetadata>()?.Why))
            .Where(r => q is null || r.Route.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Route, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal).ToList();

    private static readonly Lazy<IReadOnlyList<RiskInfo>> AllRisks = new(LoadRisks);

    public static IReadOnlyList<RiskInfo> Risks(string? q) =>
        AllRisks.Value.Where(r => q is null || r.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Filas «| R-NNN-k | riesgo | severidad | decisión | prueba | estado | revisado |» del registro.</summary>
    private static IReadOnlyList<RiskInfo> LoadRisks()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("riesgos.md");
        if (stream is null) return [];
        using var reader = new StreamReader(stream);
        var list = new List<RiskInfo>();
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("| R-", StringComparison.Ordinal)) continue;
            var cells = line.Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 8) continue;
            list.Add(new RiskInfo(cells[1], cells[2], cells[3], cells[4], cells[6]));
        }
        return list;
    }
}
