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
    /// <param name="model">Spec 051: para seguir la ruta de una pertenencia (<c>CURRENTUSER() IN Departamento.Responsables</c>).</param>
    public static SqlPredicate? For(EntityDef e, UserContext user, bool write, IReadOnlyDictionary<string, EntityDef>? model = null) =>
        Build(e, Of(user), write, grants: !user.IsSuperuser, model: model);

    /// <summary>
    /// Igual que <see cref="For"/>, pero una partida hereda el alcance de su documento (buscadores y referencias
    /// a partidas, p. ej. la partida de la orden en una recepción). La subconsulta reutiliza el alias <c>t</c>,
    /// que dentro de ella se refiere al documento.
    /// </summary>
    public static SqlPredicate? ForRecordOrParent(EntityDef e, IReadOnlyDictionary<string, EntityDef> model, UserContext user)
    {
        if (!e.IsChild) return For(e, user, write: false, model);
        if (!model.TryGetValue(e.ParentEntity ?? "", out var parent) || For(parent, user, write: false, model) is not { } p) return null;
        return p with { Sql = $"t.{Quote(e.ParentField!.Name)} IN (SELECT t.[Id] FROM {AppTable(parent.Name)} t WHERE t.[IsDeleted] = 0 AND {p.Sql})" };
    }

    /// <summary>
    /// Spec 029: ¿los permisos por registro (no la empresa) restringen la lectura de este catálogo para el usuario? Es lo
    /// que hace que una fórmula que lo recorre se le oculte (<see cref="DerivedAccess"/>).
    /// </summary>
    public static bool HasGrants(EntityDef e, UserContext user)
    {
        // Spec 051: sin traducir (no necesita el modelo): restringe si ningún permiso aplicable es «todos» sin condición.
        if (user.IsSuperuser || e.Security?.Records is not { } grants) return false;
        var who = Of(user);
        return !grants.Any(g => Applies(g, who, write: false) && string.Equals(g.EffectiveScope, RecordScopes.All, StringComparison.OrdinalIgnoreCase)
                               && string.IsNullOrWhiteSpace(g.Where));
    }

    private static bool Applies(RecordGrant g, Who who, bool write) =>
        (g.Roles is not { Count: > 0 } roles || roles.Any(r => who.Roles.Contains(r, StringComparer.OrdinalIgnoreCase))) && (!write || g.Writes);

    internal static SqlPredicate? Build(EntityDef e, Who who, bool write, bool grants = true, IReadOnlyDictionary<string, EntityDef>? model = null)
    {
        var bag = new ParamBag();
        // Spec 010: sin empresa no hay registros de un catálogo por empresa (el id 0 no existe).
        var company = e.PerCompany ? $"t.[CompanyId] = {bag.Add(who.CompanyId ?? 0)}" : null;
        var records = grants ? Grants(e, who, write, bag, model) : null;
        if (company is null) return records is null ? null : new SqlPredicate(records, bag.Items);
        return new SqlPredicate(records is null ? company : $"({company} AND {records})", bag.Items);
    }

    /// <summary>Spec 006: <c>OR</c> de los permisos por registro aplicables; null = sin restricción.</summary>
    private static string? Grants(EntityDef e, Who who, bool write, ParamBag bag, IReadOnlyDictionary<string, EntityDef>? model)
    {
        if (e.Security?.Records is not { } grants) return null;
        var parts = new List<string>();
        foreach (var g in grants)
        {
            if (!Applies(g, who, write)) continue;
            var scope = ScopeSql(e, g.EffectiveScope, who, bag);
            var where = string.IsNullOrWhiteSpace(g.Where) ? null : Translate(g.Where, e, who, bag, model: model);
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
        // Sin unidad asignada, «de mi unidad» (y su árbol) se reduce a «propios». Los equipos (spec 015) son filas de
        // meta.[User] con su unidad: estos alcances ya los incluyen.
        if (!string.IsNullOrWhiteSpace(who.Unit))
        {
            if (string.Equals(scope, RecordScopes.Unit, StringComparison.OrdinalIgnoreCase))
                return $"{owner} IN (SELECT u.[Id] FROM meta.[User] u WHERE u.[Unit] = {bag.Add(who.Unit!)})";
            // Spec 015: la ruta materializada solo tiene dígitos y '/', así que no trae comodines de LIKE.
            if (string.Equals(scope, RecordScopes.UnitTree, StringComparison.OrdinalIgnoreCase))
                return $"{owner} IN (SELECT u.[Id] FROM meta.[User] u JOIN meta.[BusinessUnit] b ON b.[Name] = u.[Unit] " +
                       $"WHERE b.[Path] LIKE (SELECT m.[Path] FROM meta.[BusinessUnit] m WHERE m.[Name] = {bag.Add(who.Unit!)}) + N'%')";
        }
        var me = bag.Add(who.UserId);
        // Spec 015: con teamOwners, «propios» incluye lo de los equipos activos de los que soy miembro.
        return e.Security?.TeamOwners == true
            ? $"({owner} = {me} OR {owner} IN (SELECT tm.[TeamId] FROM meta.[TeamMember] tm JOIN meta.[User] tu ON tu.[Id] = tm.[TeamId] " +
              $"WHERE tm.[UserId] = {me} AND tu.[IsActive] = 1))"
            : $"{owner} = {me}";
    }

    /// <summary>
    /// Traduce una condición del lenguaje de expresiones (subconjunto) a SQL. Lanza <see cref="ExprException"/>
    /// con un mensaje para el modelador si usa algo que no se puede evaluar en la base.
    /// </summary>
    public static string Translate(string source, EntityDef e, Who who, ParamBag bag, IReadOnlySet<string>? knownRoles = null,
        IReadOnlyDictionary<string, EntityDef>? model = null) =>
        new Translator(e, who, bag, knownRoles, model, rollup: false).Bool(ExprParser.Parse(source));

    /// <summary>
    /// Spec 014 (H2): filtro de un acumulado, que el motor evalúa con <see cref="ExprEnv.System"/> sobre el alias <c>t</c>.
    /// Admite además rutas (<c>Recepcion.Estado</c>; spec 023: hasta tres saltos) por campos referencia o padre, leídas como
    /// <see cref="Data.RelatedLoader.Row"/> (registro vigente o nulo), y traduce <c>=</c>, <c>&lt;&gt;</c> e <c>IN</c> con la
    /// semántica del evaluador: contra un nulo son falso o verdadero, no desconocido. Lanza <see cref="ExprException"/> si la
    /// condición no es traducible; el motor la evalúa entonces en memoria.
    /// </summary>
    public static string TranslateRollup(string source, EntityDef e, IReadOnlyDictionary<string, EntityDef> model, ParamBag bag) =>
        new Translator(e, new Who(0, null, []), bag, null, model, rollup: true).Bool(ExprParser.Parse(source));

    /// <summary>Valida una regla <c>where</c> con un usuario ficticio; devuelve el error o null.</summary>
    public static string? Check(string source, EntityDef e, IReadOnlySet<string> knownRoles, IReadOnlyDictionary<string, EntityDef>? model = null)
    {
        try { Translate(source, e, new Who(0, "unidad", []), new ParamBag(), knownRoles, model); return null; }
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

    /// <param name="model">Spec 051: para las rutas (de una pertenencia, o de cualquier valor en modo acumulado).</param>
    /// <param name="rollup">Modo acumulado (ver <see cref="TranslateRollup"/>).</param>
    private sealed class Translator(EntityDef e, Who who, ParamBag bag, IReadOnlySet<string>? knownRoles,
        IReadOnlyDictionary<string, EntityDef>? model, bool rollup)
    {
        private static readonly HashSet<string> Comparisons = ["=", "<>", "<", "<=", ">", ">="];
        private int _aliases;

        public string Bool(Node n) => n switch
        {
            Binary { Op: "AND" or "OR" } b => $"({Bool(b.Left)} {b.Op} {Bool(b.Right)})",
            Unary { Op: "NOT" } u => $"(NOT {Bool(u.Operand)})",
            // Como ExprEvaluator.Compare: nulo = nulo es verdadero y nulo = x es falso (dos valores, también bajo NOT).
            Binary { Op: "=" or "<>" } b when rollup => TwoValued(Value(b.Left), Value(b.Right), b.Op == "="),
            Binary b when Comparisons.Contains(b.Op) => $"{Value(b.Left)} {b.Op} {Value(b.Right)}",
            InList i when rollup => $"(CASE WHEN {Value(i.Value)} IN ({string.Join(", ", i.Items.Select(Value))}) THEN 1 ELSE 0 END = 1)",
            InList i => $"{Value(i.Value)} IN ({string.Join(", ", i.Items.Select(Value))})",
            Literal { Value: bool v } => v ? "1 = 1" : "1 = 0",
            // Regresión (spec 007): una fórmula sí/no guardada (p. ej. «Bajo mínimo») también es una condición.
            PathRef { IsOwn: true } p when Column(p) is var (col, f) && (f.Type == FieldTypes.Boolean || f is { Type: FieldTypes.Formula, ResultType: FieldTypes.Boolean }) => $"{col} = 1",
            Call c => BoolCall(c),
            _ => throw new ExprException("Una regla de acceso debe ser una condición (verdadero o falso)."),
        };

        private string TwoValued(string l, string r, bool equal) =>
            $"(CASE WHEN {l} = {r} OR ({l} IS NULL AND {r} IS NULL) THEN 1 ELSE 0 END = {(equal ? 1 : 0)})";

        private string BoolCall(Call c)
        {
            if (rollup && c.Name is "HASROLE" or "ISCREATOR")
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
                case "CONTAINS" when c.Args is [PathRef list, var x]:
                    return Membership(list, x);
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
            Call { Name: "CURRENTUSER" or "CURRENTUNIT" } c when rollup =>
                throw new ExprException($"{c.Name}() depende de quién dispara el evento: se evalúa en memoria."),
            Call { Name: "CURRENTUSER", Args.Count: 0 } => bag.Add(who.UserId),
            Call { Name: "CURRENTUNIT", Args.Count: 0 } => bag.Add(who.Unit),
            Call { Name: "TODAY", Args.Count: 0 } => bag.Add(DateTime.Today),
            // Spec 016: vencimientos y recordatorios («entrega en los próximos 3 días»): la fecha se calcula aquí.
            Call { Name: "ADDDAYS", Args: [Call { Name: "TODAY", Args.Count: 0 }, var days] } when Days(days) is { } d => bag.Add(DateTime.Today.AddDays(d)),
            Call { Name: "ADDDAYS" } => throw new ExprException("ADDDAYS() solo se puede usar como ADDDAYS(TODAY(), n), con n un número entero."),
            Call c => throw new ExprException($"{c.Name}() no se puede usar en una regla de acceso."),
            _ => throw new ExprException("Las reglas de acceso solo comparan campos del catálogo con valores, CURRENTUSER(), CURRENTUNIT() o TODAY()."),
        };

        private static int? Days(Node n) => n switch
        {
            Literal { Value: decimal d } when d == decimal.Truncate(d) && Math.Abs(d) <= 36_500 => (int)d,
            Unary { Op: "-", Operand: Literal { Value: decimal d } } when d == decimal.Truncate(d) && d <= 36_500 => -(int)d,
            _ => null,
        };

        /// <summary>
        /// Spec 051: <c>X IN Lista</c> → <c>EXISTS</c> sobre la tabla de unión del campo de varios valores, con la llave del dueño
        /// (el propio registro, o la cadena de subconsultas por Id de una ruta, con el dueño vigente) y X como parámetro o
        /// columna. Dos valores: sin dueño, sin filas o X nulo, falso. Identificadores solo de los metadatos.
        /// </summary>
        private string Membership(PathRef list, Node x)
        {
            var head = e.FindField(list.Head);
            if (head is null || head.IsArchived) throw new ExprException($"El campo '{list.Head}' no existe en {e.Name}.");
            string owner;
            EntityDef ownerEntity;
            FieldDef field;
            if (list.IsOwn) (owner, ownerEntity, field) = ("t.[Id]", e, head);
            else
            {
                if (model is null) throw new ExprException($"'{list}': la regla necesita el modelo para seguir la ruta.");
                if (head.Type is not (FieldTypes.Reference or FieldTypes.Parent))
                    throw new ExprException($"'{list.Head}' no es una referencia; no se puede usar '{list}'.");
                // La llave del penúltimo salto (una columna o la cadena de subconsultas de una ruta) y el dueño, vigente.
                var before = list.Rest.Take(list.Rest.Count - 1).ToList();
                var key = before.Count == 0 ? $"t.{Quote(head.Name)}" : Column(new PathRef(list.Head, before), forList: true).Sql;
                var hop = before.Count == 0 ? head : ExprAnalyzer.MemberPath(head, before, model, list.ToString(), out _);
                if (hop is not { Type: FieldTypes.Reference or FieldTypes.Parent } || !model.TryGetValue(hop.ReferenceEntity ?? "", out var target))
                    throw new ExprException($"'{list}' no se puede leer en la base.");
                ownerEntity = target;
                field = target.FindField(list.Rest[^1]) ?? throw new ExprException($"'{list.Rest[^1]}' no existe en {target.Name}.");
                var o = $"o{_aliases++}";
                owner = $"(SELECT {o}.[Id] FROM {AppTable(target.Name)} {o} WHERE {o}.[Id] = {key} AND {o}.[IsDeleted] = 0)";
            }
            if (field.IsArchived || !FieldTypes.IsMulti(field.Type))
                throw new ExprException($"'{list}' no tiene varios valores: «X IN Campo» pregunta por un campo de varios valores.");
            var value = x switch
            {
                Literal { Value: decimal d } when d == decimal.Truncate(d) && d is > 0 and <= long.MaxValue => bag.Add((long)d),
                Call { Name: "CURRENTUSER" } when field.Type == FieldTypes.MultiUser => Value(x),
                // Una columna del mismo tipo que los valores (una persona, o una referencia al mismo catálogo): nunca una
                // conversión implícita de texto en la base.
                PathRef p when Column(p) is var (sql, vf) && (field.Type == FieldTypes.MultiUser ? vf.Type == FieldTypes.User
                    : vf.Type is FieldTypes.Reference or FieldTypes.Parent && string.Equals(vf.ReferenceEntity, field.ReferenceEntity, StringComparison.OrdinalIgnoreCase)) => sql,
                _ => throw new ExprException($"En «X IN {field.Name}», X debe ser CURRENTUSER(), un campo o un número."),
            };
            var j = $"j{_aliases++}";
            return $"EXISTS (SELECT 1 FROM {JoinTable(ownerEntity.Name, field.Name)} {j} WHERE {j}.[OwnerId] = {owner} AND {j}.[ValueId] = {value})";
        }

        /// <param name="forList">Spec 051: la llave de la lista de una pertenencia (una ruta también vale en una regla de acceso).</param>
        private (string Sql, FieldDef Field) Column(PathRef p, bool forList = false)
        {
            if (!p.IsOwn && !rollup && !forList) throw new ExprException($"'{p}': una regla de acceso solo usa campos del propio catálogo.");
            if (!p.IsOwn && model is null) throw new ExprException($"'{p}' no se puede leer en la base.");
            var f = Stored(e, p.Head);
            if (p.IsOwn) return ($"t.{Quote(f.Name)}", f);
            // Spec 014: campo de un registro referido o del documento, vigente; si no existe, nulo. Spec 023: hasta tres
            // saltos, cada uno una subconsulta por Id (como RelatedLoader.Walk). Identificadores solo de los metadatos.
            var key = $"t.{Quote(f.Name)}";
            var hop = f;
            for (var i = 0; ; i++)
            {
                if (hop.Type is not (FieldTypes.Reference or FieldTypes.Parent) || !model!.TryGetValue(hop.ReferenceEntity ?? "", out var target))
                    throw new ExprException($"'{p}' no se puede leer en la base.");
                var tf = Stored(target, p.Rest[i]);
                var alias = $"r{_aliases++}";
                key = $"(SELECT {alias}.{Quote(tf.Name)} FROM {AppTable(target.Name)} {alias} WHERE {alias}.[Id] = {key} AND {alias}.[IsDeleted] = 0)";
                if (i == p.Rest.Count - 1) return (key, tf);
                hop = tf;
            }
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
    /// <summary>
    /// Acceso del usuario a un campo por sus roles (sin considerar el estado del registro). Spec 029: una fórmula se oculta
    /// también si lee algo que el usuario no ve (<see cref="DerivedAccess"/>).
    /// </summary>
    public static string ByRole(EntityDef e, FieldDef f, UserContext u)
    {
        var own = ByOwnRule(e, f, u);
        return own != FieldAccessLevels.Hidden && DerivedAccess.Hides(f, u) ? FieldAccessLevels.Hidden : own;
    }

    /// <summary>Solo la regla declarada del campo (<see cref="SecurityPolicy.Fields"/>).</summary>
    internal static string ByOwnRule(EntityDef e, FieldDef f, UserContext u)
    {
        if (u.IsSuperuser || e.Security?.For(f.Name) is not { } rule) return FieldAccessLevels.Edit;
        if (rule.Read is not null && !u.HasRole(rule.Read)) return FieldAccessLevels.Hidden;
        if (rule.Edit is not null && !u.HasRole(rule.Edit)) return FieldAccessLevels.Read;
        return FieldAccessLevels.Edit;
    }

    public static bool CanSee(EntityDef e, FieldDef f, UserContext u) => ByRole(e, f, u) != FieldAccessLevels.Hidden;

    /// <summary>
    /// Spec 045 (auditoría 8, D-01): ¿ve los registros del catálogo? Lee su raíz y, si es una partida, ve el campo de partidas
    /// del documento que apunta a ella (lo que ya exigen el registro, la bitácora, OData y las fórmulas). Una sola regla para
    /// reportes, exploración, tableros y cada salto de una ruta.
    /// </summary>
    public static bool SeesLines(EntityDef e, UserContext u, IReadOnlyDictionary<string, EntityDef> model)
    {
        if (!e.IsChild) return u.Access(e.Name).Read;
        if (!model.TryGetValue(e.ParentEntity ?? "", out var root) || !u.Access(root.Name).Read) return false;
        return root.ActiveFields.Any(f => f.Type == FieldTypes.Lines && string.Equals(f.ChildEntity, e.Name, StringComparison.OrdinalIgnoreCase)
            && CanSee(root, f, u));
    }

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

    /// <summary>
    /// Spec 029 (revisión adversarial F-B): ¿el campo para mostrar del catálogo se le oculta? Puede ser una fórmula
    /// (<c>Almacen.Clave &amp; Producto.Clave</c>) que <see cref="DerivedAccess"/> oculta; entonces etiquetas, buscador y
    /// agrupaciones no lo usan.
    /// </summary>
    public static bool DisplayHidden(EntityDef e, UserContext u) =>
        e.EffectiveDisplayField is { } d && e.FindField(d) is { } f && !CanSee(e, f, u);

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
            // Spec 029: también sin reglas propias, una fórmula de la partida puede leer algo oculto.
            if (f.Type == FieldTypes.Lines && row.TryGetValue(f.Name, out var lines) && lines is IEnumerable<Dictionary<string, object?>> list
                && model.TryGetValue(f.ChildEntity ?? "", out var child))
                foreach (var line in list) Strip(child, line, u, model);
        }
    }
}
