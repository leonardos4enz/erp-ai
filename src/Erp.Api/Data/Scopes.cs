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
/// <c>Producto.CostoUltimo</c>. Los nombres de tabla y columna salen de los metadatos.
/// </summary>
public sealed class RelatedLoader(SqlConnection cn, SqlTransaction? tx, IReadOnlyDictionary<string, EntityDef> model)
{
    private readonly Dictionary<(string, long), Values?> _rows = new();
    private readonly Dictionary<int, Values?> _users = new();

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
