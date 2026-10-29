using System.Collections.Concurrent;

namespace Erp.Api.Studio;

/// <summary>
/// Spec 036: avisos dentro del proceso. Despiertan al flujo SSE cuando el trabajador escribe algo (la fuente sigue siendo la
/// base: en otra instancia el flujo relee cada <c>Studio:StreamPollMs</c>) y cortan al instante el turno que la persona
/// detiene si corre aquí (si no, el trabajador lo lee al renovar el lease). Cuenta los flujos abiertos por persona.
/// </summary>
public sealed class StudioSignals
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _waits = new();
    private readonly ConcurrentDictionary<int, (Guid Lease, Action Stop)> _runs = new();
    private readonly ConcurrentDictionary<int, int> _streams = new();

    public void Pulse(int sessionId)
    {
        if (_waits.TryRemove(sessionId, out var tcs)) tcs.TrySetResult();
    }

    /// <summary>Espera un aviso de la sesión o el tiempo indicado (lo que pase primero).</summary>
    public async Task WaitAsync(int sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = _waits.GetOrAdd(sessionId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try { await tcs.Task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { }
    }

    /// <summary>Registra el turno que corre en este proceso; <paramref name="stop"/> lo corta.</summary>
    public IDisposable Run(int sessionId, Guid lease, Action stop)
    {
        var entry = (lease, stop);
        _runs[sessionId] = entry;
        return new Release(() => _runs.TryRemove(new KeyValuePair<int, (Guid, Action)>(sessionId, entry)));
    }

    /// <summary>H5 CA4: corta el turno si corre en este proceso. Devuelve false si no.</summary>
    public bool Stop(int sessionId, Guid lease)
    {
        if (!_runs.TryGetValue(sessionId, out var run) || run.Lease != lease) return false;
        try { run.Stop(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>H5 CA3: reserva un flujo de la persona; null si ya tiene el máximo abierto en esta instancia.</summary>
    public IDisposable? OpenStream(int userId, int max)
    {
        var granted = false;
        _streams.AddOrUpdate(userId, _ => { granted = true; return 1; }, (_, n) => { granted = n < max; return granted ? n + 1 : n; });
        return granted ? new Release(() => _streams.AddOrUpdate(userId, 0, (_, n) => Math.Max(0, n - 1))) : null;
    }

    private sealed class Release(Action action) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) action(); }
    }
}
