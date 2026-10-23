using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Environments;

public sealed record ExportRequest(string? Id, string? Name, string? Version, string? Description, string? Icon,
    string[]? Entities, string[]? Dashboards);
public sealed record ApproveRequest(string? DiffHash, string? Comment);
public sealed record RejectRequest(string? Comment);
public sealed record ApplyRequest(string? DiffHash);

/// <summary><c>Label</c>: nombre del catálogo para quien revisa (regresión F-03 de la verificación 009).</summary>
public sealed record PackagePermission(string Entity, string Label, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete);
public sealed record PackageRoleDto(string Name, bool IsNew, bool CanViewAudit, IReadOnlyList<PackagePermission> Permissions);
public sealed record PackageDiffDto(bool Valid, IReadOnlyList<PlanError> Errors, IReadOnlyList<PlanStep> Steps, IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Warnings, IReadOnlyList<PackageRoleDto> Roles, string DiffHash, bool Empty);
public sealed record ExportPreviewDto(bool Valid, IReadOnlyList<PlanError> Errors, IReadOnlyList<PlanStep> Steps, int Operations,
    IReadOnlyList<string> Entities, IReadOnlyList<PackageRoleDto> Roles, IReadOnlyList<string> Dependencies, IReadOnlyList<string> Warnings,
    string Hash, bool Signed);
public sealed record PackageImportDto(int Id, string PackageId, string Name, string Version, string? PreviousVersion, string Status,
    string Hash, bool Signed, string? SourceEnvironment, DateTime? ExportedAt, string UploadedBy, DateTime UploadedAt,
    string? ReviewedBy, DateTime? ReviewedAt, string? ReviewComment, string? AppliedBy, DateTime? AppliedAt, int? ChangeSetId,
    string? Description = null, string? InstalledVersion = null, PackageDiffDto? Diff = null,
    bool CanApprove = false, bool CanApply = false, string? BlockedReason = null);

/// <summary>
/// Spec 009: exportar lo hecho en el modelador como paquete y aplicarlo en otro entorno por diff, con aprobación.
/// Todo pasa por el Plan de Cambios (constitución I.3). Solo superusuario: el paquete crea roles y permisos.
/// </summary>
public sealed class EnvironmentService(Db db, MetadataStore store, PlanService plans, ModuleService modules, EnvironmentOptions env)
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Applied = "applied";
    public const int MaxComment = 500;

    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    private static void RequireSuperuser(UserContext user)
    {
        if (!user.IsSuperuser) throw new Forbidden("Solo un administrador puede administrar entornos.");
    }

    private IReadOnlySet<string> ServerModules() => modules.Packages().Select(p => p.Id).ToHashSet(Ci);

    // ---------------- Entorno ----------------

    public async Task<object> InfoAsync(UserContext user, CancellationToken ct)
    {
        RequireSuperuser(user);
        await using var cn = await db.OpenAsync(ct);
        var packages = await cn.QueryAsync<(string Id, string Version, DateTime InstalledAt)>("""
            SELECT m.Id, m.Version, m.InstalledAt FROM meta.Module m
             WHERE EXISTS (SELECT 1 FROM meta.PackageImport i WHERE i.PackageId = m.Id AND i.Status = 'applied') ORDER BY m.Id
            """);
        return new
        {
            name = env.Name, requireApproval = env.RequireApproval, signing = env.Signing,
            packages = packages.Select(p => new { id = p.Id, version = p.Version, installedAt = Utc(p.InstalledAt) }),
        };
    }

    // ---------------- Exportar ----------------

    public async Task<object> ExportableAsync(UserContext user, CancellationToken ct)
    {
        RequireSuperuser(user);
        var model = await store.GetAsync(ct);
        var boards = await store.GetDashboardsAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var exports = await cn.QueryAsync<(string PackageId, string Name, string Version, string ContentHash, DateTime ExportedAt, string Username)>("""
            SELECT TOP 20 e.PackageId, e.Name, e.Version, e.ContentHash, e.ExportedAt, u.Username
              FROM meta.PackageExport e JOIN meta.[User] u ON u.Id = e.ExportedBy ORDER BY e.Id DESC
            """);
        return new
        {
            entities = model.Values.Where(e => !e.IsArchived && !e.IsChild).OrderBy(e => e.Group ?? "").ThenBy(e => e.LabelPlural)
                // Regresión (verificación 009, F-02): las partidas se muestran por su etiqueta, no por su nombre técnico.
                .Select(e => new { e.Name, e.Label, e.LabelPlural, e.Group,
                    children = Children(model, e.Name).Where(c => !Ci.Equals(c, e.Name)).Select(c => model[c].LabelPlural).ToList() }),
            dashboards = boards.Values.Where(d => !d.IsArchived).OrderBy(d => d.Label).Select(d => new { d.Name, d.Label }),
            exports = exports.Select(x => new { packageId = x.PackageId, name = x.Name, version = x.Version, hash = x.ContentHash,
                exportedAt = Utc(x.ExportedAt), exportedBy = x.Username }),
        };
    }

    /// <summary>El catálogo y sus partidas (y las partidas de éstas).</summary>
    private static List<string> Children(IReadOnlyDictionary<string, EntityDef> model, string root)
    {
        var result = new List<string> { root };
        for (var i = 0; i < result.Count; i++)
            result.AddRange(model.Values.Where(e => !e.IsArchived && Ci.Equals(e.ParentEntity, result[i]) && !result.Contains(e.Name, Ci))
                .OrderBy(e => e.Name).Select(e => e.Name));
        return result;
    }

    private sealed record Built(ModulePackage Package, byte[] Canonical, string Hash, ExportPreviewDto Preview);

    private async Task<Built> BuildAsync(ExportRequest req, CancellationToken ct)
    {
        var model = await store.GetAsync(ct);
        var boards = await store.GetDashboardsAsync(ct);
        var errors = new Dictionary<string, string[]>();
        var header = PackageRules.ValidateHeader(req.Id?.Trim(), req.Name, req.Version?.Trim(), req.Icon, req.Description, ServerModules()).ToList();
        if (header.Count > 0) errors["package"] = [.. header];

        var entities = new HashSet<string>(Ci);
        var picked = new List<string>();
        foreach (var name in req.Entities ?? [])
        {
            if (!model.TryGetValue(name, out var e) || e.IsArchived || e.IsChild) { errors["entities"] = [$"'{name}' no es un catálogo activo."]; continue; }
            picked.Add(e.Name);
            entities.UnionWith(Children(model, e.Name));
        }
        var dashboards = new HashSet<string>(Ci);
        foreach (var name in req.Dashboards ?? [])
        {
            if (!boards.TryGetValue(name, out var d) || d.IsArchived) { errors["dashboards"] = [$"'{name}' no es un tablero activo."]; continue; }
            dashboards.Add(d.Name);
        }
        if (entities.Count == 0 && dashboards.Count == 0) errors["entities"] = ["Elige al menos un catálogo o tablero."];
        if (errors.Count > 0) throw new ValidationFailed(errors, "El paquete no se puede exportar.");

        var name0 = req.Name!.Trim();
        var version = req.Version!.Trim();
        var diff = ModelDiff.Export(model, boards, entities, dashboards, $"{name0} {version}");

        await using var cn = await db.OpenAsync(ct);
        var roles = await LoadRolesAsync(cn, null);
        var warnings = new List<string>(diff.Warnings);
        var packageRoles = ExportRoles(roles, model, boards, entities, dashboards, warnings);
        warnings.AddRange(ViewWarnings(model, entities));

        var package = new ModulePackage(req.Id!.Trim(), name0, version, req.Icon ?? "package", req.Description?.Trim() ?? "",
            diff.Plan, packageRoles, [], null, null, new PackageSource(env.Name, Truncate(DateTime.UtcNow)), null);
        var canonical = PackageSigner.Canonical(package);
        if (env.SigningKey is { } key) package = package with { Signature = PackageSigner.Sign(canonical, key) };

        // Vista previa: lo mismo que haría un entorno con el resto del modelo igual a éste.
        // Todos los roles de este entorno: el compilador también revisa los catálogos que no viajan (y sus roles existen en el
        // destino). Los roles que cita el paquete viajan con él (ExportRoles), así que no hace falta restringir aquí.
        var known = roles.Keys.ToHashSet(Ci);
        var (_, _, compileErrors, steps) = ModelDiff.Desired(package, model, boards, known);
        var dependencies = Dependencies(model, boards, entities, dashboards);
        var preview = new ExportPreviewDto(compileErrors.Count == 0, compileErrors, steps, diff.Plan.Operations.Count,
            entities.Order(Ci).ToList(), packageRoles.Select(r => new PackageRoleDto(r.Name, true, r.CanViewAudit,
                r.Permissions.Select(p => new PackagePermission(p.Entity, model[p.Entity].LabelPlural, p.CanRead, p.CanCreate, p.CanUpdate, p.CanDelete)).ToList())).ToList(),
            dependencies, warnings, PackageSigner.Hash(canonical), package.Signature is not null);
        return new Built(package, canonical, preview.Hash, preview);
    }

    public async Task<ExportPreviewDto> ExportPreviewAsync(ExportRequest req, UserContext user, CancellationToken ct)
    {
        RequireSuperuser(user);
        return (await BuildAsync(req, ct)).Preview;
    }

    public async Task<(byte[] Content, string FileName)> ExportAsync(ExportRequest req, HttpContext http, CancellationToken ct)
    {
        RequireSuperuser(http.CurrentUser());
        var built = await BuildAsync(req, ct);
        if (!built.Preview.Valid)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["package"] = built.Preview.Errors.Select(e => e.Message).Take(20).ToArray() },
                "El paquete no es válido: revisa la vista previa.");
        var p = built.Package;
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await cn.ExecuteAsync("""
            INSERT meta.PackageExport (PackageId, Name, Version, ContentHash, Signed, EntitiesJson, ExportedBy)
            VALUES (@Id, @Name, @Version, @Hash, @Signed, @Entities, @UserId)
            """, new { p.Id, p.Name, p.Version, built.Hash, Signed = p.Signature is not null,
            Entities = JsonSerializer.Serialize(built.Preview.Entities), UserId = http.CurrentUser().Id }, tx);
        // Constitución II.4: la exportación queda en la bitácora (qué se llevó y con qué huella).
        await Audit.WriteAsync(cn, tx, http, AuditActions.EnvironmentExport, "Paquete", p.Id, after: new
        {
            p.Id, p.Version, hash = built.Hash, signed = p.Signature is not null, entities = built.Preview.Entities,
            dashboards = ModelDiff.Scope(p).Dashboards, roles = p.Roles.Select(r => r.Name),
        });
        await tx.CommitAsync(ct);
        return (JsonSerializer.SerializeToUtf8Bytes(p, PackageJson.File), $"{p.Id}-{p.Version}.json");
    }

    /// <summary>
    /// Roles que viajan: no del sistema, con permisos sobre los catálogos del paquete o citados en su metadato. Solo sus
    /// permisos sobre esos catálogos y «ver auditoría»; administrar modelo o usuarios no viaja (spec 009 · H5).
    /// </summary>
    private static List<ModuleRole> ExportRoles(IReadOnlyDictionary<string, RoleRow> roles, IReadOnlyDictionary<string, EntityDef> model,
        IReadOnlyDictionary<string, DashboardDef> boards, IReadOnlySet<string> entities, IReadOnlySet<string> dashboards, List<string> warnings)
    {
        var relaxed = new JsonSerializerOptions(MetadataStore.Json) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var text = JsonSerializer.Serialize(new
        {
            entities = model.Values.Where(e => entities.Contains(e.Name)),
            boards = boards.Values.Where(d => dashboards.Contains(d.Name)),
        }, relaxed);
        bool Cited(string name) =>
            text.Contains($"\"{JsonEncodedText.Encode(name, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)}\"", StringComparison.OrdinalIgnoreCase) ||
            text.Contains($"'{name}'", StringComparison.OrdinalIgnoreCase);

        var result = new List<ModuleRole>();
        foreach (var r in roles.Values.Where(r => !r.IsSystem && !r.IsSuperuser).OrderBy(r => r.Name, Ci))
        {
            var perms = r.Permissions.Where(p => entities.Contains(p.Entity) && (p.CanRead || p.CanCreate || p.CanUpdate || p.CanDelete))
                .OrderBy(p => p.Entity, Ci).Select(p => new ModulePermission(model[p.Entity].Name, p.CanRead, p.CanCreate, p.CanUpdate, p.CanDelete)).ToList();
            if (perms.Count == 0 && !Cited(r.Name)) continue;
            if (r.CanManageModel || r.CanManageUsers)
                warnings.Add($"El rol «{r.Name}» administra el modelo o los usuarios: esa capacidad no viaja; asígnala en el destino si hace falta.");
            result.Add(new ModuleRole(r.Name, r.Description, false, false, r.CanViewAudit, perms));
        }
        return result;
    }

    /// <summary>Una vista compartida que filtra por un registro concreto lleva su id y su etiqueta: en otro entorno puede ser otro registro.</summary>
    private static IEnumerable<string> ViewWarnings(IReadOnlyDictionary<string, EntityDef> model, IReadOnlySet<string> entities)
    {
        foreach (var e in model.Values.Where(e => entities.Contains(e.Name)))
            foreach (var v in e.Views ?? [])
                foreach (var f in v.Filters ?? [])
                    if (e.FindField(f.Field) is { Type: FieldTypes.Reference or FieldTypes.User } && f.Value is { ValueKind: not JsonValueKind.Null } val &&
                        !(val.ValueKind == JsonValueKind.String && val.GetString()!.StartsWith('$')))
                        yield return $"La vista «{v.Label}» de «{e.LabelPlural}» filtra por un registro concreto ({f.Label ?? val.ToString()}): en otro entorno puede ser otro registro.";
    }

    /// <summary>Catálogos de los que depende el paquete y que no van en él: el destino debe tenerlos.</summary>
    private static List<string> Dependencies(IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, DashboardDef> boards,
        IReadOnlySet<string> entities, IReadOnlySet<string> dashboards)
    {
        var deps = new HashSet<string>(Ci);
        foreach (var e in model.Values.Where(e => entities.Contains(e.Name)))
        {
            deps.UnionWith(e.ActiveFields.Select(f => f.ReferenceEntity).OfType<string>());
            deps.UnionWith((e.Automations ?? []).SelectMany(a => a.Actions).Select(a => a.Entity).OfType<string>());
        }
        foreach (var d in boards.Values.Where(d => dashboards.Contains(d.Name))) deps.UnionWith(d.Widgets.Select(w => w.Entity));
        return deps.Where(d => !entities.Contains(d) && model.ContainsKey(d)).Select(d => model[d].LabelPlural).Order().ToList();
    }

    // ---------------- Recibir ----------------

    public async Task<IReadOnlyList<PackageImportDto>> ListAsync(UserContext user, CancellationToken ct)
    {
        RequireSuperuser(user);
        await using var cn = await db.OpenAsync(ct);
        return (await cn.QueryAsync<ImportRow>($"{SelectImport} ORDER BY i.Id DESC OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY")).Select(ToDto).ToList();
    }

    public async Task<PackageImportDto> UploadAsync(byte[] content, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        RequireSuperuser(user);
        ModulePackage? p;
        try { p = content.Length == 0 ? null : JsonSerializer.Deserialize<ModulePackage>(content, PackageJson.Read); }
        catch (JsonException) { p = null; }
        await using var cn = await db.OpenAsync(ct);
        if (p is null) await RejectUploadAsync(cn, http, null, "El archivo no es un paquete JSON válido.");

        var canonical = PackageSigner.Canonical(p!);
        var hash = PackageSigner.Hash(canonical);
        // Spec 009 · H5 CA3: con clave, un paquete sin firma o alterado no se guarda (y el intento queda registrado).
        if (env.SigningKey is { } key && !PackageSigner.Verify(canonical, p!.Signature, key))
            await RejectUploadAsync(cn, http, p, p.Signature is null
                ? "Este entorno exige paquetes firmados y el archivo no trae firma."
                : "La firma del paquete no es válida: el archivo fue modificado o viene de un entorno que no comparte la clave.", hash);

        var roles = await LoadRolesAsync(cn, null);
        var problems = PackageRules.Validate(p!, ServerModules(), roles.Values.Where(r => r.IsSystem || r.IsSuperuser).Select(r => r.Name).ToHashSet(Ci)).ToList();
        var installed = await cn.ExecuteScalarAsync<string?>("SELECT Version FROM meta.Module WHERE Id = @Id", new { p!.Id });
        if (problems.Count == 0 && !PackageRules.IsNewer(p.Version, installed))
            problems.Add($"La versión {p.Version} no es mayor que la aplicada ({installed}): un paquete no se aplica dos veces ni hacia atrás.");
        if (problems.Count > 0) await RejectUploadAsync(cn, http, p, string.Join(" ", problems.Take(3)), hash, problems);

        await using var tx = await cn.BeginTransactionAsync(ct);
        if (await cn.ExecuteScalarAsync<int?>(
                "SELECT TOP 1 Id FROM meta.PackageImport WITH (UPDLOCK, HOLDLOCK) WHERE ContentHash = @hash AND Status IN ('pending', 'approved')",
                new { hash }, tx) is { } dup)
            throw new Conflict($"Ese mismo paquete ya está en revisión (#{dup}).");
        var id = await cn.ExecuteScalarAsync<int>("""
            INSERT meta.PackageImport (PackageId, Name, Version, Description, SourceEnvironment, ExportedAt, ContentJson, ContentHash, Signed, Status, UploadedBy)
            OUTPUT inserted.Id VALUES (@Id, @Name, @Version, @Description, @Env, @ExportedAt, @Json, @hash, @Signed, 'pending', @UserId)
            """, new
        {
            p.Id, Name = p.Name.Trim(), p.Version, Description = Trim(p.Description, 500), Env = p.Source?.Environment, ExportedAt = p.Source?.ExportedAt,
            Json = JsonSerializer.Serialize(p, PackageJson.Canonical), hash, Signed = p.Signature is not null, UserId = user.Id,
        }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.EnvironmentUpload, "Paquete", p.Id,
            after: new { import = id, p.Id, p.Version, hash, signed = p.Signature is not null, source = p.Source?.Environment });
        await tx.CommitAsync(ct);
        return await DetailAsync(id, user, ct);
    }

    /// <summary>Un paquete rechazado al subir no se guarda, pero el intento sí queda en la auditoría (H5 CA3).</summary>
    private static async Task RejectUploadAsync(DbConnection cn, HttpContext http, ModulePackage? p, string reason, string? hash = null,
        IReadOnlyList<string>? all = null)
    {
        await Audit.WriteAsync(cn, null, http, AuditActions.EnvironmentRejected, "Paquete", p?.Id is { Length: <= 50 } pid ? pid : null,
            after: new { p?.Version, hash, reason });
        throw new ValidationFailed(new Dictionary<string, string[]> { ["package"] = [.. all ?? [reason]] }, reason);
    }

    public async Task<PackageImportDto> DetailAsync(int id, UserContext user, CancellationToken ct)
    {
        RequireSuperuser(user);
        await using var cn = await db.OpenAsync(ct);
        var row = await cn.QuerySingleOrDefaultAsync<ImportRow>($"{SelectImport} WHERE i.Id = @id", new { id }) ?? throw new NotFound("El paquete no existe.");
        var dto = ToDto(row);
        if (row.Status is not (Pending or Approved)) return dto;

        var p = Parse(row.ContentJson);
        var installed = await cn.ExecuteScalarAsync<string?>("SELECT Version FROM meta.Module WHERE Id = @PackageId", new { row.PackageId });
        var model = await MetadataStore.LoadAsync(cn, null);
        var boards = await MetadataStore.LoadDashboardsAsync(cn, null);
        var diff = ComputeDiff(p, model, boards, await LoadRolesAsync(cn, null));
        var blocked = Blocked(row, diff, installed, user, out var canApprove);
        return dto with
        {
            Description = row.Description, InstalledVersion = installed, Diff = diff,
            CanApprove = canApprove, CanApply = blocked is null, BlockedReason = blocked,
        };
    }

    /// <summary>Por qué no se puede aplicar todavía (nulo = se puede). También dice si este usuario puede aprobar.</summary>
    private string? Blocked(ImportRow row, PackageDiffDto diff, string? installed, UserContext user, out bool canApprove)
    {
        canApprove = false;
        if (!PackageRules.IsNewer(row.Version, installed))
            return $"Este entorno ya tiene la versión {installed} de «{row.Name}»: ya no se puede aplicar la {row.Version}.";
        if (!diff.Valid) return "El paquete tiene errores o conflictos con este entorno.";
        if (!env.RequireApproval) return null;
        if (row.Status == Pending)
        {
            canApprove = user.Id != row.UploadedById;
            return canApprove
                ? "Este entorno requiere aprobación antes de aplicar."
                : "Este entorno requiere que lo apruebe un administrador distinto de quien lo subió.";
        }
        return row.ApprovedDiffHash == diff.DiffHash ? null
            : "El entorno cambió desde la aprobación: el diff ya no es el que se aprobó. Recházalo y súbelo de nuevo.";
    }

    public async Task<PackageImportDto> ApproveAsync(int id, ApproveRequest req, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        RequireSuperuser(user);
        if (!env.RequireApproval) throw new Conflict("Este entorno no requiere aprobación: el paquete se aplica directamente.");
        var comment = Comment(req.Comment, required: false);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var row = await LockAsync(cn, tx, id);
        if (row.Status != Pending) throw new Conflict("Solo se aprueba un paquete pendiente.");
        // Cuatro ojos (H4 CA1): quien sube no aprueba.
        if (row.UploadedById == user.Id) throw new Forbidden("Lo debe aprobar un administrador distinto de quien lo subió.");
        var diff = ComputeDiff(Parse(row.ContentJson), await MetadataStore.LoadAsync(cn, tx), await MetadataStore.LoadDashboardsAsync(cn, tx), await LoadRolesAsync(cn, tx));
        if (!diff.Valid) throw new Conflict("El paquete tiene errores o conflictos con este entorno: no se puede aprobar.");
        if (!string.Equals(req.DiffHash, diff.DiffHash, StringComparison.Ordinal))
            throw new Conflict("El entorno cambió mientras revisabas: vuelve a abrir el paquete y revisa el diff.");
        await cn.ExecuteAsync("""
            UPDATE meta.PackageImport SET Status = 'approved', ReviewedBy = @UserId, ReviewedAt = SYSUTCDATETIME(),
                ReviewComment = @comment, ApprovedDiffHash = @DiffHash WHERE Id = @id
            """, new { id, UserId = user.Id, comment, diff.DiffHash }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.EnvironmentApprove, "Paquete", row.PackageId,
            after: new { import = id, row.Version, hash = row.ContentHash, diffHash = diff.DiffHash, comment });
        await tx.CommitAsync(ct);
        return await DetailAsync(id, user, ct);
    }

    public async Task<PackageImportDto> RejectAsync(int id, RejectRequest req, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        RequireSuperuser(user);
        var comment = Comment(req.Comment, required: true);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var row = await LockAsync(cn, tx, id);
        if (row.Status is not (Pending or Approved)) throw new Conflict("El paquete ya no está en revisión.");
        await cn.ExecuteAsync("""
            UPDATE meta.PackageImport SET Status = 'rejected', ReviewedBy = @UserId, ReviewedAt = SYSUTCDATETIME(), ReviewComment = @comment
             WHERE Id = @id
            """, new { id, UserId = user.Id, comment }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.EnvironmentReject, "Paquete", row.PackageId,
            after: new { import = id, row.Version, hash = row.ContentHash, previousStatus = row.Status, comment });
        await tx.CommitAsync(ct);
        return await DetailAsync(id, user, ct);
    }

    public async Task<(int? ChangeSetId, PackageImportDto Import)> ApplyAsync(int id, ApplyRequest req, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        RequireSuperuser(user);
        int? changeSetId = null;
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct))
        {
            // El mismo candado que PlanService: nadie cambia el modelo entre el diff y su aplicación.
            var lockResult = await cn.ExecuteScalarAsync<int>("""
                DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'erp-model', @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;
                """, transaction: tx);
            if (lockResult < 0) throw new Conflict("Otro cambio de modelo está en curso. Intenta de nuevo.");

            var row = await LockAsync(cn, tx, id);
            if (row.Status is not (Pending or Approved)) throw new Conflict("El paquete ya no está en revisión.");
            if (env.RequireApproval && row.Status != Approved) throw new Conflict("Este entorno requiere que otro administrador apruebe el paquete antes de aplicarlo.");

            // Lo guardado debe ser exactamente lo que se subió (y, con clave, lo que firmó el origen).
            var p = Parse(row.ContentJson);
            var canonical = PackageSigner.Canonical(p);
            if (PackageSigner.Hash(canonical) != row.ContentHash || (env.SigningKey is { } key && !PackageSigner.Verify(canonical, p.Signature, key)))
                throw new Conflict("El contenido guardado del paquete no coincide con su huella o su firma: no se aplica.");

            var installed = await cn.ExecuteScalarAsync<string?>("SELECT Version FROM meta.Module WITH (UPDLOCK, HOLDLOCK) WHERE Id = @PackageId", new { row.PackageId }, tx);
            if (!PackageRules.IsNewer(row.Version, installed)) throw new Conflict($"Este entorno ya tiene la versión {installed} de «{row.Name}».");

            var model = await MetadataStore.LoadAsync(cn, tx);
            var roles = await LoadRolesAsync(cn, tx);
            var diff = ComputeDiff(p, model, await MetadataStore.LoadDashboardsAsync(cn, tx), roles, out var plan);
            if (!diff.Valid) throw new Conflict("El paquete tiene errores o conflictos con este entorno.");
            if (!string.Equals(req.DiffHash, diff.DiffHash, StringComparison.Ordinal))
                throw new Conflict("El entorno cambió desde que revisaste el paquete: vuelve a abrirlo y revisa el diff.");
            if (env.RequireApproval && row.ApprovedDiffHash != diff.DiffHash)
                throw new Conflict("El entorno cambió desde la aprobación: el diff ya no es el que se aprobó.");

            // Roles nuevos antes del plan (los usan sus transiciones, seguridad y webhooks); permisos después.
            var grants = diff.Roles.Select(r => new ModuleRole(r.Name, p.AllRoles.First(x => Ci.Equals(x.Name, r.Name)).Description,
                false, false, r.IsNew && r.CanViewAudit,
                r.Permissions.Select(x => new ModulePermission(x.Entity, x.CanRead, x.CanCreate, x.CanUpdate, x.CanDelete)).ToList())).ToList();
            var roleIds = await ModuleService.EnsureRolesAsync(cn, tx, grants.Where(r => diff.Roles.First(x => x.Name == r.Name).IsNew));
            foreach (var r in grants.Where(r => !roleIds.ContainsKey(r.Name))) roleIds[r.Name] = roles[r.Name].Id;
            if (plan.Operations.Count > 0) changeSetId = await plans.ApplyInTransactionAsync(cn, tx, plan, http);
            await ModuleService.GrantAsync(cn, tx, grants, roleIds, await MetadataStore.LoadAsync(cn, tx));

            await cn.ExecuteAsync("""
                MERGE meta.Module AS t USING (SELECT @PackageId AS Id) AS s ON t.Id = s.Id
                WHEN MATCHED THEN UPDATE SET Version = @Version
                WHEN NOT MATCHED THEN INSERT (Id, Version, InstalledBy) VALUES (@PackageId, @Version, @UserId);
                UPDATE meta.PackageImport SET Status = 'applied', AppliedBy = @UserId, AppliedAt = SYSUTCDATETIME(),
                    ChangeSetId = @changeSetId, PreviousVersion = @installed WHERE Id = @id;
                """, new { row.PackageId, row.Version, UserId = user.Id, changeSetId, installed, id }, tx);
            await Audit.WriteAsync(cn, tx, http, AuditActions.EnvironmentApply, "Paquete", row.PackageId,
                before: new { version = installed },
                after: new { import = id, row.Version, hash = row.ContentHash, diffHash = diff.DiffHash, changeSetId, approvedBy = row.ReviewedBy,
                    rolesCreated = diff.Roles.Where(r => r.IsNew).Select(r => r.Name) });
            await tx.CommitAsync(ct);
        }
        store.Invalidate();
        return (changeSetId, await DetailAsync(id, user, ct));
    }

    // ---------------- Diff ----------------

    private PackageDiffDto ComputeDiff(ModulePackage p, IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, DashboardDef> boards,
        IReadOnlyDictionary<string, RoleRow> roles) => ComputeDiff(p, model, boards, roles, out _);

    /// <summary>
    /// Estado deseado → plan mínimo → compilado contra este entorno. La huella cubre el plan y los roles y permisos que se
    /// otorgarán: aprobar o revisar un diff es aprobar exactamente eso.
    /// </summary>
    private static PackageDiffDto ComputeDiff(ModulePackage p, IReadOnlyDictionary<string, EntityDef> model, IReadOnlyDictionary<string, DashboardDef> boards,
        IReadOnlyDictionary<string, RoleRow> roles, out ChangePlan plan)
    {
        var known = roles.Keys.Union(p.AllRoles.Select(r => r.Name)).ToHashSet(Ci);
        var (desired, desiredBoards, errors, _) = ModelDiff.Desired(p, model, boards, known);
        var diff = ModelDiff.Compute(desired, desiredBoards, model, boards, $"{p.Name} {p.Version} (paquete {p.Id})");
        plan = diff.Plan;
        var allErrors = new List<PlanError>(errors);
        var steps = new List<PlanStep>();
        if (errors.Count == 0 && plan.Operations.Count > 0)
        {
            var compiled = PlanCompiler.Compile(plan, model, known, boards).Preview;
            allErrors.AddRange(compiled.Errors);
            steps.AddRange(compiled.Steps);
        }

        var warnings = new List<string>(diff.Warnings);
        var packageRoles = new List<PackageRoleDto>();
        foreach (var r in p.AllRoles.DistinctBy(r => r.Name, Ci))
        {
            var isNew = !roles.TryGetValue(r.Name, out var existing);
            var perms = (r.Permissions ?? []).Where(x => isNew || diff.Created.Contains(x.Entity))
                .Select(x => (desired.TryGetValue(x.Entity, out var d) ? d : model.GetValueOrDefault(x.Entity)) is { } e
                    ? new PackagePermission(e.Name, e.LabelPlural, x.CanRead, x.CanCreate, x.CanUpdate, x.CanDelete)
                    : new PackagePermission(x.Entity, x.Entity, x.CanRead, x.CanCreate, x.CanUpdate, x.CanDelete))
                .OrderBy(x => x.Entity, Ci).ToList();
            if (existing is not null)
                foreach (var x in (r.Permissions ?? []).Where(x => !diff.Created.Contains(x.Entity)))
                {
                    var current = existing.Permissions.FirstOrDefault(c => Ci.Equals(c.Entity, x.Entity));
                    if (current is null || current.CanRead != x.CanRead || current.CanCreate != x.CanCreate || current.CanUpdate != x.CanUpdate || current.CanDelete != x.CanDelete)
                        warnings.Add($"Los permisos de «{existing.Name}» sobre «{x.Entity}» son distintos en el paquete: los de un rol existente sobre un catálogo existente se administran en cada entorno.");
                }
            packageRoles.Add(new PackageRoleDto(existing?.Name ?? r.Name, isNew, isNew && r.CanViewAudit, perms));
        }

        var hashInput = JsonSerializer.Serialize(new { plan.Operations, roles = packageRoles }, PackageJson.Canonical);
        var diffHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)));
        var empty = plan.Operations.Count == 0 && packageRoles.All(r => !r.IsNew && r.Permissions.Count == 0);
        return new PackageDiffDto(allErrors.Count == 0 && diff.Conflicts.Count == 0, allErrors, steps, diff.Conflicts, warnings, packageRoles, diffHash, empty);
    }

    // ---------------- Datos ----------------

    private sealed record PermRow(int RoleId, string Entity, bool CanRead, bool CanCreate, bool CanUpdate, bool CanDelete);
    private sealed record RoleRow(int Id, string Name, string? Description, bool IsSystem, bool IsSuperuser, bool CanManageModel,
        bool CanManageUsers, bool CanViewAudit)
    {
        public List<PermRow> Permissions { get; } = [];
    }

    private static async Task<IReadOnlyDictionary<string, RoleRow>> LoadRolesAsync(DbConnection cn, DbTransaction? tx)
    {
        var roles = (await cn.QueryAsync<RoleRow>(
            "SELECT Id, Name, Description, IsSystem, IsSuperuser, CanManageModel, CanManageUsers, CanViewAudit FROM meta.Role", transaction: tx))
            .ToDictionary(r => r.Name, Ci);
        var byId = roles.Values.ToDictionary(r => r.Id);
        foreach (var perm in await cn.QueryAsync<PermRow>(
                     "SELECT RoleId, EntityName AS Entity, CanRead, CanCreate, CanUpdate, CanDelete FROM meta.EntityPermission", transaction: tx))
            if (byId.TryGetValue(perm.RoleId, out var r)) r.Permissions.Add(perm);
        return roles;
    }

    private sealed record ImportRow(int Id, string PackageId, string Name, string Version, string? Description, string? SourceEnvironment,
        DateTime? ExportedAt, string ContentJson, string ContentHash, bool Signed, string Status, string? PreviousVersion,
        int UploadedById, string UploadedBy, DateTime UploadedAt, int? ReviewedById, string? ReviewedBy, DateTime? ReviewedAt,
        string? ReviewComment, string? ApprovedDiffHash, string? AppliedBy, DateTime? AppliedAt, int? ChangeSetId);

    private const string SelectImport = """
        SELECT i.Id, i.PackageId, i.Name, i.Version, i.Description, i.SourceEnvironment, i.ExportedAt, i.ContentJson, i.ContentHash,
               i.Signed, i.Status, i.PreviousVersion, i.UploadedBy AS UploadedById, up.Username AS UploadedBy, i.UploadedAt,
               i.ReviewedBy AS ReviewedById, rv.Username AS ReviewedBy, i.ReviewedAt, i.ReviewComment, i.ApprovedDiffHash,
               ap.Username AS AppliedBy, i.AppliedAt, i.ChangeSetId
          FROM meta.PackageImport i
          JOIN meta.[User] up ON up.Id = i.UploadedBy
          LEFT JOIN meta.[User] rv ON rv.Id = i.ReviewedBy
          LEFT JOIN meta.[User] ap ON ap.Id = i.AppliedBy
        """;

    private static async Task<ImportRow> LockAsync(SqlConnection cn, SqlTransaction tx, int id)
    {
        // UPDLOCK sobre la fila: dos administradores no aprueban, rechazan o aplican el mismo paquete a la vez.
        if (await cn.ExecuteScalarAsync<int?>("SELECT Id FROM meta.PackageImport WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id", new { id }, tx) is null)
            throw new NotFound("El paquete no existe.");
        return await cn.QuerySingleAsync<ImportRow>($"{SelectImport} WHERE i.Id = @id", new { id }, tx);
    }

    private static PackageImportDto ToDto(ImportRow r) => new(r.Id, r.PackageId, r.Name, r.Version, r.PreviousVersion, r.Status, r.ContentHash,
        r.Signed, r.SourceEnvironment, r.ExportedAt is { } e ? Utc(e) : null, r.UploadedBy, Utc(r.UploadedAt), r.ReviewedBy,
        r.ReviewedAt is { } rv ? Utc(rv) : null, r.ReviewComment, r.AppliedBy, r.AppliedAt is { } ap ? Utc(ap) : null, r.ChangeSetId);

    private static ModulePackage Parse(string json) => JsonSerializer.Deserialize<ModulePackage>(json, PackageJson.Read)!;

    private static string? Comment(string? comment, bool required)
    {
        var c = comment?.Trim();
        if (required && string.IsNullOrEmpty(c))
            throw new ValidationFailed(new Dictionary<string, string[]> { ["comment"] = ["Escribe el motivo del rechazo."] });
        if (c?.Length > MaxComment)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["comment"] = [$"Máximo {MaxComment} caracteres."] });
        return string.IsNullOrEmpty(c) ? null : c;
    }

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();
    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
    private static DateTime Truncate(DateTime d) => new(d.Ticks - d.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}
