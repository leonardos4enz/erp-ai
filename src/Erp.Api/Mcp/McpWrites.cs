using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Erp.Api.Infrastructure;

namespace Erp.Api.Mcp;

/// <summary>Spec 044 (H3 CA1): la clave de idempotencia de una escritura por MCP y la huella de su entrada.</summary>
public static partial class McpIdempotency
{
    public const string Field = "idempotencyKey";
    public const string Description = "Clave única de esta escritura (8 a 100 caracteres: letras, números, «.», «_», «:» o «-»). Repetir la llamada con la misma clave y la misma entrada devuelve el mismo resultado sin volver a ejecutarla.";

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool Valid(string? key) => key is not null && Pattern().IsMatch(key);

    /// <summary>
    /// Semántica (STRIDE): la huella de la herramienta y su entrada en forma canónica (propiedades ordenadas, sin espacio): el
    /// orden en que el cliente escribe las propiedades no la cambia; un valor distinto, sí.
    /// </summary>
    public static byte[] Hash(string tool, JsonObject input) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\n" + Canonical(input)));

    public static string Canonical(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(k)).Append(':');
                    Write(v, sb);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(a[i], sb);
                }
                sb.Append(']');
                break;
            case null:
                sb.Append("null");
                break;
            default:
                sb.Append(node.ToJsonString());
                break;
        }
    }
}

/// <summary>Cómo empieza una escritura con clave.</summary>
public enum McpWriteStart { Run, Replay, Conflict, Busy }

public sealed record McpWriteBegin(McpWriteStart Start, long Id = 0, Guid Attempt = default, string? ResultJson = null);

/// <summary>
/// Spec 044 (H3): claves de idempotencia por conexión (<c>meta.McpWrite</c>) para las escrituras en la copia aislada, cuyo
/// efecto es en otra base. La clave se marca «en curso» con un <i>lease</i> y un intento propio; el resultado solo se guarda si
/// el intento sigue siendo suyo. Si el proceso cae entre el efecto y la marca, al vencer el lease la clave se puede reintentar
/// (el efecto puede repetirse una vez en la copia: R-044-1). <c>entregar</c> no usa esta vía: marca y efecto en una
/// transacción (<see cref="McpProposals"/>).
/// </summary>
public sealed class McpWrites(Db db, McpOptions options)
{
    internal sealed record Row(long Id, string Tool, byte[] InputHash, string Status, string? ResultJson, bool Expired);

    /// <summary>La clave de la conexión, bloqueada (con su rango, si no existe) hasta el fin de <paramref name="tx"/>.</summary>
    internal static async Task<Row?> LockAsync(DbConnection cn, DbTransaction tx, int grantId, string key) =>
        (await cn.QueryAsync<Row>("""
            SELECT Id, Tool, InputHash, Status, ResultJson, CAST(CASE WHEN PendingUntil < SYSUTCDATETIME() THEN 1 ELSE 0 END AS bit) AS Expired
              FROM meta.McpWrite WITH (UPDLOCK, HOLDLOCK) WHERE GrantId = @grantId AND IdemKey = @key
            """, new { grantId, key }, tx)).SingleOrDefault();

    public async Task<McpWriteBegin> BeginAsync(int grantId, string key, string tool, byte[] hash, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var row = await LockAsync(cn, tx, grantId, key);
        var attempt = Guid.NewGuid();
        if (row is null)
        {
            var id = await cn.ExecuteScalarAsync<long>("""
                INSERT meta.McpWrite (GrantId, IdemKey, Tool, InputHash, Status, Attempt, PendingUntil)
                OUTPUT inserted.Id VALUES (@grantId, @key, @tool, @hash, 'pending', @attempt, DATEADD(second, @lease, SYSUTCDATETIME()))
                """, new { grantId, key, tool, hash, attempt, lease = options.WriteLeaseSeconds }, tx);
            await tx.CommitAsync(ct);
            return new(McpWriteStart.Run, id, attempt);
        }
        if (row.Tool != tool || !CryptographicOperations.FixedTimeEquals(row.InputHash, hash)) return new(McpWriteStart.Conflict);
        if (row.Status == "done") return new(McpWriteStart.Replay, row.Id, ResultJson: row.ResultJson);
        if (!row.Expired) return new(McpWriteStart.Busy, row.Id);
        // Fallas parciales: un intento que no terminó (el proceso cayó) y cuyo lease venció: se reintenta con otro intento.
        await cn.ExecuteAsync("UPDATE meta.McpWrite SET Attempt = @attempt, PendingUntil = DATEADD(second, @lease, SYSUTCDATETIME()) WHERE Id = @Id",
            new { row.Id, attempt, lease = options.WriteLeaseSeconds }, tx);
        await tx.CommitAsync(ct);
        return new(McpWriteStart.Run, row.Id, attempt);
    }

    /// <summary>
    /// Guarda el resultado si el intento sigue siendo suyo y (en la copia) la copia sigue viva; si no, suelta la clave. Devuelve
    /// si quedó guardado.
    /// </summary>
    public async Task<bool> FinishAsync(long id, Guid attempt, string resultJson, int? sandboxId)
    {
        await using var cn = await db.OpenAsync();
        var saved = await cn.ExecuteAsync("""
            UPDATE meta.McpWrite SET Status = 'done', ResultJson = @resultJson, PendingUntil = NULL, SandboxId = @sandboxId
             WHERE Id = @id AND Attempt = @attempt AND Status = 'pending'
               AND (@sandboxId IS NULL OR EXISTS (SELECT 1 FROM meta.McpSandbox WHERE Id = @sandboxId))
            """, new { id, attempt, resultJson, sandboxId }) > 0;
        if (!saved) await AbandonAsync(id, attempt);
        return saved;
    }

    /// <summary>
    /// Revisión adversarial 044 (A-01): mientras la llamada corre, su clave sigue «en curso» (el lease se renueva cada tercio,
    /// solo si el intento sigue siendo suyo): un reintento recibe 409 aunque la llamada tarde más que <c>Mcp:WriteLeaseSeconds</c>.
    /// </summary>
    public IAsyncDisposable Keep(long id, Guid attempt) => new Keeper(this, id, attempt, TimeSpan.FromSeconds(Math.Max(1, options.WriteLeaseSeconds / 3)));

    private async Task RenewAsync(long id, Guid attempt, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await cn.ExecuteAsync("UPDATE meta.McpWrite SET PendingUntil = DATEADD(second, @lease, SYSUTCDATETIME()) WHERE Id = @id AND Attempt = @attempt AND Status = 'pending'",
            new { id, attempt, lease = options.WriteLeaseSeconds });
    }

    private sealed class Keeper : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public Keeper(McpWrites owner, long id, Guid attempt, TimeSpan every) => _loop = LoopAsync(owner, id, attempt, every);

        private async Task LoopAsync(McpWrites owner, long id, Guid attempt, TimeSpan every)
        {
            // Spec 045 (auditoría 8, D-09): una falla pasajera no termina la renovación; la siguiente vuelta lo vuelve a intentar.
            try
            {
                using var timer = new PeriodicTimer(every);
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    try { await owner.RenewAsync(id, attempt, _stop.Token); }
                    catch (Exception) when (!_stop.IsCancellationRequested) { /* de mejor esfuerzo, también un tiempo agotado: se reintenta en la siguiente vuelta */ }
                }
            }
            catch (OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            _stop.Dispose();
        }
    }

    /// <summary>H3 CA3: un error que no ejecutó nada no consume la clave.</summary>
    public async Task AbandonAsync(long id, Guid attempt)
    {
        await using var cn = await db.OpenAsync();
        await cn.ExecuteAsync("DELETE meta.McpWrite WHERE Id = @id AND Attempt = @attempt AND Status = 'pending'", new { id, attempt });
    }
}
