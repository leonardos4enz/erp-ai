using System.Collections.Concurrent;
using Erp.Api.Security;

namespace Erp.Api.Infrastructure;

/// <summary>
/// Spec 042 (H5, R-041-1 en parte): corridas costosas a la vez por persona. El límite <c>aggregate</c> cuenta peticiones por
/// minuto; esto cuenta las que siguen corriendo (explorar, exportar lo explorado y abrir un tablero personal, que corre una
/// consulta por tarjeta). Una persona no sostiene más de <see cref="Limit"/> a la vez: la siguiente responde 429. El permiso se
/// suelta al terminar, también si la petición se cancela o falla.
/// </summary>
public sealed class CostlyGate(IConfiguration config)
{
    private readonly ConcurrentDictionary<int, int> _running = new();

    public int Limit { get; } = Math.Max(1, config.GetValue("Security:CostlyConcurrentPerUser", 4));

    public IDisposable Enter(UserContext user)
    {
        var now = _running.AddOrUpdate(user.Id, 1, (_, n) => n + 1);
        if (now > Limit)
        {
            Release(user.Id);
            throw new AppException(StatusCodes.Status429TooManyRequests,
                "Tienes demasiadas consultas en curso. Espera a que terminen e intenta de nuevo.");
        }
        return new Lease(this, user.Id);
    }

    /// <summary>Corridas en curso de la persona (para las pruebas).</summary>
    public int Running(int userId) => _running.GetValueOrDefault(userId);

    private void Release(int userId)
    {
        // Quita la entrada al llegar a cero: el diccionario no crece con cada persona que alguna vez corrió algo.
        while (true)
        {
            var n = _running.GetValueOrDefault(userId);
            if (n <= 1)
            {
                if (_running.TryRemove(new KeyValuePair<int, int>(userId, n))) return;
                if (!_running.ContainsKey(userId)) return;
            }
            else if (_running.TryUpdate(userId, n - 1, n)) return;
        }
    }

    private sealed class Lease(CostlyGate gate, int userId) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(userId);
        }
    }
}
