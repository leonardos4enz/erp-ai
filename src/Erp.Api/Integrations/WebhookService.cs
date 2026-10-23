using System.Globalization;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;
using Erp.Api.Security;
using Microsoft.Data.SqlClient;

namespace Erp.Api.Integrations;

/// <summary>Spec 008: configuración por entorno de los webhooks (<c>Webhooks:*</c>).</summary>
public sealed record WebhookOptions(bool AllowHttp, IReadOnlyList<System.Net.IPAddress> AllowedInternal, int PollSeconds, IReadOnlyList<int> RetryDelaysSeconds, int RetentionDays)
{
    public const int BatchSize = 20;
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    public static WebhookOptions From(IConfiguration c) => new(
        c.GetValue("Webhooks:AllowHttp", false),
        (c["Webhooks:AllowedInternalAddresses"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(System.Net.IPAddress.Parse).ToList(),
        Math.Clamp(c.GetValue("Webhooks:PollSeconds", 2), 1, 300),
        (c["Webhooks:RetryDelaysSeconds"] ?? "60,300,1800,7200,21600").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Math.Clamp(int.Parse(s, CultureInfo.InvariantCulture), 1, 86_400)).Take(10).ToList(),
        Math.Clamp(c.GetValue("Webhooks:RetentionDays", 30), 1, 365));

    /// <summary>Intentos totales: el primero más uno por cada espera.</summary>
    public int MaxAttempts => RetryDelaysSeconds.Count + 1;
}

public static class DeliveryStatus
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

public sealed record WebhookSummary(string Entity, string EntityLabel, string Name, string Label, string? Description,
    IReadOnlyList<WebhookEvent> Events, string Role, IReadOnlyList<string>? Fields, bool Configured, bool Active,
    string? Destination, DateTime? SecretCreatedAt, string? LastStatus, DateTime? LastAt);
public sealed record ConfigureWebhookRequest(string? Url, bool Active);
public sealed record DeliveryDto(long Id, Guid DeliveryId, string Entity, string Webhook, string Event, string? EventKey, long? RecordId,
    string Status, int Attempts, int? LastStatusCode, string? LastError, DateTime CreatedAt, DateTime? NextAttemptAt, DateTime? DeliveredAt);

/// <summary>
/// Spec 008: administración de destinos (solo superusuario) y entrega de la bandeja de salida
/// <c>meta.WebhookDelivery</c>. El destino y el secreto se guardan cifrados; nunca se devuelven ni se auditan.
/// </summary>
public sealed class WebhookService(Db db, MetadataStore store, WebhookSecrets secrets, WebhookSender sender, WebhookOptions options, WebhookUrlPolicy policy)
{
    private static void RequireAdmin(UserContext u)
    {
        if (!u.IsSuperuser) throw new Forbidden("Solo un administrador puede configurar integraciones.");
    }

    private async Task<(EntityDef Entity, WebhookDef Hook)> FindAsync(string entity, string name, CancellationToken ct)
    {
        var e = await store.GetActiveAsync(entity, ct);
        var h = e.Webhooks?.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new NotFound("El webhook no existe.");
        return (e, h);
    }

    public async Task<IReadOnlyList<WebhookSummary>> ListAsync(UserContext u, CancellationToken ct)
    {
        RequireAdmin(u);
        var model = await store.GetAsync(ct);
        await using var cn = await db.OpenAsync(ct);
        var endpoints = (await cn.QueryAsync<EndpointInfo>(
            "SELECT EntityName, WebhookName, Destination, IsActive, SecretCreatedAt FROM meta.WebhookEndpoint"))
            .ToDictionary(x => (x.EntityName.ToLowerInvariant(), x.WebhookName.ToLowerInvariant()));
        var last = (await cn.QueryAsync<LastDelivery>("""
            SELECT d.EntityName, d.WebhookName, d.Status, d.CreatedAt FROM meta.WebhookDelivery d
             WHERE d.Id IN (SELECT MAX(Id) FROM meta.WebhookDelivery GROUP BY EntityName, WebhookName)
            """)).ToDictionary(x => (x.EntityName.ToLowerInvariant(), x.WebhookName.ToLowerInvariant()));
        return model.Values.Where(e => !e.IsArchived && e.Webhooks is { Count: > 0 })
            .OrderBy(e => e.Label, StringComparer.CurrentCulture)
            .SelectMany(e => e.Webhooks!.Select(h =>
            {
                var key = (e.Name.ToLowerInvariant(), h.Name.ToLowerInvariant());
                var ep = endpoints.GetValueOrDefault(key);
                var l = last.GetValueOrDefault(key);
                return new WebhookSummary(e.Name, e.Label, h.Name, h.Label, h.Description, h.Events, h.Role, h.Fields,
                    ep is not null, ep?.IsActive == true, ep?.Destination, ep is null ? null : Utc(ep.SecretCreatedAt), l?.Status, l is null ? null : Utc(l.CreatedAt));
            }))
            .ToList();
    }

    /// <summary>Guarda destino y estado; el secreto se genera la primera vez y solo entonces se devuelve.</summary>
    public async Task<string?> ConfigureAsync(string entity, string name, ConfigureWebhookRequest req, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        var (e, h) = await FindAsync(entity, name, ct);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var current = await cn.QuerySingleOrDefaultAsync<EndpointState>(
            "SELECT UrlProtected, Destination, IsActive FROM meta.WebhookEndpoint WITH (UPDLOCK) WHERE EntityName = @e AND WebhookName = @n",
            new { e = e.Name, n = h.Name }, tx);
        var url = string.IsNullOrWhiteSpace(req.Url) ? null : req.Url.Trim();
        if (url is null && current is null)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["url"] = ["Escribe la URL de destino."] });
        if (url is not null && policy.Check(url) is { } bad)
            throw new ValidationFailed(new Dictionary<string, string[]> { ["url"] = [bad] });

        string? secret = null;
        var destination = url is null ? current!.Destination : WebhookUrlPolicy.Mask(url);
        if (current is null)
        {
            secret = WebhookSignature.NewSecret();
            await cn.ExecuteAsync("""
                INSERT meta.WebhookEndpoint (EntityName, WebhookName, UrlProtected, Destination, SecretProtected, IsActive, UpdatedBy)
                VALUES (@e, @n, @url, @dest, @secret, @active, @uid)
                """, new { e = e.Name, n = h.Name, url = secrets.Protect(url!), dest = destination, secret = secrets.Protect(secret), active = req.Active, uid = u.Id }, tx);
        }
        else
        {
            await cn.ExecuteAsync("""
                UPDATE meta.WebhookEndpoint SET UrlProtected = COALESCE(@url, UrlProtected), Destination = @dest, IsActive = @active,
                       UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @uid
                 WHERE EntityName = @e AND WebhookName = @n
                """, new { e = e.Name, n = h.Name, url = url is null ? null : secrets.Protect(url), dest = destination, active = req.Active, uid = u.Id }, tx);
        }
        await Audit.WriteAsync(cn, tx, http, AuditActions.WebhookChange, e.Name, h.Name,
            before: current is null ? null : new { destination = current.Destination, active = current.IsActive },
            after: new { action = current is null ? "configure" : "update", destination, active = req.Active, secretCreated = secret is not null });
        await tx.CommitAsync(ct);
        return secret;
    }

    public async Task<string> RotateAsync(string entity, string name, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        var (e, h) = await FindAsync(entity, name, ct);
        var secret = WebhookSignature.NewSecret();
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var n = await cn.ExecuteAsync("""
            UPDATE meta.WebhookEndpoint SET SecretProtected = @secret, SecretCreatedAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @uid
             WHERE EntityName = @e AND WebhookName = @n
            """, new { e = e.Name, n = h.Name, secret = secrets.Protect(secret), uid = u.Id }, tx);
        if (n == 0) throw new Conflict("Configura el destino antes de rotar el secreto.");
        await Audit.WriteAsync(cn, tx, http, AuditActions.WebhookChange, e.Name, h.Name, after: new { action = "rotate" });
        await tx.CommitAsync(ct);
        return secret;
    }

    public async Task DeleteAsync(string entity, string name, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        var (e, h) = await FindAsync(entity, name, ct);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await cn.ExecuteAsync("DELETE meta.WebhookEndpoint WHERE EntityName = @e AND WebhookName = @n", new { e = e.Name, n = h.Name }, tx);
        await cn.ExecuteAsync("""
            UPDATE meta.WebhookDelivery SET Status = @canceled, LastError = N'Se quitó el destino.'
             WHERE EntityName = @e AND WebhookName = @n AND Status = @pending
            """, new { e = e.Name, n = h.Name, canceled = DeliveryStatus.Canceled, pending = DeliveryStatus.Pending }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.WebhookChange, e.Name, h.Name, after: new { action = "delete" });
        await tx.CommitAsync(ct);
    }

    /// <summary>Envía en el momento un evento <c>ping</c> firmado (sin datos) y lo deja en la bitácora.</summary>
    public async Task<object> TestAsync(string entity, string name, HttpContext http, CancellationToken ct)
    {
        var u = http.CurrentUser();
        RequireAdmin(u);
        var (e, h) = await FindAsync(entity, name, ct);
        await using var cn = await db.OpenAsync(ct);
        var ep = await cn.QuerySingleOrDefaultAsync<EndpointSecret>(
            "SELECT UrlProtected, SecretProtected, IsActive FROM meta.WebhookEndpoint WHERE EntityName = @e AND WebhookName = @n", new { e = e.Name, n = h.Name })
            ?? throw new Conflict("Configura el destino antes de probarlo.");
        var deliveryId = Guid.NewGuid();
        var body = WebhookPayload.Serialize(deliveryId, h.Name, e.Name, "ping", null, null, null, DateTime.UtcNow, new Dictionary<string, object?>());
        var result = await sender.SendAsync(secrets.Unprotect(ep.UrlProtected), secrets.Unprotect(ep.SecretProtected), deliveryId, "ping", body, ct);
        await cn.ExecuteAsync("""
            INSERT meta.WebhookDelivery (DeliveryId, EntityName, WebhookName, Event, PayloadJson, Status, Attempts, LastStatusCode, LastError, DeliveredAt)
            VALUES (@id, @e, @n, 'ping', @body, @status, 1, @code, @error, CASE WHEN @ok = 1 THEN SYSUTCDATETIME() END)
            """, new { id = deliveryId, e = e.Name, n = h.Name, body, status = result.Ok ? DeliveryStatus.Delivered : DeliveryStatus.Failed,
                   code = result.Status, error = result.Error, ok = result.Ok });
        await Audit.WriteAsync(cn, null, http, AuditActions.WebhookChange, e.Name, h.Name, after: new { action = "test", ok = result.Ok, status = result.Status });
        return new { ok = result.Ok, status = result.Status, error = result.Error, deliveryId };
    }

    public async Task<IReadOnlyList<DeliveryDto>> DeliveriesAsync(string? entity, string? name, string? status, UserContext u, CancellationToken ct)
    {
        RequireAdmin(u);
        await using var cn = await db.OpenAsync(ct);
        var rows = await cn.QueryAsync<DeliveryDto>("""
            SELECT TOP 100 Id, DeliveryId, EntityName AS Entity, WebhookName AS Webhook, Event, EventKey, RecordId, Status, Attempts,
                   LastStatusCode, LastError, CreatedAt, CASE WHEN Status = 'pending' THEN NextAttemptAt END AS NextAttemptAt, DeliveredAt
              FROM meta.WebhookDelivery
             WHERE (@entity IS NULL OR EntityName = @entity) AND (@name IS NULL OR WebhookName = @name) AND (@status IS NULL OR Status = @status)
             ORDER BY Id DESC
            """, new { entity, name, status });
        return rows.Select(r => r with { CreatedAt = Utc(r.CreatedAt), NextAttemptAt = r.NextAttemptAt is { } n ? Utc(n) : null, DeliveredAt = r.DeliveredAt is { } d ? Utc(d) : null }).ToList();
    }

    /// <summary>Vuelve a poner en cola una entrega fallida o cancelada (con sus intentos desde cero).</summary>
    public async Task RetryAsync(long id, HttpContext http, CancellationToken ct)
    {
        RequireAdmin(http.CurrentUser());
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var row = await cn.QuerySingleOrDefaultAsync<DeliveryKey>(
            "SELECT EntityName, WebhookName, Event FROM meta.WebhookDelivery WHERE Id = @id AND Status IN ('failed', 'canceled') AND Event <> 'ping'",
            new { id }, tx) ?? throw new NotFound("La entrega no existe o no se puede reintentar.");
        await cn.ExecuteAsync("""
            UPDATE meta.WebhookDelivery SET Status = 'pending', Attempts = 0, NextAttemptAt = SYSUTCDATETIME(), LeaseUntil = NULL, LastError = NULL
             WHERE Id = @id
            """, new { id }, tx);
        await Audit.WriteAsync(cn, tx, http, AuditActions.WebhookChange, row.EntityName, row.WebhookName,
            after: new { action = "retry", delivery = id });
        await tx.CommitAsync(ct);
    }

    // ======================= Entrega (servicio en segundo plano) =======================

    private sealed record EndpointInfo(string EntityName, string WebhookName, string Destination, bool IsActive, DateTime SecretCreatedAt);
    private sealed record LastDelivery(string EntityName, string WebhookName, string Status, DateTime CreatedAt);
    private sealed record EndpointState(string UrlProtected, string Destination, bool IsActive);
    private sealed record EndpointSecret(string UrlProtected, string SecretProtected, bool IsActive);
    private sealed record DeliveryKey(string EntityName, string WebhookName, string Event);
    private sealed record Claimed(long Id, Guid DeliveryId, string EntityName, string WebhookName, string Event, string PayloadJson, int Attempts);

    /// <summary>Toma un lote de entregas vencidas (con *lease*: otra instancia no las repite) y las envía. Devuelve cuántas tomó.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        List<Claimed> batch;
        await using (var cn = await db.OpenAsync(ct))
        {
            batch = (await cn.QueryAsync<Claimed>($"""
                UPDATE TOP ({WebhookOptions.BatchSize}) d
                   SET LeaseUntil = DATEADD(second, @lease, SYSUTCDATETIME()), Attempts = Attempts + 1
                OUTPUT inserted.Id, inserted.DeliveryId, inserted.EntityName, inserted.WebhookName, inserted.Event, inserted.PayloadJson, inserted.Attempts
                  FROM meta.WebhookDelivery d WITH (ROWLOCK, READPAST, UPDLOCK)
                 WHERE d.Status = 'pending' AND d.NextAttemptAt <= SYSUTCDATETIME() AND (d.LeaseUntil IS NULL OR d.LeaseUntil < SYSUTCDATETIME())
                """, new { lease = (int)WebhookOptions.Lease.TotalSeconds })).ToList();
            if (batch.Count == 0) return 0;
        }

        await Task.WhenAll(batch.Select(d => DeliverAsync(d, ct)));
        return batch.Count;
    }

    private async Task DeliverAsync(Claimed d, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        var ep = await cn.QuerySingleOrDefaultAsync<EndpointSecret>(
            "SELECT UrlProtected, SecretProtected, IsActive FROM meta.WebhookEndpoint WHERE EntityName = @EntityName AND WebhookName = @WebhookName", d);
        if (ep is not { IsActive: true })
        {
            await cn.ExecuteAsync("UPDATE meta.WebhookDelivery SET Status = 'canceled', LeaseUntil = NULL, LastError = @err WHERE Id = @Id",
                new { d.Id, err = ep is null ? "Se quitó el destino." : "El webhook está desactivado." });
            return;
        }

        SendResult result;
        try
        {
            result = await sender.SendAsync(secrets.Unprotect(ep.UrlProtected), secrets.Unprotect(ep.SecretProtected), d.DeliveryId, d.Event, d.PayloadJson, ct);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Las llaves de Data Protection cambiaron: el administrador debe volver a configurar el destino.
            result = new(false, null, "No se pudo descifrar el destino; vuelve a configurarlo.");
        }

        if (result.Ok)
        {
            await cn.ExecuteAsync("""
                UPDATE meta.WebhookDelivery SET Status = 'delivered', DeliveredAt = SYSUTCDATETIME(), LeaseUntil = NULL, LastStatusCode = @Status, LastError = NULL
                 WHERE Id = @Id
                """, new { d.Id, result.Status });
            return;
        }
        var done = d.Attempts >= options.MaxAttempts;
        var delay = done ? 0 : options.RetryDelaysSeconds[d.Attempts - 1];
        await cn.ExecuteAsync("""
            UPDATE meta.WebhookDelivery SET Status = @status, LeaseUntil = NULL, LastStatusCode = @code, LastError = @error,
                   NextAttemptAt = DATEADD(second, @delay, SYSUTCDATETIME())
             WHERE Id = @Id
            """, new { d.Id, status = done ? DeliveryStatus.Failed : DeliveryStatus.Pending, code = result.Status, error = Truncate(result.Error), delay });
    }

    /// <summary>Las entregas cerradas llevan datos del negocio: se borran al vencer la retención.</summary>
    public async Task PurgeAsync(CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await cn.ExecuteAsync("DELETE meta.WebhookDelivery WHERE Status <> 'pending' AND CreatedAt < DATEADD(day, -@days, SYSUTCDATETIME())",
            new { days = options.RetentionDays });
    }

    private static string? Truncate(string? s) => s is { Length: > 300 } ? s[..300] : s;
    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}

/// <summary>Spec 008: entrega en segundo plano, cada <c>Webhooks:PollSeconds</c>.</summary>
public sealed class WebhookDispatcher(IServiceScopeFactory scopes, WebhookOptions options, ILogger<WebhookDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPurge = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var took = 0;
            try
            {
                using var scope = scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<WebhookService>();
                took = await svc.DispatchBatchAsync(stoppingToken);
                if (DateTime.UtcNow - lastPurge > TimeSpan.FromHours(1))
                {
                    await svc.PurgeAsync(stoppingToken);
                    lastPurge = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Error al entregar webhooks"); }
            // Lote lleno: probablemente hay más pendientes; se sigue sin esperar.
            if (took < WebhookOptions.BatchSize)
                try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
