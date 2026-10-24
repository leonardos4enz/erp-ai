using System.Security.Cryptography;
using System.Text;
using Erp.Api.Infrastructure;
using Erp.Api.Metadata;

namespace Erp.Api.Data;

/// <summary>Capa de costo: cantidad y valor que le quedan. <c>Id</c> nulo = capa nueva.</summary>
public sealed record CostLayerState(long? Id, decimal Quantity, decimal Value);

/// <summary>Costo del movimiento (con signo), costo unitario efectivo (sin signo) y capas de la llave después de aplicarlo.</summary>
public sealed record CostResult(decimal Cost, decimal UnitCost, IReadOnlyList<CostLayerState> Layers)
{
    public decimal Remaining => Layers.Sum(l => l.Value);
}

/// <summary>
/// Spec 020 · costeo por capas, puro para probarlo sin base de datos (ver plan 020 §2). Promedio = una sola capa abierta
/// por llave; FIFO = capas en orden de llegada. Cada consumo se redondea una vez a la escala del costo y se descuenta exacto
/// de su capa, así la suma de los costos y el valor de las capas no se separan.
/// </summary>
public static class CostMath
{
    public static CostResult Apply(IReadOnlyList<CostLayerState> open, string method, decimal quantity, decimal? unitCost,
        decimal? fallbackCost, int scale)
    {
        if (!CostMethods.All.Contains(method))
            throw new AppException(400, $"Método de costeo '{method}' no válido ({string.Join(", ", CostMethods.All)}).");
        if (quantity == 0) return new(0m, 0m, open);

        if (quantity > 0)
        {
            var q = open.Sum(l => l.Quantity);
            var v = open.Sum(l => l.Value);
            // Sin costo: al promedio vigente de la llave, para no mover el costo de lo que ya había.
            var unit = unitCost ?? (q > 0 ? v / q : 0m);
            if (unit < 0) throw new AppException(400, "El costo unitario no puede ser negativo.");
            var cost = Round(quantity * unit, scale);
            IReadOnlyList<CostLayerState> layers = method == CostMethods.Fifo
                ? [.. open, new(null, quantity, cost)]
                : [.. open.Select(l => l with { Quantity = 0m, Value = 0m }), new(null, q + quantity, v + cost)];
            return new(cost, cost / quantity, layers);
        }

        var need = -quantity;
        var consumed = 0m;
        var after = new List<CostLayerState>(open.Count);
        foreach (var l in open)
        {
            if (need <= 0 || l.Quantity <= 0) { after.Add(l); continue; }
            var take = Math.Min(need, l.Quantity);
            var value = take == l.Quantity ? l.Value : Round(l.Value * take / l.Quantity, scale);
            after.Add(l with { Quantity = l.Quantity - take, Value = l.Value - value });
            consumed += value;
            need -= take;
        }
        // Lo que las capas no cubren (p. ej. existencias anteriores al costeo) sale al costo de respaldo; no hay capas negativas.
        if (need > 0) consumed += Round(need * Math.Max(fallbackCost ?? 0m, 0m), scale);
        return new(-consumed, consumed / -quantity, after);
    }

    /// <summary>Costo unitario de las siguientes <paramref name="quantity"/> unidades que saldrían (nulo sin capas).</summary>
    public static decimal? PeekUnitCost(IReadOnlyList<CostLayerState> open, decimal quantity, int scale = 2)
    {
        if (quantity <= 0) return null;
        var need = quantity;
        decimal covered = 0m, value = 0m;
        foreach (var l in open.Where(l => l.Quantity > 0))
        {
            if (need <= 0) break;
            var take = Math.Min(need, l.Quantity);
            value += take == l.Quantity ? l.Value : Round(l.Value * take / l.Quantity, scale);
            covered += take;
            need -= take;
        }
        return covered > 0 ? value / covered : null;
    }

    /// <summary>Llave de las capas: la misma normalización que el bloqueo del acumulado por coincidencia, más la empresa.</summary>
    public static string KeyHash(EntityDef book, int? companyId, IEnumerable<object> values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{RecordService.RollupLockKey(book, values)}|empresa:{companyId?.ToString() ?? "-"}")));

    private static decimal Round(decimal v, int scale) => decimal.Round(v, scale, MidpointRounding.AwayFromZero);
}
