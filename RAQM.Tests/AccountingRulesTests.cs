using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using Xunit;

namespace RAQM.Tests;
public sealed class AccountingRulesTests
{
    [Fact]
    public void PostingReceiptTimestampUsesStablePostgreSqlMicrosecondPrecision()
    {
        var input = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.FromHours(3)).AddTicks(19);
        var expected = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(10);
        Assert.Equal(expected, AccountingRules.StoredUtcInstant(input));
        Assert.Equal(expected, AccountingRules.StoredUtcInstant(expected));
    }
    [Theory]
    [InlineData("IQD", 3, "123.456")]
    [InlineData("USD", 2, "123.45")]
    [InlineData("JPY", 0, "123")]
    [InlineData("KWD", 3, "0.001")]
    [InlineData("EUR", 2, "0.01")]
    public void SupportedCurrencyUnitsAreExplicit(string currency, int units, string value)
    {
        Assert.Equal(units, CurrencyPolicy.Units(currency));
        CurrencyPolicy.Validate(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), units);
    }

    [Theory]
    [InlineData("USD", "0.001")]
    [InlineData("IQD", "0.0001")]
    [InlineData("JPY", "1.1")]
    [InlineData("USD", "-1")]
    [InlineData("IQD", "10000000000000000")]
    public void InvalidMoneyIsRejectedWithoutImplicitRounding(string currency, string amount) => Assert.Equal("amount_precision", Assert.Throws<ApiException>(() => CurrencyPolicy.Validate(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), CurrencyPolicy.Units(currency))).Code);
    [Theory]
    [InlineData("ZZZ")]
    [InlineData("usd")]
    [InlineData("XXX")]
    public void UnsupportedPolicyCurrencyFailsClosed(string currency) => Assert.Equal("currency_unsupported", Assert.Throws<ApiException>(() => CurrencyPolicy.Units(currency)).Code);
    [Theory]
    [InlineData(AccountClass.Asset, BalanceSide.Debit)]
    [InlineData(AccountClass.Expense, BalanceSide.Debit)]
    [InlineData(AccountClass.Liability, BalanceSide.Credit)]
    [InlineData(AccountClass.Equity, BalanceSide.Credit)]
    [InlineData(AccountClass.Revenue, BalanceSide.Credit)]
    public void NormalAndContraBalancesFollowClassification(AccountClass type, BalanceSide expected)
    {
        Assert.Equal(expected, AccountingRules.Normal(type, false));
        Assert.NotEqual(expected, AccountingRules.Normal(type, true));
    }

    [Fact]
    public void BalanceUsesExactDecimalAndRejectsMixedSides()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal((0.3m, 0.3m), AccountingRules.Lines([new(a, 0.1m, 0, null), new(a, 0.2m, 0, null), new(b, 0, 0.3m, null)], "USD", true));
        Assert.Equal("journal_unbalanced", Assert.Throws<ApiException>(() => AccountingRules.Lines([new(a, 1, 0, null), new(b, 0, 0.99m, null)], "USD", true)).Code);
        Assert.Equal("line_sides", Assert.Throws<ApiException>(() => AccountingRules.Lines([new(a, 1, 1, null)], "USD", false)).Code);
        Assert.Equal("line_sides", Assert.Throws<ApiException>(() => AccountingRules.Lines([new(a, 0, 0, null)], "USD", false)).Code);
    }

    [Fact]
    public void ParentGraphRejectsSelfIndirectCyclesAndForeignParents()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var map = new Dictionary<Guid, Guid?>
        {
            [a] = null,
            [b] = a,
            [c] = b
        };
        AccountingRules.Hierarchy(c, b, map);
        Assert.Equal(3, AccountingRules.Descendants(a, map).Count);
        Assert.Single(AccountingRules.Descendants(c, map));
        Assert.Equal("account_cycle", Assert.Throws<ApiException>(() => AccountingRules.Hierarchy(a, c, map)).Code);
        Assert.Throws<ApiException>(() => AccountingRules.Hierarchy(a, a, map));
        Assert.Equal("account_parent", Assert.Throws<ApiException>(() => AccountingRules.Hierarchy(a, Guid.NewGuid(), map)).Code);
    }

    [Fact]
    public void CalendarHandlesLeapYearsAndNonJanuaryBoundaries()
    {
        var leap = FiscalCalendar.Generate(new(2024, 1, 1));
        Assert.Equal(new DateOnly(2024, 2, 29), leap[1].End);
        Assert.Equal(new DateOnly(2024, 12, 31), leap[11].End);
        var april = FiscalCalendar.Generate(new(2024, 4, 1));
        Assert.Equal(new DateOnly(2025, 3, 31), april[11].End);
        var late = FiscalCalendar.Generate(new(2024, 1, 31));
        Assert.Equal(new DateOnly(2024, 2, 28), late[0].End);
        Assert.Equal(new DateOnly(2024, 2, 29), late[1].Start);
        Assert.Equal(new DateOnly(2025, 1, 30), late[11].End);
        Assert.Equal(12, april.Count);
        for (var i = 1; i < late.Count; i++)
            Assert.Equal(late[i - 1].End.AddDays(1), late[i].Start);
    }

    [Fact]
    public void FinancialFingerprintIgnoresKeyAndDecimalRepresentationButNotFinancialIntent()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var request = new PostingRequest(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), "Sales", Guid.NewGuid(), "key1", new(2026, 1, 1), "USD", [new(a, 10.0m, 0, null), new(b, 0, 10.00m, null)], null, new(2026, 1, 1), "Sale");
        var retry = request with
        {
            IdempotencyKey = "key2",
            Lines = [new(a, 10.000m, 0, null), new(b, 0, 10m, null)]
        };
        Assert.Equal(AccountingRules.Fingerprint(request), AccountingRules.Fingerprint(retry));
        Assert.NotEqual(AccountingRules.Fingerprint(request), AccountingRules.Fingerprint(request with { AccountingDate = new(2026, 1, 2) }));
        Assert.NotEqual(AccountingRules.Fingerprint(request), AccountingRules.Fingerprint(request with { SourceDocumentId = Guid.NewGuid() }));
    }
}
