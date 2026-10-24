using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

/// <summary>Una acción falló: se revierte todo y el usuario ve qué automatización fue.</summary>
/// <param name="transient">Spec 016: la causa fue otra operación sobre el mismo registro (409): repetirlo puede funcionar.</param>
public sealed class AutomationFailed(string label, string message, bool transient = false)
    : AppException(StatusCodes.Status400BadRequest, $"No se pudo completar «{label}»: {message}")
{
    public bool Transient { get; } = transient;
}

/// <summary>
/// Spec 005 · automatizaciones entre registros. Corren dentro de la transacción del evento que las dispara,
/// con autoridad del motor (sin revisar permisos ni estado editable del destino) pero respetando fórmulas,
/// reglas del registro modificado y condiciones de las transiciones. Ver plan 005.
/// </summary>
public sealed partial class RecordService
{
    public const int MaxAutomationDepth = 5;
    public const int MaxRollupSources = 10_000;

    /// <summary>Contexto de ejecución: la transacción del evento, quién lo disparó y la cadena de automatizaciones.</summary>
    /// <param name="Generation">Spec 016: cuántos trabajos asíncronos encadenados llevaron hasta aquí (0 = una persona).</param>
    private sealed record AutoCtx(SqlConnection Cn, SqlTransaction Tx, IReadOnlyDictionary<string, EntityDef> Model,
        UserContext User, HttpContext Http, int Depth, IReadOnlyList<string> Chain, CancellationToken Ct, FolioSource? Folios = null,
        int Generation = 0);

    /// <summary>Corre las automatizaciones de <paramref name="e"/> para el evento (y estado o transición, si aplica).</summary>
    private async Task FireAsync(AutoCtx c, EntityDef e, long id, string ev, string? key)
    {
        var autos = (e.Automations ?? []).Where(a => a.On == ev && ev switch
        {
            AutomationEvents.EnterState => string.Equals(a.State, key, StringComparison.OrdinalIgnoreCase),
            AutomationEvents.Transition => string.Equals(a.Transition, key, StringComparison.OrdinalIgnoreCase),
            _ => true,
        }).ToList();
        if (autos.Count == 0)
        {
            await EnqueueWebhooksAsync(c, e, id, ev, key);
            return;
        }
        if (c.Depth >= MaxAutomationDepth)
            throw new AppException(400, $"Las automatizaciones se encadenaron más de {MaxAutomationDepth} niveles " +
                                        $"({string.Join(" › ", c.Chain)}). Revisa si hay un ciclo.");

        foreach (var auto in autos)
        {
            var draft = await LoadDraftAsync(c.Cn, c.Tx, e, c.Model, id, c.Ct);
            if (draft is null) return;
            var loader = new RelatedLoader(c.Cn, c.Tx, c.Model);
            if (auto.When is not null && !ExprEvaluator.Test(auto.When, HeaderScope(draft, e, c.Model, loader), Env(c.User, draft, loader), whenNull: false))
                continue;
            // Spec 016: la asíncrona deja su trabajo en la bandeja, en esta misma transacción; sus acciones corren después.
            if (auto.IsAsync)
            {
                await EnqueueAutomationAsync(c, e, id, auto, ev, key);
                continue;
            }
            await RunActionsAsync(c with { Depth = c.Depth + 1, Chain = [.. c.Chain, $"{e.Label}: {auto.Label}"] }, e, id, auto);
        }
        // Spec 008: la carga del webhook ya refleja lo que hicieron las automatizaciones de este mismo evento.
        await EnqueueWebhooksAsync(c, e, id, ev, key);
    }

    /// <summary>Acciones de una automatización (o de una tarea, spec 016) con el error nombrando a la más interna.</summary>
    private async Task RunActionsAsync(AutoCtx c, EntityDef e, long id, Automation auto)
    {
        try
        {
            foreach (var action in auto.Actions) await RunActionAsync(c, e, id, auto, action);
        }
        catch (AutomationFailed) { throw; } // ya nombra la automatización más interna
        catch (AppException ex) { throw new AutomationFailed(auto.Label, FirstMessage(ex), ex is Conflict); }
        catch (ExprException ex) { throw new AutomationFailed(auto.Label, $"error al calcular: {ex.Message}"); }
    }

    private static string FirstMessage(AppException ex) =>
        ex.Errors is { Count: > 0 } errs ? string.Join(" ", errs.Values.SelectMany(v => v).Distinct()) : ex.Message;

    private async Task RunActionAsync(AutoCtx c, EntityDef e, long id, Automation auto, AutomationAction a)
    {
        // Se relee el origen: una acción anterior pudo cambiar datos que esta usa.
        var draft = await LoadDraftAsync(c.Cn, c.Tx, e, c.Model, id, c.Ct);
        if (draft is null) return;
        var loader = new RelatedLoader(c.Cn, c.Tx, c.Model);
        var env = Env(c.User, draft, loader);
        var lineScopes = new Dictionary<string, List<DocScope>>(StringComparer.OrdinalIgnoreCase);
        var header = HeaderScope(draft, e, c.Model, loader, lineScopes);
        var scopeEntity = AutomationRules.Scope(e, a, c.Model)!;

        List<(Values Values, IExprScope Scope)> items = a.Lines is null
            ? [(draft.Header, header)]
            : (draft.Lines.GetValueOrDefault(a.Lines) ?? []).Select((l, i) => (l.Values, (IExprScope)lineScopes[a.Lines][i])).ToList();
        var origin = $"{e.Label} {DisplayOf(e, draft.Header, id)}";

        foreach (var (values, scope) in items)
        {
            loader.Reset();
            if (a.When is not null && !ExprEvaluator.Test(a.When, scope, env, whenNull: false)) continue;
            switch (a.Type)
            {
                case AutomationActions.Update:
                {
                    if (SelfOrTarget(e, id, scopeEntity, a, values, c.Model) is not { } tgt) break;
                    var (target, tid) = tgt;
                    var set = a.Set!.Select(s => (Field: target.FindField(s.Field)!, Value: ExprEvaluator.Evaluate(s.Value, scope, env))).ToList();
                    await SystemUpdateAsync(c, target, tid, set.Select(s => (s.Field, Coerce(s.Field, s.Value))).ToList());
                    break;
                }
                case AutomationActions.Transition:
                {
                    if (SelfOrTarget(e, id, scopeEntity, a, values, c.Model) is not { } tgt) break;
                    var (target, tid) = tgt;
                    var t = target.Workflow!.Transitions.First(x => string.Equals(x.Name, a.Transition, StringComparison.OrdinalIgnoreCase));
                    await SystemTransitionAsync(c, target, tid, t, $"Automática: {auto.Label} ({origin}).");
                    break;
                }
                case AutomationActions.Create:
                {
                    var target = c.Model[a.Entity!];
                    var newDraft = new DocDraft { CreatorId = c.User.Id };
                    foreach (var s in a.Set ?? [])
                        if (target.FindField(s.Field) is { } f) newDraft.Header[f.Name] = Coerce(f, ExprEvaluator.Evaluate(s.Value, scope, env));
                    // Spec 020: partidas del documento nuevo (p. ej. las de una póliza), en el ámbito de la acción.
                    foreach (var lt in a.CreateLines ?? [])
                    {
                        if (lt.When is not null && !ExprEvaluator.Test(lt.When, scope, env, whenNull: false)) continue;
                        var linesField = target.FindField(lt.Field)!;
                        var child = c.Model[linesField.ChildEntity!];
                        var line = new LineDraft();
                        foreach (var s in lt.Set)
                            if (child.FindField(s.Field) is { } f) line.Values[f.Name] = Coerce(f, ExprEvaluator.Evaluate(s.Value, scope, env));
                        (newDraft.Lines.TryGetValue(linesField.Name, out var list) ? list : newDraft.Lines[linesField.Name] = []).Add(line);
                    }
                    var newId = await SystemCreateAsync(c, target, newDraft);
                    if (a.Transition is not null)
                    {
                        var t = target.Workflow!.Transitions.First(x => string.Equals(x.Name, a.Transition, StringComparison.OrdinalIgnoreCase));
                        await SystemTransitionAsync(c, target, newId, t, $"Automática: {auto.Label} ({origin}).");
                    }
                    break;
                }
                case AutomationActions.Rollup:
                    await RollupAsync(c, scopeEntity, values, a);
                    break;
                case AutomationActions.Cost:
                    await CostAsync(c, e, id, values, scope, env, a);
                    break;
            }
        }
    }

    private static string DisplayOf(EntityDef e, Values v, long id) =>
        e.EffectiveDisplayField is { } d && v.GetValueOrDefault(d) is { } x ? Convert.ToString(x, CultureInfo.InvariantCulture)! : $"#{id}";

    /// <summary>Spec 016: sin <c>target</c>, el propio registro (ver <see cref="AutomationRules.Target"/>).</summary>
    private static (EntityDef Target, long Id)? SelfOrTarget(EntityDef e, long id, EntityDef scope, AutomationAction a,
        Values values, IReadOnlyDictionary<string, EntityDef> model) =>
        a.Target is null ? (e, id) : TargetOf(scope, a.Target, values, model);

    private static (EntityDef Target, long Id)? TargetOf(EntityDef scope, string targetField, Values values, IReadOnlyDictionary<string, EntityDef> model)
    {
        var f = scope.FindField(targetField)!;
        return values.GetValueOrDefault(f.Name) is { } raw ? (model[f.ReferenceEntity!], Convert.ToInt64(raw, CultureInfo.InvariantCulture)) : null;
    }

    // ---------------- Escrituras del motor ----------------

    /// <summary>
    /// Spec 012 (H3): antes de validar una transición, bloquea los documentos que modificarán las automatizaciones de esa
    /// transición y del estado destino (destinos <c>target</c> del encabezado o de las partidas). Así las reglas y
    /// condiciones que leen esos documentos (p. ej. «no exceder lo pendiente», «la orden sigue enviada») ven datos
    /// actuales: sin esto, dos recepciones simultáneas validaban contra el mismo pendiente. Orden fijo (catálogo, id) para
    /// no provocar interbloqueos; los bloqueos posteriores del motor sobre esos documentos son reentrantes.
    /// </summary>
    private static Task<bool> LockAutomationTargetsAsync(SqlConnection cn, SqlTransaction tx, EntityDef e,
        IReadOnlyDictionary<string, EntityDef> model, DocDraft draft, WorkflowTransition t, CancellationToken ct) =>
        LockAutomationTargetsAsync(cn, tx, e, model, draft, auto =>
            auto.On == AutomationEvents.Transition && string.Equals(auto.Transition, t.Name, StringComparison.OrdinalIgnoreCase)
            || auto.On == AutomationEvents.EnterState && string.Equals(auto.State, t.To, StringComparison.OrdinalIgnoreCase), ct);

    /// <summary>
    /// Spec 014 (H6): lo mismo para cualquier evento, y también para los acumulados por <c>match</c>: se toma su bloqueo por
    /// clave (el mismo recurso que <see cref="RollupAsync"/>) antes de insertar o validar. Sin esto, en un catálogo sin folio
    /// (la secuencia serializa a los que tienen) dos altas simultáneas con la misma clave se esperaban entre sí: la suma de
    /// la primera esperaba la fila sin confirmar de la segunda, que esperaba la clave de la primera, hasta que el bloqueo
    /// vencía (10 s) y la operación se rechazaba aunque cupiera. Primero las claves (ordenadas), luego los documentos.
    /// Devuelve si bloqueó algún documento (quien llama vuelve a leer lo relacionado).
    /// </summary>
    private static async Task<bool> LockAutomationTargetsAsync(SqlConnection cn, SqlTransaction tx, EntityDef e,
        IReadOnlyDictionary<string, EntityDef> model, DocDraft draft, Func<Automation, bool> fires, CancellationToken ct)
    {
        var refs = new HashSet<(string Entity, long Id)>();
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var auto in (e.Automations ?? []).Where(fires))
        {
            foreach (var a in auto.Actions)
            {
                if (AutomationRules.Scope(e, a, model) is not { } scope) continue;
                var rows = a.Lines is null ? [draft.Header] : (draft.Lines.GetValueOrDefault(a.Lines) ?? []).Select(l => l.Values);
                if (a.Target is not null && scope.FindField(a.Target) is { Type: FieldTypes.Reference } f)
                {
                    foreach (var v in rows)
                        if (v.GetValueOrDefault(f.Name) is { } raw) refs.Add((f.ReferenceEntity!, Convert.ToInt64(raw, CultureInfo.InvariantCulture)));
                }
                else if (a is { Type: AutomationActions.Rollup or AutomationActions.Cost, Target: null, Match: { Count: > 0 } match } && model.TryGetValue(a.Entity ?? "", out var dest))
                {
                    foreach (var v in rows)
                    {
                        var values = match.Select(m => scope.FindField(m.Source) is { } src ? v.GetValueOrDefault(src.Name) : null).ToList();
                        if (values.All(x => x is not null)) keys.Add(RollupLockKey(dest, values!));
                    }
                }
            }
        }
        foreach (var key in keys) await AppLockAsync(cn, tx, key, ct);
        if (refs.Count == 0) return false;

        // Documento raíz de cada destino (una partida se bloquea por su documento, como en LockRootAsync).
        var roots = new SortedSet<(string Entity, long Id)>(Comparer<(string Entity, long Id)>.Create((x, y) =>
            string.CompareOrdinal(x.Entity, y.Entity) is var c && c != 0 ? c : x.Id.CompareTo(y.Id)));
        foreach (var (entity, id) in refs)
        {
            if (!model.TryGetValue(entity, out var target)) continue;
            var root = AutomationRules.Root(target, model);
            if (!target.IsChild) { roots.Add((root.Name, id)); continue; }
            await using var p = new SqlCommand($"SELECT {Quote(target.ParentField!.Name)} FROM {AppTable(target.Name)} WHERE [Id] = @id", cn, tx);
            p.Parameters.Add(new SqlParameter("@id", id));
            if (await p.ExecuteScalarAsync(ct) is long pid) roots.Add((root.Name, pid));
        }
        foreach (var (entity, id) in roots)
        {
            await using var cmd = new SqlCommand($"SELECT [Id] FROM {AppTable(entity)} WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @id", cn, tx);
            cmd.Parameters.Add(new SqlParameter("@id", id));
            await cmd.ExecuteScalarAsync(ct);
        }
        return roots.Count > 0;
    }

    /// <summary>Documento que contiene a <paramref name="target"/> (él mismo o, si es partida, su documento), bloqueado.</summary>
    private static async Task<(EntityDef Root, long RootId)?> LockRootAsync(AutoCtx c, EntityDef target, long id)
    {
        var root = AutomationRules.Root(target, c.Model);
        var rootId = id;
        if (target.IsChild)
        {
            await using var p = new SqlCommand(
                $"SELECT {Quote(target.ParentField!.Name)} FROM {AppTable(target.Name)} WHERE [Id] = @id AND [IsDeleted] = 0", c.Cn, c.Tx);
            p.Parameters.Add(new SqlParameter("@id", id));
            if (await p.ExecuteScalarAsync(c.Ct) is not long pid) return null;
            rootId = pid;
        }
        // Serializa las escrituras del motor sobre el mismo documento (p. ej. dos recepciones a la vez).
        await using var cmd = new SqlCommand($"SELECT [Id] FROM {AppTable(root.Name)} WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @id AND [IsDeleted] = 0", c.Cn, c.Tx);
        cmd.Parameters.Add(new SqlParameter("@id", rootId));
        return await cmd.ExecuteScalarAsync(c.Ct) is null ? null : (root, rootId);
    }

    /// <summary>
    /// Asigna campos de un registro (o de una partida, guardando su documento). Recalcula fórmulas, valida las
    /// reglas del registro modificado y dispara «al editar» del documento. Si nada cambia, no escribe.
    /// </summary>
    private async Task SystemUpdateAsync(AutoCtx c, EntityDef target, long id, IReadOnlyList<(FieldDef Field, object? Value)> set)
    {
        if (await LockRootAsync(c, target, id) is not { } locked)
            throw new AppException(400, $"El registro relacionado de {target.Label} no existe.");
        var (root, rootId) = locked;
        var draft = (await LoadDraftAsync(c.Cn, c.Tx, root, c.Model, rootId, c.Ct))!;
        var before = Snapshot(draft);

        string? linesField = null;
        var lineIndex = -1;
        Values values;
        if (target.IsChild)
        {
            linesField = root.ActiveFields.First(f => f.Type == FieldTypes.Lines && string.Equals(f.ChildEntity, target.Name, StringComparison.OrdinalIgnoreCase)).Name;
            lineIndex = draft.Lines[linesField].FindIndex(l => l.Id == id);
            if (lineIndex < 0) throw new AppException(400, $"La partida relacionada de {root.Label} ya no existe.");
            values = draft.Lines[linesField][lineIndex].Values;
        }
        else values = draft.Header;

        var changed = false;
        foreach (var (f, v) in set)
        {
            if (Equals(ExprEvaluator.Normalize(values.GetValueOrDefault(f.Name)), ExprEvaluator.Normalize(v))) continue;
            values[f.Name] = v;
            changed = true;
        }
        if (!changed) return;

        var loader = new RelatedLoader(c.Cn, c.Tx, c.Model);
        var env = Env(c.User, draft, loader);
        Compute(draft, root, c.Model, loader, env, defaults: false);
        // Solo las reglas del registro que cambió: una regla del encabezado no bloquea acumular en una partida.
        var errors = new Dictionary<string, string[]>();
        var lineScopes = new Dictionary<string, List<DocScope>>(StringComparer.OrdinalIgnoreCase);
        var header = HeaderScope(draft, root, c.Model, loader, lineScopes);
        if (linesField is null) RuleErrors(root, header, env, "", errors);
        else RuleErrors(target, lineScopes[linesField][lineIndex], env, "", errors);
        if (errors.Count > 0) throw new ValidationFailed(errors);
        await CheckLedgerAsync(c.Cn, c.Tx, root, c.Model, draft, draft.CompanyId, LedgerSnapshot(root, before), rootId, c.Ct);

        await UpdateHeaderAsync(c.Cn, c.Tx, root, rootId, draft.Header, rowVersion: null, c.User.Id, c.Ct);
        await SaveLinesAsync(c.Cn, c.Tx, root, c.Model, rootId, draft, draft, c.User.Id, c.Ct);
        await Audit.WriteAsync(c.Cn, c.Tx, c.Http, "automation.update", root.Name, rootId.ToString(CultureInfo.InvariantCulture), before, Snapshot(draft));
        await FireAsync(c, root, rootId, AutomationEvents.Update, null);
    }

    /// <summary>Alta con valores calculados por la automatización (puede asignar campos no editables por personas).</summary>
    private async Task<long> SystemCreateAsync(AutoCtx c, EntityDef target, DocDraft draft)
    {
        var loader = new RelatedLoader(c.Cn, c.Tx, c.Model);
        void CheckRefs(EntityDef e, Values values)
        {
            foreach (var f in e.ActiveFields)
            {
                if (values.GetValueOrDefault(f.Name) is not { } v) continue;
                if (f.Type == FieldTypes.Reference && loader.Row(f.ReferenceEntity!, Convert.ToInt64(v, CultureInfo.InvariantCulture)) is null)
                    throw new AppException(400, $"{e.Label} › {f.Label}: el registro no existe.");
            }
        }
        CheckRefs(target, draft.Header);
        foreach (var (field, lines) in draft.Lines)
            if (target.FindField(field) is { ChildEntity: { } childName } && c.Model.TryGetValue(childName, out var child))
                foreach (var l in lines) CheckRefs(child, l.Values);
        return await InsertDraftAsync(c, target, draft, "automation.create");
    }

    /// <summary>Transición del destino si está en uno de sus estados de origen (si no, se omite).</summary>
    private async Task SystemTransitionAsync(AutoCtx c, EntityDef target, long id, WorkflowTransition t, string comment)
    {
        if (await LockRootAsync(c, target, id) is null) throw new AppException(400, $"El registro relacionado de {target.Label} no existe.");
        var draft = (await LoadDraftAsync(c.Cn, c.Tx, target, c.Model, id, c.Ct))!;
        if (!t.From.Contains(draft.Header.GetValueOrDefault(Workflow.StateField) as string ?? "", StringComparer.OrdinalIgnoreCase)) return;
        var loader = new RelatedLoader(c.Cn, c.Tx, c.Model);
        if (t.When is not null && !ExprEvaluator.Test(t.When, HeaderScope(draft, target, c.Model, loader), Env(c.User, draft, loader), whenNull: false))
            throw new AppException(400, t.WhenMessage ?? $"No se cumplen las condiciones de «{t.Label}» en {target.Label}.");
        foreach (var rf in t.RequiredFields ?? [])
            if (target.FindField(rf) is { } f && IsBlank(draft.Header.GetValueOrDefault(f.Name)))
                throw new AppException(400, $"{f.Label} es obligatorio para «{t.Label}» en {target.Label}.");
        await ApplyTransitionAsync(c, target, id, draft, Snapshot(draft), t, comment.Length > 1000 ? comment[..1000] : comment, rowVersion: null);
    }

    /// <summary>
    /// Acumular: <c>destino.field = SUM(origen.sum)</c> sobre todos los registros de origen vigentes que apuntan
    /// al mismo destino y cumplen el filtro. Recalcula desde la fuente, así que repetirlo no descuadra.
    /// </summary>
    private async Task RollupAsync(AutoCtx c, EntityDef source, Values values, AutomationAction a)
    {
        EntityDef target;
        long? targetId;
        var where = new List<string>();
        var pars = new List<SqlParameter>();

        if (a.Target is not null)
        {
            if (TargetOf(source, a.Target, values, c.Model) is not { } tgt) return;
            var (t, tid) = tgt;
            (target, targetId) = (t, tid);
            if (await LockRootAsync(c, target, tid) is null) return;
            where.Add($"{Quote(source.FindField(a.Target)!.Name)} = @k0");
            pars.Add(new SqlParameter("@k0", tid));
        }
        else
        {
            target = c.Model[a.Entity!];
            var keys = new List<(FieldDef Dest, FieldDef Src, object Value)>();
            foreach (var m in a.Match!)
            {
                var src = source.FindField(m.Source)!;
                if (values.GetValueOrDefault(src.Name) is not { } v) return; // sin clave no hay a dónde acumular
                keys.Add((target.FindField(m.Field)!, src, v));
            }
            // Bloqueo por clave: dos operaciones simultáneas no crean dos destinos iguales.
            await AppLockAsync(c.Cn, c.Tx, RollupLockKey(target, keys.Select(k => k.Value)), c.Ct);
            targetId = await FindByMatchAsync(c, target, keys.Select(k => (k.Dest, k.Value)).ToList());
            for (var i = 0; i < keys.Count; i++)
            {
                where.Add($"{Quote(keys[i].Src.Name)} = @k{i}");
                pars.Add(new SqlParameter($"@k{i}", keys[i].Value));
            }
            if (targetId is null && !a.CreateMissing) return;
            if (targetId is null)
            {
                var sumNew = await SumSourcesAsync(c, source, where, pars, a);
                var nv = new Values();
                foreach (var k in keys) nv[k.Dest.Name] = k.Value;
                var field = target.FindField(a.Field!)!;
                nv[field.Name] = Coerce(field, sumNew);
                await SystemCreateAsync(c, target, new DocDraft { CreatorId = c.User.Id, Header = nv });
                return;
            }
        }

        var sum = await SumSourcesAsync(c, source, where, pars, a);
        var dest = target.FindField(a.Field!)!;
        await SystemUpdateAsync(c, target, targetId!.Value, [(dest, Coerce(dest, sum))]);
    }

    private static async Task<decimal> SumSourcesAsync(AutoCtx c, EntityDef source, List<string> where, List<SqlParameter> pars, AutomationAction a)
    {
        var sumField = source.FindField(a.Sum!)!;
        // Spec 014 (H2): si el campo está en la tabla y el filtro se puede traducir, la base suma sin cargar los registros
        // (sin tope). Solo lo que no es traducible se evalúa en memoria, con el tope de siempre.
        if (SqlRollup(source, sumField, a.Filter, c.Model) is { } filter)
        {
            await using var cmd = new SqlCommand(
                $"SELECT SUM(t.{Quote(sumField.Name)}) FROM {AppTable(source.Name)} t WHERE {string.Join(" AND ", where)} AND t.[IsDeleted] = 0{filter.Sql}",
                c.Cn, c.Tx);
            foreach (var p in pars) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
            foreach (var (name, value) in filter.Parameters) cmd.Parameters.Add(new SqlParameter(name, value));
            return ExprEvaluator.Normalize(await cmd.ExecuteScalarAsync(c.Ct)) as decimal? ?? 0m;
        }

        var cols = source.StoredFields.Select(f => Quote(f.Name)).Prepend("[Id]");
        var rows = new List<Values>();
        await using (var cmd = new SqlCommand(
            $"SELECT TOP ({MaxRollupSources + 1}) {string.Join(", ", cols)} FROM {AppTable(source.Name)} WHERE {string.Join(" AND ", where)} AND [IsDeleted] = 0",
            c.Cn, c.Tx))
        {
            foreach (var p in pars) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
            await using var r = await cmd.ExecuteReaderAsync(c.Ct);
            while (await r.ReadAsync(c.Ct)) rows.Add(ReadValues(r));
        }
        if (rows.Count > MaxRollupSources)
            throw new AppException(400, $"Hay más de {MaxRollupSources:N0} registros de {source.LabelPlural} para acumular.");

        var loader = new RelatedLoader(c.Cn, c.Tx, c.Model);
        var total = 0m;
        foreach (var row in rows)
        {
            if (a.Filter is not null && !ExprEvaluator.Test(a.Filter, new DocScope(source, row, loader), ExprEnv.System, whenNull: false)) continue;
            if (ExprEvaluator.Normalize(row.GetValueOrDefault(sumField.Name)) is decimal d) total += d;
        }
        return total;
    }

    /// <summary>
    /// Spec 014: condición SQL adicional (<c>" AND (…)"</c>, o vacía sin filtro) para sumar en la base, o <c>null</c> si hay
    /// que sumar en memoria (campo sin columna o filtro no traducible).
    /// </summary>
    internal static SqlPredicate? SqlRollup(EntityDef source, FieldDef sumField, string? filter, IReadOnlyDictionary<string, EntityDef> model)
    {
        if (FieldTypes.IsVirtual(sumField.Type) || sumField.IsArchived) return null;
        if (filter is null) return new SqlPredicate("", []);
        var bag = new RecordFilter.ParamBag("@rf");
        try { return new SqlPredicate($" AND {RecordFilter.TranslateRollup(filter, source, model, bag)}", bag.Items); }
        catch (ExprException) { return null; }
    }

    /// <summary>Clave del bloqueo de un acumulado por <c>match</c>: el mismo texto para los valores leídos de la base o capturados.</summary>
    internal static string RollupLockKey(EntityDef target, IEnumerable<object> values) =>
        $"erp-rollup:{target.Name}:" + string.Join("|", values.Select(v => ExprEvaluator.Normalize(v) switch
        {
            decimal d => (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
            string s => s.Trim().ToUpperInvariant(),
            var x => Convert.ToString(x, CultureInfo.InvariantCulture),
        }));

    private static async Task AppLockAsync(SqlConnection cn, SqlTransaction tx, string key, CancellationToken ct)
    {
        // El recurso admite 255 caracteres: se usa un hash de la clave.
        var resource = "erp-rollup:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40];
        await using var cmd = new SqlCommand("""
            DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;
            """, cn, tx);
        cmd.Parameters.Add(new SqlParameter("@res", resource));
        if ((int)(await cmd.ExecuteScalarAsync(ct))! < 0) throw new Conflict("Otra operación está actualizando el mismo registro. Intenta de nuevo.");
    }

    // ---------------- Recalcular fórmulas guardadas (spec 005) ----------------

    /// <summary>
    /// Recalcula las fórmulas de todos los documentos vigentes de <paramref name="root"/> (encabezado y partidas).
    /// Se usa al aplicar un plan que agrega o cambia una fórmula: sin esto, los registros existentes quedarían
    /// con la columna vacía. Solo toca columnas de fórmula: ni valores por defecto ni datos capturados.
    /// </summary>
    internal static async Task RecomputeFormulasAsync(SqlConnection cn, SqlTransaction tx, EntityDef root,
        IReadOnlyDictionary<string, EntityDef> model, CancellationToken ct)
    {
        var ids = new List<long>();
        await using (var cmd = new SqlCommand($"SELECT [Id] FROM {AppTable(root.Name)} WHERE [IsDeleted] = 0", cn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
        // Spec 010: cada registro se calcula con su propia empresa (moneda base), no con la de quien cambia el modelo.
        var companies = new Dictionary<int, (string Code, string Currency)>();
        await using (var cmd = new SqlCommand("SELECT Id, Code, BaseCurrency FROM meta.Company", cn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) companies[r.GetInt32(0)] = (r.GetString(1), r.GetString(2).Trim());

        foreach (var id in ids)
        {
            var d = (await LoadDraftAsync(cn, tx, root, model, id, ct))!;
            var loader = new RelatedLoader(cn, tx, model);
            (string Code, string Currency)? company = d.CompanyId is { } co && companies.TryGetValue(co, out var info) ? info : null;
            var env = new ExprEnv(0, [], d.CreatorId) { Company = company?.Code, BaseCurrency = company?.Currency, Rates = loader, Costs = loader };
            var header = HeaderScope(d, root, model, loader);
            foreach (var (field, list) in d.Lines)
            {
                if (root.FindField(field) is not { ChildEntity: { } childName } || !model.TryGetValue(childName, out var child)) continue;
                var order = FormulaGraph.Order(child);
                foreach (var line in list)
                {
                    var ls = new DocScope(child, line.Values, loader, parent: header);
                    foreach (var f in order) line.Values[f.Name] = Coerce(f, ExprEvaluator.Evaluate(f.Formula!, ls, env), lenient: true);
                    await UpdateColumnsAsync(cn, tx, child, line.Id!.Value, order, line.Values, ct);
                }
            }
            header = HeaderScope(d, root, model, loader);
            var headerOrder = FormulaGraph.Order(root);
            foreach (var f in headerOrder) d.Header[f.Name] = Coerce(f, ExprEvaluator.Evaluate(f.Formula!, header, env), lenient: true);
            await UpdateColumnsAsync(cn, tx, root, id, headerOrder, d.Header, ct);
        }
    }

    private static async Task UpdateColumnsAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, long id,
        IReadOnlyList<FieldDef> fields, Values values, CancellationToken ct)
    {
        if (fields.Count == 0) return;
        await using var cmd = new SqlCommand { Connection = cn, Transaction = tx };
        cmd.CommandText = $"UPDATE {AppTable(e.Name)} SET {string.Join(", ", fields.Select((f, i) => $"{Quote(f.Name)} = @p{i}"))} WHERE [Id] = @id";
        for (var i = 0; i < fields.Count; i++) cmd.Parameters.Add(new SqlParameter($"@p{i}", values.GetValueOrDefault(fields[i].Name) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
