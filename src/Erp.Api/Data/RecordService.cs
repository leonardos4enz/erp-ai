using System.Globalization;
using System.Text;
using System.Text.Json;
using Erp.Api.Expressions;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Data;

public sealed record ListQuery(int Page = 1, int PageSize = 25, string? Search = null, string? Sort = null, string? Dir = null, string? State = null);
public sealed record StateCount(string State, string Label, string Color, int Count);
public sealed record ListResult(IReadOnlyList<Dictionary<string, object?>> Items, int Total, IReadOnlyList<StateCount>? StateCounts = null);
public sealed record LookupItem(long Id, string Label);
public sealed record TransitionRequest(string? Transition, string? Comment, JsonElement? Values,
    [property: System.Text.Json.Serialization.JsonPropertyName("_rowVersion")] string? RowVersion);
public sealed record HistoryItem(DateTime At, string User, string? From, string? FromLabel, string To, string ToLabel,
    string? Transition, string? TransitionLabel, string? Comment);

/// <summary>
/// Motor genérico de registros y documentos sobre las tablas <c>app.*</c>.
/// Reglas de seguridad: identificadores solo desde <see cref="EntityDef"/> (nunca desde la petición),
/// valores siempre parametrizados, fórmulas evaluadas en memoria con <see cref="ExprEvaluator"/>.
/// </summary>
public sealed class RecordService(Db db, MetadataStore store)
{
    public const int MaxPageSize = 200;
    public const int MaxSearchLength = 100;
    public const int MaxLongText = 1_000_000;
    public const int MaxLines = 500;

    private static readonly HashSet<string> IgnoredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "_rowVersion", "_workflow", "createdAt", "createdBy", "updatedAt", "updatedBy",
    };

    // ======================= Lectura =======================

    public async Task<ListResult> ListAsync(string entityName, ListQuery q, UserContext user, CancellationToken ct)
    {
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Read);

        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var (select, from) = SelectClause(e, model);
        var where = new StringBuilder("t.[IsDeleted] = 0");

        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var searchable = e.StoredFields.Where(f => FieldTypes.Searchable.Contains(f.Type)).ToList();
            if (searchable.Count > 0)
            {
                var term = q.Search.Trim();
                if (term.Length > MaxSearchLength) term = term[..MaxSearchLength];
                cmd.Parameters.Add(new SqlParameter("@search", "%" + EscapeLike(term) + "%"));
                where.Append(" AND (").Append(string.Join(" OR ",
                    searchable.Select(f => $"t.{Quote(f.Name)} LIKE @search ESCAPE '\\'"))).Append(')');
            }
        }

        // El filtro por estado se compara contra los estados declarados, nunca se concatena.
        var baseWhere = where.ToString();
        var state = e.Workflow?.FindState(q.State);
        if (state is not null)
        {
            cmd.Parameters.Add(new SqlParameter("@state", state.Name));
            where.Append($" AND t.{Quote(Workflow.StateField)} = @state");
        }

        var orderBy = ResolveSort(e, q.Sort, q.Dir);
        cmd.Parameters.Add(new SqlParameter("@skip", (page - 1) * size));
        cmd.Parameters.Add(new SqlParameter("@take", size));
        var counts = e.Workflow is null ? "" :
            $"SELECT t.{Quote(Workflow.StateField)}, COUNT(*) FROM {AppTable(e.Name)} t WHERE {baseWhere} GROUP BY t.{Quote(Workflow.StateField)};";
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM {AppTable(e.Name)} t WHERE {where};
            SELECT {select} FROM {from} WHERE {where} ORDER BY {orderBy} OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            {counts}
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var total = reader.GetInt32(0);
        await reader.NextResultAsync(ct);
        var items = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct)) items.Add(Format(e, ReadRaw(reader)));

        List<StateCount>? stateCounts = null;
        if (e.Workflow is not null)
        {
            await reader.NextResultAsync(ct);
            var byState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync(ct)) if (!reader.IsDBNull(0)) byState[reader.GetString(0)] = reader.GetInt32(1);
            stateCounts = e.Workflow.States.Select(s => new StateCount(s.Name, s.Label, s.Color, byState.GetValueOrDefault(s.Name))).ToList();
        }
        return new ListResult(items, total, stateCounts);
    }

    public async Task<Dictionary<string, object?>> GetAsync(string entityName, long id, UserContext user, CancellationToken ct)
    {
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Read);
        await using var cn = await db.OpenAsync(ct);
        return await LoadOutputAsync(cn, null, e, model, id, user, ct) ?? throw new NotFound("El registro no existe.");
    }

    public async Task<IReadOnlyList<LookupItem>> LookupAsync(string entityName, string? search, UserContext user, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        var e = await store.GetActiveAsync(entityName, ct);
        // Se permite consultar opciones si puede leer el catálogo o capturar en uno que lo referencia
        // (incluye partidas, cuyo permiso es el de su documento).
        bool CanWrite(EntityDef o) => o.IsChild
            ? model.TryGetValue(o.ParentEntity!, out var p) && (user.Access(p.Name).Create || user.Access(p.Name).Update)
            : user.Access(o.Name).Create || user.Access(o.Name).Update;
        var allowed = user.Access(e.Name).Read || model.Values.Any(o => !o.IsArchived &&
            o.ActiveFields.Any(f => f.Type == FieldTypes.Reference && string.Equals(f.ReferenceEntity, e.Name, StringComparison.OrdinalIgnoreCase)) &&
            CanWrite(o));
        Require(allowed);

        var label = e.EffectiveDisplayField is { } d ? $"t.{Quote(d)}" : "CAST(t.[Id] AS nvarchar(20))";
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        var where = "t.[IsDeleted] = 0";
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (term.Length > MaxSearchLength) term = term[..MaxSearchLength];
            cmd.Parameters.Add(new SqlParameter("@search", "%" + EscapeLike(term) + "%"));
            where += $" AND {label} LIKE @search ESCAPE '\\'";
        }
        cmd.CommandText = $"SELECT TOP 20 t.[Id], {label} FROM {AppTable(e.Name)} t WHERE {where} ORDER BY 2";
        var list = new List<LookupItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new LookupItem(reader.GetInt64(0), reader.IsDBNull(1) ? $"#{reader.GetInt64(0)}" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!));
        return list;
    }

    public async Task<IReadOnlyList<LookupItem>> UserLookupAsync(string? search, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT TOP 20 Id, DisplayName FROM meta.[User] WHERE IsActive = 1 AND (@s IS NULL OR DisplayName LIKE @s ESCAPE '\\' OR Username LIKE @s ESCAPE '\\') ORDER BY DisplayName", cn);
        var term = string.IsNullOrWhiteSpace(search) ? null : "%" + EscapeLike(search.Trim()[..Math.Min(search.Trim().Length, MaxSearchLength)]) + "%";
        cmd.Parameters.Add(new SqlParameter("@s", (object?)term ?? DBNull.Value));
        var list = new List<LookupItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new LookupItem(r.GetInt32(0), r.GetString(1)));
        return list;
    }

    public async Task<IReadOnlyList<HistoryItem>> HistoryAsync(string entityName, long id, UserContext user, CancellationToken ct)
    {
        var (e, _) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Read);
        if (e.Workflow is null) return [];
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT h.At, u.DisplayName, h.FromState, h.ToState, h.Transition, h.Comment
              FROM meta.WorkflowHistory h JOIN meta.[User] u ON u.Id = h.UserId
             WHERE h.EntityName = @e AND h.RecordId = @id ORDER BY h.Id
            """, cn);
        cmd.Parameters.Add(new SqlParameter("@e", e.Name));
        cmd.Parameters.Add(new SqlParameter("@id", id));
        var list = new List<HistoryItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? from = r.IsDBNull(2) ? null : r.GetString(2), to = r.GetString(3), t = r.IsDBNull(4) ? null : r.GetString(4);
            list.Add(new HistoryItem(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc), r.GetString(1),
                from, e.Workflow.FindState(from)?.Label ?? from, to, e.Workflow.FindState(to)?.Label ?? to,
                t, e.Workflow.Transitions.FirstOrDefault(x => x.Name == t)?.Label ?? t, r.IsDBNull(5) ? null : r.GetString(5)));
        }
        return list;
    }

    // ======================= Escritura =======================

    public async Task<Dictionary<string, object?>> CreateAsync(string entityName, JsonElement body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Create);

        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var id = await CreateInTransactionAsync(cn, tx, e, model, body, user, http, ct);
        var row = (await LoadOutputAsync(cn, tx, e, model, id, user, ct))!;
        await tx.CommitAsync(ct);
        return row;
    }

    /// <summary>Alta completa (valores por defecto, fórmulas, reglas, folio, partidas) dentro de una transacción ajena.</summary>
    internal async Task<long> CreateInTransactionAsync(SqlConnection cn, SqlTransaction tx, EntityDef e,
        IReadOnlyDictionary<string, EntityDef> model, JsonElement body, UserContext user, HttpContext http, CancellationToken ct)
    {
        var loader = new RelatedLoader(cn, tx, model);
        var draft = new DocDraft { CreatorId = user.Id };
        await ParseIntoAsync(draft, e, model, body, loader, lenient: false, ct);
        if (e.Workflow is not null) draft.Header[Workflow.StateField] = e.Workflow.Initial;
        var env = Env(user, draft);
        Compute(draft, e, model, loader, env);
        Validate(draft, e, model, loader, env, user);

        await AssignFoliosAsync(cn, tx, e, draft, ct);
        var id = await InsertAsync(cn, tx, e, draft.Header, extra: null, user.Id, ct);
        await SaveLinesAsync(cn, tx, e, model, id, draft, existing: null, user.Id, ct);
        if (e.Workflow is not null)
            await WriteHistoryAsync(cn, tx, e, id, null, e.Workflow.Initial, null, null, user.Id, ct);
        await Audit.WriteAsync(cn, tx, http, AuditActions.Create, e.Name, id.ToString(), after: Snapshot(draft));
        return id;
    }

    public async Task<Dictionary<string, object?>> UpdateAsync(string entityName, long id, JsonElement body, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Update);
        var rowVersion = ReadRowVersion(body.ValueKind == JsonValueKind.Object && body.TryGetProperty("_rowVersion", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString() : null);

        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var loader = new RelatedLoader(cn, tx, model);
        var draft = await LoadDraftAsync(cn, tx, e, model, id, ct) ?? throw new NotFound("El registro no existe.");
        var before = Snapshot(draft);

        if (e.Workflow?.FindState(draft.Header.GetValueOrDefault(Workflow.StateField) as string) is { Editable: false } st)
            throw new AppException(StatusCodes.Status409Conflict, $"El documento está en estado «{st.Label}» y ya no se puede editar.");

        await ParseIntoAsync(draft, e, model, body, loader, lenient: false, ct);
        var env = Env(user, draft);
        Compute(draft, e, model, loader, env);
        Validate(draft, e, model, loader, env, user);

        await UpdateHeaderAsync(cn, tx, e, id, draft.Header, rowVersion, user.Id, ct);
        var existingLines = await LoadDraftAsync(cn, tx, e, model, id, ct);
        await SaveLinesAsync(cn, tx, e, model, id, draft, existingLines, user.Id, ct);
        await Audit.WriteAsync(cn, tx, http, AuditActions.Update, e.Name, id.ToString(), before, Snapshot(draft));
        var row = (await LoadOutputAsync(cn, tx, e, model, id, user, ct))!;
        await tx.CommitAsync(ct);
        return row;
    }

    /// <summary>Calcula valores por defecto y fórmulas sin guardar (totales en vivo en la UI).</summary>
    public async Task<Dictionary<string, object?>> ComputeAsync(string entityName, long? id, JsonElement values, UserContext user, CancellationToken ct)
    {
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(id is null ? user.Access(e.Name).Create : user.Access(e.Name).Update);
        await using var cn = await db.OpenAsync(ct);
        var loader = new RelatedLoader(cn, null, model);
        var draft = id is null ? new DocDraft { CreatorId = user.Id } : await LoadDraftAsync(cn, null, e, model, id.Value, ct) ?? throw new NotFound("El registro no existe.");
        if (id is null && e.Workflow is not null) draft.Header[Workflow.StateField] = e.Workflow.Initial;
        await ParseIntoAsync(draft, e, model, values, loader, lenient: true, ct);
        Compute(draft, e, model, loader, Env(user, draft));
        return await FormatDraftAsync(cn, e, model, draft, ct);
    }

    public async Task<Dictionary<string, object?>> TransitionAsync(string entityName, long id, TransitionRequest req, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Read);
        var wf = e.Workflow ?? throw new AppException(400, "Este catálogo no tiene proceso.");
        var t = wf.Transitions.FirstOrDefault(x => string.Equals(x.Name, req.Transition, StringComparison.OrdinalIgnoreCase))
                ?? throw new AppException(400, "La acción no existe.");
        var rowVersion = ReadRowVersion(req.RowVersion);

        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var loader = new RelatedLoader(cn, tx, model);
        var draft = await LoadDraftAsync(cn, tx, e, model, id, ct) ?? throw new NotFound("El registro no existe.");
        var before = Snapshot(draft);
        var from = draft.Header.GetValueOrDefault(Workflow.StateField) as string;
        var env = Env(user, draft);

        var (allowed, reason, missingRole) = CanTransition(e, t, draft, model, loader, env, user);
        if (!allowed) throw missingRole ? new Forbidden(reason!) : new AppException(400, reason!);

        var errors = new Dictionary<string, string[]>();
        var comment = req.Comment?.Trim();
        if (comment?.Length > 1000) errors["comment"] = ["El comentario admite máximo 1000 caracteres."];
        if (t.RequireComment && string.IsNullOrWhiteSpace(comment)) errors["comment"] = ["Escribe un comentario."];

        // Solo se aceptan los campos que la transición pide; el resto del documento no cambia.
        if (req.Values is { ValueKind: JsonValueKind.Object } vals)
            foreach (var prop in vals.EnumerateObject())
            {
                var f = t.RequiredFields?.FirstOrDefault(n => string.Equals(n, prop.Name, StringComparison.OrdinalIgnoreCase)) is { } n2 ? e.FindField(n2) : null;
                if (f is null) { errors[prop.Name] = ["Este campo no se captura en esta acción."]; continue; }
                try { draft.Header[f.Name] = await ParseValueAsync(f, prop.Value, loader, ct); }
                catch (FieldError ex) { errors[f.Name] = [ex.Message]; }
            }
        foreach (var rf in t.RequiredFields ?? [])
            if (e.FindField(rf) is { } f && IsBlank(draft.Header.GetValueOrDefault(f.Name)) && !errors.ContainsKey(f.Name))
                errors[f.Name] = [$"{f.Label} es obligatorio para «{t.Label}»."];
        if (errors.Count > 0) throw new ValidationFailed(errors);

        draft.Header[Workflow.StateField] = t.To;
        var scope = HeaderScope(draft, e, model, loader);
        foreach (var s in t.Set ?? [])
            if (e.FindField(s.Field) is { } f)
                draft.Header[f.Name] = Coerce(f, ExprEvaluator.Evaluate(s.Value, scope, env));
        Compute(draft, e, model, loader, env);
        Validate(draft, e, model, loader, env, user, checkRequired: false);

        await UpdateHeaderAsync(cn, tx, e, id, draft.Header, rowVersion, user.Id, ct);
        await WriteHistoryAsync(cn, tx, e, id, from, t.To, t.Name, comment, user.Id, ct);
        await Audit.WriteAsync(cn, tx, http, $"workflow.{t.Name}", e.Name, id.ToString(), before, Snapshot(draft));
        var row = (await LoadOutputAsync(cn, tx, e, model, id, user, ct))!;
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task DeleteAsync(string entityName, long id, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var (e, model) = await RootEntityAsync(entityName, ct);
        Require(user.Access(e.Name).Delete);

        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var draft = await LoadDraftAsync(cn, tx, e, model, id, ct) ?? throw new NotFound("El registro no existe.");
        if (e.Workflow?.FindState(draft.Header.GetValueOrDefault(Workflow.StateField) as string) is { Editable: false } st)
            throw new AppException(StatusCodes.Status409Conflict, $"El documento está en estado «{st.Label}» y no se puede eliminar.");

        // Integridad: no se elimina un registro que siga en uso por registros activos.
        foreach (var other in model.Values)
        foreach (var f in other.Fields.Where(f => f.Type == FieldTypes.Reference && string.Equals(f.ReferenceEntity, e.Name, StringComparison.OrdinalIgnoreCase)))
        {
            await using var check = new SqlCommand(
                $"SELECT TOP 1 1 FROM {AppTable(other.Name)} WHERE {Quote(f.Name)} = @id AND [IsDeleted] = 0", cn, tx);
            check.Parameters.Add(new SqlParameter("@id", id));
            if (await check.ExecuteScalarAsync(ct) is not null)
            {
                var where = other.IsChild && model.TryGetValue(other.ParentEntity!, out var p) ? p.LabelPlural : other.LabelPlural;
                throw new Conflict($"No se puede eliminar: está en uso en {where} ({f.Label}).");
            }
        }

        await SoftDeleteAsync(cn, tx, e, "[Id] = @id", id, user.Id, ct);
        foreach (var lf in e.ActiveFields.Where(f => f.Type == FieldTypes.Lines))
            if (model.TryGetValue(lf.ChildEntity!, out var child))
                await SoftDeleteAsync(cn, tx, child, $"{Quote(child.ParentField!.Name)} = @id", id, user.Id, ct);
        await Audit.WriteAsync(cn, tx, http, AuditActions.Delete, e.Name, id.ToString(), before: Snapshot(draft));
        await tx.CommitAsync(ct);
    }

    // ======================= Pipeline del documento =======================

    private async Task<(EntityDef Entity, IReadOnlyDictionary<string, EntityDef> Model)> RootEntityAsync(string name, CancellationToken ct)
    {
        var e = await store.GetActiveAsync(name, ct);
        if (e.IsChild) throw new AppException(400, "Las partidas se consultan y editan desde su documento.");
        return (e, await store.GetAsync(ct));
    }

    private static ExprEnv Env(UserContext user, DocDraft draft) => new(user.Id, user.Roles, draft.CreatorId);

    private static DocScope HeaderScope(DocDraft d, EntityDef e, IReadOnlyDictionary<string, EntityDef> model, RelatedLoader loader, Dictionary<string, List<DocScope>>? lineScopes = null)
    {
        var lines = new Dictionary<string, IReadOnlyList<IExprScope>>(StringComparer.OrdinalIgnoreCase);
        var scope = new DocScope(e, d.Header, loader, lines);
        foreach (var (field, list) in d.Lines)
        {
            if (e.FindField(field) is not { ChildEntity: { } childName } || !model.TryGetValue(childName, out var child)) continue;
            var scopes = list.Select(l => new DocScope(child, l.Values, loader, parent: scope)).ToList();
            lines[field] = scopes;
            lineScopes?.Add(field, scopes);
        }
        return scope;
    }

    /// <summary>Aplica valores por defecto (campo vacío) y fórmulas: partidas primero, luego el encabezado.</summary>
    private static void Compute(DocDraft d, EntityDef e, IReadOnlyDictionary<string, EntityDef> model, RelatedLoader loader, ExprEnv env)
    {
        var header = HeaderScope(d, e, model, loader);
        ApplyDefaults(e, d.Header, header, env);

        foreach (var (field, list) in d.Lines)
        {
            if (e.FindField(field) is not { ChildEntity: { } childName } || !model.TryGetValue(childName, out var child)) continue;
            var order = FormulaGraph.Order(child);
            foreach (var line in list)
            {
                var ls = new DocScope(child, line.Values, loader, parent: header);
                ApplyDefaults(child, line.Values, ls, env);
                foreach (var f in order) line.Values[f.Name] = Coerce(f, ExprEvaluator.Evaluate(f.Formula!, ls, env));
            }
        }

        header = HeaderScope(d, e, model, loader);
        foreach (var f in FormulaGraph.Order(e)) d.Header[f.Name] = Coerce(f, ExprEvaluator.Evaluate(f.Formula!, header, env));
    }

    private static void ApplyDefaults(EntityDef e, Values values, IExprScope scope, ExprEnv env)
    {
        foreach (var f in e.ActiveFields.Where(f => f.DefaultValue is not null && !f.ReadOnly))
            if (IsBlank(values.GetValueOrDefault(f.Name)))
                values[f.Name] = Coerce(f, ExprEvaluator.Evaluate(f.DefaultValue!, scope, env), lenient: true);
    }

    private static void Validate(DocDraft d, EntityDef e, IReadOnlyDictionary<string, EntityDef> model, RelatedLoader loader, ExprEnv env,
        UserContext user, bool checkRequired = true)
    {
        var errors = new Dictionary<string, string[]>();
        var lineScopes = new Dictionary<string, List<DocScope>>(StringComparer.OrdinalIgnoreCase);
        var header = HeaderScope(d, e, model, loader, lineScopes);

        if (checkRequired) RequiredErrors(e, d.Header, user, "", errors);
        RuleErrors(e, header, env, "", errors);

        foreach (var (field, list) in d.Lines)
        {
            if (list.Count > MaxLines) errors[field] = [$"Máximo {MaxLines} partidas por documento."];
            if (e.FindField(field) is not { ChildEntity: { } childName } || !model.TryGetValue(childName, out var child)) continue;
            for (var i = 0; i < list.Count; i++)
            {
                var prefix = $"{field}[{i}].";
                if (checkRequired) RequiredErrors(child, list[i].Values, user, prefix, errors);
                RuleErrors(child, lineScopes[field][i], env, prefix, errors);
            }
        }
        if (errors.Count > 0) throw new ValidationFailed(errors);
    }

    private static void RequiredErrors(EntityDef e, Values values, UserContext user, string prefix, Dictionary<string, string[]> errors)
    {
        foreach (var f in e.ActiveFields.Where(f => !f.ReadOnly && f.Type != FieldTypes.Lines))
            if ((f.Required || user.HasRole(f.RequiredForRoles)) && IsBlank(values.GetValueOrDefault(f.Name)))
                errors[prefix + f.Name] = [$"{f.Label} es obligatorio."];
    }

    private static void RuleErrors(EntityDef e, IExprScope scope, ExprEnv env, string prefix, Dictionary<string, string[]> errors)
    {
        foreach (var r in e.Rules ?? [])
        {
            if (r.When is not null && !ExprEvaluator.Test(r.When, scope, env, whenNull: false)) continue;
            if (ExprEvaluator.Test(r.Assert, scope, env, whenNull: true)) continue;
            var key = prefix + (r.Field ?? "registro");
            errors[key] = errors.TryGetValue(key, out var prev) ? [.. prev, r.Message] : [r.Message];
        }
    }

    /// <summary><c>MissingRole</c> distingue "no te toca" (403) de "no se cumplen las condiciones" (400).</summary>
    private static (bool Allowed, string? Reason, bool MissingRole) CanTransition(EntityDef e, WorkflowTransition t, DocDraft d,
        IReadOnlyDictionary<string, EntityDef> model, RelatedLoader loader, ExprEnv env, UserContext user)
    {
        var state = d.Header.GetValueOrDefault(Workflow.StateField) as string;
        if (!t.From.Contains(state ?? "", StringComparer.OrdinalIgnoreCase))
            return (false, $"La acción «{t.Label}» no aplica en el estado actual.", false);
        var roleOk = user.IsSuperuser || (t.Roles is { Count: > 0 } ? user.HasRole(t.Roles) : user.Access(e.Name).Update);
        if (!roleOk)
            return (false, t.Roles is { Count: > 0 } ? $"Requiere el rol {string.Join(" o ", t.Roles)}." : "Requiere permiso de edición.", true);
        if (t.When is not null && !ExprEvaluator.Test(t.When, HeaderScope(d, e, model, loader), env, whenNull: false))
            return (false, t.WhenMessage ?? "No se cumplen las condiciones para esta acción.", false);
        return (true, null, false);
    }

    // ======================= Parseo de la petición =======================

    /// <summary>Error de validación con mensaje pensado para el usuario (los de .NET no se exponen).</summary>
    private sealed class FieldError(string message) : Exception(message);

    /// <summary>
    /// Sobrepone los valores de la petición al borrador. Campos calculados se ignoran (los calcula el
    /// servidor); campos desconocidos son error. Un campo de partidas reemplaza el conjunto completo.
    /// </summary>
    private static async Task ParseIntoAsync(DocDraft d, EntityDef e, IReadOnlyDictionary<string, EntityDef> model, JsonElement body,
        RelatedLoader loader, bool lenient, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new AppException(400, "Se esperaba un objeto JSON.");
        var errors = new Dictionary<string, string[]>();

        foreach (var prop in body.EnumerateObject())
        {
            if (IgnoredKeys.Contains(prop.Name) || prop.Name.EndsWith("__label", StringComparison.Ordinal)) continue;
            var f = e.FindField(prop.Name);
            if (f is null || f.IsArchived) { if (!lenient) errors[prop.Name] = ["Campo desconocido."]; continue; }
            if (f.ClientLocked) continue; // calculado o asignado por el sistema: el cliente no lo escribe

            if (f.Type == FieldTypes.Lines)
            {
                if (!model.TryGetValue(f.ChildEntity!, out var child)) continue;
                if (prop.Value.ValueKind == JsonValueKind.Null) { d.Lines[f.Name] = []; continue; }
                if (prop.Value.ValueKind != JsonValueKind.Array) { errors[f.Name] = ["Se esperaba una lista de partidas."]; continue; }
                var lines = new List<LineDraft>();
                var i = 0;
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (i >= MaxLines) { errors[f.Name] = [$"Máximo {MaxLines} partidas por documento."]; break; }
                    if (item.ValueKind != JsonValueKind.Object) { errors[$"{f.Name}[{i}]"] = ["Partida inválida."]; i++; continue; }
                    long? lineId = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : null;
                    var existing = lineId is null ? null : d.Lines.GetValueOrDefault(f.Name)?.FirstOrDefault(l => l.Id == lineId);
                    if (lineId is not null && existing is null && !lenient) { errors[$"{f.Name}[{i}]"] = ["La partida no pertenece a este documento."]; i++; continue; }
                    var line = new LineDraft { Id = existing?.Id, Values = existing is null ? new Values() : new Values(existing.Values) };
                    foreach (var lp in item.EnumerateObject())
                    {
                        if (IgnoredKeys.Contains(lp.Name) || lp.Name.EndsWith("__label", StringComparison.Ordinal)) continue;
                        var lf = child.FindField(lp.Name);
                        if (lf is null || lf.IsArchived) { if (!lenient) errors[$"{f.Name}[{i}].{lp.Name}"] = ["Campo desconocido."]; continue; }
                        if (lf.ClientLocked || lf.Type == FieldTypes.Lines) continue;
                        try { line.Values[lf.Name] = await ParseValueAsync(lf, lp.Value, loader, ct); }
                        catch (FieldError ex) { if (!lenient) errors[$"{f.Name}[{i}].{lf.Name}"] = [ex.Message]; }
                    }
                    lines.Add(line);
                    i++;
                }
                d.Lines[f.Name] = lines;
                continue;
            }

            try { d.Header[f.Name] = await ParseValueAsync(f, prop.Value, loader, ct); }
            catch (FieldError ex) { if (!lenient) errors[f.Name] = [ex.Message]; }
        }
        if (errors.Count > 0) throw new ValidationFailed(errors);
    }

    private static Task<object?> ParseValueAsync(FieldDef f, JsonElement v, RelatedLoader loader, CancellationToken ct)
    {
        object? value;
        try { value = ConvertValue(f, v); }
        catch (FieldError) { throw; }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw new FieldError($"Valor inválido para {f.Label}.");
        }
        if (value is long refId && f.Type == FieldTypes.Reference && loader.Row(f.ReferenceEntity!, refId) is null)
            throw new FieldError("El registro seleccionado no existe.");
        if (value is int userId && f.Type == FieldTypes.User && loader.User(userId) is null)
            throw new FieldError("El usuario seleccionado no existe.");
        return Task.FromResult(value);
    }

    internal static object? ConvertValue(FieldDef f, JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Null) return null;
        switch (f.Type)
        {
            case FieldTypes.Text:
            case FieldTypes.LongText:
            {
                var s = v.GetString()!.Trim();
                if (s.Length == 0) return null;
                var max = f.Type == FieldTypes.Text ? f.MaxLength ?? FieldTypes.DefaultTextLength : MaxLongText;
                if (s.Length > max) throw new FieldError($"{f.Label} admite máximo {max} caracteres.");
                return s;
            }
            case FieldTypes.Integer:
                if (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())) return null;
                return v.ValueKind == JsonValueKind.String
                    ? long.Parse(v.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture)
                    : v.GetInt64();
            case FieldTypes.Decimal:
            {
                if (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())) return null;
                var d = v.ValueKind == JsonValueKind.String
                    ? decimal.Parse(v.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)
                    : v.GetDecimal();
                int p = f.Precision ?? 18, s = f.Scale ?? 2;
                if (d.Scale > s && decimal.Round(d, s) != d) throw new FieldError($"{f.Label} admite máximo {s} decimales.");
                if (Math.Abs(decimal.Truncate(d)).ToString(CultureInfo.InvariantCulture).TrimStart('0').Length > p - s)
                    throw new FieldError($"{f.Label} es demasiado grande.");
                return decimal.Round(d, s);
            }
            case FieldTypes.Boolean:
                return v.GetBoolean();
            case FieldTypes.Date:
                if (string.IsNullOrWhiteSpace(v.GetString())) return null;
                return DateOnly.ParseExact(v.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue);
            case FieldTypes.DateTime:
                if (string.IsNullOrWhiteSpace(v.GetString())) return null;
                // Se almacena en UTC; el cliente convierte a la zona del usuario.
                return DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            case FieldTypes.Choice:
            {
                var s = v.GetString()!.Trim();
                if (s.Length == 0) return null;
                if (f.Options?.Contains(s) != true) throw new FieldError($"'{s}' no es una opción válida de {f.Label}.");
                return s;
            }
            case FieldTypes.Reference:
                if (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())) return null;
                return v.ValueKind == JsonValueKind.String
                    ? long.Parse(v.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture)
                    : v.GetInt64();
            case FieldTypes.User:
                if (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())) return null;
                return v.ValueKind == JsonValueKind.String
                    ? int.Parse(v.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture)
                    : v.GetInt32();
            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>Convierte el resultado de una expresión al tipo del campo destino.</summary>
    internal static object? Coerce(FieldDef f, object? value, bool lenient = false)
    {
        value = ExprEvaluator.Normalize(value);
        if (value is null) return null;
        var type = f.Type == FieldTypes.Formula ? f.ResultType : f.Type;
        try
        {
            return type switch
            {
                FieldTypes.Decimal => decimal.Round(Convert.ToDecimal(value, CultureInfo.InvariantCulture), f.Scale ?? 2, MidpointRounding.AwayFromZero),
                FieldTypes.Integer => (long)decimal.Round(Convert.ToDecimal(value, CultureInfo.InvariantCulture), 0, MidpointRounding.AwayFromZero),
                FieldTypes.Boolean => value is bool b ? b : throw new InvalidCastException(),
                FieldTypes.Date => value is DateTime d ? d.Date : throw new InvalidCastException(),
                FieldTypes.DateTime => value is DateTime dt ? dt : throw new InvalidCastException(),
                FieldTypes.Reference => (long)Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                FieldTypes.User => (int)Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                FieldTypes.Choice => value is string s && f.Options?.Contains(s) == true ? s : lenient ? null : throw new InvalidCastException(),
                _ => Truncate(Convert.ToString(value, CultureInfo.InvariantCulture)!, f.MaxLength ?? 400),
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            if (lenient) return null;
            throw new ExprException($"El cálculo de {f.Label} no produjo un valor de tipo {type}.");
        }
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;
    private static bool IsBlank(object? v) => v is null || v is string s && string.IsNullOrWhiteSpace(s);

    private static byte[] ReadRowVersion(string? value)
    {
        if (string.IsNullOrEmpty(value))
            throw new ValidationFailed(new Dictionary<string, string[]> { ["_rowVersion"] = ["Falta la versión del registro."] });
        try { return Convert.FromBase64String(value); }
        catch (FormatException) { throw new ValidationFailed(new Dictionary<string, string[]> { ["_rowVersion"] = ["Versión inválida."] }); }
    }

    // ======================= Persistencia =======================

    private static async Task AssignFoliosAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, DocDraft d, CancellationToken ct)
    {
        foreach (var f in e.ActiveFields.Where(f => f.Type == FieldTypes.Autonumber))
        {
            // UPDLOCK+HOLDLOCK: dos altas simultáneas nunca obtienen el mismo número.
            await using var cmd = new SqlCommand("""
                DECLARE @v bigint;
                UPDATE meta.Sequence WITH (UPDLOCK, HOLDLOCK) SET @v = NextValue, NextValue = NextValue + 1
                 WHERE EntityName = @e AND FieldName = @f;
                IF @v IS NULL BEGIN INSERT meta.Sequence (EntityName, FieldName, NextValue) VALUES (@e, @f, 2); SET @v = 1; END
                SELECT @v;
                """, cn, tx);
            cmd.Parameters.Add(new SqlParameter("@e", e.Name));
            cmd.Parameters.Add(new SqlParameter("@f", f.Name));
            var next = (long)(await cmd.ExecuteScalarAsync(ct))!;
            d.Header[f.Name] = (f.Prefix ?? "") + next.ToString(CultureInfo.InvariantCulture).PadLeft(f.Padding ?? 6, '0');
        }
    }

    private static async Task<long> InsertAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, Values values,
        (string Column, object Value)? extra, int userId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand { Connection = cn, Transaction = tx };
        var cols = new List<string>();
        var pars = new List<string>();
        var i = 0;
        foreach (var f in e.StoredFields.Where(f => f.Type != FieldTypes.Parent))
        {
            if (!values.TryGetValue(f.Name, out var v) || v is null) continue;
            cols.Add(Quote(f.Name));
            pars.Add($"@p{i}");
            cmd.Parameters.Add(new SqlParameter($"@p{i++}", v));
        }
        if (extra is { } x)
        {
            cols.Add(Quote(x.Column));
            pars.Add("@parent");
            cmd.Parameters.Add(new SqlParameter("@parent", x.Value));
        }
        cols.Add("[CreatedBy]");
        pars.Add("@uid");
        cmd.Parameters.Add(new SqlParameter("@uid", userId));
        cmd.CommandText = $"INSERT INTO {AppTable(e.Name)} ({string.Join(", ", cols)}) OUTPUT inserted.[Id] VALUES ({string.Join(", ", pars)})";
        return (long)(await ExecuteMappingUniqueAsync(e, () => cmd.ExecuteScalarAsync(ct)))!;
    }

    private static async Task UpdateHeaderAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, long id, Values values,
        byte[] rowVersion, int userId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand { Connection = cn, Transaction = tx };
        var sets = new List<string>();
        var i = 0;
        foreach (var f in e.StoredFields.Where(f => f.Type is not (FieldTypes.Parent or FieldTypes.Autonumber)))
        {
            sets.Add($"{Quote(f.Name)} = @p{i}");
            cmd.Parameters.Add(new SqlParameter($"@p{i++}", values.GetValueOrDefault(f.Name) ?? DBNull.Value));
        }
        cmd.Parameters.Add(new SqlParameter("@uid", userId));
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@rv", System.Data.SqlDbType.Timestamp) { Value = rowVersion });
        cmd.CommandText = $"""
            UPDATE {AppTable(e.Name)} SET {string.Join(", ", sets.Append("[UpdatedAt] = SYSUTCDATETIME()").Append("[UpdatedBy] = @uid"))}
             WHERE [Id] = @id AND [RowVer] = @rv AND [IsDeleted] = 0
            """;
        var affected = (int)(await ExecuteMappingUniqueAsync(e, async () => (object?)await cmd.ExecuteNonQueryAsync(ct)))!;
        if (affected == 0) throw new Conflict("Otro usuario modificó este registro. Recarga para ver los cambios.");
    }

    private static async Task SaveLinesAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, IReadOnlyDictionary<string, EntityDef> model,
        long parentId, DocDraft draft, DocDraft? existing, int userId, CancellationToken ct)
    {
        foreach (var (field, lines) in draft.Lines)
        {
            if (e.FindField(field) is not { ChildEntity: { } childName } || !model.TryGetValue(childName, out var child)) continue;
            var parentCol = child.ParentField!.Name;
            var keep = new HashSet<long>();
            foreach (var line in lines)
            {
                if (line.Id is { } lid)
                {
                    keep.Add(lid);
                    await using var cmd = new SqlCommand { Connection = cn, Transaction = tx };
                    var sets = new List<string>();
                    var i = 0;
                    foreach (var f in child.StoredFields.Where(f => f.Type is not (FieldTypes.Parent or FieldTypes.Autonumber)))
                    {
                        sets.Add($"{Quote(f.Name)} = @p{i}");
                        cmd.Parameters.Add(new SqlParameter($"@p{i++}", line.Values.GetValueOrDefault(f.Name) ?? DBNull.Value));
                    }
                    cmd.Parameters.Add(new SqlParameter("@uid", userId));
                    cmd.Parameters.Add(new SqlParameter("@id", lid));
                    cmd.Parameters.Add(new SqlParameter("@parent", parentId));
                    cmd.CommandText = $"""
                        UPDATE {AppTable(child.Name)} SET {string.Join(", ", sets.Append("[UpdatedAt] = SYSUTCDATETIME()").Append("[UpdatedBy] = @uid"))}
                         WHERE [Id] = @id AND {Quote(parentCol)} = @parent AND [IsDeleted] = 0
                        """;
                    await ExecuteMappingUniqueAsync(child, async () => (object?)await cmd.ExecuteNonQueryAsync(ct));
                }
                else
                {
                    keep.Add(await InsertAsync(cn, tx, child, line.Values, (parentCol, parentId), userId, ct));
                }
            }
            // Las partidas que ya no vienen se eliminan (borrado lógico).
            foreach (var old in existing?.Lines.GetValueOrDefault(field) ?? [])
                if (old.Id is { } oid && !keep.Contains(oid))
                    await SoftDeleteAsync(cn, tx, child, "[Id] = @id", oid, userId, ct);
        }
    }

    private static async Task SoftDeleteAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, string where, long id, int userId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            $"UPDATE {AppTable(e.Name)} SET [IsDeleted] = 1, [UpdatedAt] = SYSUTCDATETIME(), [UpdatedBy] = @uid WHERE {where} AND [IsDeleted] = 0", cn, tx);
        cmd.Parameters.Add(new SqlParameter("@uid", userId));
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteHistoryAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, long id, string? from, string to,
        string? transition, string? comment, int userId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            INSERT meta.WorkflowHistory (EntityName, RecordId, FromState, ToState, Transition, Comment, UserId)
            VALUES (@e, @id, @from, @to, @t, @c, @u)
            """, cn, tx);
        cmd.Parameters.Add(new SqlParameter("@e", e.Name));
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@from", (object?)from ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@to", to));
        cmd.Parameters.Add(new SqlParameter("@t", (object?)transition ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@c", (object?)comment ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@u", userId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ======================= Carga y formato =======================

    /// <summary>Documento con valores tipados (para cálculo) incluidas sus partidas.</summary>
    private static async Task<DocDraft?> LoadDraftAsync(SqlConnection cn, SqlTransaction? tx, EntityDef e,
        IReadOnlyDictionary<string, EntityDef> model, long id, CancellationToken ct)
    {
        var cols = e.StoredFields.Select(f => Quote(f.Name)).Prepend("[CreatedBy]").Prepend("[Id]");
        Values? header = null;
        await using (var cmd = new SqlCommand($"SELECT {string.Join(", ", cols)} FROM {AppTable(e.Name)} WHERE [Id] = @id AND [IsDeleted] = 0", cn, tx))
        {
            cmd.Parameters.Add(new SqlParameter("@id", id));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) header = ReadValues(r);
        }
        if (header is null) return null;
        var draft = new DocDraft { Id = id, CreatorId = header["CreatedBy"] as int?, Header = header };

        foreach (var lf in e.ActiveFields.Where(f => f.Type == FieldTypes.Lines))
        {
            if (!model.TryGetValue(lf.ChildEntity!, out var child) || child.ParentField is null) continue;
            var ccols = child.StoredFields.Select(f => Quote(f.Name)).Prepend("[Id]");
            await using var cmd = new SqlCommand(
                $"SELECT {string.Join(", ", ccols)} FROM {AppTable(child.Name)} WHERE {Quote(child.ParentField.Name)} = @id AND [IsDeleted] = 0 ORDER BY [Id]", cn, tx);
            cmd.Parameters.Add(new SqlParameter("@id", id));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<LineDraft>();
            while (await r.ReadAsync(ct))
            {
                var v = ReadValues(r);
                list.Add(new LineDraft { Id = (long)v["Id"]!, Values = v });
            }
            draft.Lines[lf.Name] = list;
        }
        return draft;
    }

    private static Values ReadValues(SqlDataReader r)
    {
        var v = new Values();
        for (var i = 0; i < r.FieldCount; i++) v[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        return v;
    }

    /// <summary>Registro listo para el cliente: etiquetas de referencias, partidas y estado del proceso.</summary>
    private async Task<Dictionary<string, object?>?> LoadOutputAsync(SqlConnection cn, SqlTransaction? tx, EntityDef e,
        IReadOnlyDictionary<string, EntityDef> model, long id, UserContext user, CancellationToken ct)
    {
        var (select, from) = SelectClause(e, model);
        Dictionary<string, object?>? row;
        await using (var cmd = new SqlCommand($"SELECT {select} FROM {from} WHERE t.[Id] = @id AND t.[IsDeleted] = 0", cn, tx))
        {
            cmd.Parameters.Add(new SqlParameter("@id", id));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            row = await reader.ReadAsync(ct) ? Format(e, ReadRaw(reader)) : null;
        }
        if (row is null) return null;

        foreach (var lf in e.ActiveFields.Where(f => f.Type == FieldTypes.Lines))
        {
            if (!model.TryGetValue(lf.ChildEntity!, out var child) || child.ParentField is null) continue;
            var (cs, cf) = SelectClause(child, model);
            await using var cmd = new SqlCommand($"SELECT {cs} FROM {cf} WHERE t.{Quote(child.ParentField.Name)} = @id AND t.[IsDeleted] = 0 ORDER BY t.[Id]", cn, tx);
            cmd.Parameters.Add(new SqlParameter("@id", id));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var lines = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(ct)) lines.Add(Format(child, ReadRaw(reader)));
            row[lf.Name] = lines;
        }

        if (e.Workflow is { } wf)
        {
            var draft = (await LoadDraftAsync(cn, tx, e, model, id, ct))!;
            var loader = new RelatedLoader(cn, tx, model);
            var env = Env(user, draft);
            var stateName = draft.Header.GetValueOrDefault(Workflow.StateField) as string;
            var state = wf.FindState(stateName);
            row["_workflow"] = new
            {
                state = stateName,
                stateLabel = state?.Label ?? stateName,
                color = state?.Color ?? "neutral",
                editable = state?.Editable ?? true,
                transitions = wf.Transitions.Where(t => t.From.Contains(stateName ?? "", StringComparer.OrdinalIgnoreCase)).Select(t =>
                {
                    var (allowed, reason, _) = CanTransition(e, t, draft, model, loader, env, user);
                    return new
                    {
                        name = t.Name, label = t.Label, icon = t.Icon, variant = t.Variant ?? "secondary", to = t.To,
                        allowed, reason, requireComment = t.RequireComment, requiredFields = t.RequiredFields ?? [],
                    };
                }).ToList(),
            };
        }
        return row;
    }

    /// <summary>Borrador calculado en formato de salida (para /compute): agrega etiquetas de referencias.</summary>
    private static Task<Dictionary<string, object?>> FormatDraftAsync(SqlConnection cn, EntityDef e,
        IReadOnlyDictionary<string, EntityDef> model, DocDraft d, CancellationToken ct)
    {
        var loader = new RelatedLoader(cn, null, model);
        var row = FormatValues(e, model, d.Header, loader);
        foreach (var (field, lines) in d.Lines)
            if (e.FindField(field) is { ChildEntity: { } c } && model.TryGetValue(c, out var child))
                row[field] = lines.Select(l =>
                {
                    var lr = FormatValues(child, model, l.Values, loader);
                    if (l.Id is { } lid) lr["id"] = lid;
                    return lr;
                }).ToList();
        return Task.FromResult(row);
    }

    private static Dictionary<string, object?> FormatValues(EntityDef e, IReadOnlyDictionary<string, EntityDef> model, Values values, RelatedLoader loader)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var f in e.StoredFields.Where(f => f.Type != FieldTypes.Parent))
        {
            var v = values.GetValueOrDefault(f.Name);
            row[f.Name] = FormatField(f, v);
            if (v is null) continue;
            if (f.Type == FieldTypes.Reference && model.TryGetValue(f.ReferenceEntity!, out var target))
                row[f.Name + "__label"] = target.EffectiveDisplayField is { } d
                    ? Convert.ToString(loader.Row(target.Name, Convert.ToInt64(v))?.GetValueOrDefault(d), CultureInfo.InvariantCulture)
                    : $"#{v}";
            if (f.Type == FieldTypes.User)
                row[f.Name + "__label"] = loader.User(Convert.ToInt32(v))?.GetValueOrDefault("Nombre");
        }
        return row;
    }

    private static object? FormatField(FieldDef? f, object? value) =>
        (f is null ? null : f.Type == FieldTypes.Formula ? f.ResultType : f.Type, value) switch
        {
            (_, null) => null,
            (FieldTypes.Date, DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            (FieldTypes.DateTime, DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            _ => value,
        };

    private static (string Select, string From) SelectClause(EntityDef e, IReadOnlyDictionary<string, EntityDef> model)
    {
        var cols = new List<string> { "t.[Id]" };
        var from = new StringBuilder($"{AppTable(e.Name)} t");
        var j = 0;
        foreach (var f in e.StoredFields)
        {
            cols.Add($"t.{Quote(f.Name)}");
            var alias = $"r{j++}";
            if (f.Type == FieldTypes.Reference && model.TryGetValue(f.ReferenceEntity!, out var target))
            {
                var label = target.EffectiveDisplayField is { } d ? $"CAST({alias}.{Quote(d)} AS nvarchar(400))" : $"CAST({alias}.[Id] AS nvarchar(20))";
                cols.Add($"{label} AS [{Quote(f.Name)[1..^1]}__label]");
                from.Append($" LEFT JOIN {AppTable(target.Name)} {alias} ON {alias}.[Id] = t.{Quote(f.Name)}");
            }
            else if (f.Type == FieldTypes.User)
            {
                cols.Add($"{alias}.[DisplayName] AS [{Quote(f.Name)[1..^1]}__label]");
                from.Append($" LEFT JOIN meta.[User] {alias} ON {alias}.[Id] = t.{Quote(f.Name)}");
            }
        }
        cols.AddRange(["t.[CreatedAt]", "cu.[DisplayName] AS [CreatedByName]", "t.[UpdatedAt]", "uu.[DisplayName] AS [UpdatedByName]", "t.[RowVer]"]);
        from.Append(" LEFT JOIN meta.[User] cu ON cu.[Id] = t.[CreatedBy] LEFT JOIN meta.[User] uu ON uu.[Id] = t.[UpdatedBy]");
        return (string.Join(", ", cols), from.ToString());
    }

    private static string ResolveSort(EntityDef e, string? sort, string? dir)
    {
        var direction = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        if (string.IsNullOrEmpty(sort)) return "t.[Id] DESC";
        var column = sort.ToLowerInvariant() switch
        {
            "id" => "[Id]",
            "createdat" => "[CreatedAt]",
            "updatedat" => "[UpdatedAt]",
            _ => e.StoredFields.FirstOrDefault(f => f.Type != FieldTypes.LongText &&
                     string.Equals(f.Name, sort, StringComparison.OrdinalIgnoreCase)) is { } f ? Quote(f.Name) : null,
        };
        return column is null ? "t.[Id] DESC" : $"t.{column} {direction}, t.[Id] {direction}";
    }

    private static Values ReadRaw(SqlDataReader r) => ReadValues(r);

    private static Dictionary<string, object?> Format(EntityDef e, Values raw)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in raw)
        {
            switch (name)
            {
                case "Id": row["id"] = value; break;
                case "RowVer": row["_rowVersion"] = Convert.ToBase64String((byte[])value!); break;
                case "CreatedAt": row["createdAt"] = AsUtc(value); break;
                case "UpdatedAt": row["updatedAt"] = AsUtc(value); break;
                case "CreatedByName": row["createdBy"] = value; break;
                case "UpdatedByName": row["updatedBy"] = value; break;
                default:
                    var f = e.FindField(name);
                    if (f?.Type == FieldTypes.Parent) break;
                    row[name] = FormatField(f, value);
                    break;
            }
        }
        return row;
    }

    private static object? AsUtc(object? v) => v is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : v;

    /// <summary>Instantánea para la auditoría: datos del documento y sus partidas, sin metadatos técnicos.</summary>
    private static Dictionary<string, object?> Snapshot(DocDraft d)
    {
        var snap = d.Header.Where(kv => kv.Key is not ("Id" or "CreatedBy")).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (field, lines) in d.Lines)
            snap[field] = lines.Select(l => l.Values.Where(kv => kv.Key != "Id").ToDictionary(kv => kv.Key, kv => kv.Value)).ToList();
        return snap;
    }

    private static void Require(bool allowed)
    {
        if (!allowed) throw new Forbidden();
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    /// <summary>Traduce violaciones de índice único a un error de validación sobre el campo correspondiente.</summary>
    private static async Task<object?> ExecuteMappingUniqueAsync(EntityDef e, Func<Task<object?>> action)
    {
        try { return await action(); }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            var field = e.Fields.FirstOrDefault(f => (f.Unique || f.Type == FieldTypes.Autonumber) &&
                ex.Message.Contains($"UX_{e.Name}_{f.Name}", StringComparison.OrdinalIgnoreCase));
            throw new ValidationFailed(new Dictionary<string, string[]>
            {
                [field?.Name ?? "registro"] = [$"Ya existe un registro con ese valor{(field is null ? "" : $" de {field.Label}")}."],
            });
        }
    }
}
