using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.IncomeTax;
using static RAQM.Api.Services.TaxJurisdictions.TaxConfigurationService;

namespace RAQM.Api.Services.WithholdingTax;

public sealed record WithholdingAmount(decimal Gross, decimal Withheld, decimal Cash);

public static class WithholdingMeasurement
{
    public static WithholdingAmount Payment(decimal gross, decimal alreadySubject, decimal billNet, decimal rate, decimal threshold, int units)
    {
        CurrencyPolicy.Validate(gross, units);
        CurrencyPolicy.Validate(alreadySubject, units);
        CurrencyPolicy.Validate(threshold, units);
        TradeRules.Rate(rate);
        if (gross <= 0 || gross + alreadySubject > billNet || billNet < threshold)
            throw Bad("Withholding requires an eligible gross settlement within the tax-exclusive bill basis.");
        var withheld = IncomeTaxMeasurement.Measure(gross + alreadySubject, rate, units) -
            IncomeTaxMeasurement.Measure(alreadySubject, rate, units);
        return new(gross, withheld, gross - withheld);
    }
}
