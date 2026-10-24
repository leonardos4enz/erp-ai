using System.Globalization;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

/// <summary>
/// Spec 020 · acción <c>cost</c>: valúa el registro con capas de costo (<c>meta.CostLayer</c>) por llave, con promedio
/// ponderado o FIFO, y deja el costo en el registro y el valor restante en el saldo. Ver plan 020 §2.
/// </summary>
public sealed partial class RecordService
{
    private async Task CostAsync(AutoCtx c, EntityDef e, long id, Values values, IExprScope scope, ExprEnv env, AutomationAction a)
    {
        var book = c.Model[a.Entity!];
        var keys = new List<(FieldDef Dest, object Value)>();
        foreach (var m in a.Match!)
        {
            if (values.GetValueOrDefault(e.FindField(m.Source)!.Name) is not { } v) return; // sin llave no hay capas que mover
            keys.Add((book.FindField(m.Field)!, v));
        }
        var quantity = ExprEvaluator.Normalize(values.GetValueOrDefault(e.FindField(a.Quantity!)!.Name)) as decimal? ?? 0m;
        var method = Convert.ToString(ExprEvaluator.Evaluate(a.Method!, scope, env), CultureInfo.InvariantCulture)?.Trim().ToLowerInvariant() ?? "";
        var unitCost = a.UnitCost is null ? null : ExprEvaluator.Normalize(ExprEvaluator.Evaluate(a.UnitCost, scope, env)) as decimal?;
        var fallback = a.FallbackCost is null ? null : ExprEvaluator.Normalize(ExprEvaluator.Evaluate(a.FallbackCost, scope, env)) as decimal?;
        // Spec 022: políticas declaradas en la acción (omitidas = 020) y costo total exacto de una entrada.
        var options = new CostOptions(
            a.UncostedEntry ?? CostPolicies.Average,
            a.MethodChange == CostPolicies.Merge,
            a.TotalCost is null ? null : ExprEvaluator.Normalize(ExprEvaluator.Evaluate(a.TotalCost, scope, env)) as decimal?);
        var costField = e.FindField(a.CostField!)!;
        var scale = ScaleOf(costField);

        // Mismo recurso que el acumulado por coincidencia: las capas de una llave las mueve una transacción a la vez.
        var keyValues = keys.Select(k => k.Value).ToList();
        await AppLockAsync(c.Cn, c.Tx, RollupLockKey(book, keyValues), c.Ct);
        int? company = book.PerCompany ? c.User.Company?.Id ?? 0 : null;
        var hash = CostMath.KeyHash(book, company, keyValues);

        var open = new List<CostLayerState>();
        await using (var cmd = new SqlCommand(
            "SELECT Id, Quantity, Value FROM meta.CostLayer WITH (UPDLOCK, HOLDLOCK) WHERE Book = @b AND KeyHash = @h AND Quantity > 0 ORDER BY Id", c.Cn, c.Tx))
        {
            cmd.Parameters.Add(new SqlParameter("@b", book.Name));
            cmd.Parameters.Add(new SqlParameter("@h", hash));
            await using var r = await cmd.ExecuteReaderAsync(c.Ct);
            while (await r.ReadAsync(c.Ct)) open.Add(new(r.GetInt64(0), r.GetDecimal(1), r.GetDecimal(2)));
        }

        var result = CostMath.Apply(open, method, quantity, unitCost, fallback, scale, options);
        var before = open.ToDictionary(l => l.Id!.Value);
        foreach (var l in result.Layers)
        {
            if (l.Id is { } lid && before[lid] == l) continue;
            if (l is { Id: null, Quantity: 0m, Value: 0m }) continue; // capa fundida (022) que ya se consumió o volvió a fundir
            await using var cmd = new SqlCommand { Connection = c.Cn, Transaction = c.Tx };
            if (l.Id is { } existing)
            {
                cmd.CommandText = "UPDATE meta.CostLayer SET Quantity = @q, Value = @v WHERE Id = @id";
                cmd.Parameters.Add(new SqlParameter("@id", existing));
            }
            else
            {
                cmd.CommandText = """
                    INSERT meta.CostLayer (Book, KeyHash, CompanyId, SourceEntity, SourceId, Quantity, Value)
                    VALUES (@b, @h, @co, @se, @si, @q, @v)
                    """;
                cmd.Parameters.Add(new SqlParameter("@b", book.Name));
                cmd.Parameters.Add(new SqlParameter("@h", hash));
                cmd.Parameters.Add(new SqlParameter("@co", (object?)company ?? DBNull.Value));
                cmd.Parameters.Add(new SqlParameter("@se", e.Name));
                cmd.Parameters.Add(new SqlParameter("@si", id));
            }
            cmd.Parameters.Add(new SqlParameter("@q", System.Data.SqlDbType.Decimal) { Precision = 28, Scale = 8, Value = l.Quantity });
            cmd.Parameters.Add(new SqlParameter("@v", System.Data.SqlDbType.Decimal) { Precision = 28, Scale = 8, Value = l.Value });
            await cmd.ExecuteNonQueryAsync(c.Ct);
        }

        // El costo queda en el propio registro (con signo: una salida resta valor).
        var set = new List<(FieldDef Field, object? Value)> { (costField, Coerce(costField, result.Cost)) };
        if (a.UnitCostField is not null && quantity != 0)
        {
            var uf = e.FindField(a.UnitCostField)!;
            set.Add((uf, Coerce(uf, result.UnitCost)));
        }
        await SystemUpdateAsync(c, e, id, set);

        // El saldo de la llave recibe lo que queda en las capas (recalculado, no incrementado). Sin saldo, solo se omite.
        if (await FindByMatchAsync(c, book, keys) is { } balanceId)
        {
            var vf = book.FindField(a.Field!)!;
            var balance = new List<(FieldDef Field, object? Value)> { (vf, Coerce(vf, result.Remaining)) };
            // Spec 022: la cantidad que queda en las capas, para un promedio de lo costeado (no de toda la existencia).
            if (a.QuantityField is not null && book.FindField(a.QuantityField) is { } qf) balance.Add((qf, Coerce(qf, result.RemainingQuantity)));
            await SystemUpdateAsync(c, book, balanceId, balance);
        }
    }

    private static int ScaleOf(FieldDef f) => (f.Type == FieldTypes.Formula ? f.ResultType : f.Type) == FieldTypes.Integer ? 0 : f.Scale ?? 2;

    /// <summary>Registro vigente de <paramref name="target"/> cuyos campos coinciden (en la empresa del contexto si es por empresa), bloqueado.</summary>
    private static async Task<long?> FindByMatchAsync(AutoCtx c, EntityDef target, IReadOnlyList<(FieldDef Dest, object Value)> keys)
    {
        await using var find = new SqlCommand { Connection = c.Cn, Transaction = c.Tx };
        // Spec 010: en un catálogo por empresa el destino es el de la misma empresa: nunca se acumula en otra.
        var company = target.PerCompany ? $" AND {Quote(DdlGenerator.CompanyColumn)} = @company" : "";
        find.CommandText = $"SELECT TOP 1 [Id] FROM {AppTable(target.Name)} WITH (UPDLOCK, HOLDLOCK) WHERE " +
                           string.Join(" AND ", keys.Select((k, i) => $"{Quote(k.Dest.Name)} = @f{i}")) + company + " AND [IsDeleted] = 0 ORDER BY [Id]";
        for (var i = 0; i < keys.Count; i++) find.Parameters.Add(new SqlParameter($"@f{i}", keys[i].Value));
        if (target.PerCompany) find.Parameters.Add(new SqlParameter("@company", (object?)c.User.Company?.Id ?? 0));
        return await find.ExecuteScalarAsync(c.Ct) as long?;
    }
}
