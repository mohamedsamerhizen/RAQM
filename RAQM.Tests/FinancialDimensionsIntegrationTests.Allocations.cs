using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Budgeting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.CostAccounting;
using RAQM.Api.Services.FinancialDimensions;
using Xunit;

namespace RAQM.Tests;

public sealed partial class FinancialDimensionsIntegrationTests
{
    private async Task<Guid> ExpenseLine(Setup s, DimensionDefinition definition, DimensionValue value, decimal amount)
    {
        var draft = await Journal(s, amount);
        await Attach(s, DimensionSource.Manual, draft.Id, draft.Version, [new(1, [new(definition.Id, value.Id)])]);
        await Post(s, draft, $"source-{draft.Id:N}");
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().JournalLines.Where(x => x.JournalId == draft.Id && x.Number == 1)
            .Select(x => x.Id).SingleAsync();
    }
    private static Task<DimensionAllocation> Allocation(Setup s, Guid line, AllocationMode mode, string key, params AllocationTarget[] destinations) =>
        Send<DimensionAllocation>(s.Maker.Client, s.Dimensions + "/allocations", new CostAllocationRequest(key, line, s.Date, mode,
            AllocationBasis.Percent, destinations, "Reviewed allocation basis"));
    private static Task<DimensionAllocation> AllocationChange(Setup s, DimensionAllocation row, string action, DateOnly? correction = null) =>
        Send<DimensionAllocation>(s.Owner.Client, $"{s.Dimensions}/allocations/{row.Id}/{action}", new AllocationAction(row.Version, "Approved", correction, true));

    [Fact]
    public async Task AnalyticalAllocationMovesManagementExpenseWithoutCreatingFinancialActuals()
    {
        using var s = await Arrange();
        var definition = await Definition(s, DimensionKind.CostCenter);
        var source = await Value(s, definition, "SHARED");
        var sales = await Value(s, definition, "SALES");
        var support = await Value(s, definition, "SUPPORT");
        var line = await ExpenseLine(s, definition, source, 10_000);
        var draft = await Allocation(s, line, AllocationMode.Analytical, "analysis", new(sales.Id, 60), new(support.Id, 40));
        var executed = await AllocationChange(s, await AllocationChange(s, draft, "approve"), "execute");
        Assert.Null(executed.JournalId);
        var report = await Report(s, definition.Id, extra: "&management=true");
        Assert.Equal(10_000m, Group(report, source.Id).GetProperty("expense").GetDecimal());
        Assert.Equal(0m, Group(report, source.Id).GetProperty("managementExpense").GetDecimal());
        Assert.Equal(6_000m, Group(report, sales.Id).GetProperty("managementExpense").GetDecimal());
        Assert.Equal(4_000m, Group(report, support.Id).GetProperty("managementExpense").GetDecimal());
        Assert.Equal(0m, report.GetProperty("reconciliation").GetProperty("analyticalRedistributionNet").GetDecimal());
        await AllocationChange(s, executed, "correct", s.Date.AddDays(1));
        Assert.Equal(6_000m, Group(await Report(s, definition.Id, extra: "&management=true"), sales.Id).GetProperty("managementExpense").GetDecimal());
        Assert.Equal(0m, Group(await Report(s, definition.Id, s.Date.AddDays(1), "&management=true"), sales.Id).GetProperty("managementExpense").GetDecimal());
    }

    [Fact]
    public async Task CompetingFinancialAllocationsSerializeAndRollbackLeavesNoJournalOrDimensionEvidence()
    {
        using var s = await Arrange();
        var definition = await Definition(s, DimensionKind.CostCenter);
        var source = await Value(s, definition, "SHARED");
        var dest = await Value(s, definition, "DEST");
        var line = await ExpenseLine(s, definition, source, 100);
        var a = await AllocationChange(s, await Allocation(s, line, AllocationMode.Financial, "a", new AllocationTarget(dest.Id, 100)), "approve");
        var b = await AllocationChange(s, await Allocation(s, line, AllocationMode.Financial, "b", new AllocationTarget(dest.Id, 100)), "approve");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var tx = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
            var allocation = scope.ServiceProvider.GetRequiredService<CostAllocationService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => tx.Execute<int>(s.Owner.Id, s.Owner.Org, s.Company.Id, async (_, ct) =>
            {
                await allocation.Change(s.Owner.Id, s.Owner.Org, s.Company.Id, a.Id, "execute", new(a.Version, "Authorized", AllowBackdated: true), ct);
                throw new InvalidOperationException("Fault after financial reclassification");
            }, default));
        }
        Assert.Equal(100m, Group(await Report(s, definition.Id), source.Id).GetProperty("expense").GetDecimal());
        async Task<(HttpStatusCode Status, DimensionAllocation? Allocation)> Attempt(DimensionAllocation row)
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Dimensions}/allocations/{row.Id}/execute",
                new AllocationAction(row.Version, "Authorized", AllowBackdated: true));
            return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<DimensionAllocation>() : null);
        }
        var outcomes = await Task.WhenAll(Attempt(a), Attempt(b));
        var winner = Assert.Single(outcomes, x => x.Status == HttpStatusCode.OK).Allocation!;
        Assert.Single(outcomes, x => x.Status == HttpStatusCode.Conflict);
        var report = await Report(s, definition.Id);
        Assert.Equal(100m, Group(report, dest.Id).GetProperty("expense").GetDecimal());
        Assert.Equal(100m, report.GetProperty("reconciliation").GetProperty("direct").GetDecimal());
        var corrected = await AllocationChange(s, winner, "correct", s.Date.AddDays(1));
        Assert.NotNull(corrected.CorrectionJournalId);
        Assert.Equal(100m, Group(await Report(s, definition.Id, s.Date.AddDays(1)), source.Id).GetProperty("expense").GetDecimal());
    }

    [Fact]
    public async Task R04_002_TypedAllocationCorrectionCannotBeUndoneByGenericReversal()
    {
        using var s = await Arrange();
        var definition = await Definition(s, DimensionKind.CostCenter);
        var source = await Value(s, definition, "A");
        var destination = await Value(s, definition, "B");
        var sourceLine = await ExpenseLine(s, definition, source, 80m);
        var draft = await Allocation(s, sourceLine, AllocationMode.Financial, "allocation",
            new AllocationTarget(destination.Id, 100));
        var executed = await AllocationChange(s, await AllocationChange(s, draft, "approve"), "execute");
        var date = s.Date.AddDays(1);
        var corrected = await AllocationChange(s, executed, "correct", date);
        var attempts = new List<HttpStatusCode>();
        foreach (var journal in new[] { corrected.JournalId!.Value, corrected.CorrectionJournalId!.Value })
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Core}/journals/{journal}/reverse",
                new ReverseJournalRequest(date, "Generic reversal of typed evidence", $"reverse-{journal:N}", true));
            attempts.Add(response.StatusCode);
        }
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var state = await db.CostAllocationExecutions.Where(x => x.Id == corrected.Id).Select(x => x.State).SingleAsync();
        var inverses = await db.Journals.CountAsync(j => j.CompanyId == s.Company.Id && j.SourceModule == "Reversal");
        var report = await Report(s, definition.Id, date);
        var sourceExpense = report.GetProperty("groups").EnumerateArray()
            .Where(x => x.GetProperty("valueId").ValueKind == JsonValueKind.String &&
                x.GetProperty("valueId").GetGuid() == source.Id).Sum(x => x.GetProperty("expense").GetDecimal());
        var destinationExpense = report.GetProperty("groups").EnumerateArray()
            .Where(x => x.GetProperty("valueId").ValueKind == JsonValueKind.String &&
                x.GetProperty("valueId").GetGuid() == destination.Id).Sum(x => x.GetProperty("expense").GetDecimal());
        Assert.True(attempts.All(x => x == HttpStatusCode.Conflict) && inverses == 0 &&
            state == AllocationState.Corrected && sourceExpense == 80m && destinationExpense == 0m,
            $"Generic reversal HTTP [{string.Join(", ", attempts.Select(x => (int)x))}], inverses {inverses}, state {state}, A expense {sourceExpense}, B expense {destinationExpense}");
    }

    [Fact]
    public async Task FinancialAllocationCannotExecuteAfterItsPeriodCloses()
    {
        using var s = await Arrange();
        var definition = await Definition(s, DimensionKind.CostCenter);
        var from = await Value(s, definition, "A");
        var to = await Value(s, definition, "B");
        var line = await ExpenseLine(s, definition, from, 100);
        var row = await AllocationChange(s, await Allocation(s, line, AllocationMode.Financial, "late", new AllocationTarget(to.Id, 100)), "approve");
        var period = s.Year.Periods[0];
        await Send<PeriodResponse>(s.Owner.Client, $"{s.Core}/periods/{period.Id}/state",
            new PeriodStateRequest(PeriodState.Closed, "Close", period.Version), method: HttpMethod.Put);
        using var denied = await s.Owner.Client.PostAsJsonAsync($"{s.Dimensions}/allocations/{row.Id}/execute",
            new AllocationAction(row.Version, "Closed", AllowBackdated: true));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
    }

    [Fact]
    public async Task R02_007_CorrectionCannotRestoreAlreadyReusedHardScopeCapacity()
    {
        using var s = await Arrange();
        var definition = await Definition(s, DimensionKind.CostCenter);
        var source = await Value(s, definition, "A");
        var destination = await Value(s, definition, "B");
        var account = s.Chart["5100"];
        var budgets = s.Root + "/budgets";
        var master = await Send<FinancialBudget>(s.Maker.Client, budgets,
            new BudgetRequest(s.Year.Id, BudgetKind.Operating, "SCOPE", "Operating", "Authority", 5000));
        var version = await Send<BudgetVersion>(s.Maker.Client, $"{budgets}/{master.Id}/versions",
            new BudgetVersionRequest("scope", "Approved envelope", [new(account, 2400, AllocationMethod.Equal)]));
        var first = version.Allocations.Single(x => x.PeriodId == s.Year.Periods[0].Id);
        await Send<DimensionBudgetSlice>(s.Maker.Client, $"{s.Dimensions}/budget-versions/{version.Id}/slices",
            new BudgetSliceRequest(first.Id, source.Id, 100, true));
        await Send<DimensionBudgetSlice>(s.Maker.Client, $"{s.Dimensions}/budget-versions/{version.Id}/slices",
            new BudgetSliceRequest(first.Id, destination.Id, 100, true));
        var action = $"{budgets}/versions/{version.Id}";
        version = await Send<BudgetVersion>(s.Maker.Client, action + "/submit", new BudgetAction(version.Version, "Submitted", s.Owner.Id));
        version = await Send<BudgetVersion>(s.Owner.Client, action + "/review", new BudgetAction(version.Version, "Reviewed"));
        version = await Send<BudgetVersion>(s.Owner.Client, action + "/approve", new BudgetAction(version.Version, "Approved"));
        await Send<BudgetVersion>(s.Owner.Client, action + "/activate", new BudgetAction(version.Version, "Activated", EffectiveDate: s.Date));
        var line = await ExpenseLine(s, definition, source, 100);
        var draft = await Allocation(s, line, AllocationMode.Financial, "move-capacity", new AllocationTarget(destination.Id, 100));
        var executed = await AllocationChange(s, await AllocationChange(s, draft, "approve"), "execute");
        await ExpenseLine(s, definition, source, 100);
        using var denied = await s.Owner.Client.PostAsJsonAsync($"{s.Dimensions}/allocations/{executed.Id}/correct",
            new AllocationAction(executed.Version, "Restore source expense", s.Date.AddDays(1), true));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var report = await s.Owner.Client.GetFromJsonAsync<List<ScopedBudgetRow>>(
            $"{s.Dimensions}/budget-report?value={source.Id}&cutoff={s.Date.AddDays(1):yyyy-MM-dd}");
        var row = Assert.Single(report!);
        Assert.Equal((100m, 100m), (row.Authority, row.Actual));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var retained = await db.CostAllocationExecutions.SingleAsync(x => x.Id == executed.Id);
        Assert.Equal(AllocationState.Executed, retained.State);
        Assert.Null(retained.CorrectionJournalId);
        Assert.False(await db.Journals.AnyAsync(x => x.CompanyId == s.Company.Id &&
            x.SourceModule == "Dimensions.Correction" && x.SourceDocumentId == executed.Id));
    }

    [Fact]
    public async Task ScopedBudgetSharesItsParentAuthorityAndRejectsSpendingBeyondTheAssignedDepartment()
    {
        using var s = await Arrange();
        var definition = await Definition(s, DimensionKind.Department);
        var ops = await Value(s, definition, "OPS");
        var other = await Value(s, definition, "OTHER");
        var account = s.Chart["5100"];
        var budgets = s.Root + "/budgets";
        var master = await Send<FinancialBudget>(s.Maker.Client, budgets, new BudgetRequest(s.Year.Id, BudgetKind.Operating, "OPS", "Operating", "Authority", 2000));
        var version = await Send<BudgetVersion>(s.Maker.Client, $"{budgets}/{master.Id}/versions", new BudgetVersionRequest("budget", "Approved envelope",
            [new(account, 1200, AllocationMethod.Equal)]));
        var first = version.Allocations.Single(x => x.PeriodId == s.Year.Periods[0].Id);
        var route = $"{s.Dimensions}/budget-versions/{version.Id}/slices";
        await Send<DimensionBudgetSlice>(s.Maker.Client, route, new BudgetSliceRequest(first.Id, ops.Id, 70, true));
        using var excess = await s.Maker.Client.PostAsJsonAsync(route, new BudgetSliceRequest(first.Id, other.Id, 31, true));
        Assert.Equal(HttpStatusCode.Conflict, excess.StatusCode);
        var action = $"{budgets}/versions/{version.Id}";
        version = await Send<BudgetVersion>(s.Maker.Client, action + "/submit", new BudgetAction(version.Version, "Submitted", s.Owner.Id));
        version = await Send<BudgetVersion>(s.Owner.Client, action + "/review", new BudgetAction(version.Version, "Reviewed"));
        version = await Send<BudgetVersion>(s.Owner.Client, action + "/approve", new BudgetAction(version.Version, "Approved"));
        await Send<BudgetVersion>(s.Owner.Client, action + "/activate", new BudgetAction(version.Version, "Activated", EffectiveDate: s.Date));
        var expense = await Journal(s, 50);
        await Attach(s, DimensionSource.Manual, expense.Id, expense.Version, [new(1, [new(definition.Id, ops.Id)])]);
        await Post(s, expense, "within");
        var excessive = await Journal(s, 21);
        await Attach(s, DimensionSource.Manual, excessive.Id, excessive.Version, [new(1, [new(definition.Id, ops.Id)])]);
        using var denied = await s.Owner.Client.PostAsJsonAsync($"{s.Core}/journals/{excessive.Id}/post",
            new PostJournalRequest("excess", excessive.Version, true, "Test"));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var candidates = new[] { await Journal(s, 20), await Journal(s, 20) };
        foreach (var candidate in candidates)
            await Attach(s, DimensionSource.Manual, candidate.Id, candidate.Version,
                [new(1, [new(definition.Id, ops.Id)])]);
        var attempts = await Task.WhenAll(candidates.Select(x => s.Owner.Client.PostAsJsonAsync($"{s.Core}/journals/{x.Id}/post",
            new PostJournalRequest($"scope-{x.Id:N}", x.Version, true, "Use final scope authority"))));
        try
        {
            Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var attempt in attempts) attempt.Dispose(); }
        var report = await s.Owner.Client.GetFromJsonAsync<List<ScopedBudgetRow>>($"{s.Dimensions}/budget-report?value={ops.Id}&cutoff={s.Date:yyyy-MM-dd}");
        var amounts = Assert.Single(report!);
        Assert.Equal((70m, 70m), (amounts.Authority, amounts.Actual));
    }
}
