using System.Text;

namespace Erp.Api.Explorer;

/// <summary>
/// Spec 025: script de estructura (solo <c>CREATE</c>, nunca datos) de las tablas tal como existen en SQL Server. Es un
/// archivo para descargar: el explorador no lo ejecuta. Los nombres se citan como <c>QUOTENAME</c> (<c>]</c> → <c>]]</c>).
/// </summary>
public static class DdlScript
{
    public static string Name(string name) => "[" + name.Replace("]", "]]") + "]";
    private static string Table(string schema, string name) => $"{Name(schema)}.{Name(name)}";
    private static string List(IEnumerable<string> names) => string.Join(", ", names.Select(Name));

    public static string For(string schema, IEnumerable<DbTable> tables, DateTime generatedAt)
    {
        var list = tables.ToList();
        var sb = new StringBuilder();
        sb.Append("-- Estructura del esquema ").Append(schema).Append(" (").Append(list.Count).Append(list.Count == 1 ? " tabla" : " tablas")
          .Append("), generada por el explorador del modelo el ").Append(generatedAt.ToString("yyyy-MM-dd HH:mm 'UTC'")).Append('.').AppendLine();
        sb.AppendLine("-- Solo estructura: no incluye datos. Generado del catálogo de SQL Server, no de los metadatos.");
        sb.AppendLine();

        foreach (var t in list)
        {
            sb.Append("CREATE TABLE ").Append(Table(t.Schema, t.Name)).AppendLine(" (");
            var lines = t.Columns.Select(Column).ToList();
            if (t.PrimaryKey is { } pk)
                lines.Add($"CONSTRAINT {Name(pk.Name)} PRIMARY KEY ({List(pk.Columns)})");
            foreach (var u in t.Indexes.Where(i => i.UniqueConstraint))
                lines.Add($"CONSTRAINT {Name(u.Name)} UNIQUE ({List(u.Columns)})");
            sb.AppendLine(string.Join(",\n", lines.Select(l => "    " + l)));
            sb.AppendLine(");");
            foreach (var i in t.Indexes.Where(i => !i.PrimaryKey && !i.UniqueConstraint))
            {
                sb.Append("CREATE ").Append(i.Unique ? "UNIQUE " : "").Append("INDEX ").Append(Name(i.Name))
                  .Append(" ON ").Append(Table(t.Schema, t.Name)).Append(" (").Append(List(i.Columns)).Append(')');
                if (i.Included.Count > 0) sb.Append(" INCLUDE (").Append(List(i.Included)).Append(')');
                if (i.Filter is not null) sb.Append(" WHERE ").Append(i.Filter);
                sb.AppendLine(";");
            }
            sb.AppendLine();
        }

        // Las llaves foráneas al final: así el script no depende del orden de las tablas.
        var fks = list.SelectMany(t => t.ForeignKeys.Select(f => (t, f))).ToList();
        if (fks.Count > 0) sb.AppendLine("-- Llaves foráneas");
        foreach (var (t, f) in fks)
        {
            sb.Append("ALTER TABLE ").Append(Table(t.Schema, t.Name)).Append(" ADD CONSTRAINT ").Append(Name(f.Name))
              .Append(" FOREIGN KEY (").Append(List(f.Columns)).Append(") REFERENCES ").Append(Table(f.RefSchema, f.RefTable))
              .Append(" (").Append(List(f.RefColumns)).Append(')');
            if (f.OnDelete is { } d && d != "NO_ACTION") sb.Append(" ON DELETE ").Append(d.Replace('_', ' '));
            sb.AppendLine(";");
        }
        return sb.ToString();
    }

    private static string Column(DbColumn c)
    {
        if (c.Computed is not null) return $"{Name(c.Name)} AS {c.Computed}";
        var sb = new StringBuilder(Name(c.Name)).Append(' ').Append(c.Type);
        if (c.Identity) sb.Append(" IDENTITY(1,1)");
        sb.Append(c.Nullable ? " NULL" : " NOT NULL");
        if (c.Default is { } d)
        {
            if (d.Name is not null) sb.Append(" CONSTRAINT ").Append(Name(d.Name));
            sb.Append(" DEFAULT ").Append(d.Definition);
        }
        return sb.ToString();
    }
}
