using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Api.Metadata;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Security;

/// <summary>
/// Spec 012 (H1): la bitácora de datos con la misma seguridad que la pantalla del registro. Quien audita sin ser
/// superusuario solo ve eventos de catálogos que puede leer y de registros dentro de su alcance de lectura (empresa y
/// permisos por registro, de la 006 y la 010), y sin los campos que su rol no ve. Los eventos que no son de datos
/// (sesiones, modelo, seguridad, entornos…) siguen visibles: para eso tiene el permiso de auditoría.
/// </summary>
public static class AuditFilter
{
    /// <summary>Acciones sobre un registro concreto (además de <c>workflow.*</c>).</summary>
    public static readonly IReadOnlyList<string> RecordActions =
        [AuditActions.Create, AuditActions.Update, AuditActions.Delete, "automation.update", "automation.create"];

    /// <summary>Acciones sobre un catálogo (sin registro): se ven si se puede leer el catálogo.</summary>
    public static readonly IReadOnlyList<string> EntityActions = [AuditActions.Import, AuditActions.Export, AuditActions.ViewChange];

    public static bool IsRecordAction(string action) =>
        RecordActions.Contains(action) || action.StartsWith("workflow.", StringComparison.Ordinal);

    public static bool IsDataAction(string action) => IsRecordAction(action) || EntityActions.Contains(action);

    /// <summary>
    /// Condición sobre el alias <c>a</c> de <c>meta.AuditLog</c>; null = sin restricción (superusuario). Los nombres de
    /// acciones y catálogos van como parámetros; los identificadores de tabla salen de los metadatos.
    /// </summary>
    public static SqlPredicate? Build(IReadOnlyDictionary<string, EntityDef> model, UserContext user)
    {
        if (user.IsSuperuser) return null;
        var pars = new List<(string Name, object Value)>();
        string Add(object v)
        {
            var name = $"@aud{pars.Count}";
            pars.Add((name, v));
            return name;
        }
        var record = string.Join(", ", RecordActions.Select(a => Add(a)));
        var entity = string.Join(", ", EntityActions.Select(a => Add(a)));
        var parts = new List<string>
        {
            $"(a.[Action] NOT IN ({record}) AND a.[Action] NOT LIKE 'workflow.%' AND a.[Action] NOT IN ({entity}))",
        };
        var n = 0;
        // Solo catálogos raíz: la bitácora de un documento lleva el nombre del documento (sus partidas van dentro).
        foreach (var e in model.Values.Where(e => !e.IsChild && user.Access(e.Name).Read).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var name = Add(e.Name);
            // Sin IsDeleted: el rastro de un registro eliminado se conserva para quien lo veía.
            if (RecordFilter.For(e, user, write: false) is not { } scope)
            {
                parts.Add($"a.[EntityName] = {name}");
                continue;
            }
            var p = scope.Prefixed($"aue{n++}_");
            pars.AddRange(p.Parameters);
            parts.Add($"(a.[EntityName] = {name} AND (a.[Action] IN ({entity}) OR EXISTS (SELECT 1 FROM {AppTable(e.Name)} t " +
                      $"WHERE t.[Id] = TRY_CAST(a.[RecordId] AS bigint) AND {p.Sql})))");
        }
        return new SqlPredicate($"({string.Join(" OR ", parts)})", pars);
    }

    /// <summary>
    /// Quita del antes/después de un evento de datos los campos que el usuario no ve (también dentro de las partidas).
    /// Devuelve el JSON sin cambios si no es de un catálogo del modelo o si el usuario es superusuario.
    /// </summary>
    public static JsonElement? Redact(string action, string? entityName, JsonElement? json, UserContext user, IReadOnlyDictionary<string, EntityDef> model)
    {
        if (json is not { ValueKind: JsonValueKind.Object } value || user.IsSuperuser || !IsRecordAction(action)
            || entityName is null || !model.TryGetValue(entityName, out var e)) return json;
        var node = JsonNode.Parse(value.GetRawText())!.AsObject();
        Strip(e, node, user, model);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Strip(EntityDef e, JsonObject node, UserContext user, IReadOnlyDictionary<string, EntityDef> model)
    {
        foreach (var key in node.Select(kv => kv.Key).ToList())
        {
            // Campos que ya no existen (archivados o renombrados) también se ocultan: no se sabe quién podía verlos.
            if (e.FindField(key) is not { } f)
            {
                if (!string.Equals(key, Workflow.StateField, StringComparison.OrdinalIgnoreCase)) node.Remove(key);
                continue;
            }
            if (!FieldAccess.CanSee(e, f, user)) { node.Remove(key); continue; }
            if (f.Type == FieldTypes.Lines && node[key] is JsonArray lines && model.TryGetValue(f.ChildEntity ?? "", out var child))
                foreach (var line in lines.OfType<JsonObject>()) Strip(child, line, user, model);
        }
    }
}
