using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using RAQM.Api.Data.Migrations;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Helpers.FixedAssets;
using RAQM.Api.Services.FixedAssets;
using Xunit;

namespace RAQM.Tests;

public sealed class FixedAssetRulesTests
{
    private static readonly DateOnly April = new(2026, 4, 1);

    [Fact]
    public void StraightLineDepreciationUsesAvailabilityAndFiscalDayProration()
    {
        var end = April.AddDays(29);
        var cost = new ComponentBalance(Guid.NewGuid(), 24000, 0, 0, 0, 0, 48, 0);
        Assert.Equal(1, AssetMath.Fraction(April, end, April, end));
        Assert.Equal(.5m, AssetMath.Fraction(April, end, April.AddDays(15), end));
        Assert.Equal((500m, 500m, 1m), AssetMath.Depreciate(cost, 1, "USD"));
        Assert.Equal((250m, 250m, .5m), AssetMath.Depreciate(cost, .5m, "USD"));
        Assert.Equal(0, AssetMath.Fraction(April, end, end.AddDays(1), end));
        Assert.Equal(0, AssetMath.Depreciate(cost, 0, "USD").Charge);
    }

    [Theory]
    [InlineData("USD", 33.33, 33.34)]
    [InlineData("IQD", 33.333, 33.334)]
    [InlineData("JPY", 33, 34)]
    public void FinalPeriodAllocatesRoundingResidualWithoutCrossingResidualFloor(string currency, decimal first, decimal last)
    {
        var component = new AssetComponent { LifeMonths = 3, InitialResidual = 20 };
        var state = AssetMath.Balance(component, [new() { Date = April, Cost = 120 }]);
        var charges = new List<decimal>();
        for (var period = 0; period < 3; period++)
        {
            var result = AssetMath.Depreciate(state, 1, currency);
            charges.Add(result.Charge);
            state = state with { Depreciation = state.Depreciation + result.Charge, RemainingMonths = state.RemainingMonths - result.Used };
        }
        Assert.Equal(first, charges[0]);
        Assert.Equal(last, charges[2]);
        Assert.Equal(100, charges.Sum());
        Assert.Equal(20, state.NetBookValue);
        Assert.Equal(0, AssetMath.Depreciate(state, 1, currency).Charge);
    }

    [Fact]
    public void EstimateReallocatesFutureDepreciationAndPreservesPriorCharges()
    {
        var component = new AssetComponent { LifeMonths = 10, InitialResidual = 100 };
        var prior = new AssetMovement[] { new() { Date = April, Sequence = 1, Cost = 1100 },
            new() { Date = April.AddDays(29), Sequence = 2, Depreciation = 100, Units = 1 },
            new() { Date = April.AddMonths(1), Sequence = 3, RemainingOverride = 6, ResidualOverride = 400 } };
        var state = AssetMath.Balance(component, prior);
        Assert.Equal(100, state.Depreciation);
        Assert.Equal(6, state.RemainingMonths);
        Assert.Equal(400, state.Residual);
        Assert.Equal(100, AssetMath.Depreciate(state, 1, "USD").Charge);
    }

    [Fact]
    public void ImpairmentRecoveryCannotExceedTheUnimpairedHypotheticalCarryingAmount()
    {
        var impaired = new ComponentBalance(Guid.NewGuid(), 10000, 2000, 2000, 2000, 0, 24, 2000);
        var following = AssetMath.Depreciate(impaired, 1, "USD");
        Assert.Equal(250, following.Charge);
        Assert.Equal(333.33m, following.Shadow);
        var state = impaired with { Depreciation = 2250, ShadowDepreciation = 2333.33m, RemainingMonths = 23 };
        Assert.Equal(1916.67m, AssetMath.Recovery(state, 9500));
        Assert.Equal(250, AssetMath.Recovery(state, 6000));
        Assert.Equal(0, AssetMath.Recovery(state, 5750));
        Assert.Equal(0, AssetMath.Recovery(state with { ReversibleImpairment = 0 }, 9500));
    }

    [Theory]
    [InlineData(4000, 1000, 0)]
    [InlineData(2000, 0, 1000)]
    [InlineData(0, 0, 3000)]
    public void DisposalClearsCostAndDepreciationAndPostsOnlyTheComputedResult(decimal proceeds, decimal gain, decimal loss)
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var lines = AssetMath.Disposal(ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], 10000, 7000, 0, proceeds);
        Assert.Equal(10000, lines.Single(x => x.AccountId == ids[0]).Credit);
        Assert.Equal(7000, lines.Single(x => x.AccountId == ids[1]).Debit);
        Assert.Equal(gain, lines.Where(x => x.AccountId == ids[3]).Sum(x => x.Credit));
        Assert.Equal(loss, lines.Where(x => x.AccountId == ids[4]).Sum(x => x.Debit));
        Assert.Equal(proceeds, lines.Where(x => x.AccountId == ids[5]).Sum(x => x.Debit));
        Assert.Equal(lines.Sum(x => x.Debit), lines.Sum(x => x.Credit));
    }

    [Fact]
    public void ExistingReceiptIsClearedAndOnlyNewDisposalCostsAffectCash()
    {
        var ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        var lines = AssetMath.Disposal(ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], 10000, 6000, 500, 4000, ids[6], 200);
        Assert.Equal(4000, lines.Single(x => x.AccountId == ids[5]).Debit);
        var cash = lines.Single(x => x.AccountId == ids[6]);
        Assert.Equal((0m, 200m), (cash.Debit, cash.Credit));
        Assert.Equal(500, lines.Single(x => x.AccountId == ids[2]).Debit);
        Assert.Equal(300, lines.Single(x => x.AccountId == ids[3]).Credit);
    }

    [Fact]
    public void ComponentRunHasNoParentChargeAndLeavesNonDepreciableLandUntouched()
    {
        var category = new AssetCategory { Depreciable = true, DepreciationExpenseId = Guid.NewGuid(),
            Controls = [new() { Role = AssetControlRole.AccumulatedDepreciation, AccountId = Guid.NewGuid() }] };
        var land = new AssetCategory { Depreciable = false };
        var a = new FixedAsset { CategoryId = category.Id, RecognitionDate = April, AvailableDate = April,
            Components = [new() { LifeMonths = 24, Number = 1 }, new() { LifeMonths = 12, Number = 2 }] };
        var b = new FixedAsset { CategoryId = land.Id, RecognitionDate = April, AvailableDate = April, Components = [new() { LifeMonths = 0 }] };
        var period = new AccountingPeriod { StartDate = April, EndDate = April.AddDays(29) };
        var snapshot = new AssetSnapshot([category, land], [a, b], [new() { Date = April, ComponentId = a.Components[0].Id, Cost = 12000 },
            new() { Date = April, ComponentId = a.Components[1].Id, Cost = 1200 }, new() { Date = April, ComponentId = b.Components[0].Id, Cost = 80000 }], []);
        var plan = AssetPlanningService.Depreciation(snapshot, [period], period, "USD");
        Assert.Collection(plan.Movements, first => Assert.Equal(500, first.Depreciation), second => Assert.Equal(100, second.Depreciation));
        Assert.Equal(600, plan.Lines.Sum(x => x.Debit));
        Assert.Equal(600, plan.Lines.Sum(x => x.Credit));
        Assert.Equal(a.Id, Assert.Single(plan.Assets));
    }

    [Fact]
    public void MigrationOnlyAddsFixedAssetTablesAndDatabaseContracts()
    {
        var operations = new FixedAssetsV1().UpOperations;
        Assert.Equal(7, operations.OfType<CreateTableOperation>().Count());
        Assert.All(operations.OfType<CreateTableOperation>(), x => Assert.Equal("fixed_assets", x.Schema));
        Assert.DoesNotContain(operations, x => x is DropTableOperation or DropPrimaryKeyOperation or AddColumnOperation or AlterColumnOperation);
        Assert.Contains(operations.OfType<SqlOperation>(), x => x.Sql.Contains("z_fixed_assets_journal"));
    }

    [Fact]
    public async Task Phase04OpenApiContractsAndCompanyRouteAuthorizationArePreserved()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test");
        using var client = factory.CreateClient();
        var baseline = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Contracts", "phase04-openapi-before.json")))!;
        var actual = (await client.GetFromJsonAsync<JsonObject>("/openapi/v1.json"))!;
        foreach (var item in baseline["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(item.Value, actual["paths"]![item.Key]), $"Route changed: {item.Key}");
        foreach (var item in baseline["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(item.Key, item.Value, actual["components"]!["schemas"]![item.Key]), $"Schema changed: {item.Key}");
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText!.Contains("/fixed-assets")).ToArray();
        Assert.Equal(12, routes.Length);
        Assert.All(routes, route =>
        {
            Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(route.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Contains("{companyId:guid}", route.RoutePattern.RawText);
        });
    }
}
