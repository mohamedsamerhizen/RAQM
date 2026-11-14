using RAQM.Api.DTOs.TaxAccounting;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Helpers;
using RAQM.Api.Services.IncomeTax;
using RAQM.Api.Services.RegulatoryDocuments;
using RAQM.Api.Services.TaxCalculation;
using RAQM.Api.Services.WithholdingTax;
using Xunit;

namespace RAQM.Tests;

public sealed class TaxAccountingRulesTests
{
    [Theory]
    [InlineData(1000, false)]
    [InlineData(1100, true)]
    public void StandardTaxPreservesIndependentNetAndGross(int price, bool inclusive)
    {
        var line = new TaxPricingLine("Service", 1, price, 0, inclusive, "SERVICES");
        var rule = new TaxRule { Treatment = TaxTreatment.Standard, RateId = Guid.NewGuid() };
        var result = TaxCalculationService.Price(1, line, [(rule, 10m)], "USD", null);
        Assert.Equal((1000m, 100m, 1100m), (result.Net, result.Tax, result.Total));
        Assert.Equal(rule.RateId, Assert.Single(result.Components).RateId);
    }

    [Fact]
    public void MultipleRatesSplitRoundedTaxWithoutDroppingResidualCents()
    {
        var first = new TaxRule { Order = 1, Treatment = TaxTreatment.Standard };
        var second = new TaxRule { Order = 2, Treatment = TaxTreatment.Standard };
        var line = new TaxPricingLine("Small", 1, 0.15m, 0, false, "TEST");
        var rows = new[] { 1, 2, 3 }.Select(i => TaxCalculationService.Price(i, line, [(first, 5m), (second, 5m)], "USD", null)).ToArray();
        Assert.All(rows, x => Assert.Equal(new[] { 0.01m, 0.01m }, x.Components.Select(c => c.Amount)));
        Assert.Equal(0.45m, rows.Sum(x => x.Net));
        Assert.Equal(0.06m, rows.Sum(x => x.Tax));
        Assert.Equal(0.51m, rows.Sum(x => x.Total));
    }

    [Fact]
    public void ExemptionAndZeroRatingHaveDifferentEvidenceAtTheSameTotal()
    {
        var item = new TaxPricingLine("Goods", 1, 50m, 0, false, "GOODS");
        var zero = new TaxRule { Treatment = TaxTreatment.ZeroRated, RateId = Guid.NewGuid() };
        var exempt = new TaxRule { Treatment = TaxTreatment.Exempt };
        var zeroQuote = TaxCalculationService.Price(1, item, [(zero, 0m)], "USD", null);
        var exemptQuote = TaxCalculationService.Price(1, item, [(exempt, 0m)], "USD", "Certificate");
        Assert.Equal(50m, zeroQuote.Total);
        Assert.Equal(zeroQuote.Total, exemptQuote.Total);
        Assert.NotNull(zeroQuote.Components.Single().RateId);
        Assert.Null(exemptQuote.Components.Single().RateId);
        Assert.Equal("Certificate", exemptQuote.ExemptionEvidence);
        Assert.Throws<ApiException>(() => TaxCalculationService.Price(1, item, [(zero, 0m), (exempt, 0m)], "USD", "Certificate"));
    }

    [Fact]
    public void PaymentStageWithholdingConservesCumulativeRoundingAndCash()
    {
        Assert.Equal(new WithholdingAmount(0.05m, 0m, 0.05m),
            WithholdingMeasurement.Payment(0.05m, 0m, 0.15m, 10m, 0m, 2));
        Assert.Equal(new WithholdingAmount(0.10m, 0.02m, 0.08m),
            WithholdingMeasurement.Payment(0.10m, 0.05m, 0.15m, 10m, 0m, 2));
        Assert.Equal(new WithholdingAmount(1000, 50, 950), WithholdingMeasurement.Payment(1000, 0, 1000, 5, 1000, 2));
        var partial = new[] { WithholdingMeasurement.Payment(0.33m, 0m, 1m, 5m, 0, 2),
            WithholdingMeasurement.Payment(0.33m, 0.33m, 1m, 5m, 0, 2), WithholdingMeasurement.Payment(0.34m, 0.66m, 1m, 5m, 0, 2) };
        Assert.Equal(new[] { 0.02m, 0.01m, 0.02m }, partial.Select(x => x.Withheld));
        Assert.Equal(0.95m, partial.Sum(x => x.Cash));
        Assert.Throws<ApiException>(() => WithholdingMeasurement.Payment(200, 850, 1000, 5, 0, 2));
    }

    [Fact]
    public void CurrentAndDeferredTaxUseExplicitBasesAndLiabilitySign()
    {
        Assert.Equal(250m, IncomeTaxMeasurement.Current(900m, 100m, 25m, 2));
        var taxable = IncomeTaxMeasurement.Deferred(1500m, 1000m, false, 25m, 0m, 2);
        Assert.Equal(125m, taxable.Liability);
        var deductible = IncomeTaxMeasurement.Deferred(1000m, 400m, true, 25m, 300m, 2);
        Assert.Equal((-600m, 75m, 75m), (deductible.TemporaryDifference, deductible.Asset, deductible.Unrecognized));
        Assert.Throws<ApiException>(() => IncomeTaxMeasurement.Deferred(1000m, 400m, true, 25m, 601m, 2));
    }

    [Fact]
    public void StructuredInvoiceRejectsBrokenTaxConservationOrCreditReference()
    {
        var line = new StructuredTaxLine(1, Guid.NewGuid(), null, "Service", 1000, 100, 1100, Guid.NewGuid(), TaxTreatment.Standard, Guid.NewGuid());
        var invoice = new StructuredTaxInvoice(Guid.NewGuid(), Guid.NewGuid(), "INV-1", new(2025, 1, 1), "USD", "Seller",
            "SELLER-TAX-ID", "US", "Buyer", "BUYER-TAX-ID", null, null, 1000, 100, 1100, [line]);
        var schema = new RaqmInvoiceSchema();
        schema.Validate(invoice);
        Assert.Throws<ApiException>(() => schema.Validate(invoice with { Total = 1099 }));
        Assert.Throws<ApiException>(() => schema.Validate(invoice with { OriginalDocumentId = Guid.NewGuid() }));
    }
}
