using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Erp.Api.Metadata;

/// <summary>
/// Spec 026 (H2): de qué paquete viene cada parte del modelo. No es un metadato: se reproduce en memoria la cadena de cada
/// paquete del servidor instalado (base y actualizaciones hasta la versión instalada) con el compilador puro, y se compara
/// con el modelo actual. Así no se puede falsear desde un plan y sirve para lo ya instalado.
/// </summary>
public static class Provenance
{
    public static class Kinds
    {
        public const string Entity = "entity", Field = "field", Rule = "rule", Automation = "automation", View = "view",
            Report = "report", Webhook = "webhook", Schedule = "schedule", Workflow = "workflow", Layout = "layout",
            Security = "security", Ledger = "ledger", PrintTemplate = "printTemplate";
    }

    public sealed record PackageStamp(string Id, string Name, string Version);
    public sealed record ElementOrigin(string Kind, string Name, string? Package, string? PackageName, string? Step, bool Modified);
    public sealed record EntityOrigin(string Entity, PackageStamp? InstalledBy, IReadOnlyList<PackageStamp> Packages, IReadOnlyList<ElementOrigin> Elements);
    public sealed record UpgradeConflict(string Entity, string EntityLabel, string Kind, string Name, string Reason, string Effect);

    /// <summary>Lo que dejaron los paquetes: definición de cada elemento y quién la dejó.</summary>
    public sealed class Reproduction
    {
        internal readonly Dictionary<(string Entity, string Kind, string Name), (JsonNode Def, PackageStamp Package, string Step)> Elements = new(Key.Comparer);
        internal readonly Dictionary<string, PackageStamp> CreatedBy = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Key : IEqualityComparer<(string Entity, string Kind, string Name)>
    {
        public static readonly Key Comparer = new();
        private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
        public bool Equals((string Entity, string Kind, string Name) a, (string Entity, string Kind, string Name) b) =>
            Ci.Equals(a.Entity, b.Entity) && a.Kind == b.Kind && Ci.Equals(a.Name, b.Name);
        public int GetHashCode((string Entity, string Kind, string Name) k) =>
            HashCode.Combine(Ci.GetHashCode(k.Entity), k.Kind, Ci.GetHashCode(k.Name));
    }

    /// <summary>Paquetes instalados en orden de dependencias (<c>requires</c>), con empate por nombre.</summary>
    public static IReadOnlyList<ModulePackage> InstallOrder(IReadOnlyList<ModulePackage> packages, IReadOnlyDictionary<string, string> installed)
    {
        var pending = packages.Where(p => installed.ContainsKey(p.Id)).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<ModulePackage>();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(p => (p.Requires ?? []).All(r => done.Contains(r) || !pending.Any(x => string.Equals(x.Id, r, StringComparison.OrdinalIgnoreCase))))
                       ?? pending[0]; // un ciclo no debería existir; no se bloquea por él
            order.Add(next);
            done.Add(next.Id);
            pending.Remove(next);
        }
        return order;
    }

    /// <summary>Compila la cadena de cada paquete instalado y sella los elementos que cada paso crea o cambia.</summary>
    public static Reproduction Reproduce(IReadOnlyList<ModulePackage> packages, IReadOnlyDictionary<string, string> installed, IReadOnlySet<string> roles)
    {
        var result = new Reproduction();
        var ordered = InstallOrder(packages, installed);
        var allRoles = roles.Union(ordered.SelectMany(p => p.AllRoles.Select(r => r.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, EntityDef> working = new Dictionary<string, EntityDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ordered)
        {
            var version = installed[p.Id];
            var stamp = new PackageStamp(p.Id, p.Name, version);
            var steps = new List<(string Step, ChangePlan Plan)> { ("base", p.Plan) };
            steps.AddRange((p.Upgrades ?? []).Where(u => IsAtMost(u.Version, version)).Select(u => (u.Version, u.Plan)));
            foreach (var (step, plan) in steps)
            {
                var compiled = PlanCompiler.Compile(plan, working, allRoles);
                foreach (var name in compiled.TouchedEntities)
                {
                    var after = compiled.ResultingModel[name];
                    var before = working.GetValueOrDefault(name);
                    if (before is null) result.CreatedBy.TryAdd(after.Name, stamp);
                    var old = before is null ? [] : Elements(before);
                    var now = Elements(after);
                    foreach (var (k, def) in now)
                        if (!old.TryGetValue(k, out var prev) || !JsonNode.DeepEquals(prev, def))
                            result.Elements[(after.Name, k.Kind, k.Name)] = (def, stamp, step);
                    foreach (var k in old.Keys.Where(k => !now.ContainsKey(k)))
                        result.Elements.Remove((after.Name, k.Kind, k.Name));
                }
                working = compiled.ResultingModel;
            }
        }
        return result;
    }

    private static bool IsAtMost(string version, string installed) =>
        Version.TryParse(version, out var v) && Version.TryParse(installed, out var i) && v <= i;

    /// <summary>Origen de cada elemento del modelo actual.</summary>
    public static IReadOnlyList<EntityOrigin> Compute(IReadOnlyDictionary<string, EntityDef> model, Reproduction rep)
    {
        var list = new List<EntityOrigin>();
        foreach (var e in model.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var elements = new List<ElementOrigin>();
            var packages = new List<PackageStamp>();
            foreach (var (k, def) in Elements(e))
            {
                if (rep.Elements.TryGetValue((e.Name, k.Kind, k.Name), out var s))
                {
                    elements.Add(new(k.Kind, k.Name, s.Package.Id, s.Package.Name, s.Step, !JsonNode.DeepEquals(def, s.Def)));
                    if (!packages.Any(p => p.Id == s.Package.Id)) packages.Add(s.Package);
                }
                else elements.Add(new(k.Kind, k.Name, null, null, null, false));
            }
            var created = rep.CreatedBy.GetValueOrDefault(e.Name);
            if (created is not null && packages.All(p => p.Id != created.Id)) packages.Insert(0, created);
            list.Add(new(e.Name, created, packages.OrderBy(p => p.Id == created?.Id ? 0 : 1).ToList(), elements));
        }
        return list;
    }

    /// <summary>Spec 026 (H2 CA4): lo local o modificado que las actualizaciones pendientes reemplazarían o quitarían.</summary>
    public static IReadOnlyList<UpgradeConflict> Conflicts(IReadOnlyDictionary<string, EntityDef> model, Reproduction rep,
        IEnumerable<ChangePlan> pending, IReadOnlySet<string> roles)
    {
        var origins = Compute(model, rep).ToDictionary(o => o.Entity, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, EntityDef> working = model;
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in pending)
        {
            var compiled = PlanCompiler.Compile(plan, working, roles);
            touched.UnionWith(compiled.TouchedEntities);
            working = compiled.ResultingModel;
        }
        var list = new List<UpgradeConflict>();
        foreach (var name in touched.Where(model.ContainsKey).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var before = Elements(model[name]);
            var after = Elements(working[name]);
            foreach (var (k, def) in before)
            {
                var removed = !after.TryGetValue(k, out var next);
                if (!removed && JsonNode.DeepEquals(def, next)) continue;
                var o = origins[name].Elements.First(x => x.Kind == k.Kind && string.Equals(x.Name, k.Name, StringComparison.OrdinalIgnoreCase));
                if (o.Package is not null && !o.Modified) continue;
                list.Add(new(model[name].Name, model[name].Label, k.Kind, k.Name, o.Package is null ? "local" : "modified", removed ? "removed" : "replaced"));
            }
        }
        return list;
    }

    private static readonly JsonSerializerOptions Json = MetadataStore.Json;

    /// <summary>Elementos comparables de un catálogo, en forma canónica.</summary>
    public static Dictionary<(string Kind, string Name), JsonNode> Elements(EntityDef e)
    {
        var map = new Dictionary<(string Kind, string Name), JsonNode>(new PairComparer());
        void Add(string kind, string name, object? value)
        {
            if (value is null) return;
            if (Normalize(JsonSerializer.SerializeToNode(value, value.GetType(), Json)) is { } node) map[(kind, name)] = node;
        }
        Add(Kinds.Entity, "", new { e.Label, e.LabelPlural, e.Icon, e.Description, e.DisplayField, e.Group, e.PerCompany, e.IsArchived });
        foreach (var f in e.Fields) Add(Kinds.Field, f.Name, f with { DisplayOrder = 0 });
        foreach (var r in e.Rules ?? []) Add(Kinds.Rule, r.Name, r);
        foreach (var a in e.Automations ?? []) Add(Kinds.Automation, a.Name, a);
        foreach (var v in e.Views ?? []) Add(Kinds.View, v.Name, v);
        foreach (var r in e.Reports ?? []) Add(Kinds.Report, r.Name, r);
        foreach (var w in e.Webhooks ?? []) Add(Kinds.Webhook, w.Name, w);
        foreach (var s in e.Schedules ?? []) Add(Kinds.Schedule, s.Name, s);
        foreach (var t in e.PrintTemplates ?? []) Add(Kinds.PrintTemplate, t.Name, t); // spec 027
        Add(Kinds.Workflow, "", e.Workflow);
        Add(Kinds.Layout, "", e.Layout);
        Add(Kinds.Security, "", e.Security);
        Add(Kinds.Ledger, "", e.Ledger);
        return map;
    }

    private sealed class PairComparer : IEqualityComparer<(string Kind, string Name)>
    {
        public bool Equals((string Kind, string Name) a, (string Kind, string Name) b) =>
            a.Kind == b.Kind && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Kind, string Name) k) => HashCode.Combine(k.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Name));
    }

    /// <summary>Sin nulos, listas vacías ni <c>false</c>: no cambian el significado y el servidor los omite o rellena distinto.</summary>
    internal static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case null: return null;
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, v) in o.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    if (k is not "displayOrder" and not "version" && Normalize(v) is { } n) copy[k] = n;
                return copy.Count == 0 ? null : copy;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var v in a) arr.Add(Normalize(v) ?? JsonValue.Create((string?)null));
                return arr.Count == 0 ? null : arr;
            case JsonValue v when v.GetValueKind() == JsonValueKind.False: return null;
            default: return node.DeepClone();
        }
    }

    // ---------- Memo por instancia del modelo en caché (MetadataStore la cambia al invalidar o vencer) ----------

    private static readonly ConditionalWeakTable<object, Holder> Memo = new();
    private sealed class Holder { public string Key = ""; public Reproduction? Value; }

    public static Reproduction ReproduceCached(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyList<ModulePackage> packages,
        IReadOnlyDictionary<string, string> installed, IReadOnlySet<string> roles)
    {
        var key = string.Join(";", installed.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value}"))
                  + "|" + string.Join(",", roles.Order(StringComparer.OrdinalIgnoreCase));
        var holder = Memo.GetOrCreateValue(model);
        lock (holder)
        {
            if (holder.Value is null || holder.Key != key) { holder.Value = Reproduce(packages, installed, roles); holder.Key = key; }
            return holder.Value;
        }
    }
}
