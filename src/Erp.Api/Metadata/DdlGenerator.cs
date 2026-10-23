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
        FieldTypes.Reference or FieldTypes.Parent => "bigint",
        FieldTypes.Autonumber => "nvarchar(30)",
        FieldTypes.State => "nvarchar(50)",
        FieldTypes.User => "int",
        FieldTypes.Formula => f.ResultType switch
        {
            FieldTypes.Decimal => $"decimal({f.Precision ?? 18},{f.Scale ?? 2})",
            FieldTypes.Integer => "bigint",
            FieldTypes.Text => "nvarchar(400)",
            FieldTypes.Boolean => "bit",
            FieldTypes.Date => "date",
            _ => throw new InvalidOperationException($"Tipo de resultado no soportado: {f.ResultType}"),
        },
        _ => throw new InvalidOperationException($"Tipo de campo no soportado: {f.Type}"),
    };

    /// <summary>Las columnas dinámicas siempre son NULL: la obligatoriedad la aplica el motor (spec 001).</summary>
    private static string ColumnDefinition(string entity, FieldDef f)
    {
        var sb = new StringBuilder($"{Quote(f.Name)} {ColumnType(f)} NULL");
        if (f.Type is FieldTypes.Reference or FieldTypes.Parent)
            sb.Append($" CONSTRAINT {Constraint("FK", entity, f.Name)} REFERENCES {AppTable(f.ReferenceEntity!)} ([Id])");
        else if (f.Type == FieldTypes.User)
            sb.Append($" CONSTRAINT {Constraint("FK", entity, f.Name)} REFERENCES [meta].[User] ([Id])");
        return sb.ToString();
    }

    /// <summary>Columna de estado del proceso; los registros existentes quedan en el estado inicial.</summary>
    public static IReadOnlyList<string> AddStateColumn(string entity, string initialState)
    {
        Quote(initialState); // nombre de estado validado como identificador: seguro como literal
        var col = Quote(Workflow.StateField);
        return
        [
            $"ALTER TABLE {AppTable(entity)} ADD {col} nvarchar(50) NULL;",
            // Se ejecuta en lote aparte: SQL Server no ve la columna nueva en el mismo lote.
            $"UPDATE {AppTable(entity)} SET {col} = N'{initialState}' WHERE {col} IS NULL;",
            $"CREATE INDEX {Constraint("IX", entity, Workflow.StateField)} ON {AppTable(entity)} ({col}) WHERE [IsDeleted] = 0;",
        ];
    }

    public static IReadOnlyList<string> CreateTable(string entity, IEnumerable<FieldDef> fields)
    {
        var list = fields.ToList();
        var cols = new List<string>
        {
            $"[Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT {Constraint("PK", entity)} PRIMARY KEY",
        };
        cols.AddRange(list.Where(f => !FieldTypes.IsVirtual(f.Type)).Select(f => ColumnDefinition(entity, f)));
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
        if (FieldTypes.IsVirtual(f.Type)) return [];
        var statements = new List<string> { $"ALTER TABLE {AppTable(entity)} ADD {ColumnDefinition(entity, f)};" };
        statements.AddRange(Indexes(entity, f));
        return statements;
    }

    private static IEnumerable<string> Indexes(string entity, FieldDef f)
    {
        if (f.Unique || f.Type == FieldTypes.Autonumber)
            yield return $"CREATE UNIQUE INDEX {Constraint("UX", entity, f.Name)} ON {AppTable(entity)} ({Quote(f.Name)}) " +
                         $"WHERE {Quote(f.Name)} IS NOT NULL AND [IsDeleted] = 0;";
        else if (f.Type is FieldTypes.Reference or FieldTypes.Parent or FieldTypes.User)
            yield return $"CREATE INDEX {Constraint("IX", entity, f.Name)} ON {AppTable(entity)} ({Quote(f.Name)});";
    }
}
