using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;
using static Erp.Api.Metadata.Identifiers;

namespace Erp.Api.Metadata;

public sealed record ModuleRole(string Name, string? Description, bool CanManageModel, bool CanManageUsers, bool CanViewAudit,
    IReadOnlyList<ModulePermission> Permissions);
public sealed record ModulePermission(string Entity, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete);
public sealed record ModuleSeed(string Entity, string Key, IReadOnlyList<JsonObject> Records);
/// <summary>Spec 004: cambios de una versión posterior a la base, aplicables sobre una instalación existente.</summary>
public sealed record ModuleUpgrade(string Version, string Description, ChangePlan Plan);

public sealed record ModulePackage(string Id, string Name, string Version, string Icon, string Description,
    ChangePlan Plan, IReadOnlyList<ModuleRole> Roles, IReadOnlyList<ModuleSeed> Seed, IReadOnlyList<ModuleUpgrade>? Upgrades = null)
{
    /// <summary>Plan base seguido de todas las actualizaciones: lo que recibe una instalación nueva.</summary>
    public IReadOnlyList<(string Label, ChangePlan Plan)> FullChain =>
        [("base", Plan), .. (Upgrades ?? []).Select(u => ($"v{u.Version}", u.Plan))];

    public IReadOnlyList<ModuleUpgrade> PendingAfter(string installed) =>
        (Upgrades ?? []).Where(u => System.Version.Parse(u.Version) > System.Version.Parse(installed)).ToList();
}

public sealed record ModuleInfo(string Id, string Name, string Description, string Icon, string Version, bool Installed,
    IReadOnlyList<string> Entities, string? InstalledVersion, bool UpgradeAvailable, IReadOnlyList<string> PendingUpgrades);

/// <summary>
/// Módulos instalables (spec 002 H7). Un módulo es solo datos: plan de cambios + roles + datos iniciales.
/// Los paquetes se leen de la carpeta del servidor; nunca se reciben por la API.
/// </summary>
public sealed partial class ModuleService(Db db, MetadataStore store, PlanService plans, RecordService records,
    IConfiguration config, IWebHostEnvironment env, JsonSerializerOptions json)
{
    [GeneratedRegex("^[a-z][a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private string Folder => Path.GetFullPath(config["Modules:Path"] ?? Path.Combine(env.ContentRootPath, "..", "..", "modules"));

    public IReadOnlyList<ModulePackage> Packages()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.EnumerateFiles(Folder, "*.json")
            .Select(path => JsonSerializer.Deserialize<ModulePackage>(File.ReadAllText(path), json)!)
            .Where(p => IdPattern().IsMatch(p.Id))
            .OrderBy(p => p.Name)
            .ToList();
    }

    private ModulePackage Find(string id) =>
        Packages().FirstOrDefault(p => p.Id == id) ?? throw new NotFound("El módulo no existe.");

    public async Task<IReadOnlyList<ModuleInfo>> ListAsync()
    {
        await using var cn = await db.OpenAsync();
        var installed = (await cn.QueryAsync<(string Id, string Version)>("SELECT Id, Version FROM meta.Module")).ToDictionary(x => x.Id, x => x.Version);
        return Packages().Select(p =>
        {
            var current = installed.GetValueOrDefault(p.Id);
            var pending = current is null ? [] : p.PendingAfter(current);
            return new ModuleInfo(p.Id, p.Name, p.Description, p.Icon, p.Version, current is not null,
                p.Plan.Operations.OfType<CreateEntityOp>().Select(o => o.Label).ToList(),
                current, pending.Count > 0, pending.Select(u => $"{u.Version}: {u.Description}").ToList());
        }).ToList();
    }

    public async Task<object> PreviewAsync(string id, CancellationToken ct)
    {
        var p = Find(id);
        var preview = await plans.PreviewChainAsync(p.FullChain, p.Roles.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), ct);
        await using var cn = await db.OpenAsync(ct);
        var existing = (await PlanService.LoadRolesAsync(cn, null));
        return new
        {
            preview.Valid,
            preview.Errors,
            preview.Steps,
            roles = p.Roles.Select(r => r.Name).Where(n => !existing.Contains(n)).ToList(),
            seedRecords = p.Seed.Sum(s => s.Records.Count),
        };
    }

    public async Task<int> InstallAsync(string id, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        if (!user.IsSuperuser) throw new Forbidden("Solo un administrador puede instalar módulos.");
        var p = Find(id);

        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        if (await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM meta.Module WHERE Id = @id", new { id }, tx) is not null)
            throw new Conflict("El módulo ya está instalado.");

        // 1) Roles primero: el plan los usa en reglas y transiciones.
        var roleIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in p.Roles)
        {
            roleIds[r.Name] = await cn.ExecuteScalarAsync<int?>("SELECT Id FROM meta.Role WHERE Name = @Name", new { r.Name }, tx)
                ?? await cn.ExecuteScalarAsync<int>("""
                    INSERT meta.Role (Name, Description, CanManageModel, CanManageUsers, CanViewAudit)
                    OUTPUT inserted.Id VALUES (@Name, @Description, @CanManageModel, @CanManageUsers, @CanViewAudit)
                    """, new { r.Name, r.Description, r.CanManageModel, r.CanManageUsers, r.CanViewAudit }, tx);
        }

        // 2) Modelo: plan base y luego cada actualización, en orden (spec 004 · H2 CA1).
        var changeSetId = 0;
        foreach (var (_, plan) in p.FullChain)
            changeSetId = await plans.ApplyInTransactionAsync(cn, tx, plan, http);
        var model = await MetadataStore.LoadAsync(cn, tx);

        // 3) Permisos por catálogo.
        foreach (var r in p.Roles)
        foreach (var perm in r.Permissions)
        {
            if (!model.TryGetValue(perm.Entity, out var e)) throw new AppException(400, $"El módulo da permisos sobre '{perm.Entity}', que no existe.");
            await cn.ExecuteAsync("""
                MERGE meta.EntityPermission AS t
                USING (SELECT @RoleId AS RoleId, @Entity AS EntityName) AS s ON t.RoleId = s.RoleId AND t.EntityName = s.EntityName
                WHEN MATCHED THEN UPDATE SET CanRead = @CanRead, CanCreate = @CanCreate, CanUpdate = @CanUpdate, CanDelete = @CanDelete
                WHEN NOT MATCHED THEN INSERT (RoleId, EntityName, CanRead, CanCreate, CanUpdate, CanDelete)
                    VALUES (@RoleId, @Entity, @CanRead, @CanCreate, @CanUpdate, @CanDelete);
                """, new { RoleId = roleIds[r.Name], Entity = e.Name, perm.CanRead, perm.CanCreate, perm.CanUpdate, perm.CanDelete }, tx);
        }

        // 4) Datos iniciales, por el mismo motor que la captura (valores por defecto, fórmulas, reglas, folios).
        var seeded = 0;
        foreach (var s in p.Seed)
        {
            if (!model.TryGetValue(s.Entity, out var e)) throw new AppException(400, $"Datos iniciales para '{s.Entity}', que no existe.");
            var keyField = e.FindField(s.Key) ?? throw new AppException(400, $"'{s.Key}' no es un campo de {e.Name}.");
            foreach (var rec in s.Records)
            {
                var resolved = await ResolveRefsAsync(cn, tx, model, rec);
                var keyValue = resolved[keyField.Name]?.ToString();
                if (keyValue is not null && await ExistsAsync(cn, tx, e, keyField, keyValue)) continue;
                using var doc = JsonDocument.Parse(resolved.ToJsonString());
                await records.CreateInTransactionAsync(cn, tx, e, model, doc.RootElement, user, http, ct);
                seeded++;
            }
        }

        await cn.ExecuteAsync("INSERT meta.Module (Id, Version, InstalledBy) VALUES (@Id, @Version, @UserId)",
            new { p.Id, p.Version, UserId = user.Id }, tx);
        await Audit.WriteAsync(cn, tx, http, "module.install", "Modulo", p.Id,
            after: new { p.Id, p.Version, changeSetId, roles = p.Roles.Select(r => r.Name), seeded });
        await tx.CommitAsync(ct);
        store.Invalidate();
        return changeSetId;
    }

    public async Task<object> UpgradePreviewAsync(string id, CancellationToken ct)
    {
        var p = Find(id);
        var pending = p.PendingAfter(await InstalledVersionAsync(id) ?? throw new Conflict("El módulo no está instalado."));
        if (pending.Count == 0) throw new Conflict("El módulo ya está en la versión más reciente.");
        var preview = await plans.PreviewChainAsync(pending.Select(u => ($"v{u.Version}", u.Plan)).ToList(), new HashSet<string>(), ct);
        return new { preview.Valid, preview.Errors, preview.Steps, roles = Array.Empty<string>(), seedRecords = 0,
            versions = pending.Select(u => new { u.Version, u.Description }) };
    }

    /// <summary>Aplica en una transacción solo las versiones posteriores a la instalada; los datos se conservan.</summary>
    public async Task<int> UpgradeAsync(string id, HttpContext http, CancellationToken ct)
    {
        if (!http.CurrentUser().IsSuperuser) throw new Forbidden("Solo un administrador puede actualizar módulos.");
        var p = Find(id);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var installed = await cn.ExecuteScalarAsync<string?>("SELECT Version FROM meta.Module WITH (UPDLOCK) WHERE Id = @id", new { id }, tx)
            ?? throw new Conflict("El módulo no está instalado.");
        var pending = p.PendingAfter(installed);
        if (pending.Count == 0) throw new Conflict("El módulo ya está en la versión más reciente.");

        var changeSetId = 0;
        foreach (var u in pending)
            changeSetId = await plans.ApplyInTransactionAsync(cn, tx, u.Plan with { Description = $"{p.Name} {u.Version}: {u.Description}" }, http);
        await cn.ExecuteAsync("UPDATE meta.Module SET Version = @Version WHERE Id = @id", new { id, p.Version }, tx);
        await Audit.WriteAsync(cn, tx, http, "module.upgrade", "Modulo", p.Id,
            before: new { version = installed }, after: new { version = p.Version, changeSetId, applied = pending.Select(u => u.Version) });
        await tx.CommitAsync(ct);
        store.Invalidate();
        return changeSetId;
    }

    private async Task<string?> InstalledVersionAsync(string id)
    {
        await using var cn = await db.OpenAsync();
        return await cn.ExecuteScalarAsync<string?>("SELECT Version FROM meta.Module WHERE Id = @id", new { id });
    }

    /// <summary>Reemplaza <c>{ "$ref": "Entidad", "Campo": valor }</c> por el id del registro correspondiente.</summary>
    private static async Task<JsonObject> ResolveRefsAsync(SqlConnection cn, SqlTransaction tx, IReadOnlyDictionary<string, EntityDef> model, JsonObject rec)
    {
        var result = new JsonObject();
        foreach (var (name, node) in rec)
        {
            if (node is JsonObject obj && obj["$ref"]?.GetValue<string>() is { } refEntity)
            {
                if (!model.TryGetValue(refEntity, out var target)) throw new AppException(400, $"Referencia a '{refEntity}', que no existe.");
                var (keyName, keyNode) = obj.First(kv => kv.Key != "$ref");
                var keyField = target.FindField(keyName) ?? throw new AppException(400, $"'{keyName}' no es un campo de {target.Name}.");
                await using var cmd = new SqlCommand(
                    $"SELECT TOP 1 [Id] FROM {AppTable(target.Name)} WHERE {Quote(keyField.Name)} = @v AND [IsDeleted] = 0", cn, tx);
                cmd.Parameters.Add(new SqlParameter("@v", keyNode!.ToString()));
                var refId = await cmd.ExecuteScalarAsync() ?? throw new AppException(400, $"No se encontró {target.Name} con {keyName} = {keyNode}.");
                result[name] = (long)refId;
            }
            else result[name] = node?.DeepClone();
        }
        return result;
    }

    private static async Task<bool> ExistsAsync(SqlConnection cn, SqlTransaction tx, EntityDef e, FieldDef key, string value)
    {
        await using var cmd = new SqlCommand($"SELECT TOP 1 1 FROM {AppTable(e.Name)} WHERE {Quote(key.Name)} = @v AND [IsDeleted] = 0", cn, tx);
        cmd.Parameters.Add(new SqlParameter("@v", value));
        return await cmd.ExecuteScalarAsync() is not null;
    }
}
