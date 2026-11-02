using System.Text.Json;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Data;

/// <summary>
/// Spec 052: filtro compuesto del buscador de una referencia. Condiciones <c>campo del destino · operador · (constante | origen)</c>
/// unidas por Y; el origen es una ruta de hasta tres saltos desde el registro que se captura, resuelta con el borrador (primer salto
/// y documento padre en memoria) y <see cref="RelatedLoader"/>. El SQL lo arma <see cref="FilterSql.Condition"/>: nombres de los
/// metadatos, valores como parámetros. Lo usan el buscador (<c>POST /lookup/{campo}</c>) y la revisión al guardar.
/// </summary>
public static class LookupFilters
{
    public const int MaxConditions = 10;
    public const int MaxSourceSegments = 3;
    public const int MaxJson = 4000;

    /// <summary>Condiciones efectivas: la forma corta de la 005 (<c>Field = Source</c>) primero, luego <c>Conditions</c>.</summary>
    public static IReadOnlyList<LookupCondition> Of(LookupFilter lf)
    {
        var list = new List<LookupCondition>();
        if (!string.IsNullOrWhiteSpace(lf.Field)) list.Add(new LookupCondition { Field = lf.Field, Op = "eq", Source = lf.Source });
        list.AddRange(lf.Conditions ?? []);
        return list;
    }

    /// <summary>Sin forma corta ni condiciones: quitar el filtro (<c>updateField</c>).</summary>
    public static bool IsEmpty(LookupFilter lf) => string.IsNullOrWhiteSpace(lf.Field) && lf.Conditions is not { Count: > 0 };

    // ======================= Validación del plan =======================

    public static IEnumerable<string> Validate(EntityDef e, FieldDef f, LookupFilter lf, IReadOnlyDictionary<string, EntityDef> model)
    {
        var where = $"{e.Name}.{f.Name} (filtro del buscador)";
        if (f.Type != FieldTypes.Reference) { yield return $"{where}: solo las referencias admiten filtro."; yield break; }
        if (!model.TryGetValue(f.ReferenceEntity ?? "", out var target)) yield break; // se reporta en la validación del campo
        var conds = Of(lf);
        if (conds.Count > MaxConditions) { yield return $"{where}: máximo {MaxConditions} condiciones."; yield break; }
        if (JsonSerializer.Serialize(lf, MetadataStore.Json).Length > MaxJson) { yield return $"{where}: el filtro es demasiado largo."; yield break; }
        for (var i = 0; i < conds.Count; i++)
            if (Check(e, target, conds[i], model) is { } err)
                yield return conds.Count == 1 ? $"{where}: {err}" : $"{where}, condición {i + 1}: {err}";
    }

    private static string? Check(EntityDef e, EntityDef target, LookupCondition c, IReadOnlyDictionary<string, EntityDef> model)
    {
        var tf = Target(target, c);
        if (tf is null) return $"'{c.Field}' no es un campo de {target.Name}.";
        if (!FilterSql.Allows(tf, c.Op)) return $"el operador '{c.Op}' no aplica al campo '{tf.Name}'.";
        var hasValue = c.Value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
        var hasSource = !string.IsNullOrWhiteSpace(c.Source);
        if (FilterOps.NoValue.Contains(c.Op)) return hasValue || hasSource ? $"«{c.Op}» no lleva valor ni origen." : null;
        if (hasValue == hasSource) return $"'{tf.Name}' necesita un valor fijo o un origen (uno de los dos).";
        if (hasValue) return FilterSql.Validate(target, new ViewFilter { Field = tf.Name, Op = c.Op!, Value = c.Value });
        var (hops, error) = SourcePath(e, c.Source!, model);
        if (error is not null) return error;
        var end = hops![^1].Field;
        return Compatible(tf, c.Op!, end) ? null
            : $"el origen '{c.Source}' no se compara con '{tf.Name}' con «{c.Op}» (tipos o catálogos distintos).";
    }

    private static FieldDef? Target(EntityDef target, LookupCondition c) =>
        target.FindField(c.Field ?? "") is { IsArchived: false } tf && (!FieldTypes.IsVirtual(tf.Type) || FieldTypes.IsMulti(tf.Type)) ? tf : null;

    /// <summary>
    /// Saltos del origen desde <paramref name="e"/>: de 1 a 3 segmentos; los intermedios son referencias o el documento padre; el
    /// último, un campo con columna o de varios valores. Devuelve el error para el modelador.
    /// </summary>
    internal static (List<(EntityDef Entity, FieldDef Field)>? Hops, string? Error) SourcePath(EntityDef e, string path,
        IReadOnlyDictionary<string, EntityDef> model)
    {
        var parts = path.Split('.');
        if (parts.Length > MaxSourceSegments || parts.Any(string.IsNullOrWhiteSpace))
            return (null, $"'{path}' no es un origen (Campo, Relacion.Campo o hasta tres saltos).");
        var hops = new List<(EntityDef, FieldDef)>();
        var cur = e;
        for (var i = 0; i < parts.Length; i++)
        {
            var f = cur.FindField(parts[i]);
            if (f is null || f.IsArchived) return (null, $"el origen '{path}': '{parts[i]}' no es un campo de {cur.Name}.");
            hops.Add((cur, f));
            if (i == parts.Length - 1)
                return FieldTypes.IsVirtual(f.Type) && !FieldTypes.IsMulti(f.Type)
                    ? (null, $"el origen '{path}': '{f.Name}' no tiene valor que comparar.")
                    : (hops, null);
            if (f.Type is not (FieldTypes.Reference or FieldTypes.Parent) || !model.TryGetValue(f.ReferenceEntity ?? "", out var next))
                return (null, $"el origen '{path}': '{f.Name}' no es una referencia ni el documento padre; la ruta no sigue.");
            cur = next;
        }
        return (null, $"'{path}' no es un origen.");
    }

    /// <summary>Catálogo al que apunta un id: el referenciado, o «persona».</summary>
    private static string? Domain(FieldDef f) => f.Type switch
    {
        FieldTypes.Reference or FieldTypes.Parent or FieldTypes.MultiReference => f.ReferenceEntity?.ToLowerInvariant(),
        FieldTypes.User or FieldTypes.MultiUser => "@user",
        _ => null,
    };

    private static bool Compatible(FieldDef target, string op, FieldDef source)
    {
        var (tk, sk) = (Kind(target), Kind(source));
        var sameDomain = Domain(target) is { } d && d == Domain(source);
        return op switch
        {
            // Revisión adversarial 052 (F-07): una fecha con una fecha y hora solo coincidiría a medianoche UTC.
            "eq" or "ne" or "gt" or "gte" or "lt" or "lte" => tk != ValueKind.Multi && tk == sk && (tk != ValueKind.Id || sameDomain)
                && (tk != ValueKind.Date || IsDateTime(target) == IsDateTime(source)),
            "contains" => tk == ValueKind.Multi ? sk == ValueKind.Id && sameDomain : tk == ValueKind.Text && sk == ValueKind.Text,
            "in" => sk == ValueKind.Multi && sameDomain && tk is ValueKind.Multi or ValueKind.Id,
            _ => false,
        };
        static bool IsDateTime(FieldDef f) => (f.Type == FieldTypes.Formula ? f.ResultType : f.Type) == FieldTypes.DateTime;
        static ValueKind Kind(FieldDef f) => InterfaceRules.KindOf(f) is ValueKind.LongText ? ValueKind.Text : InterfaceRules.KindOf(f);
    }

    // ======================= Ejecución =======================

    /// <summary>
    /// ¿Una condición con origen nombra un campo que la persona no ve (el del destino o cualquier salto)? Entonces el filtro no se usa
    /// con ella: compararía un valor que controla con uno oculto (oráculo). Las constantes las decide quien modela (decisión 6).
    /// </summary>
    public static bool HiddenFor(EntityDef owner, EntityDef target, LookupFilter lf, IReadOnlyDictionary<string, EntityDef> model, UserContext user)
    {
        foreach (var c in Of(lf))
        {
            if (string.IsNullOrWhiteSpace(c.Source)) continue;
            if (Target(target, c) is not { } tf || !FieldAccess.CanSee(target, tf, user)) return true;
            var (hops, _) = SourcePath(owner, c.Source, model);
            if (hops is null || hops.Any(h => !FieldAccess.CanSee(h.Entity, h.Field, user))) return true;
        }
        return false;
    }

    /// <summary>
    /// Valor del origen con el borrador: el primer salto sale de <paramref name="own"/> y, en una partida, el campo padre lleva al
    /// encabezado en memoria (<paramref name="parent"/>); los demás saltos, de <paramref name="loader"/> (con <c>viewer</c>, solo lo que
    /// ve). Null: sin valor (vacío, roto o fuera de alcance). Un campo de varios valores da su lista de ids.
    /// </summary>
    internal static object? Resolve(EntityDef owner, Values own, Values? parent, string path, IReadOnlyDictionary<string, EntityDef> model,
        RelatedLoader loader)
    {
        var parts = path.Split('.');
        var cur = owner;
        Values? values = own;
        long? id = null; // registro guardado del que salen los valores (null: el borrador)
        for (var i = 0; i < parts.Length; i++)
        {
            if (cur.FindField(parts[i]) is not { IsArchived: false } f) return null;
            if (i == parts.Length - 1)
            {
                if (!FieldTypes.IsMulti(f.Type)) return values.GetValueOrDefault(f.Name);
                return id is null ? values.GetValueOrDefault(f.Name) as IReadOnlyCollection<long> ?? [] : loader.Members(cur.Name, id.Value, [f.Name]);
            }
            if (!model.TryGetValue(f.ReferenceEntity ?? "", out var nextEntity)) return null;
            // El documento de una partida en captura está en memoria (la partida nueva aún no tiene su id).
            if (f.Type == FieldTypes.Parent && id is null && parent is not null) { (cur, values) = (nextEntity, parent); continue; }
            if (values.GetValueOrDefault(f.Name) is not { } next) return null;
            cur = nextEntity;
            id = Convert.ToInt64(next, System.Globalization.CultureInfo.InvariantCulture);
            if (loader.Row(cur.Name, id.Value) is not { } row) return null;
            values = row;
        }
        return null;
    }

    /// <summary>
    /// Condición SQL (alias <c>t</c> del destino) del filtro con los valores del borrador; null si un origen no tiene valor: no se
    /// ofrece nada (spec 005). <paramref name="meId"/> resuelve <c>$me</c>.
    /// </summary>
    internal static string? Build(EntityDef owner, EntityDef target, LookupFilter lf, Values own, Values? parent,
        IReadOnlyDictionary<string, EntityDef> model, RelatedLoader loader, int meId, SqlParameterCollection ps)
    {
        var parts = new List<string>();
        var n = 0;
        string Add(object v)
        {
            var name = $"@lf{n++}";
            ps.Add(new SqlParameter(name, v));
            return name;
        }
        foreach (var c in Of(lf))
        {
            if (Target(target, c) is not { } tf) return null;
            List<object> values;
            if (FilterOps.NoValue.Contains(c.Op)) values = [];
            else if (string.IsNullOrWhiteSpace(c.Source))
                values = FilterSql.ConstantValues(target, tf, new ViewFilter { Field = tf.Name, Op = c.Op, Value = c.Value }, meId);
            else
            {
                switch (Resolve(owner, own, parent, c.Source, model, loader))
                {
                    case null:
                    case string s when s.Trim().Length == 0:
                    case IReadOnlyCollection<long> { Count: 0 }:
                        return null;
                    case IReadOnlyCollection<long> list:
                        values = list.Cast<object>().ToList();
                        break;
                    case var v:
                        values = [v];
                        break;
                }
            }
            parts.Add(FilterSql.Condition(target, tf, c.Op, values, Add));
        }
        return parts.Count == 0 ? "1 = 1" : string.Join(" AND ", parts);
    }
}
