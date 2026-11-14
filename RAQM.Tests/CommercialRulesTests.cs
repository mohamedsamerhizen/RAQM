using Microsoft.Extensions.Configuration;
using RAQM.Api.Entities.SaaS;
using RAQM.Api.Helpers;
using RAQM.Api.Services.SaaS;
using Xunit;

namespace RAQM.Tests;

public sealed class CommercialRulesTests
{
    [Theory]
    [InlineData(2023, 1, 31, 1, CommercialInterval.Monthly, 2023, 2, 28)]
    [InlineData(2024, 1, 31, 2, CommercialInterval.Monthly, 2024, 3, 31)]
    [InlineData(2024, 2, 29, 1, CommercialInterval.Annual, 2025, 2, 28)]
    [InlineData(2024, 2, 29, 4, CommercialInterval.Annual, 2028, 2, 29)]
    public void BillingBoundariesKeepTheOriginalAnchor(int y, int m, int d, int cycle, CommercialInterval interval,
        int expectedYear, int expectedMonth, int expectedDay) =>
        Assert.Equal(new DateOnly(expectedYear, expectedMonth, expectedDay), CommercialRules.Boundary(new(y, m, d), cycle, interval));

    [Fact]
    public void HalfPeriodUpgradeRetainsCreditAndAdditionalCharge()
    {
        var current = new CommercialPlan { Price = 100m, Currency = "USD" };
        var next = new CommercialPlan { Price = 200m, Currency = "USD" };
        var lines = CommercialRules.Prorate(current, next, 1, new(2026, 4, 1), new(2026, 5, 1), new(2026, 4, 16));
        Assert.Equal(new[] { -50m, 100m }, lines.Select(x => x.Amount));
        Assert.Equal(50m, lines.Sum(x => x.Amount));
        next.Currency = "EUR";
        Assert.Throws<ApiException>(() => CommercialRules.Prorate(current, next, 1, new(2026, 4, 1), new(2026, 5, 1), new(2026, 4, 16)));
    }

    [Fact]
    public void SeatSnapshotBillsOnlyUsageAboveIncludedQuantity()
    {
        var plan = new CommercialPlan { Price = 99.90m, ExtraSeatPrice = 3.25m, IncludedSeats = 5, Currency = "USD" };
        var lines = CommercialRules.Charges(plan, 8);
        Assert.Equal(3, lines[1].Quantity);
        Assert.Equal(9.75m, lines[1].Amount);
        Assert.Equal(109.65m, lines.Sum(x => x.Amount));
        Assert.Equal(99.90m, CommercialRules.Charges(plan, 2).Sum(x => x.Amount));
    }

    [Fact]
    public void DraftPaymentDoesNotSettleAndVerifiedRefundRestoresBalance()
    {
        var now = DateTimeOffset.UtcNow;
        CommercialMovement[] records =
        [
            new() { Kind = CommercialMovementKind.Payment, Amount = 80, VerifiedAt = now },
            new() { Kind = CommercialMovementKind.Payment, Amount = 20 },
            new() { Kind = CommercialMovementKind.Refund, Amount = 15, VerifiedAt = now },
            new() { Kind = CommercialMovementKind.Credit, Amount = 5, VerifiedAt = now }
        ];
        Assert.Equal(30m, CommercialRules.Outstanding(100m, records));
    }

    [Fact]
    public void TrialAndGraceExpireAtTheirExclusiveInstant()
    {
        var cutoff = CommercialRules.Midnight(new(2026, 11, 30));
        var sub = new CommercialSubscription { Trial = true, AccessUntil = cutoff };
        Assert.Equal("trialing", CommercialRules.Status(sub, cutoff.AddSeconds(-1), 0m));
        Assert.Equal("expired", CommercialRules.Status(sub, cutoff, 0m));
        sub.Trial = false;
        Assert.Equal("grace", CommercialRules.Status(sub, cutoff.AddSeconds(-1), 20m));
        Assert.Equal("past-due", CommercialRules.Status(sub, cutoff, 20m));
    }

    [Fact]
    public void OperatorPermissionIsExplicitAndSeparatedByCapability()
    {
        var operatorId = Guid.NewGuid();
        var authority = new PlatformAuthority(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["SaaS:Operators:Catalog:0"] = operatorId.ToString("D") }).Build());
        Assert.True(authority.Allows(operatorId, "Catalog"));
        Assert.False(authority.Allows(operatorId, "Billing"));
        Assert.Throws<ApiException>(() => authority.Require(Guid.NewGuid(), "Catalog"));
    }
}
