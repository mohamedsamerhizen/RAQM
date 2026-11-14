using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Helpers.Accounting;

namespace RAQM.Api.Helpers.MultiCurrency;

public static class FxMath
{
    public static ApiException Bad(string detail) => new(400, detail, "fx_input");
    public static ApiException Conflict(string detail) => new(409, detail, "fx_state");
    public static ApiException Missing(string detail) => new(404, detail, "fx_missing");
    public static decimal Round(decimal value, int units) => decimal.Round(value, units, MidpointRounding.ToEven);
    public static void Rate(decimal rate)
    {
        if (rate <= 0 || rate > 9999999999.9999999999m || Round(rate, 10) != rate)
            throw Bad("Rate must be positive and fit numeric(20,10).");
    }
    public static decimal Convert(decimal foreign, decimal rate, int functionalUnits)
    {
        Rate(rate);
        if (foreign < 0 || foreign > CurrencyPolicy.Maximum || foreign > CurrencyPolicy.Maximum / rate + 1)
            throw Bad("Conversion exceeds the supported monetary range.");
        var result = Round(foreign * rate, functionalUnits);
        CurrencyPolicy.Validate(result, functionalUnits);
        return result;
    }
    public static decimal Relief(decimal foreign, decimal remainingForeign, decimal carrying, int units)
    {
        if (foreign <= 0 || foreign > remainingForeign || carrying < 0)
            throw Conflict("Settlement exceeds available foreign units or has invalid carrying value.");
        return foreign == remainingForeign ? carrying : Round(carrying * (foreign / remainingForeign), units);
    }
    public static decimal[] Split(decimal total, IReadOnlyList<decimal> weights, int units)
    {
        var remainingWeight = weights.Sum();
        if (total < 0 || weights.Any(x => x < 0) || (total != 0 && remainingWeight == 0)) throw Bad("Invalid conversion allocation.");
        var result = new decimal[weights.Count];
        for (var i = 0; i < weights.Count; i++)
        {
            var weight = weights[i];
            if (weight == 0) continue;
            result[i] = weight == remainingWeight ? total : Round(total * (weight / remainingWeight), units);
            total -= result[i];
            remainingWeight -= weight;
        }
        return result;
    }
    public static int Sign(FxPositionKind kind) => kind is FxPositionKind.Payable or FxPositionKind.CustomerAdvance or FxPositionKind.CustomerRefund ? -1 : 1;
    public static bool Monetary(FxPositionKind kind) => kind is not (FxPositionKind.CustomerAdvance or FxPositionKind.SupplierAdvance);
    public static decimal SettlementGain(FxPositionKind kind, decimal cashBase, decimal carryingRelieved) =>
        Sign(kind) * (cashBase - carryingRelieved);
}
