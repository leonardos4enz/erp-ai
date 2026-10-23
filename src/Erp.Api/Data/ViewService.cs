using System.Text.Json;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;

namespace Erp.Api.Data;

public sealed record SavedView(int? Id, string Name, string Label, bool Personal, IReadOnlyList<string>? Columns,
    IReadOnlyList<ViewFilter>? Filters, string? Sort, string? Dir, string? State, bool Default);
public sealed record ViewList(IReadOnlyList<SavedView> Shared, IReadOnlyList<SavedView> Personal);
public sealed record SaveViewRequest(string? Label, IReadOnlyList<string>? Columns, IReadOnlyList<ViewFilter>? Filters,
    string? Sort, string? Dir, string? State);

/// <summary>
/// Spec 007: vistas del listado. Las compartidas son metadatos (plan de cambios); las personales, preferencias de cada
/// usuario. A nadie se le ofrece una vista que filtre u ordene por un campo que no ve (spec 006).
/// </summary>
public sealed class ViewService(Db db, MetadataStore store)
{
    public const int MaxPersonal = 50;

    private sealed record Definition(IReadOnlyList<string>? Columns, IReadOnlyList<ViewFilter>? Filters, string? Sort, string? Dir, string? State);
    private sealed record Row(int Id, string Label, string DefinitionJson);

    public async Task<ViewList> ListAsync(string entityName, UserContext user, CancellationToken ct)
    {
        var e = await ReadableAsync(entityName, user, ct);
        var shared = (e.Views ?? [])
            .Where(v => user.IsSuperuser || v.Roles is null || user.HasRole(v.Roles))
            .Where(v => UsesOnlyVisible(e, v.Filters, v.Sort, user))
            .Select(v => new SavedView(null, v.Name, v.Label, false, Visible(e, v.Columns, user), v.Filters, v.Sort, v.Dir, v.State, v.Default))
            .ToList();
        await using var cn = await db.OpenAsync(ct);
        var rows = await cn.QueryAsync<Row>(
            "SELECT Id, Label, DefinitionJson FROM meta.SavedView WHERE UserId = @uid AND EntityName = @e ORDER BY Label",
            new { uid = user.Id, e = e.Name });
        // Una vista personal guardada antes de que se ocultara un campo tampoco se ofrece.
        var personal = rows.Select(r => ToView(e, r, user)).Where(v => UsesOnlyVisible(e, v.Filters, v.Sort, user)).ToList();
        return new ViewList(shared, personal);
    }

    public async Task<SavedView> SaveAsync(string entityName, int? id, SaveViewRequest req, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var e = await ReadableAsync(entityName, user, ct);
        var label = req.Label?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (label.Length is 0 or > InterfaceRules.MaxLabel) errors["label"] = [$"Escribe un nombre de 1 a {InterfaceRules.MaxLabel} caracteres."];
        var problems = InterfaceRules.ValidateQuery(e, req.Columns, req.Filters, req.Sort, req.Dir, req.State).ToList();
        if (!UsesOnlyVisible(e, req.Filters, req.Sort, user) || req.Columns?.Any(c => e.FindField(c) is { } f && !FieldAccess.CanSee(e, f, user)) == true)
            problems.Add("la vista usa un campo que no puedes ver.");
        if (problems.Count > 0) errors["view"] = problems.Select(p => char.ToUpperInvariant(p[0]) + p[1..]).ToArray();
        if (errors.Count > 0) throw new ValidationFailed(errors);

        var def = JsonSerializer.Serialize(new Definition(req.Columns, req.Filters, req.Sort, req.Dir, req.State), MetadataStore.Json);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var p = new { uid = user.Id, e = e.Name, label, def, id };
        if (await cn.ExecuteScalarAsync<int?>(
                "SELECT 1 FROM meta.SavedView WITH (UPDLOCK, HOLDLOCK) WHERE UserId = @uid AND EntityName = @e AND Label = @label AND (@id IS NULL OR Id <> @id)", p, tx) is not null)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["label"] = ["Ya tienes una vista con ese nombre."] });
        Dictionary<string, object?>? before = null;
        if (id is null)
        {
            var count = await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.SavedView WHERE UserId = @uid AND EntityName = @e", p, tx);
            if (count >= MaxPersonal) throw new ValidationFailed(new Dictionary<string, string[]> { ["label"] = [$"Máximo {MaxPersonal} vistas personales por catálogo."] });
            id = await cn.ExecuteScalarAsync<int>(
                "INSERT meta.SavedView (UserId, EntityName, Label, DefinitionJson) OUTPUT inserted.Id VALUES (@uid, @e, @label, @def)", p, tx);
        }
        else
        {
            var prev = await cn.QuerySingleOrDefaultAsync<Row>(
                "SELECT Id, Label, DefinitionJson FROM meta.SavedView WHERE Id = @id AND UserId = @uid AND EntityName = @e", p, tx)
                ?? throw new NotFound("La vista no existe.");
            before = new() { ["label"] = prev.Label, ["definition"] = prev.DefinitionJson };
            await cn.ExecuteAsync("UPDATE meta.SavedView SET Label = @label, DefinitionJson = @def, UpdatedAt = SYSUTCDATETIME() WHERE Id = @id", p, tx);
        }
        await Audit.WriteAsync(cn, tx, http, AuditActions.ViewChange, e.Name, id.ToString(), before, new { label, definition = def });
        await tx.CommitAsync(ct);
        return ToView(e, new Row(id!.Value, label, def), user);
    }

    public async Task DeleteAsync(string entityName, int id, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        var e = await ReadableAsync(entityName, user, ct);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        // Solo el dueño: una vista ajena responde igual que una inexistente.
        var prev = await cn.QuerySingleOrDefaultAsync<Row>(
            "SELECT Id, Label, DefinitionJson FROM meta.SavedView WHERE Id = @id AND UserId = @uid AND EntityName = @e",
            new { id, uid = user.Id, e = e.Name }, tx) ?? throw new NotFound("La vista no existe.");
        await cn.ExecuteAsync("DELETE FROM meta.SavedView WHERE Id = @id", new { id }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.ViewChange, e.Name, id.ToString(), before: new { label = prev.Label, definition = prev.DefinitionJson });
        await tx.CommitAsync(ct);
    }

    private async Task<EntityDef> ReadableAsync(string entityName, UserContext user, CancellationToken ct)
    {
        var e = await store.GetActiveAsync(entityName, ct);
        if (e.IsChild) throw new AppException(400, "Las partidas se consultan desde su documento.");
        if (!user.Access(e.Name).Read) throw new Forbidden();
        return e;
    }

    private static SavedView ToView(EntityDef e, Row r, UserContext user)
    {
        var d = JsonSerializer.Deserialize<Definition>(r.DefinitionJson, MetadataStore.Json)!;
        return new SavedView(r.Id, $"p{r.Id}", r.Label, true, Visible(e, d.Columns, user), d.Filters, d.Sort, d.Dir, d.State, false);
    }

    private static IReadOnlyList<string>? Visible(EntityDef e, IReadOnlyList<string>? columns, UserContext user) =>
        columns?.Where(c => e.FindField(c) is { IsArchived: false } f && FieldAccess.CanSee(e, f, user)).ToList();

    private static bool UsesOnlyVisible(EntityDef e, IReadOnlyList<ViewFilter>? filters, string? sort, UserContext user) =>
        (filters ?? []).All(f => e.FindField(f.Field) is not { } fd || FieldAccess.CanSee(e, fd, user)) &&
        (sort is null || e.FindField(sort) is not { } sf || FieldAccess.CanSee(e, sf, user));
}
