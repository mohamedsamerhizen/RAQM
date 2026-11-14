using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.Entities.Inventory;

namespace RAQM.Api.Helpers.Inventory;

public static class StockValuation
{
    public static decimal Round(decimal value, int units) => decimal.Round(value, units, MidpointRounding.AwayFromZero);

    public static decimal Share(decimal amount, decimal part, decimal total, int units) =>
        part == total ? amount : Round(amount * (part / total), units);

    public static decimal Quantity(decimal quantity, bool zeroAllowed = false)
    {
        if (quantity < 0 || (!zeroAllowed && quantity == 0) || quantity > 999999999999m || Round(quantity, 6) != quantity)
            throw new ApiException(400, "Use a supported stock quantity with up to six decimal places.", "stock_quantity");
        return quantity;
    }

    public static Guid LotId(Guid operation, int index) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{operation:N}:{index}"))).AsSpan(0, 16));

    public static StockBalance Balance(Guid item, Guid warehouse, IEnumerable<StockMovement> rows) =>
        new(item, warehouse, rows.Sum(x => x.Quantity), rows.Sum(x => x.Reserved), rows.Sum(x => x.Gross), rows.Sum(x => x.Allowance));

    public static IReadOnlyList<LotChange> Lots(IEnumerable<StockMovement> rows)
    {
        var lots = new Dictionary<Guid, LotChange>();
        var arrivalOrder = new List<Guid>();
        foreach (var row in rows.OrderBy(x => x.Sequence).ThenBy(x => x.Number))
        {
            foreach (var delta in JsonSerializer.Deserialize<LotChange[]>(row.LotsJson)!)
            {
                if (lots.TryGetValue(delta.Id, out var prior))
                    lots[delta.Id] = prior with { Quantity = prior.Quantity + delta.Quantity, Cost = prior.Cost + delta.Cost };
                else
                {
                    lots.Add(delta.Id, delta);
                    arrivalOrder.Add(delta.Id);
                }
            }
        }
        return arrivalOrder.Select(id => lots[id]).Where(x => x.Quantity > 0).ToArray();
    }

    public static MovementPlan Take(StockBalance balance, IReadOnlyList<LotChange> lots, decimal quantity, ValuationMethod method,
        int units, Guid? requiredOrigin = null)
    {
        Quantity(quantity);
        if (quantity > balance.Available)
            throw new ApiException(409, "Insufficient unreserved inventory.", "stock_available");
        var remaining = quantity;
        var changes = new List<LotChange>();
        foreach (var lot in lots.Where(x => requiredOrigin is null || x.Origin == requiredOrigin))
        {
            var used = Math.Min(lot.Quantity, remaining);
            changes.Add(lot with { Quantity = -used, Cost = -Share(lot.Cost, used, lot.Quantity, units) });
            remaining -= used;
            if (remaining == 0) break;
        }
        if (remaining > 0)
            throw new ApiException(409, "The eligible stock provenance cannot cover this quantity.", "stock_provenance");
        var gross = method == ValuationMethod.Fifo ? -changes.Sum(x => x.Cost) : Share(balance.Gross, quantity, balance.OnHand, units);
        var allowance = Math.Min(gross, Share(balance.Allowance, quantity, balance.OnHand, units));
        allowance = Math.Max(allowance, balance.Allowance - (balance.Gross - gross));
        return new(balance.WarehouseId, -quantity, -gross, -allowance, 0, changes);
    }

    public static IReadOnlyList<decimal> Allocate(decimal amount, IReadOnlyList<decimal> weights, int units)
    {
        if (weights.Count == 0 || weights.Any(x => x <= 0))
            throw new ApiException(400, "Allocation requires positive weights.", "stock_allocation");
        var remaining = amount;
        var weight = weights.Sum();
        var shares = new decimal[weights.Count];
        for (var i = 0; i < weights.Count; i++)
        {
            shares[i] = Share(remaining, weights[i], weight, units);
            remaining -= shares[i];
            weight -= weights[i];
        }
        return shares;
    }
}
