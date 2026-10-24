using Erp.Api.Expressions;
using Erp.Api.Metadata;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

/// <summary>Valores tipados de un registro (.NET), indexados por nombre de campo.</summary>
public sealed class Values : Dictionary<string, object?>
{
    public Values() : base(StringComparer.OrdinalIgnoreCase) { }
    public Values(IDictionary<string, object?> source) : base(source, StringComparer.OrdinalIgnoreCase) { }
}

/// <summary>Documento en memoria: encabezado + partidas por campo <c>lines</c>.</summary>
public sealed class DocDraft
{
    public long? Id { get; init; }
    public int? CreatorId { get; init; }
    /// <summary>Spec 010: empresa del documento guardado (catálogos por empresa).</summary>
    public int? CompanyId { get; init; }
    public Values Header { get; init; } = new();
    public Dictionary<string, List<LineDraft>> Lines { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LineDraft
{
    public long? Id { get; init; }
    public Values Values { get; init; } = new();
}

/// <summary>
/// Carga perezosa (con caché por operación) de registros relacionados para expresiones como
/// <c>Producto.CostoUltimo</c>. Los nombres de tabla y columna salen de los metadatos. Spec 010: también los tipos de
/// cambio de <c>RATE</c> y <c>CONVERT</c>.
/// </summary>
public sealed class RelatedLoader(SqlConnection cn, SqlTransaction? tx, IReadOnlyDictionary<string, EntityDef> model) : ICurrencyRates, ICostLayers
{
    private readonly Dictionary<(string, long), Values?> _rows = new();
    private readonly Dictionary<int, Values?> _users = new();
    private readonly Dictionary<int, (bool IsTeam, bool IsActive)?> _principals = new();
    private readonly Dictionary<(string, string, DateTime), decimal?> _rates = new();

    public SqlConnection Connection => cn;
    public SqlTransaction? Transaction => tx;

    /// <summary>Olvida lo leído: después de que una automatización escribe, la siguiente acción lee datos frescos.</summary>
    public void Reset() => _rows.Clear();

    public Values? Row(string entity, long id)
    {
        if (_rows.TryGetValue((entity, id), out var cached)) return cached;
        if (!model.TryGetValue(entity, out var e)) return null;
        var cols = e.StoredFields.Select(f => Quote(f.Name)).Prepend("[Id]");
        using var cmd = new SqlCommand($"SELECT {string.Join(", ", cols)} FROM {AppTable(e.Name)} WHERE [Id] = @id AND [IsDeleted] = 0", cn, tx);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        using var r = cmd.ExecuteReader();
        Values? row = null;
        if (r.Read())
        {
            row = new Values();
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        }
        return _rows[(entity, id)] = row;
    }

    /// <summary>Spec 006: ¿el registro está dentro del alcance de lectura del usuario?</summary>
    public bool Visible(string entity, long id, Security.UserContext user)
    {
        if (!model.TryGetValue(entity, out var e) || Security.RecordFilter.ForRecordOrParent(e, model, user) is not { } p) return true;
        using var cmd = new SqlCommand($"SELECT 1 FROM {AppTable(e.Name)} t WHERE t.[Id] = @id AND t.[IsDeleted] = 0 AND {p.Sql}", cn, tx);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        p.AddTo(cmd.Parameters);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Spec 010: el tipo más reciente en o antes de la fecha, directo (de → a) o inverso (a → de, 1 / tasa); con la misma
    /// fecha gana el directo. Monedas ya validadas por el evaluador; van como parámetros.
    /// </summary>
    public decimal? Rate(string from, string to, DateTime date)
    {
        if (_rates.TryGetValue((from, to, date), out var cached)) return cached;
        using var cmd = new SqlCommand("""
            SELECT TOP 1 x.Inverse, x.Rate FROM (
                SELECT 0 AS Inverse, Rate, RateDate FROM meta.ExchangeRate WHERE FromCurrency = @f AND ToCurrency = @t AND RateDate <= @d
                UNION ALL
                SELECT 1, Rate, RateDate FROM meta.ExchangeRate WHERE FromCurrency = @t AND ToCurrency = @f AND RateDate <= @d
            ) x ORDER BY x.RateDate DESC, x.Inverse
            """, cn, tx);
        cmd.Parameters.Add(new SqlParameter("@f", System.Data.SqlDbType.Char, 3) { Value = from });
        cmd.Parameters.Add(new SqlParameter("@t", System.Data.SqlDbType.Char, 3) { Value = to });
        cmd.Parameters.Add(new SqlParameter("@d", System.Data.SqlDbType.Date) { Value = date.Date });
        using var r = cmd.ExecuteReader();
        decimal? rate = null;
        if (r.Read())
        {
            var value = r.GetDecimal(1);
            rate = r.GetInt32(0) == 0 ? value : decimal.Round(1m / value, 10);
        }
        return _rates[(from, to, date)] = rate;
    }

    /// <summary>Spec 020: UNITCOST. Lee las capas abiertas de la llave en la empresa del contexto (catálogo por empresa).</summary>
    public decimal? UnitCost(string book, decimal quantity, IReadOnlyList<object?> key, string? company)
    {
        if (quantity <= 0 || key.Count == 0 || key.Any(k => k is null) || !model.TryGetValue(book, out var b)) return null;
        int? companyId = null;
        if (b.PerCompany)
        {
            using var co = new SqlCommand("SELECT Id FROM meta.Company WHERE Code = @c", cn, tx);
            co.Parameters.Add(new SqlParameter("@c", company ?? ""));
            if (co.ExecuteScalar() is not int id) return null;
            companyId = id;
        }
        using var cmd = new SqlCommand(
            "SELECT Id, Quantity, Value FROM meta.CostLayer WHERE Book = @b AND KeyHash = @h AND Quantity > 0 ORDER BY Id", cn, tx);
        cmd.Parameters.Add(new SqlParameter("@b", b.Name));
        cmd.Parameters.Add(new SqlParameter("@h", CostMath.KeyHash(b, companyId, key!)));
        using var r = cmd.ExecuteReader();
        var layers = new List<CostLayerState>();
        while (r.Read()) layers.Add(new(r.GetInt64(0), r.GetDecimal(1), r.GetDecimal(2)));
        return CostMath.PeekUnitCost(layers, quantity);
    }

    /// <summary>Spec 015: ¿el principal es un equipo y está activo? Null si no existe.</summary>
    public (bool IsTeam, bool IsActive)? Principal(int id)
    {
        if (_principals.TryGetValue(id, out var cached)) return cached;
        using var cmd = new SqlCommand("SELECT IsTeam, IsActive FROM meta.[User] WHERE Id = @id", cn, tx);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        using var r = cmd.ExecuteReader();
        (bool, bool)? row = r.Read() ? (r.GetBoolean(0), r.GetBoolean(1)) : null;
        return _principals[id] = row;
    }

    public Values? User(int id)
    {
        if (_users.TryGetValue(id, out var cached)) return cached;
        using var cmd = new SqlCommand("SELECT Username, DisplayName FROM meta.[User] WHERE Id = @id", cn, tx);
        cmd.Parameters.Add(new SqlParameter("@id", id));
        using var r = cmd.ExecuteReader();
        Values? row = r.Read() ? new Values { ["Usuario"] = r.GetString(0), ["Nombre"] = r.GetString(1) } : null;
        return _users[id] = row;
    }
}

/// <summary>Ámbito de evaluación de un encabezado o de una partida.</summary>
public sealed class DocScope(EntityDef entity, Values values, RelatedLoader loader,
    IReadOnlyDictionary<string, IReadOnlyList<IExprScope>>? lines = null, DocScope? parent = null) : IExprScope
{
    public object? Field(string name) => values.GetValueOrDefault(name);

    public object? Related(string refField, string field)
    {
        var f = entity.FindField(refField);
        if (f is null) return null;
        // En una partida, el campo padre apunta al documento en memoria (puede no estar guardado aún).
        if (f.Type == FieldTypes.Parent && parent is not null) return parent.Field(field);
        var raw = values.GetValueOrDefault(f.Name);
        if (raw is null) return null;
        var id = Convert.ToInt64(raw);
        var row = f.Type == FieldTypes.User ? loader.User((int)id) : loader.Row(f.ReferenceEntity!, id);
        return row?.GetValueOrDefault(field);
    }

    public IReadOnlyList<IExprScope> Lines(string linesField) =>
        lines is not null && lines.TryGetValue(linesField, out var l) ? l : [];
}
