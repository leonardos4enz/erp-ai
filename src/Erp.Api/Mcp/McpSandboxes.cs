using System.Collections.Concurrent;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Erp.Api.Studio;

namespace Erp.Api.Mcp;

/// <summary>Spec 044 (H2 CA2): otra llamada de la misma conexión está preparando la copia.</summary>
public sealed class McpSandboxBusy(string message, int retryAfterSeconds) : Exception(message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>Spec 044 (H2 CA4): tope de copias vivas (por persona o en el entorno).</summary>
public sealed class McpSandboxLimit(string message) : Exception(message);

/// <summary>Lo que «Conexiones de Claude» muestra de la copia de una conexión (H6 CA1).</summary>
public sealed record McpSandboxInfo(string Status, DateTime CreatedAt, DateTime LastUsedAt);

/// <summary>
/// Spec 044 (H2 CA3): la copia en uso durante una llamada. Mientras vive, renueva el <i>lease</i> de la fila cada tercio (una
/// llamada larga no pierde la copia por el barrido); al soltarse, anota el último uso.
/// </summary>
public sealed class McpSandboxLease : IAsyncDisposable
{
    private readonly McpSandboxes _owner;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _renewal;

    internal McpSandboxLease(McpSandboxes owner, int id, Guid token, Sandbox sandbox, TimeSpan every)
    {
        _owner = owner;
        Id = id;
        Token = token;
        Sandbox = sandbox;
        _renewal = RenewAsync(every);
    }

    public int Id { get; }
    public Guid Token { get; }
    public Sandbox Sandbox { get; }

    private async Task RenewAsync(TimeSpan every)
    {
        try
        {
            using var timer = new PeriodicTimer(every);
            while (await timer.WaitForNextTickAsync(_stop.Token)) await _owner.RenewAsync(Id, Token, _stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* la renovación es de mejor esfuerzo: el lease inicial y el último uso siguen protegiendo la copia */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _renewal;
        _stop.Dispose();
        await _owner.TouchAsync(Id, Token);
    }
}

/// <summary>
/// Spec 044 (H2): la copia aislada de una conexión MCP (<c>meta.McpSandbox</c>, una por conexión). La crea la primera
/// herramienta aislada bajo un bloqueo por conexión; las siguientes la reutilizan. Se borra por inactividad, cuando la conexión
/// muere o la persona cambia su sello, al desconectar, al descartarla, o si quedó a medias (CLARIFY 2). La copia es la de
/// Studio (<see cref="SandboxFactory"/>): el modelo del entorno, sin datos reales, con su propio proveedor de servicios.
/// </summary>
public sealed class McpSandboxes(Db db, SandboxFactory factory, McpOptions options, StudioOptions studio, ILogger<McpSandboxes> log) : IAsyncDisposable
{
    /// <summary>Copias abiertas en este proceso (por id de fila): abrir una arma un proveedor de servicios entero.</summary>
    private readonly ConcurrentDictionary<int, Sandbox> _open = new();

    /// <summary>Lo que se le da a una preparación antes de que el barrido la dé por caída.</summary>
    private const int PreparingMinutes = 15;

    private const string BusyMessage = "La copia aislada de esta conexión se está preparando: intenta de nuevo en unos segundos.";

    private TimeSpan Renewal => TimeSpan.FromSeconds(Math.Max(2, options.SandboxLeaseSeconds / 3));

    /// <summary>
    /// La copia de la conexión, lista para una llamada. Bajo el bloqueo de la conexión (de sesión: la preparación corre en otra
    /// base, sin una transacción abierta aquí): si otra llamada la está preparando, <see cref="McpSandboxBusy"/>.
    /// </summary>
    public async Task<McpSandboxLease> AcquireAsync(int grantId, UserContext user, CancellationToken ct)
    {
        if (!studio.Sandbox) throw new SandboxUnavailable("El entorno aislado está deshabilitado en este servidor (Studio:Sandbox).");
        var resource = $"erp-mcp-sbx:{grantId}";
        await using var cn = await db.OpenAsync(ct);
        var locked = await cn.ExecuteScalarAsync<int>("""
            DECLARE @r int; EXEC @r = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 2000; SELECT @r;
            """, new { resource });
        if (locked < 0) throw new McpSandboxBusy(BusyMessage, 5);
        try
        {
            var row = (await cn.QueryAsync<(int Id, string DbName, Guid Token)>("""
                UPDATE meta.McpSandbox SET LastUsedAt = SYSUTCDATETIME(),
                       LeaseUntil = CASE WHEN LeaseUntil > DATEADD(second, @lease, SYSUTCDATETIME()) THEN LeaseUntil ELSE DATEADD(second, @lease, SYSUTCDATETIME()) END
                OUTPUT inserted.Id, inserted.DbName, inserted.Token
                 WHERE GrantId = @grantId AND UserId = @userId AND Status = 'ready'
                """, new { grantId, userId = user.Id, lease = options.SandboxLeaseSeconds })).Cast<(int Id, string DbName, Guid Token)?>().SingleOrDefault();
            if (row is { } r)
            {
                if (await OpenCachedAsync(r.Id, r.DbName, r.Token, ct) is { } ready) return new McpSandboxLease(this, r.Id, r.Token, ready, Renewal);
                // La base ya no está (o no lleva su marca): la fila se va y se prepara otra.
                await ReleaseWhereAsync("s.Id = @id", new { id = r.Id }, "lost", CancellationToken.None);
            }
            // Una fila «creating» con el bloqueo en nuestras manos es de una preparación que cayó: nadie la está preparando.
            await ReleaseWhereAsync("s.GrantId = @grantId AND s.Status = 'creating'", new { grantId }, "stuck", CancellationToken.None);
            return await CreateAsync(cn, grantId, user, ct);
        }
        finally
        {
            await cn.ExecuteAsync("EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'", new { resource });
        }
    }

    private async Task<Sandbox?> OpenCachedAsync(int id, string name, Guid token, CancellationToken ct)
    {
        if (_open.TryGetValue(id, out var cached) && cached.Token == token) return cached;
        var opened = await factory.OpenAsync(name, token, ct, strict: true);
        if (opened is null) return null;
        if (_open.TryRemove(id, out var stale)) await stale.DisposeAsync();
        _open[id] = opened;
        return opened;
    }

    private async Task<McpSandboxLease> CreateAsync(Microsoft.Data.SqlClient.SqlConnection cn, int grantId, UserContext user, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        int id;
        string name;
        // H2 CA4: los topes, contados bajo un bloqueo global corto. La fila (con su nombre y su marca) queda antes de crear la base:
        // si el proceso cae a mitad, el barrido encuentra la fila vencida o la base huérfana.
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            if (await cn.ExecuteScalarAsync<int>("""
                    DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'erp-mcp-sbx', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;
                    """, transaction: tx) < 0)
                throw new McpSandboxBusy(BusyMessage, 5);
            if (await cn.ExecuteScalarAsync<int?>("SELECT 1 FROM meta.McpGrant WHERE Id = @grantId AND UserId = @userId AND RevokedAt IS NULL",
                    new { grantId, userId = user.Id }, tx) is null)
                throw new SandboxUnavailable("Esta conexión ya no está vigente.");
            var (mine, all) = await cn.QuerySingleAsync<(int, int)>(
                "SELECT ISNULL(SUM(CASE WHEN UserId = @userId THEN 1 ELSE 0 END), 0), COUNT(*) FROM meta.McpSandbox", new { userId = user.Id }, tx);
            if (mine >= options.SandboxesPerUser)
                throw new McpSandboxLimit($"Ya tienes {mine} copia(s) aislada(s) viva(s), el máximo: descarta una en «Conexiones de Claude» (o con aislado_descartar desde su conexión) o espera a que venza tras {options.SandboxIdleMinutes} min sin uso.");
            if (all >= options.MaxSandboxes)
                throw new McpSandboxLimit("El servidor llegó al máximo de copias aisladas por MCP: intenta más tarde.");
            // Revisión adversarial 044 (A-02): descartar y volver a preparar en bucle crea y borra bases sin tope (los de arriba
            // cuentan copias vivas): también se cuentan las preparadas en la última hora (su bitácora).
            var created = await cn.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM meta.AuditLog WHERE Action = @action AND UserId = @userId AND At > DATEADD(hour, -1, SYSUTCDATETIME())
                   AND AfterJson LIKE N'%"created"%'
                """, new { action = AuditActions.McpSandbox, userId = user.Id }, tx);
            if (created >= options.SandboxCreationsPerHour)
                throw new McpSandboxLimit($"Preparaste {created} copias aisladas en la última hora, el máximo: reutiliza la que tienes en lugar de descartarla y volver a prepararla, o espera.");
            id = await cn.ExecuteScalarAsync<int>("""
                INSERT meta.McpSandbox (GrantId, UserId, DbName, Token, Status, LastUsedAt, LeaseUntil)
                OUTPUT inserted.Id VALUES (@grantId, @userId, N'', @token, 'creating', SYSUTCDATETIME(), DATEADD(minute, @minutes, SYSUTCDATETIME()))
                """, new { grantId, userId = user.Id, token, minutes = PreparingMinutes }, tx);
            name = factory.McpNameFor(id);
            await cn.ExecuteAsync("UPDATE meta.McpSandbox SET DbName = @name WHERE Id = @id", new { name, id }, tx);
            await tx.CommitAsync(ct);
        }

        Sandbox sandbox;
        try { sandbox = await factory.CreateAsync(name, token, ct); }
        catch
        {
            // La fábrica ya borró la base a medias; la fila se va para que la siguiente llamada lo intente de nuevo.
            await using var clean = await db.OpenAsync(CancellationToken.None);
            await clean.ExecuteAsync("DELETE meta.McpSandbox WHERE Id = @id AND Token = @token", new { id, token });
            throw;
        }
        try
        {
        await using (var tx = await cn.BeginTransactionAsync(CancellationToken.None))
        {
            var ok = await cn.ExecuteAsync("""
                UPDATE meta.McpSandbox SET Status = 'ready', LastUsedAt = SYSUTCDATETIME(), LeaseUntil = DATEADD(second, @lease, SYSUTCDATETIME())
                 WHERE Id = @id AND Token = @token AND Status = 'creating'
                """, new { id, token, lease = options.SandboxLeaseSeconds }, tx);
            if (ok == 0)
            {
                // Se descartó (o murió la conexión) mientras se preparaba: la base recién hecha no se queda.
                await tx.RollbackAsync(CancellationToken.None);
                await sandbox.DisposeAsync();
                await factory.DropIfOwnedAsync(name, token);
                throw new SandboxUnavailable("La copia aislada se descartó mientras se preparaba: intenta de nuevo.");
            }
            await Audit.WriteAsync(cn, tx, new DefaultHttpContext(), AuditActions.McpSandbox, "McpGrant", grantId.ToString(),
                after: new { @event = "created" }, userId: user.Id, username: user.Username);
            await tx.CommitAsync(CancellationToken.None);
        }
        }
        catch (Exception ex) when (ex is not SandboxUnavailable)
        {
            // Revisión adversarial 044 (A-05): la copia no queda viva en memoria ni en el servidor esperando al barrido.
            await sandbox.DisposeAsync();
            await factory.DropIfOwnedAsync(name, token);
            await using var clean = await db.OpenAsync(CancellationToken.None);
            await clean.ExecuteAsync("DELETE meta.McpSandbox WHERE Id = @id AND Token = @token", new { id, token });
            throw;
        }
        _open[id] = sandbox;
        return new McpSandboxLease(this, id, token, sandbox, Renewal);
    }

    internal async Task RenewAsync(int id, Guid token, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await cn.ExecuteAsync("UPDATE meta.McpSandbox SET LeaseUntil = DATEADD(second, @lease, SYSUTCDATETIME()) WHERE Id = @id AND Token = @token",
            new { id, token, lease = options.SandboxLeaseSeconds });
    }

    internal async Task TouchAsync(int id, Guid token)
    {
        try
        {
            await using var cn = await db.OpenAsync();
            await cn.ExecuteAsync("UPDATE meta.McpSandbox SET LastUsedAt = SYSUTCDATETIME() WHERE Id = @id AND Token = @token", new { id, token });
        }
        catch (Exception ex) { log.LogWarning("No se pudo anotar el último uso de la copia MCP {Id}: {Message}", id, ex.Message); }
    }

    /// <summary>H2 CA3 y H6 CA2: borra la copia de una conexión de esta persona. Devuelve si había una.</summary>
    public async Task<bool> ReleaseAsync(int grantId, int userId, string reason) =>
        await ReleaseWhereAsync("s.GrantId = @grantId AND s.UserId = @userId", new { grantId, userId }, reason, CancellationToken.None) > 0;

    /// <summary>
    /// Borra las filas que cumplen <paramref name="where"/> (texto fijo de esta clase, nunca de la petición) con sus claves de
    /// idempotencia y su bitácora, y después sus bases (solo si llevan su marca: 036 F-03).
    /// </summary>
    private async Task<int> ReleaseWhereAsync(string where, object args, string reason, CancellationToken ct)
    {
        List<(int Id, int GrantId, int UserId, string DbName, Guid Token, string? Username)> rows;
        await using (var cn = await db.OpenAsync(ct))
        await using (var tx = await cn.BeginTransactionAsync(ct))
        {
            rows = (await cn.QueryAsync<(int, int, int, string, Guid)>($"""
                DELETE s OUTPUT deleted.Id, deleted.GrantId, deleted.UserId, deleted.DbName, deleted.Token FROM meta.McpSandbox s WHERE {where}
                """, args, tx)).Select(r => (r.Item1, r.Item2, r.Item3, r.Item4, r.Item5, (string?)null)).ToList();
            if (rows.Count == 0) return 0;
            await cn.ExecuteAsync("DELETE meta.McpWrite WHERE SandboxId IN @ids", new { ids = rows.Select(r => r.Id).ToArray() }, tx);
            foreach (var r in rows)
                await Audit.WriteAsync(cn, tx, new DefaultHttpContext(), AuditActions.McpSandbox, "McpGrant", r.GrantId.ToString(),
                    after: new { @event = "dropped", reason }, userId: r.UserId,
                    username: await cn.ExecuteScalarAsync<string?>("SELECT Username FROM meta.[User] WHERE Id = @UserId", new { r.UserId }, tx));
            await tx.CommitAsync(ct);
        }
        foreach (var r in rows)
        {
            if (_open.TryRemove(r.Id, out var sb)) await sb.DisposeAsync();
            if (r.DbName.Length > 0) await factory.DropIfOwnedAsync(r.DbName, r.Token);
        }
        log.LogInformation("Copias aisladas MCP borradas ({Reason}): {Count}", reason, rows.Count);
        return rows.Count;
    }

    /// <summary>Spec 044 (H6 CA1): la copia de cada conexión de la persona.</summary>
    public async Task<IReadOnlyDictionary<int, McpSandboxInfo>> InfoAsync(int userId, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
        return (await cn.QueryAsync<(int GrantId, string Status, DateTime CreatedAt, DateTime LastUsedAt)>(
                "SELECT GrantId, Status, CreatedAt, LastUsedAt FROM meta.McpSandbox WHERE UserId = @userId", new { userId }))
            .ToDictionary(r => r.GrantId, r => new McpSandboxInfo(r.Status, Utc(r.CreatedAt), Utc(r.LastUsedAt)));
    }

    /// <summary>
    /// H2 CA3: una vuelta del barrido. Conexiones muertas (revocadas, sin renovación vigente, borradas) o cuya persona ya no está
    /// activa o cambió su sello: aunque tengan una llamada en curso. Inactivas: sin <i>lease</i> vigente y sin uso en
    /// <c>Mcp:SandboxIdleMinutes</c>. Preparaciones vencidas. Bases sin fila. Claves vencidas.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var count = await ReleaseWhereAsync("""
            NOT EXISTS (SELECT 1 FROM meta.McpGrant g JOIN meta.[User] u ON u.Id = g.UserId
                         WHERE g.Id = s.GrantId AND g.UserId = s.UserId AND g.RevokedAt IS NULL AND u.IsActive = 1 AND u.SecurityStamp = g.SecurityStamp
                           AND EXISTS (SELECT 1 FROM meta.McpToken t WHERE t.GrantId = g.Id AND t.Kind = 'r' AND t.UsedAt IS NULL AND t.ExpiresAt > SYSUTCDATETIME()))
            """, new { }, "revoked", ct);
        count += await ReleaseWhereAsync("s.Status = 'ready' AND s.LeaseUntil < SYSUTCDATETIME() AND s.LastUsedAt < DATEADD(minute, -@idle, SYSUTCDATETIME())",
            new { idle = options.SandboxIdleMinutes }, "idle", ct);
        count += await ReleaseWhereAsync("s.Status = 'creating' AND s.LeaseUntil < SYSUTCDATETIME()", new { }, "stuck", ct);
        count += await factory.SweepMcpAsync(async () =>
        {
            await using var cn = await db.OpenAsync(ct);
            var live = (await cn.QueryAsync<int>("SELECT Id FROM meta.McpSandbox")).ToHashSet();
            // Las abiertas en este proceso cuya fila borró otra instancia se sueltan.
            foreach (var id in _open.Keys.Where(k => !live.Contains(k)).ToList())
                if (_open.TryRemove(id, out var stale)) await stale.DisposeAsync();
            return live;
        }, ct);
        await using (var cn = await db.OpenAsync(ct))
            await cn.ExecuteAsync("DELETE meta.McpWrite WHERE CreatedAt < DATEADD(hour, -@hours, SYSUTCDATETIME())", new { hours = options.IdempotencyHours });
        return count;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _open.Keys.ToList())
            if (_open.TryRemove(id, out var sb)) await sb.DisposeAsync();
    }
}

/// <summary>Spec 044 (H2 CA3): el barrido de las copias por conexión, cada <c>Mcp:SweepSeconds</c>, en todas las instancias.</summary>
public sealed class McpSandboxSweeper(McpSandboxes sandboxes, McpOptions options, ILogger<McpSandboxSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepSeconds));
        do
        {
            try { await sandboxes.SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogError(ex, "Falló una vuelta del barrido de copias aisladas MCP; se reintenta en la siguiente"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
