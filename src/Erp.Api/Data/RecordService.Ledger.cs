using System.Globalization;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

/// <summary>
/// Spec 020 · libro de partida doble: el motor exige el cuadre en los estados contabilizados y que el documento caiga en
/// un periodo abierto, por cualquier vía de escritura (captura, API, importación, automatizaciones). Ver plan 020 §1.
/// </summary>
public sealed partial class RecordService
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Estado y fecha del documento antes del cambio (null en un alta).</summary>
    private sealed record LedgerBefore(string? State, object? Date);

    private static LedgerBefore? LedgerSnapshot(EntityDef e, IReadOnlyDictionary<string, object?> values) =>
        e.Ledger is not { } l ? null
        : new(values.GetValueOrDefault(Workflow.StateField) as string, l.Date is null ? null : values.GetValueOrDefault(l.Date));

    /// <summary>
    /// Revisa el libro del documento (si lo es) y, si <paramref name="e"/> es el catálogo de periodos de algún libro, que
    /// el registro no se traslape con otro. <paramref name="draft"/> nulo = el documento se elimina (solo cuenta «antes»).
    /// </summary>
    private static async Task CheckLedgerAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, IReadOnlyDictionary<string, EntityDef> model,
        DocDraft? draft, int? companyId, LedgerBefore? before, long? id, CancellationToken ct)
    {
        if (draft is not null) await CheckPeriodOverlapAsync(cn, tx, e, model, draft, companyId, id, ct);
        if (e.Ledger is not { } l) return;
        var errors = new Dictionary<string, string[]>();

        var state = draft?.Header.GetValueOrDefault(Workflow.StateField) as string;
        if (draft is not null && l.IsPosted(state)) BalanceErrors(e, l, model, draft, errors);
        if (errors.Count > 0) throw new ValidationFailed(errors);

        if (l.Period is null) return;
        var dates = new List<object?>();
        if (before is not null && l.IsPosted(before.State)) dates.Add(before.Date);
        if (draft is not null && l.IsPosted(state)) dates.Add(draft.Header.GetValueOrDefault(l.Date!));
        foreach (var date in dates.Distinct())
        {
            if (await PeriodErrorAsync(cn, tx, e, l, model, date, companyId, ct) is { } message)
            {
                errors[l.Date!] = [message];
                throw new ValidationFailed(errors);
            }
        }
    }

    /// <summary>Al menos dos partidas; cada una con cargo o abono (no ambos, no negativos); cargos = abonos.</summary>
    private static void BalanceErrors(EntityDef e, LedgerDef l, IReadOnlyDictionary<string, EntityDef> model, DocDraft draft, Dictionary<string, string[]> errors)
    {
        var linesField = e.FindField(l.Lines)!;
        var child = model[linesField.ChildEntity!];
        var debitField = child.FindField(l.Debit)!;
        var creditField = child.FindField(l.Credit)!;
        var lines = draft.Lines.GetValueOrDefault(linesField.Name) ?? [];
        decimal debits = 0m, credits = 0m;
        for (var i = 0; i < lines.Count; i++)
        {
            var d = ExprEvaluator.Normalize(lines[i].Values.GetValueOrDefault(debitField.Name)) as decimal? ?? 0m;
            var c = ExprEvaluator.Normalize(lines[i].Values.GetValueOrDefault(creditField.Name)) as decimal? ?? 0m;
            var key = $"{linesField.Name}[{i}].{debitField.Name}";
            if (d < 0 || c < 0) errors[key] = [$"{debitField.Label} y {creditField.Label} no pueden ser negativos."];
            else if ((d > 0) == (c > 0)) errors[key] = [$"Cada partida lleva un {debitField.Label.ToLowerInvariant()} o un {creditField.Label.ToLowerInvariant()} (no ambos, ni ninguno)."];
            debits += d;
            credits += c;
        }
        if (lines.Count < 2)
            errors[linesField.Name] = [$"La partida doble necesita al menos dos partidas: un {debitField.Label.ToLowerInvariant()} y un {creditField.Label.ToLowerInvariant()}."];
        else if (debits != credits)
            errors["registro"] = [$"No cuadra: {debitField.Label.ToLowerInvariant()}s {debits.ToString("N2", Mx)} y {creditField.Label.ToLowerInvariant()}s " +
                                  $"{credits.ToString("N2", Mx)} (diferencia {Math.Abs(debits - credits).ToString("N2", Mx)}). La partida doble exige que sean iguales."];
    }

    /// <summary>
    /// Mensaje si la fecha no cae en exactamente un periodo abierto (o null si sí). El rango se lee con <c>HOLDLOCK</c> hasta el
    /// final de la transacción: cerrar el periodo (un UPDATE de su fila) espera a que esta termine (H1 CA6).
    /// </summary>
    private static async Task<string?> PeriodErrorAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, LedgerDef l,
        IReadOnlyDictionary<string, EntityDef> model, object? rawDate, int? companyId, CancellationToken ct)
    {
        var p = l.Period!;
        var dateField = e.FindField(l.Date!)!;
        if (rawDate is not DateTime date) return $"{dateField.Label} es obligatoria: el documento se contabiliza en el periodo de su fecha.";
        var pe = model[p.Entity];
        var start = pe.FindField(p.Start)!;
        var end = pe.FindField(p.End)!;
        var cols = pe.StoredFields.Select(f => Quote(f.Name)).Prepend("[Id]");
        var company = pe.PerCompany ? $" AND {Quote(DdlGenerator.CompanyColumn)} = @co" : "";
        var rows = new List<Values>();
        await using (var cmd = new SqlCommand(
            $"SELECT TOP 2 {string.Join(", ", cols)} FROM {AppTable(pe.Name)} WITH (HOLDLOCK) " +
            $"WHERE {Quote(start.Name)} <= @d AND {Quote(end.Name)} >= @d AND [IsDeleted] = 0{company} ORDER BY [Id]", cn, tx))
        {
            cmd.Parameters.Add(new SqlParameter("@d", System.Data.SqlDbType.Date) { Value = date.Date });
            if (pe.PerCompany) cmd.Parameters.Add(new SqlParameter("@co", (object?)companyId ?? 0));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(ReadValues(r));
        }
        var day = date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        if (rows.Count == 0)
            return $"No hay un periodo contable que incluya el {day}: créalo en {pe.LabelPlural} antes de contabilizar.";
        if (rows.Count > 1) return $"Hay más de un periodo que incluye el {day}: corrige los traslapes en {pe.LabelPlural}.";
        var loader = new RelatedLoader(cn, tx, model);
        if (ExprEvaluator.Test(p.Open, new DocScope(pe, rows[0], loader), ExprEnv.System, whenNull: false)) return null;
        var name = pe.EffectiveDisplayField is { } df && rows[0].GetValueOrDefault(df) is { } n ? $"«{n}» " : "";
        return $"El periodo {name}está cerrado: no se registran, cambian ni cancelan {e.LabelPlural.ToLowerInvariant()} contabilizadas con fecha del {day}.";
    }

    /// <summary>Un periodo no se traslapa con otro vigente de la misma empresa (si el catálogo es de periodos de un libro).</summary>
    private static async Task CheckPeriodOverlapAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, IReadOnlyDictionary<string, EntityDef> model,
        DocDraft draft, int? companyId, long? id, CancellationToken ct)
    {
        foreach (var p in LedgerRules.PeriodsIn(e.Name, model).Select(x => x.Period).DistinctBy(p => (p.Start.ToUpperInvariant(), p.End.ToUpperInvariant())))
        {
            var start = e.FindField(p.Start)!;
            var end = e.FindField(p.End)!;
            if (draft.Header.GetValueOrDefault(start.Name) is not DateTime s || draft.Header.GetValueOrDefault(end.Name) is not DateTime f) continue;
            var company = e.PerCompany ? $" AND {Quote(DdlGenerator.CompanyColumn)} = @co" : "";
            await using var cmd = new SqlCommand(
                $"SELECT TOP 1 [Id] FROM {AppTable(e.Name)} WITH (UPDLOCK, HOLDLOCK) WHERE {Quote(start.Name)} <= @f AND {Quote(end.Name)} >= @s " +
                $"AND [IsDeleted] = 0 AND [Id] <> @id{company}", cn, tx);
            cmd.Parameters.Add(new SqlParameter("@s", System.Data.SqlDbType.Date) { Value = s.Date });
            cmd.Parameters.Add(new SqlParameter("@f", System.Data.SqlDbType.Date) { Value = f.Date });
            cmd.Parameters.Add(new SqlParameter("@id", id ?? 0));
            if (e.PerCompany) cmd.Parameters.Add(new SqlParameter("@co", (object?)companyId ?? 0));
            if (await cmd.ExecuteScalarAsync(ct) is long other)
            {
                var loader = new RelatedLoader(cn, tx, model);
                var name = e.EffectiveDisplayField is { } df && loader.Row(e.Name, other)?.GetValueOrDefault(df) is { } n ? $"«{n}»" : $"#{other}";
                throw new ValidationFailed(new Dictionary<string, string[]> { [start.Name] = [$"Se traslapa con el periodo {name}: dos periodos no pueden compartir fechas."] });
            }
        }
    }
}
