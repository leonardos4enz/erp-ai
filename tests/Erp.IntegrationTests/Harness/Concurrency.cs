using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Erp.IntegrationTests.Harness;

/// <summary>Spec 030 (H1 CA2): peticiones a la vez y las invariantes que deben sobrevivirles.</summary>
public static class Concurrency
{
    /// <summary>
    /// Lanza <paramref name="n"/> llamadas a la vez: cada una espera en una barrera hasta que todas están listas, para que
    /// ninguna termine antes de que la última empiece (con un <c>for</c> y <c>await</c> la carrera nunca ocurre).
    /// </summary>
    public static async Task<T[]> Concurrently<T>(int n, Func<int, Task<T>> fn)
    {
        using var ready = new CountdownEvent(n);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, n).Select(i => Task.Run(async () =>
        {
            ready.Signal();
            await go.Task;
            return await fn(i);
        })).ToArray();
        // Revisión adversarial 030 (F-I): si no llegaron todas a la barrera, no hubo carrera que probar.
        if (!ready.Wait(TimeSpan.FromSeconds(30))) { go.SetResult(); throw new TimeoutException($"No arrancaron las {n} llamadas a la vez."); }
        go.SetResult();
        return await Task.WhenAll(tasks);
    }

    /// <summary>Espera hasta que <paramref name="ok"/> se cumpla (bandejas asíncronas), o falla con <paramref name="what"/>.</summary>
    public static async Task<T> WaitForAsync<T>(Func<Task<T>> fn, Func<T, bool> ok, string what, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var v = await fn();
            if (ok(v)) return v;
            if (DateTime.UtcNow > until) throw new TimeoutException($"No se cumplió: {what} (último valor: {v})");
            await Task.Delay(250);
        }
    }
}

/// <summary>Aserciones de invariante con un mensaje que dice qué se rompió.</summary>
public static class Invariant
{
    public static void AtMost(decimal actual, decimal limit, string what) =>
        Assert.True(actual <= limit, $"Invariante rota: {what} = {actual} > {limit}");

    public static void NonNegative(decimal actual, string what) =>
        Assert.True(actual >= 0, $"Invariante rota: {what} = {actual} < 0");

    public static void ExactlyOnce(int count, string what) =>
        Assert.True(count == 1, $"Invariante rota: {what} ocurrió {count} veces (se esperaba una)");

    /// <summary>Sin repetidos y, si <paramref name="consecutive"/>, sin huecos.</summary>
    public static void Unique(IEnumerable<long> values, string what, bool consecutive = false)
    {
        var list = values.Order().ToList();
        var dup = list.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dup.Count == 0, $"Invariante rota: {what} repetidos: {string.Join(", ", dup)}");
        if (consecutive && list.Count > 0)
            Assert.True(list[^1] - list[0] == list.Count - 1, $"Invariante rota: {what} con huecos entre {list[0]} y {list[^1]} ({list.Count} valores)");
    }

    /// <summary>Estados de respuesta como multiconjunto ordenado (p. ej. un 200 y nueve 400).</summary>
    public static void Statuses(IEnumerable<Res> results, params int[] expected)
    {
        var actual = results.Select(r => r.Status).Order().ToArray();
        Assert.True(actual.SequenceEqual(expected.Order()),
            $"Estados [{string.Join(", ", actual)}], se esperaba [{string.Join(", ", expected.Order())}]. Inesperados: " +
            string.Join(" | ", results.Where(r => !expected.Contains(r.Status)).Select(r => r.ToString()).Distinct().Take(3)));
    }

    public static decimal Dec(JsonNode? n) => n?.GetValue<decimal>() ?? 0m;
}

/// <summary>
/// Spec 030 (H1 CA3): fallas entre dos pasos. Cada una se deshace al salir del <c>await using</c>, aunque la prueba falle.
/// </summary>
public static class Faults
{
    /// <summary>
    /// Una restricción CHECK temporal hace fallar la escritura que la viola (la técnica de la 021 para
    /// <c>meta.AutomationRun</c>: una restricción y no un trigger, porque el reclamo usa UPDATE … OUTPUT).
    /// </summary>
    public static async Task<IAsyncDisposable> FailWhenAsync(this ErpHost host, string table, string violates)
    {
        var name = "CK_IT_Falla_" + Guid.NewGuid().ToString("N")[..8];
        await host.ExecAsync($"ALTER TABLE {table} WITH NOCHECK ADD CONSTRAINT {name} CHECK (NOT ({violates}))");
        // Revisión adversarial 030 (F-A): OBJECT_ID con el nombre solo buscaba en dbo y la restricción de meta.* se quedaba.
        return new Undo(() => host.ExecAsync($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS {name}"));
    }

    /// <summary>
    /// Otra conexión ejecuta <paramref name="sql"/> dentro de una transacción sin confirmar (retiene filas o claves únicas)
    /// hasta que se suelta: deja a la API detenida justo en el paso que se quiere observar, sin depender de tiempos.
    /// </summary>
    public static async Task<Hold> HoldAsync(this ErpHost host, string sql)
    {
        // Síncrono, como ErpHost.ScalarAsync (E/S de canalizaciones con nombre y salida de hilos).
        return await Task.Run(() =>
        {
            var cn = new SqlConnection(host.ConnectionString);
            cn.Open();
            var tx = cn.BeginTransaction();
            try
            {
                using (var cmd = new SqlCommand(sql, cn, tx)) cmd.ExecuteNonQuery();
                using var spid = new SqlCommand("SELECT @@SPID", cn, tx);
                return new Hold(host, Convert.ToInt32(spid.ExecuteScalar()), () => Task.Run(() => { tx.Rollback(); tx.Dispose(); cn.Dispose(); }));
            }
            catch
            {
                tx.Dispose();
                cn.Dispose();
                throw;
            }
        });
    }

    /// <summary>Lo que retiene otra conexión; <see cref="WaitUntilBlockingAsync"/> espera a que la API quede detenida en ello.</summary>
    public sealed class Hold(ErpHost host, int spid, Func<Task> undo) : IAsyncDisposable
    {
        private bool _done;

        /// <summary>
        /// Revisión adversarial 030 (F-C): en lugar de esperar un tiempo fijo, espera a que alguna petición esté bloqueada por
        /// esta conexión (la API llegó al paso que se quiere observar).
        /// </summary>
        public Task WaitUntilBlockingAsync() => Concurrency.WaitForAsync(
            () => host.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = {spid}"), n => n > 0,
            $"que una petición quede detenida por la sesión {spid}");

        public async ValueTask DisposeAsync()
        {
            if (_done) return;
            _done = true;
            await undo();
        }
    }

    private sealed class Undo(Func<Task> undo) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await undo();
    }
}
