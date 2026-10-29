using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Erp.Api.Infrastructure;
using Erp.Api.Security;

namespace Erp.Api.Studio;

/// <summary>
/// Spec 036 (H5): el avance de una conversación como eventos SSE. Lee la base (la misma consulta con dueño que
/// <c>GET …/sessions/{id}</c>): pasos nuevos, estado de la conversación y estado en vivo. Despierta con
/// <see cref="StudioSignals"/> o cada <c>Studio:StreamPollMs</c>; revisa la identidad cada <c>Studio:IdentitySeconds</c> y
/// termina al acabar el turno o a los <c>Studio:StreamSeconds</c>.
/// </summary>
public sealed class StudioStream(Db db, StudioOptions options, StudioSignals signals, UserContextLoader users, JsonSerializerOptions json,
    ILogger<StudioStream> log)
{
    /// <summary>Pasos por lectura: un historial largo llega en varios lotes.</summary>
    public const int Batch = 200;

    private sealed record Snapshot(string Status, int Turns, int Steps, int ToolCalls, long InputTokens, long OutputTokens, long CostMicros,
        string? PlanStatus, string? ErrorCode, string? Summary, string? LiveJson);

    public async Task RunAsync(int id, int? after, HttpContext http, CancellationToken ct)
    {
        var user = http.CurrentUser();
        if (http.Request.Headers.TryGetValue("Last-Event-ID", out var last) && int.TryParse(last, out var lastId)) after = lastId;
        var seq = Math.Max(0, after ?? 0);
        // Antes de abrir el flujo: 404 si no es suya, 429 si ya tiene el máximo abierto (respuestas normales de la API).
        if (await ReadAsync(id, user.Id, ct) is null) throw new NotFound("La sesión no existe.");
        using var slot = signals.OpenStream(user.Id, options.MaxStreamsPerUser)
                         ?? throw new AppException(StatusCodes.Status429TooManyRequests,
                             $"Ya tienes {options.MaxStreamsPerUser} conexiones abiertas con ERP AI Studio: cierra alguna pestaña.");

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
                    // H5 CA3: si la persona se desactiva, cambia su contraseña o deja de ser modeladora, el flujo se cierra.
                    var fresh = await users.LoadAsync(user.Id, user.SecurityStamp, ct);
                    if (fresh is null || fresh.IsService || !fresh.CanManageModel) { await EndAsync(res, "identity", ct); return; }
                }
                var snap = await ReadAsync(id, user.Id, ct);
                if (snap is null) { await EndAsync(res, "gone", ct); return; }
                List<StudioService.StepRow> steps;
                await using (var cn = await db.OpenAsync(ct))
                    steps = (await cn.QueryAsync<StudioService.StepRow>("""
                        SELECT TOP (@Batch) Seq, Kind, Tool, Status, Summary, InputJson, OutputJson, DurationMs, CreatedAt, Turn FROM meta.StudioStep
                         WHERE SessionId = @id AND Seq > @seq ORDER BY Seq
                        """, new { id, seq, Batch })).ToList();
                foreach (var s in steps)
                {
                    await WriteAsync(res, "step", JsonSerializer.Serialize(StudioService.ToStep(s), json), s.Seq.ToString(), ct);
                    seq = s.Seq;
                }
                var session = JsonSerializer.Serialize(new
                {
                    status = snap.Status, turns = snap.Turns, steps = snap.Steps, toolCalls = snap.ToolCalls, inputTokens = snap.InputTokens,
                    outputTokens = snap.OutputTokens, costUsd = snap.CostMicros / 1_000_000m, planStatus = snap.PlanStatus,
                    error = snap.ErrorCode is null || snap.Status == StudioStatus.Cancelled ? null : StudioService.FailureMessage(snap.ErrorCode, snap.Summary),
                }, json);
                if (session != lastSession) { await WriteAsync(res, "session", session, null, ct); lastSession = session; }
                // El estado en vivo es solo del turno en curso; al guardarse el paso, el trabajador lo borra.
                var live = StudioStatus.Active(snap.Status) && snap.LiveJson is { } lj ? lj : """{"kind":"none"}""";
                if (live != lastLive) { await WriteAsync(res, "live", live, null, ct); lastLive = live; }
                if (steps.Count == Batch) continue; // quedan pasos: sin esperar
                if (!StudioStatus.Active(snap.Status)) { await EndAsync(res, "idle", ct); return; }
                if (DateTime.UtcNow >= deadline) { await EndAsync(res, "timeout", ct); return; }
                if (DateTime.UtcNow - pingAt >= TimeSpan.FromSeconds(15))
                {
                    pingAt = DateTime.UtcNow;
                    await res.WriteAsync(": ping\n\n", ct);
                    await res.Body.FlushAsync(ct);
                }
                await signals.WaitAsync(id, TimeSpan.FromMilliseconds(options.StreamPollMs), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* la persona cerró la pestaña */ }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { log.LogDebug("Flujo de Studio {Id} cerrado: {Message}", id, ex.Message); }
    }

    private async Task<Snapshot?> ReadAsync(int id, int userId, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        return await cn.QuerySingleOrDefaultAsync<Snapshot>("""
            SELECT Status, Turns, Steps, ToolCalls, InputTokens, OutputTokens, CostMicros, PlanStatus, ErrorCode, Summary, LiveJson
              FROM meta.StudioSession WHERE Id = @id AND UserId = @userId
            """, new { id, userId });
    }

    private static Task EndAsync(HttpResponse res, string reason, CancellationToken ct) =>
        WriteAsync(res, "end", new JsonObject { ["reason"] = reason }.ToJsonString(), null, ct);

    /// <summary>Un evento SSE. Los datos son JSON en una línea (el serializador escapa los saltos), así que no se parten.</summary>
    public static async Task WriteAsync(HttpResponse res, string evt, string data, string? id, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (id is not null) sb.Append("id: ").Append(id).Append('\n');
        sb.Append("event: ").Append(evt).Append('\n');
        sb.Append("data: ").Append(data.Replace("\r", "").Replace("\n", "")).Append("\n\n");
        await res.WriteAsync(sb.ToString(), ct);
        await res.Body.FlushAsync(ct);
    }
}
