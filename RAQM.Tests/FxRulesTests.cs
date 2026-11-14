using System.Net;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.MultiCurrency;
using RAQM.Api.Helpers.FinancialReporting;
using Xunit;

namespace RAQM.Tests;

public sealed class FxRulesTests
{
    [Theory]
    [InlineData("JPY", 0)]
    [InlineData("USD", 2)]
    [InlineData("IQD", 3)]
    public void CurrencyCatalogPreservesExistingMinorUnits(string currency, int units) => Assert.Equal(units, CurrencyPolicy.Units(currency));

    [Fact]
    public void ConversionsUseBasePerForeignAndBankersRounding()
    {
        Assert.Equal(1100m, FxMath.Convert(1000, 1.10m, 2));
        Assert.Equal(1m, FxMath.Convert(10, 0.146m, 0));
        Assert.Equal(123.456m, FxMath.Convert(100, 1.234565m, 3));
        Assert.Equal(123.458m, FxMath.Convert(100, 1.234575m, 3));
    }

    [Fact]
    public void InvalidRateAndExcessiveConversionAreRejected()
    {
        Assert.Throws<ApiException>(() => FxMath.Rate(0));
        Assert.Throws<ApiException>(() => FxMath.Rate(-1.1m));
        Assert.Throws<ApiException>(() => FxMath.Rate(1.12345678901m));
        Assert.Throws<ApiException>(() => FxMath.Convert(9_999_999_999m, 9_999_999_999m, 2));
        FxMath.Rate(0.0000000001m);
    }

    [Fact]
    public void FinalReliefConsumesExactResidual()
    {
        var first = FxMath.Relief(1, 3, 0.05m, 2);
        var second = FxMath.Relief(1, 2, 0.05m - first, 2);
        var final = FxMath.Relief(1, 1, 0.05m - first - second, 2);
        Assert.Equal((0.02m, 0.02m, 0.01m), (first, second, final));
        Assert.Throws<ApiException>(() => FxMath.Relief(4, 3, 0.05m, 2));
        Assert.Equal(new[] { 0.02m, 0.02m, 0.01m }, FxMath.Split(0.05m, [1, 1, 1], 2));
    }

    [Fact]
    public void SettlementAfterMultipleClosingsUsesTheCurrentCarryingBasis()
    {
        var initial = FxMath.Convert(100, 1.10m, 2);
        var relief = FxMath.Relief(60, 100, initial, 2);
        var firstCash = FxMath.Convert(60, 1.20m, 2);
        var firstGain = FxMath.SettlementGain(FxPositionKind.Receivable, firstCash, relief);
        var closeOne = FxMath.Convert(40, 1.15m, 2);
        var closeTwo = FxMath.Convert(40, 1.25m, 2);
        var finalCash = FxMath.Convert(40, 1.30m, 2);
        var finalGain = FxMath.SettlementGain(FxPositionKind.Receivable, finalCash, FxMath.Relief(40, 40, closeTwo, 2));
        Assert.Equal(66m, relief);
        Assert.Equal(6m, firstGain);
        Assert.Equal(2m, closeOne - (initial - relief));
        Assert.Equal(4m, closeTwo - closeOne);
        Assert.Equal(2m, finalGain);
        Assert.Equal(14m, firstGain + finalGain + closeTwo - (initial - relief));
        Assert.Equal(124m, firstCash + finalCash);
    }

    [Fact]
    public void PayableAndRefundDirectionsAreOppositeTheReceivable()
    {
        Assert.Equal(-10m, FxMath.SettlementGain(FxPositionKind.Payable, 120, 110));
        Assert.Equal(-10m, FxMath.SettlementGain(FxPositionKind.CustomerRefund, 120, 110));
        Assert.Equal(10m, FxMath.SettlementGain(FxPositionKind.SupplierRefund, 120, 110));
        Assert.False(FxMath.Monetary(FxPositionKind.CustomerAdvance));
        Assert.False(FxMath.Monetary(FxPositionKind.SupplierAdvance));
        Assert.True(FxMath.Monetary(FxPositionKind.Bank));
    }

    [Fact]
    public void CashReportSeparatesExchangeEffectFromBankFeeAndTransfers()
    {
        var date = new DateOnly(2025, 1, 1);
        var functional = new LedgerAccount { Code = "1100", Name = "Functional bank", Classification = AccountClass.Asset, IsPosting = true };
        var foreign = new LedgerAccount { Code = "1110", Name = "Foreign bank", Classification = AccountClass.Asset, IsPosting = true };
        var buy = Guid.NewGuid();
        var revaluation = Guid.NewGuid();
        var sale = Guid.NewGuid();
        FinancialLine Line(Guid journal, Guid account, string source, decimal debit, decimal credit) =>
            new(Guid.NewGuid(), journal, account, date, 1, 1, source, null, null, debit, credit, null);
        FinancialLine[] lines =
        [
            Line(buy, functional.Id, "FX.BankPurchase", 0, 112), Line(buy, foreign.Id, "FX.BankPurchase", 110, 0),
            Line(revaluation, foreign.Id, "FX.Revaluation", 10, 0),
            Line(sale, functional.Id, "FX.BankSale", 52, 0), Line(sale, foreign.Id, "FX.BankSale", 0, 48)
        ];
        var result = FinancialMath.Build(ReportKind.CashMovement, new(date, date), date, [functional, foreign], lines,
            new Dictionary<Guid, StatementGroup>(), new HashSet<Guid> { functional.Id, foreign.Id }, new Dictionary<string, CashGroup>(),
            new Dictionary<Guid, decimal> { [buy] = -2, [revaluation] = 0, [sale] = 0 });
        Assert.Equal(0m, result.Difference);
        Assert.Equal(2m, result.Rows.Single(x => x.Key == "cash:out").Amount);
        Assert.Equal(14m, result.Rows.Single(x => x.Key == "cash:fx-effect").Amount);
        Assert.Equal(12m, result.Rows.Single(x => x.Key == "cash:closing").Amount);
    }

    [Fact]
    public async Task ForeignExchangeRequiresAuthentication()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/organizations/{Guid.NewGuid()}/companies/{Guid.NewGuid()}/foreign-exchange/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
