using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using static RAQM.Api.Services.TaxJurisdictions.TaxConfigurationService;

namespace RAQM.Api.Services.IncomeTax;

public sealed record DeferredMeasurement(decimal TemporaryDifference, decimal Taxable, decimal Deductible,
    decimal Liability, decimal Asset, decimal Unrecognized);

public static class IncomeTaxMeasurement
{
    public static decimal Current(decimal externalBasis, decimal adjustments, decimal rate, int units)
    {
        TradeRules.Rate(rate);
        CurrencyPolicy.Validate(externalBasis, units);
        CurrencyPolicy.Validate(Math.Abs(adjustments), units);
        return Measure(Math.Max(0, externalBasis + adjustments), rate, units);
    }

    public static DeferredMeasurement Deferred(decimal carrying, decimal taxBase, bool liability, decimal rate,
        decimal recognizable, int units)
    {
        TradeRules.Rate(rate);
        CurrencyPolicy.Validate(carrying, units);
        CurrencyPolicy.Validate(taxBase, units);
        CurrencyPolicy.Validate(recognizable, units);
        var difference = liability ? taxBase - carrying : carrying - taxBase;
        var taxable = Math.Max(0, difference);
        var deductible = Math.Max(0, -difference);
        if (recognizable > deductible) throw Bad("Recognizable basis cannot exceed the deductible temporary difference.");
        return new(difference, taxable, deductible, Measure(taxable, rate, units), Measure(recognizable, rate, units),
            Measure(deductible, rate, units) - Measure(recognizable, rate, units));
    }

    public static decimal Measure(decimal amount, decimal rate, int units)
    {
        var value = decimal.Round(amount * rate / 100m, units, MidpointRounding.ToEven);
        CurrencyPolicy.Validate(value, units);
        return value;
    }
}
