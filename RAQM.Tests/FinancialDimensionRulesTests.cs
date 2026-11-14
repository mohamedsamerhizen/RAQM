using System.Net;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.FinancialDimensions;
using RAQM.Api.Services.AnalyticalReporting;
using RAQM.Api.Services.ProjectAccounting;
using Xunit;

namespace RAQM.Tests;

public sealed class FinancialDimensionRulesTests
{
    [Fact]
    public void ExplicitDefaultFixedAndUnassignedHaveDistinctPrecedence()
    {
        var definition = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var global = new DimensionRule { DefaultValueId = Guid.NewGuid(), Mode = DimensionMode.Optional };
        var account = new DimensionRule { DefaultValueId = branch, Mode = DimensionMode.Optional };
        var direct = Guid.NewGuid();
        Assert.Equal(direct, DimensionMath.Resolve(definition, new(definition, direct), account, global, null, DimensionOrigin.Explicit)!.ValueId);
        Assert.Equal(branch, DimensionMath.Resolve(definition, null, account, global, null, DimensionOrigin.Explicit)!.ValueId);
        Assert.Equal(global.DefaultValueId, DimensionMath.Resolve(definition, null, null, global, null, DimensionOrigin.Explicit)!.ValueId);
        Assert.Equal(DimensionOrigin.Unassigned, DimensionMath.Resolve(definition, new(definition, null), account, global, null, DimensionOrigin.Explicit)!.Origin);
        account.Mode = DimensionMode.Fixed;
        Assert.Throws<ApiException>(() => DimensionMath.Resolve(definition, new(definition, direct), account, global, null, DimensionOrigin.Explicit));
        Assert.Equal(DimensionOrigin.Fixed, DimensionMath.Resolve(definition, null, account, global, null, DimensionOrigin.Explicit)!.Origin);
    }

    [Fact]
    public void RequiredProhibitedAndAllowedPoliciesFailClosed()
    {
        var definition = Guid.NewGuid();
        var value = Guid.NewGuid();
        var rule = new DimensionRule { Mode = DimensionMode.Required };
        Assert.Throws<ApiException>(() => DimensionMath.Resolve(definition, null, rule, null, null, DimensionOrigin.Explicit));
        rule.Mode = DimensionMode.Prohibited;
        Assert.Throws<ApiException>(() => DimensionMath.Resolve(definition, new(definition, value), rule, null, null, DimensionOrigin.Explicit));
        rule.Mode = DimensionMode.Optional;
        rule.AllowedJson = $"[\"{Guid.NewGuid()}\"]";
        Assert.Throws<ApiException>(() => DimensionMath.Resolve(definition, new(definition, value), rule, null, null, DimensionOrigin.Explicit));
    }

    [Fact]
    public void AllocationsConserveAmountsAndRejectMissingDenominators()
    {
        Assert.Equal(new decimal[] { 6_000, 4_000 }, DimensionMath.Distribute(10_000, AllocationBasis.Percent, [60, 40], 2));
        Assert.Equal(new decimal[] { 0.02m, 0.02m, 0.01m }, DimensionMath.Distribute(0.05m, AllocationBasis.Statistical, [1, 1, 1], 2));
        Assert.Throws<ApiException>(() => DimensionMath.Distribute(100, AllocationBasis.Statistical, [0, 0], 2));
        Assert.Throws<ApiException>(() => DimensionMath.Distribute(100, AllocationBasis.Percent, [45, 50], 2));
        Assert.Throws<ApiException>(() => DimensionMath.Distribute(100, AllocationBasis.Amount, [45, 50], 2));
    }

    [Fact]
    public void RollupAndAnalyticalRedistributionPreserveDirectAndUnassignedTotals()
    {
        var date = new DateOnly(2025, 1, 1);
        var dim = Guid.NewGuid();
        var account = Guid.NewGuid();
        var parent = new DimensionValue { DefinitionId = dim, Code = "Parent" };
        var child = new DimensionValue { DefinitionId = dim, Code = "Child", ParentId = parent.Id };
        var allocation = Guid.NewGuid();
        var basis = new FinancialLine(Guid.NewGuid(), Guid.NewGuid(), account, date, 1, 1, "ManualJournal", null, null, 100, 0, null);
        DimensionalLine Row(Guid? value, decimal amount, Guid? id = null) => new(basis, [new(dim, value)], id, date, amount);
        var result = DimensionReporting.Totals([Row(parent.Id, 100), Row(child.Id, 40), Row(null, 10), Row(parent.Id, -60, allocation),
            Row(child.Id, 60, allocation)], dim, null, new Dictionary<Guid, DimensionValue> { [parent.Id] = parent, [child.Id] = child },
            new Dictionary<Guid, AccountClass> { [account] = AccountClass.Expense });
        Assert.Equal(150, result.Where(x => !x.IsRollup).Sum(x => x.Expense));
        Assert.Equal(150, result.Where(x => !x.IsRollup).Sum(x => x.ManagementExpense));
        Assert.Equal(140, result.Single(x => x.IsRollup).ManagementExpense);
        Assert.Equal(10, result.Single(x => x.ValueId is null).Expense);
    }

    [Fact]
    public void EffectiveDatesAndTerminalProjectStateAreEnforced()
    {
        var date = new DateOnly(2025, 4, 1);
        var value = new DimensionValue { EffectiveDate = date, InactiveOn = date.AddDays(3) };
        Assert.False(DimensionMath.Active(value, date.AddDays(-1)));
        Assert.True(DimensionMath.Active(value, date));
        Assert.False(DimensionMath.Active(value, date.AddDays(3)));
        Assert.True(ProjectService.Transition(ProjectState.Approved, ProjectState.Active));
        Assert.False(ProjectService.Transition(ProjectState.Closed, ProjectState.Active));
        Assert.False(ProjectService.Transition(ProjectState.Draft, ProjectState.Closed));
    }

    [Fact]
    public async Task DimensionEndpointsRequireAuthentication()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/organizations/{Guid.NewGuid()}/companies/{Guid.NewGuid()}/dimensions/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
