using Erp.Api.Metadata;

namespace Erp.Api.Explorer;

public sealed record AppRef(string Name, string Label, string Icon, int Order);
public sealed record DbEntityRef(string Name, string Label, string App, string AppLabel, bool IsArchived, bool IsChild);
public sealed record DbFieldRef(string Name, string Label, string Type, bool IsArchived);
public sealed record DbDefault(string? Name, string Definition);
public sealed record DbKey(string Name, IReadOnlyList<string> Columns);

/// <param name="Status"><c>ok</c>, <c>system</c>, <c>archived</c> (de un campo archivado), <c>orphan</c> (sin metadato) o <c>mismatch</c>.</param>
public sealed record DbColumn(string Name, string Type, bool Nullable, bool Identity, DbDefault? Default, string? Computed, bool System,
    DbFieldRef? Field, string Status, string? ExpectedType);

/// <param name="Nullable">Alguna de sus columnas acepta nulos (del lado referido: «cero o uno»).</param>
/// <param name="Unique">Un índice único cubre exactamente sus columnas (del lado que refiere: «cero o uno»).</param>
public sealed record DbForeignKey(string Name, IReadOnlyList<string> Columns, string RefSchema, string RefTable, IReadOnlyList<string> RefColumns,
    bool Nullable, bool Unique, string OnDelete);

public sealed record DbIndex(string Name, bool Unique, bool PrimaryKey, bool UniqueConstraint, string? Filter, IReadOnlyList<string> Columns,
    IReadOnlyList<string> Included);

public sealed record DbTable(string Schema, string Name, DbEntityRef? Entity, IReadOnlyList<DbColumn> Columns, DbKey? PrimaryKey,
    IReadOnlyList<DbForeignKey> ForeignKeys, IReadOnlyList<DbIndex> Indexes, int DriftCount);

public static class DriftKinds
{
    public const string OrphanTable = "orphanTable";
    public const string ArchivedTable = "archivedTable";
    public const string MissingTable = "missingTable";
    public const string OrphanColumn = "orphanColumn";
    public const string ArchivedColumn = "archivedColumn";
    public const string MissingColumn = "missingColumn";
    public const string TypeMismatch = "typeMismatch";
}

public sealed record DbDrift(string Kind, string Table, string? Column, string Message);
public sealed record DbExternal(string Schema, string Name);
public sealed record DbSchema(string Schema, IReadOnlyList<DbTable> Tables, IReadOnlyList<DbExternal> External, IReadOnlyList<DbDrift> Drift,
    IReadOnlyList<AppRef> Apps);

/// <summary>
/// Spec 025: une el catálogo de SQL Server con el modelo. Puro, como <see cref="PlanCompiler"/>: la tabla <c>app.X</c> es la
/// del catálogo <c>X</c>; se marca lo que existe en SQL sin metadato (o con uno archivado), lo que el modelo espera y no
/// existe, y el tipo que no es el que generaría <see cref="DdlGenerator.ColumnType"/>.
/// </summary>
public static class PhysicalModel
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    public const int MaxQuery = 100;

    public static IReadOnlyList<AppRef> AppRefs(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, AppDef> apps) =>
        Apps.Effective(model, apps, _ => true).Select(a => new AppRef(a.Name, a.Label, a.Icon, a.Order)).ToList();

    /// <summary>Módulo de un catálogo; una partida va con el de su documento.</summary>
    public static string AppOf(EntityDef e, IReadOnlyDictionary<string, EntityDef> model) =>
        e.IsChild && model.TryGetValue(e.ParentEntity!, out var p) ? Apps.Of(p) : Apps.Of(e);

    public static DbSchema Build(string schema, IReadOnlyList<CatTable> catalog, IReadOnlyDictionary<string, EntityDef> model,
        IReadOnlyDictionary<string, AppDef> apps, string? app = null, string? q = null)
    {
        var appRefs = AppRefs(model, apps);
        var labels = appRefs.ToDictionary(a => a.Name, a => a.Label, Ci);
        var linked = schema == DbCatalog.App;
        var drift = new List<DbDrift>();
        var tables = new List<DbTable>();
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        foreach (var t in catalog)
        {
            var e = linked && model.TryGetValue(t.Name, out var def) ? def : null;
            var appName = e is null ? null : AppOf(e, model);
            var tableDrift = new List<DbDrift>();
            if (linked && e is null)
                tableDrift.Add(new(DriftKinds.OrphanTable, t.Name, null, $"La tabla app.{t.Name} no corresponde a ningún catálogo."));
            else if (e?.IsArchived == true)
                tableDrift.Add(new(DriftKinds.ArchivedTable, t.Name, null, $"El catálogo {e.Name} está archivado; su tabla y sus registros se conservan."));

            var columns = t.Columns.Select(c => Column(c, e, t.Name, tableDrift)).ToList();
            if (e is not null) tableDrift.AddRange(Missing(e, t));

            var pk = t.Indexes.FirstOrDefault(i => i.PrimaryKey);
            var uniqueSets = t.Indexes.Where(i => i.Unique).Select(i => i.Columns.ToHashSet(Ci)).ToList();
            var table = new DbTable(t.Schema, t.Name,
                e is null ? null : new DbEntityRef(e.Name, e.Label, appName!, labels.GetValueOrDefault(appName!) ?? appName!, e.IsArchived, e.IsChild),
                columns, pk is null ? null : new DbKey(pk.Name, pk.Columns),
                t.ForeignKeys.Select(f => new DbForeignKey(f.Name, f.Columns, f.RefSchema, f.RefTable, f.RefColumns,
                    f.Columns.Any(c => t.Columns.FirstOrDefault(x => Ci.Equals(x.Name, c))?.Nullable != false),
                    uniqueSets.Any(s => s.SetEquals(f.Columns)), f.OnDelete)).ToList(),
                t.Indexes.Select(i => new DbIndex(i.Name, i.Unique, i.PrimaryKey, i.UniqueConstraint, i.Filter, i.Columns, i.Included)).ToList(),
                tableDrift.Count);

            if (app is not null && (appName is null || !Ci.Equals(appName, app))) continue;
            if (q is not null && !Matches(table, q)) continue;
            tables.Add(table);
            drift.AddRange(tableDrift);
        }

        if (linked)
        {
            var present = catalog.Select(t => t.Name).ToHashSet(Ci);
            foreach (var e in model.Values.Where(e => !e.IsArchived && !present.Contains(e.Name)).OrderBy(e => e.Name, Ci))
            {
                var appName = AppOf(e, model);
                if (app is not null && !Ci.Equals(appName, app)) continue;
                if (q is not null && !Contains(e.Name, q) && !Contains(e.Label, q) && !Contains(e.LabelPlural, q)) continue;
                drift.Add(new(DriftKinds.MissingTable, e.Name, null, $"El catálogo {e.Name} no tiene tabla app.{e.Name}."));
            }
        }

        var shown = tables.Select(t => (t.Schema, t.Name)).ToHashSet();
        var external = tables.SelectMany(t => t.ForeignKeys)
            .Where(f => !shown.Contains((f.RefSchema, f.RefTable)))
            .Select(f => new DbExternal(f.RefSchema, f.RefTable)).Distinct()
            .OrderBy(x => x.Schema).ThenBy(x => x.Name, Ci).ToList();
        return new(schema, tables, external, drift, appRefs);
    }

    private static DbColumn Column(CatColumn c, EntityDef? e, string table, List<DbDrift> drift)
    {
        var dflt = c.DefaultDefinition is null ? null : new DbDefault(c.DefaultName, c.DefaultDefinition);
        if (e is null) return new(c.Name, c.Type, c.Nullable, c.Identity, dflt, c.Computed, false, null, "ok", null);
        if (Identifiers.SystemColumns.Contains(c.Name))
            return new(c.Name, c.Type, c.Nullable, c.Identity, dflt, c.Computed, true, null, "system", null);
        var f = e.FindField(c.Name);
        if (f is null)
        {
            drift.Add(new(DriftKinds.OrphanColumn, table, c.Name, $"La columna {c.Name} no corresponde a ningún campo de {e.Name}."));
            return new(c.Name, c.Type, c.Nullable, c.Identity, dflt, c.Computed, false, null, "orphan", null);
        }
        var field = new DbFieldRef(f.Name, f.Label, f.Type, f.IsArchived);
        if (f.IsArchived)
        {
            drift.Add(new(DriftKinds.ArchivedColumn, table, c.Name, $"El campo {f.Name} está archivado; su columna y sus datos se conservan."));
            return new(c.Name, c.Type, c.Nullable, c.Identity, dflt, c.Computed, false, field, "archived", null);
        }
        var expected = Expected(f);
        if (expected is not null && !Ci.Equals(expected, c.Type))
        {
            drift.Add(new(DriftKinds.TypeMismatch, table, c.Name, $"La columna {c.Name} es {c.Type}; el motor generaría {expected}."));
            return new(c.Name, c.Type, c.Nullable, c.Identity, dflt, c.Computed, false, field, "mismatch", expected);
        }
        return new(c.Name, c.Type, c.Nullable, c.Identity, dflt, c.Computed, false, field, "ok", expected);
    }

    private static string? Expected(FieldDef f)
    {
        if (FieldTypes.IsVirtual(f.Type)) return null;
        try { return DdlGenerator.ColumnType(f); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Columnas que el modelo espera (sistema y campos activos con columna) y no existen.</summary>
    private static IEnumerable<DbDrift> Missing(EntityDef e, CatTable t)
    {
        var present = t.Columns.Select(c => c.Name).ToHashSet(Ci);
        var system = Identifiers.SystemColumns.Where(c => e.PerCompany || !Ci.Equals(c, DdlGenerator.CompanyColumn));
        foreach (var c in system.Where(c => !present.Contains(c)).OrderBy(c => c, Ci))
            yield return new(DriftKinds.MissingColumn, t.Name, c, $"Falta la columna de sistema {c}.");
        foreach (var f in e.StoredFields.Where(f => !present.Contains(f.Name)))
            yield return new(DriftKinds.MissingColumn, t.Name, f.Name, $"El campo {f.Name} no tiene columna en app.{t.Name}.");
    }

    private static bool Matches(DbTable t, string q) =>
        Contains(t.Name, q) || (t.Entity is { } e && (Contains(e.Label, q) || Contains(e.Name, q))) ||
        t.Columns.Any(c => Contains(c.Name, q) || (c.Field is { } f && Contains(f.Label, q)));

    private static bool Contains(string? s, string q) =>
        s is not null && System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(s, q,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
}
