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
public sealed class AutomationFailed(string label, string message)
    : AppException(StatusCodes.Status400BadRequest, $"No se pudo completar «{label}»: {message}");

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
    private sealed record AutoCtx(SqlConnection Cn, SqlTransaction Tx, IReadOnlyDictionary<string, EntityDef> Model,
        UserContext User, HttpContext Http, int Depth, IReadOnlyList<string> Chain, CancellationToken Ct);

    /// <summary>Corre las automatizaciones de <paramref name="e"/> para el evento (y estado o transición, si aplica).</summary>
    private async Task FireAsync(AutoCtx c, EntityDef e, long id, string ev, string? key)
    {
        var autos = (e.Automations ?? []).Where(a => a.On == ev && ev switch
        {
            AutomationEvents.EnterState => string.Equals(a.State, key, StringComparison.OrdinalIgnoreCase),
            AutomationEvents.Transition => string.Equals(a.Transition, key, StringComparison.OrdinalIgnoreCase),
            _ => true,
        }).ToList();
        if (autos.Count == 0) return;
        if (c.Depth >= MaxAutomationDepth)
            throw new AppException(400, $"Las automatizaciones se encadenaron más de {MaxAutomationDepth} niveles " +
                                        $"({string.Join(" › ", c.Chain)}). Revisa si hay un ciclo.");

        foreach (var auto in autos)
        {
            var draft = await LoadDraftAsync(c.Cn, c.Tx, e, c.Model, id, c.Ct);
            if (draft is null) return;
            var loader = new RelatedLoader(c.Cn, c.Tx, c.Model);
            if (auto.When is not null && !ExprEvaluator.Test(auto.When, HeaderScope(draft, e, c.Model, loader), Env(c.User, draft), whenNull: false))
                continue;
            var inner = c with { Depth = c.Depth + 1, Chain = [.. c.Chain, $"{e.Label}: {auto.Label}"] };
            try
            {
                foreach (var action in auto.Actions) await RunActionAsync(inner, e, id, auto, action);
            }
            catch (AutomationFailed) { throw; } // ya nombra la automatización más interna
            catch (AppException ex) { throw new AutomationFailed(auto.Label, FirstMessage(ex)); }
            catch (ExprException ex) { throw new AutomationFailed(auto.Label, $"error al calcular: {ex.Message}"); }
        }
    }

    private static string FirstMessage(AppException ex) =>
        ex.Errors is { Count: > 0 } errs ? string.Join(" ", errs.Values.SelectMany(v => v).Distinct()) : ex.Message;

    private async Task RunActionAsync(AutoCtx c, EntityDef e, long id, Automation auto, AutomationAction a)
    {
        // Se relee el origen: una acción anterior pudo cambiar datos que esta usa.
        var draft = await LoadDraftAsync(c.Cn, c.Tx, e, c.Model, id, c.Ct);
        if (draft is null) return;
        var loader = new RelatedLoader(c.Cn, c.Tx, c.Model);
        var env = Env(c.User, draft);
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
                    if (TargetOf(scopeEntity, a.Target!, values, c.Model) is not { } tgt) break;
                    var (target, tid) = tgt;
                    var set = a.Set!.Select(s => (Field: target.FindField(s.Field)!, Value: ExprEvaluator.Evaluate(s.Value, scope, env))).ToList();
                    await SystemUpdateAsync(c, target, tid, set.Select(s => (s.Field, Coerce(s.Field, s.Value))).ToList());
                    break;
                }
                case AutomationActions.Transition:
                {
                    if (TargetOf(scopeEntity, a.Target!, values, c.Model) is not { } tgt) break;
                    var (target, tid) = tgt;
                    var t = target.Workflow!.Transitions.First(x => string.Equals(x.Name, a.Transition, StringComparison.OrdinalIgnoreCase));
                    await SystemTransitionAsync(c, target, tid, t, $"Automática: {auto.Label} ({origin}).");
                    break;
                }
                case AutomationActions.Create:
                {
                    var target = c.Model[a.Entity!];
                    var newValues = new Values();
                    foreach (var s in a.Set ?? [])
                        if (target.FindField(s.Field) is { } f) newValues[f.Name] = Coerce(f, ExprEvaluator.Evaluate(s.Value, scope, env));
                    await SystemCreateAsync(c, target, newValues);
                    break;
                }
                case AutomationActions.Rollup:
                    await RollupAsync(c, scopeEntity, values, a);
                    break;
            }
        }
    }

    private static string DisplayOf(EntityDef e, Values v, long id) =>
        e.EffectiveDisplayField is { } d && v.GetValueOrDefault(d) is { } x ? Convert.ToString(x, CultureInfo.InvariantCulture)! : $"#{id}";

    private static (EntityDef Target, long Id)? TargetOf(EntityDef scope, string targetField, Values values, IReadOnlyDictionary<string, EntityDef> model)
    {
        var f = scope.FindField(targetField)!;
        return values.GetValueOrDefault(f.Name) is { } raw ? (model[f.ReferenceEntity!], Convert.ToInt64(raw, CultureInfo.InvariantCulture)) : null;
    }

    // ---------------- Escrituras del motor ----------------

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
        var env = Env(c.User, draft);
        Compute(draft, root, c.Model, loader, env, defaults: false);
        // Solo las reglas del registro que cambió: una regla del encabezado no bloquea acumular en una partida.
        var errors = new Dictionary<string, string[]>();
        var lineScopes = new Dictionary<string, List<DocScope>>(StringComparer.OrdinalIgnoreCase);
        var header = HeaderScope(draft, root, c.Model, loader, lineScopes);
        if (linesField is null) RuleErrors(root, header, env, "", errors);
        else RuleErrors(target, lineScopes[linesField][lineIndex], env, "", errors);
        if (errors.Count > 0) throw new ValidationFailed(errors);

        await UpdateHeaderAsync(c.Cn, c.Tx, root, rootId, draft.Header, rowVersion: null, c.User.Id, c.Ct);
        await SaveLinesAsync(c.Cn, c.Tx, root, c.Model, rootId, draft, draft, c.User.Id, c.Ct);
        await Audit.WriteAsync(c.Cn, c.Tx, c.Http, "automation.update", root.Name, rootId.ToString(CultureInfo.InvariantCulture), before, Snapshot(draft));
        await FireAsync(c, root, rootId, AutomationEvents.Update, null);
    }

    /// <summary>Alta con valores calculados por la automatización (puede asignar campos no editables por personas).</summary>
    private async Task<long> SystemCreateAsync(AutoCtx c, EntityDef target, Values values)
    {
        var loader = new RelatedLoader(c.Cn, c.Tx, c.Model);
        foreach (var f in target.ActiveFields)
        {
            if (values.GetValueOrDefault(f.Name) is not { } v) continue;
            if (f.Type == FieldTypes.Reference && loader.Row(f.ReferenceEntity!, Convert.ToInt64(v, CultureInfo.InvariantCulture)) is null)
                throw new AppException(400, $"{target.Label} › {f.Label}: el registro no existe.");
        }
        return await InsertDraftAsync(c, target, new DocDraft { CreatorId = c.User.Id, Header = values }, "automation.create");
    }

    /// <summary>Transición del destino si está en uno de sus estados de origen (si no, se omite).</summary>
    private async Task SystemTransitionAsync(AutoCtx c, EntityDef target, long id, WorkflowTransition t, string comment)
    {
        if (await LockRootAsync(c, target, id) is null) throw new AppException(400, $"El registro relacionado de {target.Label} no existe.");
        var draft = (await LoadDraftAsync(c.Cn, c.Tx, target, c.Model, id, c.Ct))!;
        if (!t.From.Contains(draft.Header.GetValueOrDefault(Workflow.StateField) as string ?? "", StringComparer.OrdinalIgnoreCase)) return;
        var loader = new RelatedLoader(c.Cn, c.Tx, c.Model);
        if (t.When is not null && !ExprEvaluator.Test(t.When, HeaderScope(draft, target, c.Model, loader), Env(c.User, draft), whenNull: false))
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
            await AppLockAsync(c, $"erp-rollup:{target.Name}:" + string.Join("|", keys.Select(k => Convert.ToString(k.Value, CultureInfo.InvariantCulture))));
            await using (var find = new SqlCommand { Connection = c.Cn, Transaction = c.Tx })
            {
                find.CommandText = $"SELECT TOP 1 [Id] FROM {AppTable(target.Name)} WITH (UPDLOCK, HOLDLOCK) WHERE " +
                                   string.Join(" AND ", keys.Select((k, i) => $"{Quote(k.Dest.Name)} = @f{i}")) + " AND [IsDeleted] = 0 ORDER BY [Id]";
                for (var i = 0; i < keys.Count; i++) find.Parameters.Add(new SqlParameter($"@f{i}", keys[i].Value));
                targetId = await find.ExecuteScalarAsync(c.Ct) as long?;
            }
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
                await SystemCreateAsync(c, target, nv);
                return;
            }
        }

        var sum = await SumSourcesAsync(c, source, where, pars, a);
        var dest = target.FindField(a.Field!)!;
        await SystemUpdateAsync(c, target, targetId!.Value, [(dest, Coerce(dest, sum))]);
    }

    private static async Task<decimal> SumSourcesAsync(AutoCtx c, EntityDef source, List<string> where, List<SqlParameter> pars, AutomationAction a)
    {
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
        var sumField = source.FindField(a.Sum!)!;
        var total = 0m;
        foreach (var row in rows)
        {
            if (a.Filter is not null && !ExprEvaluator.Test(a.Filter, new DocScope(source, row, loader), ExprEnv.System, whenNull: false)) continue;
            if (ExprEvaluator.Normalize(row.GetValueOrDefault(sumField.Name)) is decimal d) total += d;
        }
        return total;
    }

    private static async Task AppLockAsync(AutoCtx c, string key)
    {
        // El recurso admite 255 caracteres: se usa un hash de la clave.
        var resource = "erp-rollup:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40];
        await using var cmd = new SqlCommand("""
            DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;
            """, c.Cn, c.Tx);
        cmd.Parameters.Add(new SqlParameter("@res", resource));
        if ((int)(await cmd.ExecuteScalarAsync(c.Ct))! < 0) throw new Conflict("Otra operación está actualizando el mismo registro. Intenta de nuevo.");
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

        foreach (var id in ids)
        {
            var d = (await LoadDraftAsync(cn, tx, root, model, id, ct))!;
            var loader = new RelatedLoader(cn, tx, model);
            var env = new ExprEnv(0, [], d.CreatorId);
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
