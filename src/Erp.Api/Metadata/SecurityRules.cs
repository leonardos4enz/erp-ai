using Erp.Api.Security;

namespace Erp.Api.Metadata;

/// <summary>Spec 006: validación estática de la política de seguridad de un catálogo (ver plan 006).</summary>
public static class SecurityRules
{
    public const int MaxFieldRules = 200;

    public static IEnumerable<string> Validate(EntityDef e, SecurityPolicy s, IReadOnlyDictionary<string, EntityDef> model, IReadOnlySet<string> roles)
    {
        var msgs = new List<string>();
        // El estado de una partida es el de su documento.
        var workflow = e.IsChild && model.TryGetValue(e.ParentEntity ?? "", out var parent) ? parent.Workflow : e.Workflow;

        void CheckRoles(IReadOnlyList<string>? list, string where)
        {
            foreach (var r in list ?? [])
                if (!roles.Contains(r)) msgs.Add($"{where}: el rol '{r}' no existe.");
        }

        if (e.IsChild && (s.Records is not null || s.Owner is not null || s.SystemManaged))
            msgs.Add("es una partida: el dueño, los permisos por registro y «lo administra el sistema» se declaran en su documento.");

        var fields = s.Fields ?? [];
        if (fields.Count > MaxFieldRules) msgs.Add($"máximo {MaxFieldRules} reglas de campo.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fs in fields)
        {
            var f = e.FindField(fs.Field ?? "");
            var where = $"campo '{fs.Field}'";
            if (f is null || f.IsArchived) { msgs.Add($"{where}: no existe en {e.Name}."); continue; }
            if (!seen.Add(f.Name)) msgs.Add($"{where}: está repetido.");
            if (f.Type is FieldTypes.State or FieldTypes.Parent) { msgs.Add($"{where}: el estado y el documento padre no admiten seguridad por campo."); continue; }
            CheckRoles(fs.Read, where);
            CheckRoles(fs.Edit, where);
            if (f.ClientLocked && (fs.Edit is not null || fs.EditableIn is not null))
                msgs.Add($"{where}: lo calcula o asigna el sistema; solo admite «read».");
            if (fs.EditableIn is not null)
            {
                if (workflow is null) msgs.Add($"{where}: «editableIn» requiere que el {(e.IsChild ? "documento" : "catálogo")} tenga proceso.");
                else
                    foreach (var st in fs.EditableIn)
                        if (workflow.FindState(st) is null) msgs.Add($"{where}: el estado '{st}' no existe en el proceso.");
            }
            if (fs.Read is not null && string.Equals(e.EffectiveDisplayField, f.Name, StringComparison.OrdinalIgnoreCase))
                msgs.Add($"{where}: es el campo para mostrar del catálogo (aparece en referencias y buscadores); no se puede ocultar.");
            // Igual que en la spec 004: un obligatorio sin valor por defecto debe poder capturarlo quien da de alta.
            if (f.Required && f.DefaultValue is null && !f.ClientLocked &&
                (fs.Read is not null || fs.Edit is not null ||
                 (fs.EditableIn is not null && workflow is not null && !fs.EditableIn.Contains(workflow.Initial, StringComparer.OrdinalIgnoreCase))))
                msgs.Add($"{where}: es obligatorio y sin valor por defecto; todos deben poder capturarlo al crear (o dale un valor por defecto).");
        }

        if (s.Owner is not null && e.FindField(s.Owner) is not { IsArchived: false, Type: FieldTypes.User })
            msgs.Add($"el dueño '{s.Owner}' debe ser un campo de tipo usuario de {e.Name}.");

        if (s.Records is { } grants)
        {
            if (grants.Count == 0) msgs.Add("«records» vacío dejaría el catálogo sin acceso: declara al menos un permiso u omite «records».");
            if (grants.Count > RecordFilter.MaxGrants) msgs.Add($"máximo {RecordFilter.MaxGrants} permisos por registro.");
            for (var i = 0; i < grants.Count; i++)
            {
                var g = grants[i];
                var where = $"permiso {i + 1}";
                if (g.Roles is { Count: 0 }) msgs.Add($"{where}: indica los roles u omite «roles» para todos.");
                CheckRoles(g.Roles, where);
                if (!RecordScopes.Valid.Contains(g.EffectiveScope)) msgs.Add($"{where}: alcance '{g.Scope}' inválido (all, unit u own).");
                if (g.Access is not null && g.Access is not ("read" or "write")) msgs.Add($"{where}: acceso '{g.Access}' inválido (read o write).");
                if (!string.IsNullOrWhiteSpace(g.Where) && RecordFilter.Check(g.Where, e, roles) is { } err) msgs.Add($"{where} (where): {err}");
            }
        }
        return msgs;
    }
}
