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
/// Spec 034 (auditoría 6 A-07): toda acción está en una de las cuatro listas y el filtro deniega por omisión: una acción
/// nueva sin clasificar no la ve quien no es superusuario (antes la veía cualquier auditor, aunque no leyera el catálogo).
/// </summary>
public static class AuditFilter
{
    /// <summary>Acciones sobre un registro con sus valores en el antes/después (además de <c>workflow.*</c>): se recortan por campo.</summary>
    public static readonly IReadOnlyList<string> RecordActions =
        [AuditActions.Create, AuditActions.Update, AuditActions.Delete, AuditActions.AutomationUpdate, AuditActions.AutomationCreate];

    /// <summary>
    /// Spec 034: acciones que nombran un registro sin llevar sus valores (el PDF: catálogo, registro y plantilla). Se filtran
    /// como las de registro; su antes/después no se recorta (no son campos).
    /// </summary>
    public static readonly IReadOnlyList<string> ReferenceActions = [AuditActions.PrintPdf];

    /// <summary>
    /// Acciones sobre un catálogo (sin registro): se ven si se puede leer el catálogo. Spec 034: también la configuración que
    /// vive en un catálogo (tareas programadas, reintentos de trabajos y webhooks): el nombre y el resultado son del catálogo.
    /// </summary>
    public static readonly IReadOnlyList<string> EntityActions =
    [
        AuditActions.Import, AuditActions.Export, AuditActions.ViewChange, AuditActions.ScheduleChange, AuditActions.AutomationRetry,
        AuditActions.WebhookChange,
    ];

    /// <summary>
    /// Spec 034: lo que no es de datos y ve todo auditor (sesiones, modelo, seguridad, integraciones, entornos, empresas,
    /// asistente, módulos, imágenes, Studio). <c>model.change</c> nombra catálogos: es el modelo, que el permiso de auditoría
    /// cubre (012). Una acción que no esté en ninguna lista no se le muestra a quien no es superusuario.
    /// </summary>
    public static readonly IReadOnlyList<string> OtherActions =
    [
        AuditActions.LoginOk, AuditActions.LoginFailed, AuditActions.Logout, AuditActions.ModelChange, AuditActions.UserChange,
        AuditActions.RoleChange, AuditActions.ApiKeyChange, AuditActions.OAuthClientChange, AuditActions.OAuthToken,
        AuditActions.ModuleInstall, AuditActions.ModuleUpgrade, AuditActions.EnvironmentExport, AuditActions.EnvironmentUpload,
        AuditActions.EnvironmentRejected, AuditActions.EnvironmentApprove, AuditActions.EnvironmentReject, AuditActions.EnvironmentApply,
        AuditActions.EnvironmentRevertRequest, AuditActions.EnvironmentRevert, AuditActions.CompanyChange, AuditActions.RateChange,
        AuditActions.CompanySwitch, AuditActions.AssistantPropose, AuditActions.AssistantApply, AuditActions.AssistantDiscard,
        AuditActions.AssistantKey, AuditActions.UnitChange, AuditActions.TeamChange, AuditActions.SecurityRequest,
        AuditActions.SecurityApprove, AuditActions.SecurityReject, AuditActions.FileChange, AuditActions.StudioStart,
        AuditActions.StudioFinish, AuditActions.StudioCancel, AuditActions.StudioDiscard, AuditActions.StudioApply,
    ];

    public static bool IsRecordAction(string action) =>
        RecordActions.Contains(action) || action.StartsWith(AuditActions.WorkflowPrefix, StringComparison.Ordinal);

    public static bool IsDataAction(string action) => IsRecordAction(action) || ReferenceActions.Contains(action) || EntityActions.Contains(action);

    /// <summary>¿Está en alguna lista? (La guardia de <c>Hardening5Tests</c> lo exige a cada constante de <see cref="AuditActions"/>.)</summary>
    public static bool IsClassified(string action) => IsDataAction(action) || OtherActions.Contains(action);

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
        var entity = string.Join(", ", EntityActions.Select(a => Add(a)));
        // Spec 034: lo que no es de datos, por lista (denegar por omisión), no «lo que no está en las de datos».
        var parts = new List<string> { $"a.[Action] IN ({string.Join(", ", OtherActions.Select(a => Add(a)))})" };
        var record = string.Join(", ", RecordActions.Concat(ReferenceActions).Select(a => Add(a)));
        var recordAction = $"(a.[Action] IN ({record}) OR a.[Action] LIKE 'workflow.%')";
        var n = 0;
        // Solo catálogos raíz: la bitácora de un documento lleva el nombre del documento (sus partidas van dentro).
        foreach (var e in model.Values.Where(e => !e.IsChild && user.Access(e.Name).Read).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var name = Add(e.Name);
            // Sin IsDeleted: el rastro de un registro eliminado se conserva para quien lo veía.
            if (RecordFilter.For(e, user, write: false) is not { } scope)
            {
                parts.Add($"(a.[EntityName] = {name} AND (a.[Action] IN ({entity}) OR {recordAction}))");
                continue;
            }
            var p = scope.Prefixed($"aue{n++}_");
            pars.AddRange(p.Parameters);
            parts.Add($"(a.[EntityName] = {name} AND (a.[Action] IN ({entity}) OR {recordAction} AND EXISTS (SELECT 1 FROM {AppTable(e.Name)} t " +
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
