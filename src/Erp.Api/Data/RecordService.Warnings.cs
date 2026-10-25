using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Data;

/// <summary>Spec 023: regla de aviso incumplida. <c>Key</c> como en los errores: campo, <c>Partidas[i].Campo</c> o «registro».</summary>
internal sealed record RuleWarning(string Key, string Message, EntityDef Entity, BusinessRule Rule);

/// <summary>Spec 023: avisos de cada fila de una importación (la fila se importa igual).</summary>
internal sealed class WarningSink
{
    public List<RuleWarning> Items { get; } = [];
}

public sealed partial class RecordService
{
    /// <summary>
    /// Avisos que el usuario puede ver: se descarta el de una regla que lee un campo oculto para él (su resultado revelaría
    /// el dato), como las condiciones de layout (spec 007). La auditoría guarda todos.
    /// </summary>
    internal static List<RuleWarning> VisibleWarnings(IEnumerable<RuleWarning> warnings, IReadOnlyDictionary<string, EntityDef> model, UserContext user)
    {
        return warnings.Where(w => !Reads(w.Rule.When, w.Entity) && !Reads(w.Rule.Assert, w.Entity)).ToList();

        bool Reads(string? expr, EntityDef e)
        {
            if (string.IsNullOrWhiteSpace(expr)) return false;
            try { return ReadsHidden(ExprParser.Parse(expr), e, model, user); }
            catch (ExprException) { return true; }
        }
    }

    internal static Dictionary<string, string[]> WarningMap(IEnumerable<RuleWarning> warnings) =>
        warnings.GroupBy(w => w.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(w => w.Message).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Lo que se audita: el registro y, si se guardó con avisos, cuáles (constitución II.4).</summary>
    private static Dictionary<string, object?> AuditAfter(DocDraft d, IReadOnlyList<RuleWarning> warnings)
    {
        var snap = Snapshot(d);
        if (warnings.Count > 0) snap["_warnings"] = WarningMap(warnings);
        return snap;
    }

    /// <summary>
    /// Política de la transición ante avisos (spec 023). <paramref name="accepted"/> nulo = la ejecuta una automatización.
    /// </summary>
    private static void CheckWarnings(WorkflowTransition t, IReadOnlyList<RuleWarning> warnings, IReadOnlyCollection<string>? accepted,
        IReadOnlyDictionary<string, EntityDef> model, UserContext user)
    {
        if (warnings.Count == 0) return;
        switch (t.OnWarning ?? WarningPolicies.Confirm)
        {
            case WarningPolicies.Block:
                throw new ValidationFailed(WarningMap(warnings));
            case WarningPolicies.Confirm when accepted is not null:
                var visible = VisibleWarnings(warnings, model, user);
                if (visible.Any(w => !accepted.Contains(w.Message)))
                    throw new AppException(StatusCodes.Status428PreconditionRequired,
                        "La acción tiene avisos: revísalos y confirma para continuar.", WarningMap(visible));
                break;
        }
    }
}
