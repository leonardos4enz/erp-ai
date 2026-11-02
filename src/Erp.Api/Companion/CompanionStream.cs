using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Security;
using Erp.Api.Studio;

namespace Erp.Api.Companion;

/// <summary>
/// Spec 046 (H1 CA3): el avance de una conversación del Acompañante como los eventos SSE de Studio (036): <c>step</c>,
/// <c>session</c>, <c>live</c> y <c>end</c>, con el mismo formato (<see cref="StudioStream.WriteAsync"/>). Lee con la dueña,
/// despierta con <see cref="CompanionSignals"/> o cada <c>Companion:StreamPollMs</c> y revisa la identidad cada
/// <c>Companion:IdentitySeconds</c>.
/// </summary>
public sealed class CompanionStream(Db db, CompanionOptions options, CompanionSignals signals, UserContextLoader users, JsonSerializerOptions json,
    ILogger<CompanionStream> log)
{
    public const int Batch = 200;

    private sealed record Snapshot(string Status, int Turns, int Steps, int Pending, string? ErrorCode, string? Summary, string? LiveJson, int? CompanyId);

    public async Task RunAsync(int id, int? after, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        if (user.IsService) throw new Forbidden("El Acompañante es para personas, no para cuentas de servicio.");
        if (http.Request.Headers.TryGetValue("Last-Event-ID", out var last) && int.TryParse(last, out var lastId)) after = lastId;
        var seq = Math.Max(0, after ?? 0);
        if (await ReadAsync(id, user.Id, ct) is not { } first || !CompanionService.InCompany(first.CompanyId, user)) throw new NotFound("La conversación no existe.");
        using var slot = signals.OpenStream(user.Id, options.MaxStreamsPerUser)
                         ?? throw new AppException(StatusCodes.Status429TooManyRequests,
                             $"Ya tienes {options.MaxStreamsPerUser} conexiones abiertas con el Acompañante: cierra alguna pestaña.");

        var res = http.Response;
        res.ContentType = "text/event-stream; charset=utf-8";
        res.Headers.CacheControl = "no-store";
        res.Headers["X-Accel-Buffering"] = "no";
        await res.Body.FlushAsync(ct);

        var deadline = DateTime.UtcNow.AddSeconds(options.StreamSeconds);
        var identityAt = DateTime.UtcNow;
        var pingAt = DateTime.UtcNow;
        string? lastSession = null, lastLive = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow - identityAt >= TimeSpan.FromSeconds(options.IdentitySeconds))
                {
                    identityAt = DateTime.UtcNow;
                    var fresh = await users.LoadAsync(user.Id, user.SecurityStamp, ct);
                    if (fresh is null || fresh.IsService) { await StudioStream.WriteAsync(res, "end", """{"reason":"identity"}""", null, ct); return; }
                    user = fresh;
                }
                var snap = await ReadAsync(id, user.Id, ct);
                // F-07: sacada de la empresa de la conversación, el flujo se cierra.
                if (snap is not null && !CompanionService.InCompany(snap.CompanyId, user)) { await StudioStream.WriteAsync(res, "end", """{"reason":"identity"}""", null, ct); return; }
                if (snap is null) { await StudioStream.WriteAsync(res, "end", """{"reason":"gone"}""", null, ct); return; }
                List<StudioService.StepRow> steps;
                await using (var cn = await db.OpenAsync(ct))
                    steps = (await cn.QueryAsync<StudioService.StepRow>("""
                        SELECT TOP (@Batch) Seq, Kind, Tool, Status, Summary, InputJson, OutputJson, DurationMs, CreatedAt, Turn FROM meta.CompanionStep
                         WHERE ConversationId = @id AND Seq > @seq ORDER BY Seq
                        """, new { id, seq, Batch })).ToList();
                foreach (var s in steps)
                {
                    await StudioStream.WriteAsync(res, "step", JsonSerializer.Serialize(StudioService.ToStep(s), json), s.Seq.ToString(), ct);
                    seq = s.Seq;
                }
                var session = JsonSerializer.Serialize(new
                {
                    status = snap.Status, turns = snap.Turns, steps = snap.Steps, pendingActions = snap.Pending,
                    error = snap.ErrorCode is null || snap.Status == StudioStatus.Cancelled ? null : CompanionService.FailureMessage(snap.ErrorCode, snap.Summary),
                }, json);
                if (session != lastSession) { await StudioStream.WriteAsync(res, "session", session, null, ct); lastSession = session; }
                var live = StudioStatus.Active(snap.Status) && snap.LiveJson is { } lj ? lj : """{"kind":"none"}""";
                if (live != lastLive) { await StudioStream.WriteAsync(res, "live", live, null, ct); lastLive = live; }
                if (steps.Count == Batch) continue;
                if (!StudioStatus.Active(snap.Status)) { await StudioStream.WriteAsync(res, "end", new JsonObject { ["reason"] = "idle" }.ToJsonString(), null, ct); return; }
                if (DateTime.UtcNow >= deadline) { await StudioStream.WriteAsync(res, "end", new JsonObject { ["reason"] = "timeout" }.ToJsonString(), null, ct); return; }
                if (DateTime.UtcNow - pingAt >= TimeSpan.FromSeconds(15))
                {
                    pingAt = DateTime.UtcNow;
                    await res.WriteAsync(": ping\n\n", ct);
                    await res.Body.FlushAsync(ct);
                }
                await signals.WaitAsync(id, TimeSpan.FromMilliseconds(options.StreamPollMs), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { log.LogDebug("Flujo del Acompañante {Id} cerrado: {Message}", id, ex.Message); }
    }

    private async Task<Snapshot?> ReadAsync(int id, int userId, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        return await cn.QuerySingleOrDefaultAsync<Snapshot>("""
            SELECT Status, Turns, Steps, (SELECT COUNT(*) FROM meta.CompanionAction a WHERE a.ConversationId = c.Id AND a.Status = 'proposed') AS Pending,
                   ErrorCode, Summary, LiveJson, CompanyId
              FROM meta.CompanionConversation c WHERE Id = @id AND UserId = @userId AND Status <> 'deleted'
            """, new { id, userId });
    }
}
