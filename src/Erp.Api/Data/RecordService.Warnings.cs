using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

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

    internal const string HiddenWarnings = "La acción no se puede ejecutar: el registro incumple una regla del catálogo. Pide ayuda a quien administra el proceso.";

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
    /// <param name="warnings">Los del motor (con su autoridad): deciden si se bloquea y se guardan en la auditoría.</param>
    /// <param name="shown">
    /// Spec 031 (H1, H4; auditoría 5 A-01 y A-04): lo que se le dice o se le pregunta a la persona, evaluado con su alcance en
    /// cada salto y sin los avisos que leen campos ocultos (lo mismo que le da <c>/compute</c>). Nunca se intersecta con
    /// <paramref name="warnings"/>: decir cuáles del suyo coinciden con los del motor revelaría un bit por aviso.
    /// </param>
    private static void CheckWarnings(WorkflowTransition t, IReadOnlyList<RuleWarning> warnings, IReadOnlyCollection<string>? accepted,
        Func<List<RuleWarning>> shown)
    {
        switch (t.OnWarning ?? WarningPolicies.Confirm)
        {
            // Spec 029 (H4 CA3, auditoría 4 D-08): bloquea con los del motor, pero solo dice los que el usuario puede ver (si no,
            // un mensaje genérico). Que bloquee o no es un bit del motor, como las condiciones de transición (R-029-3).
            case WarningPolicies.Block when warnings.Count > 0:
                var told = shown();
                if (told.Count > 0) throw new ValidationFailed(WarningMap(told));
                throw new AppException(StatusCodes.Status400BadRequest, HiddenWarnings);
            // Se pregunta por lo que la persona ve, aunque el motor no tenga avisos: así la pregunta no revela nada.
            case WarningPolicies.Confirm when accepted is not null:
                var visible = shown();
                if (visible.Any(w => !accepted.Contains(w.Message)))
                    throw new AppException(StatusCodes.Status428PreconditionRequired,
                        "La acción tiene avisos: revísalos y confirma para continuar.", WarningMap(visible));
                break;
        }
    }

    /// <summary>
    /// Spec 031 (H1): los avisos que se le dicen a <paramref name="user"/> sobre <paramref name="basis"/> (una copia sin
    /// calcular): <paramref name="prepare"/> la calcula con un cargador que respeta su alcance en cada salto (como
    /// <c>/compute</c>) y se quitan los que leen campos ocultos. Nada si no ve el registro (una automatización que actúa sobre
    /// otro) o si calcular para él falla: el motor ya decidió con su autoridad.
    /// </summary>
    private static List<RuleWarning> ShownWarnings(SqlConnection cn, SqlTransaction? tx, EntityDef e, IReadOnlyDictionary<string, EntityDef> model,
        DocDraft basis, UserContext user, long? id, Action<DocDraft, RelatedLoader, ExprEnv> prepare)
    {
        if (!HasWarningRules(e, model)) return [];
        var loader = new RelatedLoader(cn, tx, model, viewer: user);
        if (id is { } rid && !loader.Visible(e.Name, rid, user)) return [];
        var d = CloneDraft(basis);
        var env = Env(user, d, loader);
        try
        {
            prepare(d, loader, env);
            return VisibleWarnings(Warnings(d, e, model, loader, env), model, user);
        }
        // Revisión adversarial F-F: también la aritmética (desbordes con los saltos vacíos de la persona). Un error de SQL no se
        // atrapa: puede dejar la transacción condenada.
        catch (Exception ex) when (ex is ExprException or AppException or ArithmeticException) { return []; }
    }

    /// <summary>Copia del borrador (encabezado y partidas): los valores son escalares.</summary>
    private static DocDraft CloneDraft(DocDraft d)
    {
        var copy = new DocDraft { Id = d.Id, CreatorId = d.CreatorId, CompanyId = d.CompanyId, Header = new Values(d.Header) };
        foreach (var (field, lines) in d.Lines)
            copy.Lines[field] = lines.Select(l => new LineDraft { Id = l.Id, Values = new Values(l.Values) }).ToList();
        return copy;
    }
}
