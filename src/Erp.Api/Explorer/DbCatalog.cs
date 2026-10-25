using System.Data.Common;
using Dapper;

namespace Erp.Api.Explorer;

/// <summary>Spec 025: columna tal como la describe el catálogo de SQL Server (tipo ya formateado).</summary>
public sealed record CatColumn(string Name, string Type, bool Nullable, bool Identity, string? DefaultName, string? DefaultDefinition,
    string? Computed);

public sealed record CatIndex(string Name, bool PrimaryKey, bool Unique, bool UniqueConstraint, string? Filter,
    IReadOnlyList<string> Columns, IReadOnlyList<string> Included);

public sealed record CatForeignKey(string Name, IReadOnlyList<string> Columns, string RefSchema, string RefTable,
    IReadOnlyList<string> RefColumns, string OnDelete);

public sealed record CatTable(string Schema, string Name, IReadOnlyList<CatColumn> Columns, IReadOnlyList<CatIndex> Indexes,
    IReadOnlyList<CatForeignKey> ForeignKeys);

/// <summary>Spec 025: nombre de tipo SQL a partir de <c>sys.types</c> y <c>sys.columns</c>, como se escribe en un DDL.</summary>
public static class SqlTypes
{
    private static readonly HashSet<string> Unicode = new(StringComparer.OrdinalIgnoreCase) { "nvarchar", "nchar" };
    private static readonly HashSet<string> Sized = new(StringComparer.OrdinalIgnoreCase) { "varchar", "char", "varbinary", "binary", "nvarchar", "nchar" };
    private static readonly HashSet<string> Exact = new(StringComparer.OrdinalIgnoreCase) { "decimal", "numeric" };
    private static readonly HashSet<string> Fractional = new(StringComparer.OrdinalIgnoreCase) { "datetime2", "time", "datetimeoffset" };

    /// <param name="maxLength">En bytes (<c>sys.columns.max_length</c>); -1 = <c>max</c>.</param>
    public static string Format(string type, int maxLength, int precision, int scale)
    {
        var t = type.ToLowerInvariant();
        if (t == "timestamp") return "rowversion"; // así lo nombra sys.types; el motor lo declara rowversion
        if (Sized.Contains(t))
            return maxLength == -1 ? $"{t}(max)" : $"{t}({(Unicode.Contains(t) ? maxLength / 2 : maxLength)})";
        if (Exact.Contains(t)) return $"{t}({precision},{scale})";
        if (Fractional.Contains(t)) return $"{t}({scale})";
        return t;
    }
}

/// <summary>
/// Spec 025: lee la estructura de un esquema del catálogo de SQL Server. Constitución II.1: el esquema llega a SQL como
/// parámetro y solo uno de la lista blanca (<see cref="Allowed"/>); nunca se consulta una fila de datos ni un conteo.
/// </summary>
public static class DbCatalog
{
    public const string App = "app";
    public const string Meta = "meta";

    /// <summary>Esquema permitido para quien pregunta, o null. El valor que se usa es el de la lista, no el de la petición.</summary>
    public static string? Allowed(string? requested, bool superuser) =>
        string.Equals(requested ?? App, App, StringComparison.OrdinalIgnoreCase) ? App
        : superuser && string.Equals(requested, Meta, StringComparison.OrdinalIgnoreCase) ? Meta
        : null;

    private const string Sql = """
        SELECT t.object_id AS ObjectId, t.name AS Name
          FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
         WHERE s.name = @schema AND t.is_ms_shipped = 0;

        SELECT c.object_id AS ObjectId, c.column_id AS ColumnId, c.name AS Name, ty.name AS TypeName,
               CAST(c.max_length AS int) AS MaxLength, CAST(c.precision AS int) AS [Precision], CAST(c.scale AS int) AS Scale,
               c.is_nullable AS IsNullable, c.is_identity AS IsIdentity, dc.name AS DefaultName, dc.definition AS DefaultDefinition,
               cc.definition AS Computed
          FROM sys.columns c
          JOIN sys.tables t ON t.object_id = c.object_id
          JOIN sys.schemas s ON s.schema_id = t.schema_id
          JOIN sys.types ty ON ty.user_type_id = c.user_type_id
          LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
          LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
         WHERE s.name = @schema AND t.is_ms_shipped = 0;

        SELECT i.object_id AS ObjectId, i.index_id AS IndexId, i.name AS Name, i.is_primary_key AS PrimaryKey,
               i.is_unique AS IsUnique, i.is_unique_constraint AS UniqueConstraint, i.filter_definition AS Filter
          FROM sys.indexes i
          JOIN sys.tables t ON t.object_id = i.object_id
          JOIN sys.schemas s ON s.schema_id = t.schema_id
         WHERE s.name = @schema AND t.is_ms_shipped = 0 AND i.type > 0 AND i.is_hypothetical = 0;

        SELECT ic.object_id AS ObjectId, ic.index_id AS IndexId, c.name AS Name, CAST(ic.key_ordinal AS int) AS KeyOrdinal,
               ic.index_column_id AS ColumnOrdinal, ic.is_included_column AS Included
          FROM sys.index_columns ic
          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
          JOIN sys.tables t ON t.object_id = ic.object_id
          JOIN sys.schemas s ON s.schema_id = t.schema_id
         WHERE s.name = @schema AND t.is_ms_shipped = 0;

        SELECT fk.object_id AS ObjectId, fk.parent_object_id AS ParentId, fk.name AS Name, rs.name AS RefSchema, rt.name AS RefTable,
               fk.delete_referential_action_desc AS OnDelete
          FROM sys.foreign_keys fk
          JOIN sys.tables t ON t.object_id = fk.parent_object_id
          JOIN sys.schemas s ON s.schema_id = t.schema_id
          JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
          JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
         WHERE s.name = @schema AND t.is_ms_shipped = 0;

        SELECT fkc.constraint_object_id AS ForeignKeyId, fkc.constraint_column_id AS Ordinal, pc.name AS ColumnName, rc.name AS RefColumn
          FROM sys.foreign_key_columns fkc
          JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
          JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
          JOIN sys.tables t ON t.object_id = fkc.parent_object_id
          JOIN sys.schemas s ON s.schema_id = t.schema_id
         WHERE s.name = @schema AND t.is_ms_shipped = 0;
        """;

    /// <param name="schema">Uno de <see cref="App"/> o <see cref="Meta"/> (ya pasado por <see cref="Allowed"/>).</param>
    public static async Task<IReadOnlyList<CatTable>> ReadAsync(DbConnection cn, string schema, CancellationToken ct = default)
    {
        if (schema is not (App or Meta)) throw new ArgumentException("Esquema no permitido.", nameof(schema));
        using var grid = await cn.QueryMultipleAsync(new CommandDefinition(Sql, new { schema }, cancellationToken: ct));
        var tables = (await grid.ReadAsync<TableRow>()).ToList();
        var columns = (await grid.ReadAsync<ColumnRow>()).ToLookup(c => c.ObjectId);
        var indexes = (await grid.ReadAsync<IndexRow>()).ToList();
        var indexColumns = (await grid.ReadAsync<IndexColumnRow>()).ToLookup(c => (c.ObjectId, c.IndexId));
        var fks = (await grid.ReadAsync<ForeignKeyRow>()).ToList();
        var fkColumns = (await grid.ReadAsync<ForeignKeyColumnRow>()).ToLookup(c => c.ForeignKeyId);

        return tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => new CatTable(
            schema, t.Name,
            columns[t.ObjectId].OrderBy(c => c.ColumnId).Select(c => new CatColumn(c.Name,
                SqlTypes.Format(c.TypeName, c.MaxLength, c.Precision, c.Scale), c.IsNullable, c.IsIdentity,
                c.DefaultName, c.DefaultDefinition, c.Computed)).ToList(),
            indexes.Where(i => i.ObjectId == t.ObjectId).OrderBy(i => i.IndexId).Select(i =>
            {
                var cols = indexColumns[(i.ObjectId, i.IndexId)].ToList();
                return new CatIndex(i.Name ?? $"index_{i.IndexId}", i.PrimaryKey, i.IsUnique, i.UniqueConstraint, i.Filter,
                    cols.Where(c => !c.Included).OrderBy(c => c.KeyOrdinal).Select(c => c.Name).ToList(),
                    cols.Where(c => c.Included).OrderBy(c => c.ColumnOrdinal).Select(c => c.Name).ToList());
            }).ToList(),
            fks.Where(f => f.ParentId == t.ObjectId).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f =>
            {
                var cols = fkColumns[f.ObjectId].OrderBy(c => c.Ordinal).ToList();
                return new CatForeignKey(f.Name, cols.Select(c => c.ColumnName).ToList(), f.RefSchema, f.RefTable,
                    cols.Select(c => c.RefColumn).ToList(), f.OnDelete);
            }).ToList())).ToList();
    }

    private sealed record TableRow(int ObjectId, string Name);
    private sealed record ColumnRow(int ObjectId, int ColumnId, string Name, string TypeName, int MaxLength, int Precision, int Scale,
        bool IsNullable, bool IsIdentity, string? DefaultName, string? DefaultDefinition, string? Computed);
    private sealed record IndexRow(int ObjectId, int IndexId, string? Name, bool PrimaryKey, bool IsUnique, bool UniqueConstraint, string? Filter);
    private sealed record IndexColumnRow(int ObjectId, int IndexId, string Name, int KeyOrdinal, int ColumnOrdinal, bool Included);
    private sealed record ForeignKeyRow(int ObjectId, int ParentId, string Name, string RefSchema, string RefTable, string OnDelete);
    private sealed record ForeignKeyColumnRow(int ForeignKeyId, int Ordinal, string ColumnName, string RefColumn);
}
