using Erp.Api.Expressions;

namespace Erp.Api.Metadata;

/// <summary>
/// Spec 020 · validación estática del libro de partida doble: partidas, cargo y abono numéricos, fecha, estados y
/// catálogo de periodos. Pura, como <see cref="AutomationRules"/>.
/// </summary>
public static class LedgerRules
{
    public static IEnumerable<string> Validate(EntityDef e, LedgerDef l, IReadOnlyDictionary<string, EntityDef> model, IReadOnlySet<string> roles)
    {
        if (e.IsChild) { yield return "una partida no puede ser un libro: decláralo en su documento."; yield break; }

        if (e.FindField(l.Lines ?? "") is not { IsArchived: false, Type: FieldTypes.Lines, ChildEntity: { } childName } ||
            !model.TryGetValue(childName, out var child) || child.IsArchived)
        {
            yield return $"'{l.Lines}' no es un campo de partidas de {e.Name}.";
        }
        else
        {
            foreach (var (name, what) in new[] { (l.Debit, "cargo"), (l.Credit, "abono") })
                if (child.FindField(name ?? "") is not { IsArchived: false } f || !ExprAnalyzer.IsNumeric(f))
                    yield return $"el {what} '{name}' debe ser un campo numérico de {child.Name}.";
            if (string.Equals(l.Debit, l.Credit, StringComparison.OrdinalIgnoreCase))
                yield return "cargo y abono deben ser campos distintos.";
        }

        if (l.Date is not null && e.FindField(l.Date) is not { IsArchived: false, Type: FieldTypes.Date or FieldTypes.DateTime })
            yield return $"'{l.Date}' debe ser un campo de fecha de {e.Name}.";

        if (l.States is { } states)
        {
            if (e.Workflow is null) yield return $"{e.Name} no tiene proceso: omite los estados (todo queda contabilizado).";
            else if (states.Count == 0) yield return "indica al menos un estado contabilizado u omite la lista.";
            else
                foreach (var s in states.Where(s => e.Workflow.FindState(s) is null))
                    yield return $"el estado '{s}' no existe en el proceso de {e.Name}.";
        }

        if (l.Period is not { } p) yield break;
        if (l.Date is null) yield return "los periodos necesitan el campo de fecha del documento (date).";
        if (!model.TryGetValue(p.Entity ?? "", out var pe) || pe.IsArchived) { yield return $"el catálogo de periodos '{p.Entity}' no existe."; yield break; }
        if (pe.IsChild || string.Equals(pe.Name, e.Name, StringComparison.OrdinalIgnoreCase))
        { yield return $"'{pe.Name}' no puede ser el catálogo de periodos de {e.Name}."; yield break; }
        if (pe.PerCompany && !e.PerCompany) yield return $"{e.Name} es compartido entre empresas y sus periodos ({pe.Name}) son por empresa.";
        foreach (var (name, what) in new[] { (p.Start, "inicio"), (p.End, "fin") })
            if (pe.FindField(name ?? "") is not { IsArchived: false, Type: FieldTypes.Date })
                yield return $"el {what} del periodo '{name}' debe ser un campo de fecha de {pe.Name}.";
        if (string.IsNullOrWhiteSpace(p.Open)) yield return "indica cuándo está abierto un periodo (open).";
        else
            foreach (var err in ExprAnalyzer.Analyze(p.Open, pe, model, roles).Errors)
                yield return $"periodo abierto: {err}";
    }

    /// <summary>Libros cuyos periodos viven en <paramref name="periodEntity"/> (para impedir traslapes al guardar un periodo).</summary>
    public static IEnumerable<(EntityDef Book, LedgerPeriod Period)> PeriodsIn(string periodEntity, IReadOnlyDictionary<string, EntityDef> model) =>
        model.Values.Where(e => !e.IsArchived && e.Ledger?.Period is { } p && string.Equals(p.Entity, periodEntity, StringComparison.OrdinalIgnoreCase))
            .Select(e => (e, e.Ledger!.Period!));
}
