using System.Net;
using RAQM.Api.DTOs.Budgeting;
using RAQM.Api.DTOs.Forecasting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Budgeting;
using RAQM.Api.Helpers.Forecasting;
using Xunit;

namespace RAQM.Tests;

public sealed class BudgetPlanningRulesTests
{
    [Fact]
    public void EqualAnnualAllocationReconcilesToTheApprovedTotal()
    {
        Assert.Equal(Enumerable.Repeat(10_000m, 12), BudgetMath.Allocate(new(Guid.NewGuid(), 120_000, AllocationMethod.Equal), 12, 2));
    }

    [Theory]
    [InlineData(0, 3, 4, 3)]
    [InlineData(2, 3.33, 3.34, 3.33)]
    [InlineData(3, 3.333, 3.334, 3.333)]
    public void WeightedAllocationUsesMinorUnitsAndRetainsEveryResidual(int units, double a, double b, double c)
    {
        var result = BudgetMath.Allocate(new(Guid.NewGuid(), 10, AllocationMethod.Weights, [1, 1, 1]), 3, units);
        Assert.Equal(new[] { (decimal)a, (decimal)b, (decimal)c }, result);
        Assert.Equal(10, result.Sum());
    }

    [Fact]
    public void ExplicitAllocationsRejectInvalidWeightsAndAmounts()
    {
        var account = Guid.NewGuid();
        Assert.Throws<ApiException>(() => BudgetMath.Allocate(new(account, 100, AllocationMethod.Percent, [20, 30, 49]), 3, 2));
        Assert.Throws<ApiException>(() => BudgetMath.Allocate(new(account, 100, AllocationMethod.Manual, [20, 30, 49]), 3, 2));
        Assert.Throws<ApiException>(() => BudgetMath.Allocate(new(account, 100.001m, AllocationMethod.Equal), 3, 2));
        Assert.Equal(new decimal[] { 20, 30, 50 }, BudgetMath.Allocate(new(account, 100, AllocationMethod.Percent, [20, 30, 50]), 3, 2));
    }

    [Fact]
    public void ActualsUseEconomicSignsAndAvoidYearEndAndDepreciationConsumption()
    {
        Assert.Equal(90, BudgetMath.Actual(BudgetKind.Revenue, 10, 100));
        Assert.Equal(90, BudgetMath.Actual(BudgetKind.Operating, 100, 10));
        Assert.Equal(-10, BudgetMath.Favorable(BudgetKind.Revenue, 100, 90));
        Assert.Equal(10, BudgetMath.Favorable(BudgetKind.Operating, 100, 90));
        Assert.False(BudgetMath.Counts(BudgetKind.Operating, "FinancialClose.YearEnd"));
        Assert.False(BudgetMath.Counts(BudgetKind.Capital, "FixedAssets.Depreciation"));
        Assert.True(BudgetMath.Counts(BudgetKind.Capital, "FixedAssets.Acquisition"));
        Assert.True(BudgetMath.Counts(BudgetKind.Inventory, "Inventory.Receipt"));
        Assert.False(BudgetMath.Counts(BudgetKind.Inventory, "Inventory.MatchBill"));
        Assert.False(BudgetMath.Counts(BudgetKind.Inventory, "Inventory.Issue"));
    }

    [Fact]
    public void CashProjectionFindsShortfallBeforeLaterCollectionsRecoverTheMonth()
    {
        var date = new DateOnly(2026, 4, 1);
        var result = ForecastMath.Project(100, [(date, date.AddMonths(1).AddDays(-1))],
            [new("AP", Guid.NewGuid(), "Supplier", date.AddDays(2), -120, false),
             new("AR", Guid.NewGuid(), "Customer", date.AddDays(7), 50, false)]);
        var month = Assert.Single(result);
        Assert.Equal(-20, month.Lowest);
        Assert.Equal(30, month.Closing);
        Assert.True(month.Shortfall);
    }

    [Fact]
    public void ScenarioPreservesBaselineAndAdjustsOperatingResultAndCashSeparately()
    {
        var at = new DateOnly(2026, 3, 31);
        var first = at.AddDays(1);
        var last = first.AddMonths(1).AddDays(-1);
        CashEvent[] events = [new("AR", Guid.NewGuid(), "Customer", first, 100, false), new("PO", Guid.NewGuid(), "Order", first, -70, true)];
        var basis = new ForecastSnapshot("USD", at,
            [new(Guid.NewGuid(), BudgetKind.Revenue, first, last, 100_000, 100_000, 100_000, null),
             new(Guid.NewGuid(), BudgetKind.Operating, first, last, 70_000, 70_000, 70_000, null)], 0,
            events, ForecastMath.Project(0, [(first, last)], events), 5, []);
        var result = ForecastMath.Scenario(basis, new("stress", "DOWN", "Stress", Guid.NewGuid(), -20, 5, 10, 35, "Scenario"), 2);
        Assert.Equal(6_500, result.Rows[0].Amount - result.Rows[1].Amount);
        Assert.Equal(100_000, basis.Rows[0].Amount);
        Assert.Equal(-77, Assert.Single(result.CashBuckets).Closing);
        Assert.Equal(30, Assert.Single(basis.CashBuckets).Closing);
        Assert.Equal(5, result.PostedSequence);
    }

    [Fact]
    public async Task PlanningEndpointsRequireAuthentication()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1");
        using var client = factory.CreateClient();
        var root = $"/api/v1/organizations/{Guid.NewGuid()}/companies/{Guid.NewGuid()}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(root + "/budgets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(root + "/forecasts")).StatusCode);
    }
}
