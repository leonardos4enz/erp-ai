using System.Text;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Metadata;

/// <summary>
/// Genera DDL a partir de metadatos ya validados. No recibe texto libre del usuario:
/// todo identificador pasa por <see cref="Identifiers.Quote"/> y los tipos salen de una lista cerrada.
/// </summary>
public static class DdlGenerator
{
    public static string ColumnType(FieldDef f) => f.Type switch
    {
        FieldTypes.Text => $"nvarchar({f.MaxLength ?? FieldTypes.DefaultTextLength})",
        FieldTypes.LongText => "nvarchar(max)",
        FieldTypes.Integer => "bigint",
        FieldTypes.Decimal => $"decimal({f.Precision ?? 18},{f.Scale ?? 2})",
        FieldTypes.Boolean => "bit",
        FieldTypes.Date => "date",
        FieldTypes.DateTime => "datetime2(0)",
        FieldTypes.Choice => $"nvarchar({FieldTypes.MaxChoiceLength})",
        FieldTypes.Reference => "bigint",
        _ => throw new InvalidOperationException($"Tipo de campo no soportado: {f.Type}"),
    };

    /// <summary>Las columnas dinámicas siempre son NULL: la obligatoriedad la aplica el motor (spec 001).</summary>
    private static string ColumnDefinition(string entity, FieldDef f)
    {
        var sb = new StringBuilder($"{Quote(f.Name)} {ColumnType(f)} NULL");
        if (f.Type == FieldTypes.Reference)
            sb.Append($" CONSTRAINT {Constraint("FK", entity, f.Name)} REFERENCES {AppTable(f.ReferenceEntity!)} ([Id])");
        return sb.ToString();
    }

    public static IReadOnlyList<string> CreateTable(string entity, IEnumerable<FieldDef> fields)
    {
        var list = fields.ToList();
        var cols = new List<string>
        {
            $"[Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT {Constraint("PK", entity)} PRIMARY KEY",
        };
        cols.AddRange(list.Select(f => ColumnDefinition(entity, f)));
        cols.AddRange(
        [
            $"[CreatedAt] datetime2(3) NOT NULL CONSTRAINT {Constraint("DF", entity, "CreatedAt")} DEFAULT SYSUTCDATETIME()",
            "[CreatedBy] int NOT NULL",
            "[UpdatedAt] datetime2(3) NULL",
            "[UpdatedBy] int NULL",
            $"[IsDeleted] bit NOT NULL CONSTRAINT {Constraint("DF", entity, "IsDeleted")} DEFAULT (0)",
            "[RowVer] rowversion NOT NULL",
        ]);

        var statements = new List<string>
        {
            $"CREATE TABLE {AppTable(entity)} (\n    {string.Join(",\n    ", cols)}\n);",
        };
        foreach (var f in list) statements.AddRange(Indexes(entity, f));
        return statements;
    }

    public static IReadOnlyList<string> AddColumn(string entity, FieldDef f)
    {
        var statements = new List<string> { $"ALTER TABLE {AppTable(entity)} ADD {ColumnDefinition(entity, f)};" };
        statements.AddRange(Indexes(entity, f));
        return statements;
    }

    private static IEnumerable<string> Indexes(string entity, FieldDef f)
    {
        if (f.Unique)
            yield return $"CREATE UNIQUE INDEX {Constraint("UX", entity, f.Name)} ON {AppTable(entity)} ({Quote(f.Name)}) " +
                         $"WHERE {Quote(f.Name)} IS NOT NULL AND [IsDeleted] = 0;";
        else if (f.Type == FieldTypes.Reference)
            yield return $"CREATE INDEX {Constraint("IX", entity, f.Name)} ON {AppTable(entity)} ({Quote(f.Name)});";
    }
}
