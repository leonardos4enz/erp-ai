using System.Text;
using Erp.Api.Expressions;
using Erp.Api.Metadata;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Security;

/// <summary>Condición SQL sobre el alias <c>t</c> con sus parámetros (nombres <c>@sec…</c>).</summary>
public sealed record SqlPredicate(string Sql, IReadOnlyList<(string Name, object Value)> Parameters)
{
    public static readonly SqlPredicate None = new("1 = 0", []);

    /// <summary>
    /// Spec 007: renombra los parámetros (<c>@sec0</c> → <c>@{prefix}0</c>) para combinar varios predicados en un
    /// mismo comando (reportes con catálogos relacionados).
    /// </summary>
    public SqlPredicate Prefixed(string prefix)
    {
        string Rename(string n) => "@" + prefix + n["@sec".Length..];
        // De mayor a menor para que @sec1 no reemplace el inicio de @sec10.
        var sql = Parameters.OrderByDescending(p => p.Name.Length).ThenByDescending(p => p.Name, StringComparer.Ordinal)
            .Aggregate(Sql, (acc, p) => acc.Replace(p.Name, Rename(p.Name), StringComparison.Ordinal));
        return new(sql, Parameters.Select(p => (Rename(p.Name), p.Value)).ToList());
    }

    /// <summary>Un SqlParameter solo puede pertenecer a un comando: se crean nuevos cada vez.</summary>
    public void AddTo(SqlParameterCollection target)
    {
        foreach (var (name, value) in Parameters) target.Add(new SqlParameter(name, value));
    }
}

/// <summary>
/// Spec 006: seguridad por registro. Traduce los permisos de <see cref="SecurityPolicy.Records"/> (alcance y
/// reglas <c>where</c>) a una condición SQL. Lectura y escritura usan siempre este mismo filtro, en la base.
/// Identificadores solo desde metadatos (citados); todo valor va como parámetro.
/// </summary>
public static class RecordFilter
{
    public const int MaxGrants = 20;

    /// <summary>Quién consulta. En la validación del plan se usa un usuario ficticio. Spec 010: <c>CompanyId</c> = empresa actual.</summary>
    public sealed record Who(int UserId, string? Unit, IReadOnlyCollection<string> Roles, int? CompanyId = null);

    public static Who Of(UserContext u) => new(u.Id, u.Unit, u.Roles, u.Company?.Id);

    /// <summary>
    /// Condición de lectura (o de escritura) del usuario; <c>null</c> = sin restricción. Spec 010: en un catálogo por
    /// empresa solo los registros de su empresa actual, también para el superusuario (la empresa es contexto, no
    /// privilegio); los permisos por registro (006) sí se los salta el superusuario.
    /// </summary>
    public static SqlPredicate? For(EntityDef e, UserContext user, bool write) =>
        Build(e, Of(user), write, grants: !user.IsSuperuser);

    /// <summary>
    /// Igual que <see cref="For"/>, pero una partida hereda el alcance de su documento (buscadores y referencias
    /// a partidas, p. ej. la partida de la orden en una recepción). La subconsulta reutiliza el alias <c>t</c>,
    /// que dentro de ella se refiere al documento.
    /// </summary>
    public static SqlPredicate? ForRecordOrParent(EntityDef e, IReadOnlyDictionary<string, EntityDef> model, UserContext user)
    {
        if (!e.IsChild) return For(e, user, write: false);
        if (!model.TryGetValue(e.ParentEntity ?? "", out var parent) || For(parent, user, write: false) is not { } p) return null;
        return p with { Sql = $"t.{Quote(e.ParentField!.Name)} IN (SELECT t.[Id] FROM {AppTable(parent.Name)} t WHERE t.[IsDeleted] = 0 AND {p.Sql})" };
    }

    internal static SqlPredicate? Build(EntityDef e, Who who, bool write, bool grants = true)
    {
        var bag = new ParamBag();
        // Spec 010: sin empresa no hay registros de un catálogo por empresa (el id 0 no existe).
        var company = e.PerCompany ? $"t.[CompanyId] = {bag.Add(who.CompanyId ?? 0)}" : null;
        var records = grants ? Grants(e, who, write, bag) : null;
        if (company is null) return records is null ? null : new SqlPredicate(records, bag.Items);
        return new SqlPredicate(records is null ? company : $"({company} AND {records})", bag.Items);
    }

    /// <summary>Spec 006: <c>OR</c> de los permisos por registro aplicables; null = sin restricción.</summary>
    private static string? Grants(EntityDef e, Who who, bool write, ParamBag bag)
    {
        if (e.Security?.Records is not { } grants) return null;
        var parts = new List<string>();
        foreach (var g in grants)
        {
            if (g.Roles is { Count: > 0 } roles && !roles.Any(r => who.Roles.Contains(r, StringComparer.OrdinalIgnoreCase))) continue;
            if (write && !g.Writes) continue;
            var scope = ScopeSql(e, g.EffectiveScope, who, bag);
            var where = string.IsNullOrWhiteSpace(g.Where) ? null : Translate(g.Where, e, who, bag);
            if (scope is null && where is null) return null; // «todos» sin condición: no restringe
            parts.Add(scope is null ? where! : where is null ? scope : $"({scope} AND {where})");
        }
        return parts.Count == 0 ? SqlPredicate.None.Sql : $"({string.Join(" OR ", parts)})";
    }

    private static string OwnerColumn(EntityDef e) =>
        e.Security?.Owner is { } o && e.FindField(o) is { } f ? $"t.{Quote(f.Name)}" : "t.[CreatedBy]";

    private static string? ScopeSql(EntityDef e, string scope, Who who, ParamBag bag)
    {
        if (string.Equals(scope, RecordScopes.All, StringComparison.OrdinalIgnoreCase)) return null;
        var owner = OwnerColumn(e);
        // Sin unidad asignada, «de mi unidad» se reduce a «propios».
        if (string.Equals(scope, RecordScopes.Unit, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(who.Unit))
            return $"{owner} IN (SELECT u.[Id] FROM meta.[User] u WHERE u.[Unit] = {bag.Add(who.Unit!)})";
        return $"{owner} = {bag.Add(who.UserId)}";
    }

    /// <summary>
    /// Traduce una condición del lenguaje de expresiones (subconjunto) a SQL. Lanza <see cref="ExprException"/>
    /// con un mensaje para el modelador si usa algo que no se puede evaluar en la base.
    /// </summary>
    public static string Translate(string source, EntityDef e, Who who, ParamBag bag, IReadOnlySet<string>? knownRoles = null) =>
        new Translator(e, who, bag, knownRoles).Bool(ExprParser.Parse(source));

    /// <summary>
    /// Spec 014 (H2): filtro de un acumulado, que el motor evalúa con <see cref="ExprEnv.System"/> sobre el alias <c>t</c>.
    /// Admite además rutas de un nivel (<c>Recepcion.Estado</c>) por un campo referencia o padre, leídas como
    /// <see cref="Data.RelatedLoader.Row"/> (registro vigente o nulo), y traduce <c>=</c>, <c>&lt;&gt;</c> e <c>IN</c> con la
    /// semántica del evaluador: contra un nulo son falso o verdadero, no desconocido. Lanza <see cref="ExprException"/> si la
    /// condición no es traducible; el motor la evalúa entonces en memoria.
    /// </summary>
    public static string TranslateRollup(string source, EntityDef e, IReadOnlyDictionary<string, EntityDef> model, ParamBag bag) =>
        new Translator(e, new Who(0, null, []), bag, null, model).Bool(ExprParser.Parse(source));

    /// <summary>Valida una regla <c>where</c> con un usuario ficticio; devuelve el error o null.</summary>
    public static string? Check(string source, EntityDef e, IReadOnlySet<string> knownRoles)
    {
        try { Translate(source, e, new Who(0, "unidad", []), new ParamBag(), knownRoles); return null; }
        catch (ExprException ex) { return ex.Message; }
    }

    public sealed class ParamBag(string prefix = "@sec")
    {
        private readonly List<(string, object)> _items = [];
        public IReadOnlyList<(string Name, object Value)> Items => _items;
        public string Add(object? value)
        {
            var name = $"{prefix}{_items.Count}";
            _items.Add((name, value ?? DBNull.Value));
            return name;
        }
    }

    /// <param name="model">Con modelo, modo acumulado (ver <see cref="TranslateRollup"/>).</param>
    private sealed class Translator(EntityDef e, Who who, ParamBag bag, IReadOnlySet<string>? knownRoles,
        IReadOnlyDictionary<string, EntityDef>? model = null)
    {
        private static readonly HashSet<string> Comparisons = ["=", "<>", "<", "<=", ">", ">="];
        private int _aliases;

        public string Bool(Node n) => n switch
        {
            Binary { Op: "AND" or "OR" } b => $"({Bool(b.Left)} {b.Op} {Bool(b.Right)})",
            Unary { Op: "NOT" } u => $"(NOT {Bool(u.Operand)})",
            // Como ExprEvaluator.Compare: nulo = nulo es verdadero y nulo = x es falso (dos valores, también bajo NOT).
            Binary { Op: "=" or "<>" } b when model is not null => TwoValued(Value(b.Left), Value(b.Right), b.Op == "="),
            Binary b when Comparisons.Contains(b.Op) => $"{Value(b.Left)} {b.Op} {Value(b.Right)}",
            InList i when model is not null => $"(CASE WHEN {Value(i.Value)} IN ({string.Join(", ", i.Items.Select(Value))}) THEN 1 ELSE 0 END = 1)",
            InList i => $"{Value(i.Value)} IN ({string.Join(", ", i.Items.Select(Value))})",
            Literal { Value: bool v } => v ? "1 = 1" : "1 = 0",
            // Regresión (spec 007): una fórmula sí/no guardada (p. ej. «Bajo mínimo») también es una condición.
            PathRef { Tail: null } p when Column(p) is var (col, f) && (f.Type == FieldTypes.Boolean || f is { Type: FieldTypes.Formula, ResultType: FieldTypes.Boolean }) => $"{col} = 1",
            Call c => BoolCall(c),
            _ => throw new ExprException("Una regla de acceso debe ser una condición (verdadero o falso)."),
        };

        private string TwoValued(string l, string r, bool equal) =>
            $"(CASE WHEN {l} = {r} OR ({l} IS NULL AND {r} IS NULL) THEN 1 ELSE 0 END = {(equal ? 1 : 0)})";

        private string BoolCall(Call c)
        {
            if (model is not null && c.Name is "HASROLE" or "ISCREATOR")
                throw new ExprException($"{c.Name}() depende de quién dispara el evento: se evalúa en memoria.");
            switch (c.Name)
            {
                case "ISBLANK" when c.Args.Count == 1 && c.Args[0] is PathRef p:
                {
                    var (col, f) = Column(p);
                    return f.Type is FieldTypes.Text or FieldTypes.LongText or FieldTypes.Choice
                        ? $"({col} IS NULL OR LTRIM(RTRIM({col})) = N'')" : $"{col} IS NULL";
                }
                case "HASROLE" when c.Args.Count == 1 && c.Args[0] is Literal { Value: string role }:
                    if (knownRoles is not null && !knownRoles.Contains(role)) throw new ExprException($"El rol '{role}' no existe.");
                    return who.Roles.Contains(role, StringComparer.OrdinalIgnoreCase) ? "1 = 1" : "1 = 0";
                case "ISCREATOR" when c.Args.Count == 0:
                    return $"t.[CreatedBy] = {bag.Add(who.UserId)}";
                default:
                    throw new ExprException($"{c.Name}() no se puede usar como condición en una regla de acceso.");
            }
        }

        private string Value(Node n) => n switch
        {
            Literal { Value: null } => throw new ExprException("Usa ISBLANK(Campo) en lugar de comparar con NULL."),
            Literal { Value: bool b } => bag.Add(b),
            Literal l => bag.Add(l.Value),
            Unary { Op: "-", Operand: Literal { Value: decimal d } } => bag.Add(-d),
            PathRef p => Column(p).Sql,
            Call { Name: "CURRENTUSER" or "CURRENTUNIT" } c when model is not null =>
                throw new ExprException($"{c.Name}() depende de quién dispara el evento: se evalúa en memoria."),
            Call { Name: "CURRENTUSER", Args.Count: 0 } => bag.Add(who.UserId),
            Call { Name: "CURRENTUNIT", Args.Count: 0 } => bag.Add(who.Unit),
            Call { Name: "TODAY", Args.Count: 0 } => bag.Add(DateTime.Today),
            Call c => throw new ExprException($"{c.Name}() no se puede usar en una regla de acceso."),
            _ => throw new ExprException("Las reglas de acceso solo comparan campos del catálogo con valores, CURRENTUSER(), CURRENTUNIT() o TODAY()."),
        };

        private (string Sql, FieldDef Field) Column(PathRef p)
        {
            if (p.Tail is not null && model is null) throw new ExprException($"'{p}': una regla de acceso solo usa campos del propio catálogo.");
            var f = Stored(e, p.Head);
            if (p.Tail is null) return ($"t.{Quote(f.Name)}", f);
            // Spec 014: campo de un registro referido o del documento (un nivel), vigente; si no existe, nulo.
            if (f.Type is not (FieldTypes.Reference or FieldTypes.Parent) || !model!.TryGetValue(f.ReferenceEntity ?? "", out var target))
                throw new ExprException($"'{p}' no se puede leer en la base.");
            var tf = Stored(target, p.Tail);
            var alias = $"r{_aliases++}";
            return ($"(SELECT {alias}.{Quote(tf.Name)} FROM {AppTable(target.Name)} {alias} WHERE {alias}.[Id] = t.{Quote(f.Name)} AND {alias}.[IsDeleted] = 0)", tf);
        }

        private static FieldDef Stored(EntityDef entity, string name)
        {
            var f = entity.FindField(name);
            if (f is null || f.IsArchived || FieldTypes.IsVirtual(f.Type))
                throw new ExprException($"El campo '{name}' no existe en {entity.Name}.");
            return f;
        }
    }
}

public static class FieldAccess
{
    /// <summary>Acceso del usuario a un campo por sus roles (sin considerar el estado del registro).</summary>
    public static string ByRole(EntityDef e, FieldDef f, UserContext u)
    {
        if (u.IsSuperuser || e.Security?.For(f.Name) is not { } rule) return FieldAccessLevels.Edit;
        if (rule.Read is not null && !u.HasRole(rule.Read)) return FieldAccessLevels.Hidden;
        if (rule.Edit is not null && !u.HasRole(rule.Edit)) return FieldAccessLevels.Read;
        return FieldAccessLevels.Edit;
    }

    public static bool CanSee(EntityDef e, FieldDef f, UserContext u) => ByRole(e, f, u) != FieldAccessLevels.Hidden;

    /// <summary>
    /// ¿Puede capturarlo en el formulario o la API? <paramref name="state"/> es el estado del registro (o del
    /// documento, en una partida). <c>editableIn</c> aplica a todos, incluido el superusuario: es lógica de proceso.
    /// </summary>
    public static bool CanEdit(EntityDef e, FieldDef f, UserContext u, string? state)
    {
        if (f.ClientLocked || ByRole(e, f, u) != FieldAccessLevels.Edit) return false;
        var editableIn = e.Security?.For(f.Name)?.EditableIn;
        return editableIn is null || (state is not null && editableIn.Contains(state, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Quita de una fila de salida los campos que el usuario no puede ver (y sus etiquetas).</summary>
    public static void Strip(EntityDef e, IDictionary<string, object?> row, UserContext u, IReadOnlyDictionary<string, EntityDef> model)
    {
        foreach (var f in e.ActiveFields)
        {
            if (!CanSee(e, f, u))
            {
                row.Remove(f.Name);
                row.Remove(f.Name + "__label");
                continue;
            }
            if (f.Type == FieldTypes.Lines && row.TryGetValue(f.Name, out var lines) && lines is IEnumerable<Dictionary<string, object?>> list
                && model.TryGetValue(f.ChildEntity ?? "", out var child) && child.Security?.Fields is { Count: > 0 })
                foreach (var line in list) Strip(child, line, u, model);
        }
    }
}
