using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Erp.Api.Metadata;

/// <summary>
/// Spec 021 (H2): huellas para que un cambio de modelo no pise el de otra persona. <see cref="Of"/> es la versión de un
/// catálogo (el editor la manda con la que abrió); <see cref="Base"/> cubre lo que un plan toca tal como estaba al revisar
/// la vista previa, y aplicar la exige.
/// </summary>
public static class ModelVersion
{
    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>Versión de la definición guardada (sin lo que se agrega por usuario al leerla).</summary>
    public static string Of(EntityDef e) => Sha(JsonSerializer.Serialize(e with { Version = null }, MetadataStore.Json));

    public static string Of(DashboardDef d) => Sha(JsonSerializer.Serialize(d, MetadataStore.Json));

    public static string Of(AppDef a) => Sha(JsonSerializer.Serialize(a, MetadataStore.Json));

    /// <summary>Huella de los catálogos, tableros y (spec 024) módulos tocados (un nombre que aún no existe cuenta como «-»).</summary>
    public static string Base(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, DashboardDef> boards,
        IReadOnlySet<string> entities, IReadOnlySet<string>? dashboards,
        IReadOnlyDictionary<string, AppDef>? apps = null, IReadOnlySet<string>? touchedApps = null)
    {
        var sb = new StringBuilder();
        foreach (var name in entities.Order(StringComparer.OrdinalIgnoreCase))
            sb.Append("e:").Append(name.ToLowerInvariant()).Append('=').Append(model.TryGetValue(name, out var e) ? Of(e) : "-").Append('\n');
        foreach (var name in (dashboards ?? new HashSet<string>()).Order(StringComparer.OrdinalIgnoreCase))
            sb.Append("d:").Append(name.ToLowerInvariant()).Append('=').Append(boards.TryGetValue(name, out var d) ? Of(d) : "-").Append('\n');
        foreach (var name in (touchedApps ?? new HashSet<string>()).Order(StringComparer.OrdinalIgnoreCase))
            sb.Append("a:").Append(name.ToLowerInvariant()).Append('=').Append(apps is not null && apps.TryGetValue(name, out var a) ? Of(a) : "-").Append('\n');
        return Sha(sb.ToString());
    }

    /// <summary>Catálogos cuya versión ya no es la que el cliente abrió.</summary>
    public static IEnumerable<string> Stale(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, string>? opened) =>
        (opened ?? new Dictionary<string, string>())
        .Where(kv => !model.TryGetValue(kv.Key, out var e) || !string.Equals(Of(e), kv.Value, StringComparison.OrdinalIgnoreCase))
        .Select(kv => model.TryGetValue(kv.Key, out var e) ? e.Label : kv.Key);
}
