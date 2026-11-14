using System.Text.Json;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Inventory;
using System.Net;
using Xunit;

namespace RAQM.Tests;

public sealed class InventoryValuationTests
{
    [Theory]
    [InlineData(ValuationMethod.Fifo, 32)]
    [InlineData(ValuationMethod.MovingAverage, 48)]
    public void CostUsesLocalFifoOrPerpetualWeightedAverage(ValuationMethod method, int expected)
    {
        var warehouse = Guid.NewGuid();
        var first = new LotChange(Guid.NewGuid(), Guid.NewGuid(), 5, 40);
        var second = new LotChange(Guid.NewGuid(), Guid.NewGuid(), 5, 80);
        var movement = StockValuation.Take(new(Guid.NewGuid(), warehouse, 10, 0, 120, 0), [first, second], 4, method, 2);
        Assert.Equal(-expected, movement.Gross);
        Assert.Equal(-4, Assert.Single(movement.Lots).Quantity);
        Assert.Equal(first.Origin, movement.Lots[0].Origin);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void AllocationsUseCurrencyPrecisionAndLeaveNoResidual(int units)
    {
        var amounts = StockValuation.Allocate(10, [1, 1, 1], units);
        Assert.Equal(10, amounts.Sum());
        Assert.All(amounts, amount => Assert.Equal(amount, decimal.Round(amount, units)));
        Assert.Equal(10 - amounts[0] - amounts[1], amounts[2]);
    }

    [Fact]
    public void LastIssueConsumesTheRoundedResidualAndRemainingAllowance()
    {
        var first = new LotChange(Guid.NewGuid(), Guid.NewGuid(), 3, 10);
        var balance = new StockBalance(Guid.NewGuid(), Guid.NewGuid(), 3, 0, 10, 1);
        var partial = StockValuation.Take(balance, [first], 1, ValuationMethod.Fifo, 2);
        var remainder = balance with { OnHand = 2, Gross = 10 + partial.Gross, Allowance = 1 + partial.Allowance };
        var final = StockValuation.Take(remainder, [first with { Quantity = 2, Cost = remainder.Gross }], 2, ValuationMethod.Fifo, 2);
        Assert.Equal(-10m, partial.Gross + final.Gross);
        Assert.Equal(-1m, partial.Allowance + final.Allowance);
    }

    [Fact]
    public void ReservationsAndProvenanceConstrainIssues()
    {
        var first = new LotChange(Guid.NewGuid(), Guid.NewGuid(), 5, 50);
        var balance = new StockBalance(Guid.NewGuid(), Guid.NewGuid(), 5, 2, 50, 0);
        Assert.Throws<ApiException>(() => StockValuation.Take(balance, [first], 4, ValuationMethod.MovingAverage, 2));
        Assert.Throws<ApiException>(() => StockValuation.Take(balance, [first], 2, ValuationMethod.Fifo, 2, Guid.NewGuid()));
        Assert.Equal(-30, StockValuation.Take(balance, [first], 3, ValuationMethod.Fifo, 2, first.Origin).Gross);
    }

    [Fact]
    public void ReplayKeepsOriginalLayerOrderAndAppliesCostOnlyChanges()
    {
        var first = new LotChange(Guid.NewGuid(), Guid.NewGuid(), 5, 10);
        var second = new LotChange(Guid.NewGuid(), Guid.NewGuid(), 4, 40);
        StockMovement Row(long sequence, LotChange delta) => new() { Sequence = sequence, LotsJson = JsonSerializer.Serialize(new[] { delta }) };
        var lots = StockValuation.Lots([Row(3, first with { Quantity = 0, Cost = 3 }), Row(2, second), Row(1, first),
            Row(4, first with { Quantity = -1, Cost = -2 })]);
        Assert.Equal(new[] { first.Id, second.Id }, lots.Select(x => x.Id));
        Assert.Equal((4m, 11m), (lots[0].Quantity, lots[0].Cost));
    }

    [Fact]
    public void QuantityConversionRejectsImplicitRounding()
    {
        Assert.Equal(2.123456m, StockValuation.Quantity(2.123456m));
        Assert.Throws<ApiException>(() => StockValuation.Quantity(2.1234567m));
        Assert.Throws<ApiException>(() => StockValuation.Quantity(-1));
    }

    [Fact]
    public async Task InventoryReportAndOperationsRequireAuthentication()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1");
        using var client = factory.CreateClient();
        var root = $"/api/v1/organizations/{Guid.NewGuid()}/companies/{Guid.NewGuid()}/inventory";
        using var report = await client.GetAsync(root + "/report?at=2026-01-01");
        using var operations = await client.GetAsync(root + "/operations");
        Assert.Equal(HttpStatusCode.Unauthorized, report.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, operations.StatusCode);
    }
}
