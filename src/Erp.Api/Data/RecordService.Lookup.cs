using System.Globalization;
using System.Text.Json;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

/// <summary>Spec 052: petición del buscador de un campo: el borrador como en <c>/compute</c> y, en una partida, su índice.</summary>
public sealed record LookupRequest(string? Search, long? Id, JsonElement? Values, int? Line);

public sealed partial class RecordService
{
    internal const string LookupHidden = "El filtro del buscador usa un campo que tu rol no ve.";

    /// <summary>
    /// Spec 052: opciones del buscador de <paramref name="fieldPath"/> (<c>Campo</c> o <c>Partidas.Campo</c>) de
    /// <paramref name="entityName"/>, con su filtro resuelto en el servidor con el borrador. Pide lo mismo que <c>/compute</c>
    /// (crear o editar el catálogo; con <c>id</c>, su alcance de escritura) y ver el campo.
    /// </summary>
    public async Task<IReadOnlyList<LookupItem>> LookupForAsync(string entityName, string fieldPath, LookupRequest req, UserContext user, CancellationToken ct)
    {
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(req.Id is null ? user.Access(e.Name).Create : user.Access(e.Name).Update);
        var parts = fieldPath.Split('.');
        FieldDef? linesField = null;
        var owner = e;
        if (parts.Length == 2 && e.FindField(parts[0]) is { Type: FieldTypes.Lines, IsArchived: false } lf && model.TryGetValue(lf.ChildEntity ?? "", out var child))
            (linesField, owner) = (lf, child);
        var f = parts.Length is 1 or 2 && (parts.Length == 1 || linesField is not null) ? owner.FindField(parts[^1]) : null;
        if (f is not { Type: FieldTypes.Reference, IsArchived: false } || !model.TryGetValue(f.ReferenceEntity ?? "", out var target) || target.IsArchived)
            throw new AppException(400, $"El campo no es una referencia de {e.Label}.");
        Require(FieldAccess.CanSee(owner, f, user) && (linesField is null || FieldAccess.CanSee(e, linesField, user)));

        await using var cn = await db.OpenAsync(ct);
        if (req.Id is not null) await EnsureAccessAsync(cn, null, e, model, req.Id.Value, user, write: true, ct);
        await using var cmd = cn.CreateCommand();
        string? extra = null;
        if (f.LookupFilter is { } filter)
        {
            if (LookupFilters.HiddenFor(owner, target, filter, model, user)) throw new AppException(400, LookupHidden);
            // El borrador como en /compute: lo que no puede capturar se ignora y conserva lo guardado; con su alcance en cada salto.
            var loader = new RelatedLoader(cn, null, model, viewer: user);
            var draft = req.Id is null ? new DocDraft { CreatorId = user.Id } : await LoadDraftAsync(cn, null, e, model, req.Id.Value, ct) ?? throw new NotFound("El registro no existe.");
            if (req.Id is null && e.Workflow is not null) draft.Header[Workflow.StateField] = e.Workflow.Initial;
            if (req.Values is { ValueKind: JsonValueKind.Object } values)
                await ParseIntoAsync(draft, e, model, values, loader, lenient: true, ct, new Writer(user, draft.Header.GetValueOrDefault(Workflow.StateField) as string));
            Compute(draft, e, model, loader, Env(user, draft, loader));
            Values own = draft.Header;
            if (linesField is not null)
            {
                var lines = draft.Lines.GetValueOrDefault(linesField.Name) ?? [];
                if (req.Line is not { } i || i < 0 || i >= lines.Count) throw new AppException(400, "La partida no existe.");
                own = lines[i].Values;
            }
            extra = LookupFilters.Build(owner, target, filter, own, linesField is null ? null : draft.Header, model, loader, user.Id, cmd.Parameters);
            if (extra is null) return []; // falta un origen: nada que ofrecer (spec 005)
        }
        return await QueryOptionsAsync(cmd, target, model, req.Search, user, extra, ct);
    }

    /// <summary>
    /// Spec 052 (H3): toda referencia con filtro que es nueva, cambió o cuyo origen propio cambió debe cumplirlo al guardar. Con
    /// <paramref name="person"/> (lo captura una persona), con su alcance en cada salto y sin campos que no ve; sin ella, el motor.
    /// La condición se lee en la transacción del guardado (READ COMMITTED: un cambio sin confirmar al elegido la hace esperar). Sin
    /// bloquear el elegido (revisión adversarial 052, F-01: dos guardados que eligen los mismos registros en otro orden se
    /// interbloqueaban, y lo que ya se guardó no se vuelve a revisar si el elegido cambia después).
    /// </summary>
    /// <param name="onlyFields">Revisión adversarial 052 (F-06): el motor (<c>update</c> de una automatización) solo revisa las
    /// referencias que él mismo asigna, no lo que cambió por cálculo.</param>
    private static void CheckLookupFilters(DocDraft d, EntityDef e, IReadOnlyDictionary<string, EntityDef> model, RelatedLoader loader,
        UserContext user, UserContext? person, Dictionary<string, string[]> errors, string? onlyLines = null, long? onlyLine = null,
        IReadOnlyCollection<string>? onlyFields = null)
    {
        var scoped = person is null ? loader : new RelatedLoader(loader.Connection, loader.Transaction, model, viewer: person);
        if (onlyLines is null)
            foreach (var f in Filtered(e))
                if (Changed(e, f, d.Header, d.Original, null, null))
                    Check(e, f, d.Header, null, "");
        foreach (var (field, list) in d.Lines)
        {
            if (onlyLines is not null && !string.Equals(field, onlyLines, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.FindField(field) is not { ChildEntity: { } childName } || !model.TryGetValue(childName, out var child)) continue;
            var fields = Filtered(child).ToList();
            if (fields.Count == 0) continue;
            for (var i = 0; i < list.Count; i++)
            {
                var line = list[i];
                if (onlyLine is not null && line.Id != onlyLine) continue;
                var original = line.Id is { } lid ? d.OriginalLines?.GetValueOrDefault((field.ToLowerInvariant(), lid)) : null;
                foreach (var f in fields)
                    if (Changed(child, f, line.Values, original, d.Header, d.Original))
                        Check(child, f, line.Values, d.Header, $"{field}[{i}].");
            }
        }

        void Check(EntityDef owner, FieldDef f, Values own, Values? parent, string prefix)
        {
            if (!model.TryGetValue(f.ReferenceEntity!, out var target)) return;
            if (onlyFields is not null && !onlyFields.Contains(f.Name, StringComparer.OrdinalIgnoreCase)) return;
            var id = Convert.ToInt64(own[f.Name], CultureInfo.InvariantCulture);
            var key = prefix + f.Name;
            if (person is not null && LookupFilters.HiddenFor(owner, target, f.LookupFilter!, model, person))
            {
                Add(key, $"{f.Label}: {LookupHidden}");
                return;
            }
            // Revisión adversarial 052 (F-02): una referencia conservada que ya no ve no se vuelve a evaluar con sus datos (sería un
            // oráculo al variar el origen): se rechaza siempre igual.
            if (person is not null && !scoped.Visible(target.Name, id, person))
            {
                Add(key, $"{f.Label}: el registro elegido ya no está a tu alcance; elige otro.");
                return;
            }
            using var cmd = new SqlCommand { Connection = loader.Connection, Transaction = loader.Transaction };
            var sql = LookupFilters.Build(owner, target, f.LookupFilter!, own, parent, model, scoped, (person ?? user).Id, cmd.Parameters);
            var ok = false;
            if (sql is not null)
            {
                cmd.Parameters.Add(new SqlParameter("@id", id));
                cmd.CommandText = $"SELECT 1 FROM {AppTable(target.Name)} t WHERE t.[Id] = @id AND t.[IsDeleted] = 0 AND ({sql})";
                ok = cmd.ExecuteScalar() is not null;
            }
            if (!ok) Add(key, $"{f.Label}: el registro elegido no cumple el filtro del buscador.");
        }

        // Después de los mensajes de las reglas del campo (p. ej. «el proveedor está inactivo»), no en su lugar.
        void Add(string key, string message) => errors[key] = errors.TryGetValue(key, out var prev) ? [.. prev, message] : [message];
    }

    private static IEnumerable<FieldDef> Filtered(EntityDef e) =>
        e.ActiveFields.Where(f => f.Type == FieldTypes.Reference && f.LookupFilter is { } lf && !LookupFilters.IsEmpty(lf));

    /// <summary>
    /// ¿Se revisa? La referencia tiene valor y es nueva o cambió, o cambió el primer salto propio de un origen (en una partida, el
    /// campo del encabezado al que lleva su campo padre). Sin cambios se conserva lo que tenía (como la 006).
    /// </summary>
    private static bool Changed(EntityDef owner, FieldDef f, Values cur, Values? orig, Values? parent, Values? parentOrig)
    {
        if (cur.GetValueOrDefault(f.Name) is null) return false;
        if (orig is null || !Same(orig.GetValueOrDefault(f.Name), cur.GetValueOrDefault(f.Name))) return true;
        foreach (var c in LookupFilters.Of(f.LookupFilter!))
        {
            if (string.IsNullOrWhiteSpace(c.Source)) continue;
            var parts = c.Source.Split('.');
            var head = owner.FindField(parts[0]);
            if (head is { Type: FieldTypes.Parent } && parts.Length > 1 && parent is not null)
            {
                if (parentOrig is null || !Same(parentOrig.GetValueOrDefault(parts[1]), parent.GetValueOrDefault(parts[1]))) return true;
            }
            else if (head is not null && !Same(orig.GetValueOrDefault(head.Name), cur.GetValueOrDefault(head.Name))) return true;
        }
        return false;

        static bool Same(object? a, object? b) => a is IEnumerable<long> la && b is IEnumerable<long> lb
            ? la.Order().SequenceEqual(lb.Order())
            : Equals(ExprEvaluator.Normalize(a), ExprEvaluator.Normalize(b));
    }
}
