using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Budgeting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.FinancialReporting;
using static RAQM.Api.Helpers.FinancialDimensions.DimensionMath;
using static RAQM.Api.Services.FinancialDimensions.DimensionRegister;

namespace RAQM.Api.Services.FinancialDimensions;

public sealed record ScopedBudgetRow(Guid BudgetVersionId, Guid ValueId, Guid AccountId, Guid PeriodId,
    decimal Authority, decimal Actual, decimal Commitments, bool HardLimit)
{
    public decimal Remaining => Authority - Actual - Commitments;
}

public sealed class DimensionBudgetService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    DimensionBindings bindings, FinancialLedger ledger, TimeProvider clock)
{
    private sealed record Slice(Guid VersionId, Guid DefinitionId, Guid ValueId, Guid AccountId, Guid PeriodId,
        decimal Amount, bool Hard, BudgetKind Kind);

    public Task<DimensionBudgetSlice> Add(Guid actor, Guid org, Guid company, Guid version, BudgetSliceRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var line = await (from allocation in db.BudgetAllocations join v in db.BudgetVersions on allocation.BudgetVersionId equals v.Id
                join budget in db.FinancialBudgets on v.BudgetId equals budget.Id
                where allocation.CompanyId == company && v.CompanyId == company && budget.CompanyId == company &&
                    v.Id == version && allocation.Id == input.AllocationId && v.State == BudgetState.Draft && v.CreatedBy == actor
                select new { Allocation = allocation, budget.Kind }).SingleOrDefaultAsync(token)
                ?? throw Conflict("Only the maker can scope a draft budget allocation.");
            var value = await db.DimensionValues.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == input.ValueId, token)
                ?? throw Missing("Budget dimension value was not found.");
            CurrencyPolicy.Validate(input.Amount, CurrencyPolicy.Units(scope.BaseCurrency));
            if (input.HardLimit && line.Kind is not (BudgetKind.Operating or BudgetKind.Inventory))
                throw Bad("Hard dimensional limits cover operating and stocked procurement budgets.");
            var versionRows = await Slices(company, [version], token);
            if (versionRows.Any(x => x.AccountId == line.Allocation.AccountId &&
                (x.DefinitionId != value.DefinitionId || x.Hard != input.HardLimit)))
                throw Conflict("All slices of a version and account use one dimension and control mode.");
            var assigned = await db.DimensionBudgetSlices.AsNoTracking().Where(x => x.CompanyId == company && x.BudgetAllocationId == input.AllocationId)
                .ToListAsync(token);
            if (assigned.Any(x => x.ValueId == input.ValueId) || assigned.Sum(x => x.Amount) + input.Amount > line.Allocation.Amount)
                throw Conflict("Dimensional authority cannot duplicate or exceed its parent allocation.");
            var row = Scoped(scope, new DimensionBudgetSlice { BudgetAllocationId = input.AllocationId, ValueId = input.ValueId,
                Amount = input.Amount, HardLimit = input.HardLimit });
            db.DimensionBudgetSlices.Add(row);
            access.Audit(scope, actor, version, "DimensionBudgetSlicePrepared", value.Code);
            return row;
        }, ct);

    internal async Task ValidateActivation(Guid company, Guid next, Guid? prior, Guid year, CancellationToken ct)
    {
        var proposed = await Slices(company, [next], ct);
        var old = prior.HasValue ? await Slices(company, [prior.Value], ct) : [];
        if (proposed.Count == 0 && old.Count == 0) return;
        var calendar = await db.FiscalYears.AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == year, ct);
        var periods = await db.Periods.AsNoTracking().Where(x => x.CompanyId == company && x.FiscalYearId == year).ToListAsync(ct);
        var closed = periods.Where(x => x.State != PeriodState.Open).Select(x => x.Id).ToHashSet();
        var priorClosed = old.Where(x => closed.Contains(x.PeriodId)).ToDictionary(x => (x.AccountId, x.PeriodId, x.ValueId), x => (x.Amount, x.Hard));
        var nextClosed = proposed.Where(x => closed.Contains(x.PeriodId)).ToDictionary(x => (x.AccountId, x.PeriodId, x.ValueId), x => (x.Amount, x.Hard));
        if (priorClosed.Count != nextClosed.Count || priorClosed.Any(x => !nextClosed.TryGetValue(x.Key, out var entry) || entry != x.Value))
            throw Conflict("Closed period dimensional slices must be preserved.");
        foreach (var previous in old.Where(x => x.Hard).Select(x => (x.AccountId, x.DefinitionId, x.ValueId)).Distinct())
            if (!proposed.Any(x => x.Hard && x.AccountId == previous.AccountId && x.DefinitionId == previous.DefinitionId && x.ValueId == previous.ValueId))
                throw Conflict("Carry forward the active hard dimensional scopes.");
        foreach (var account in proposed.Where(x => x.Hard && x.Kind == BudgetKind.Inventory).GroupBy(x => x.AccountId))
        {
            var values = account.Select(x => x.ValueId).ToHashSet();
            var events = await db.BudgetCommitments.AsNoTracking().Where(x => x.CompanyId == company &&
                x.AccountId == account.Key && x.ProcurementLineId.HasValue &&
                periods.Select(p => p.Id).Contains(x.PeriodId)).ToListAsync(ct);
            foreach (var line in events.GroupBy(x => x.ProcurementLineId).Where(x => x.Sum(e => e.Amount) > 0))
            {
                var binding = await bindings.Latest(company, DimensionSource.Procurement, line.Key!.Value, null, ct);
                if (!Read(binding).SelectMany(x => x.Values).Any(x => x.ValueId.HasValue && values.Contains(x.ValueId.Value)))
                    throw Conflict("Open historical procurement needs an authorized hard-budget dimension before activation.");
            }
            var actualLines = await ledger.Lines(company, calendar.EndDate, calendar.StartDate, [account.Key], performance: true)
                .ToListAsync(ct);
            var eligible = actualLines.Where(x => BudgetMath.Counts(BudgetKind.Inventory, x.OriginalSource ?? x.Source) &&
                BudgetMath.Actual(BudgetKind.Inventory, x.Debit, x.Credit) > 0).Select(x => x.LineId).ToArray();
            var tagged = await db.PostedDimensions.AsNoTracking().Where(x => x.CompanyId == company &&
                eligible.Contains(x.JournalLineId) && x.ValueId.HasValue && values.Contains(x.ValueId.Value))
                .Select(x => x.JournalLineId).Distinct().ToListAsync(ct);
            if (eligible.Except(tagged).Any())
                throw Conflict("Historical inventory actuals need an authorized hard-budget dimension before activation.");
        }
        foreach (var group in proposed.Where(x => x.Hard).GroupBy(x => (x.AccountId, x.ValueId)))
        {
            var row = group.First();
            var spend = await Actual(company, row, calendar.StartDate, calendar.EndDate, ct);
            var reserved = await Commitments(company, row.ValueId, periods.Select(x => x.Id).ToArray(), row.AccountId, DateOnly.MaxValue, ct);
            if (group.Sum(x => x.Amount) < spend + reserved) throw Conflict("New dimensional authority is below actual spending and open commitments.");
        }
    }

    public async Task CheckPosting(Guid company, PostingRequest request, IReadOnlyDictionary<int, EffectiveDimension[]> dimensions, CancellationToken ct)
    {
        if (request.SourceModule is "FinancialClose.YearEnd" or "OpeningBalance" || request.ReversesJournalId.HasValue ||
            !await db.DimensionBudgetSlices.AnyAsync(x => x.CompanyId == company && x.HardLimit, ct)) return;
        var year = await Year(company, request.AccountingDate, ct);
        if (year is null) return;
        var active = await Active(company, year, ct);
        var hard = (await Slices(company, active, ct)).Where(x => x.Hard).ToLookup(x => x.AccountId);
        var periodIds = await db.Periods.Where(x => x.CompanyId == company && x.FiscalYearId == year.Id).Select(x => x.Id).ToArrayAsync(ct);
        var changes = new Dictionary<(Guid AccountId, Guid ValueId), decimal>();
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var scopes = hard[line.AccountId].Where(x => BudgetMath.Counts(x.Kind, request.SourceModule)).ToArray();
            if (scopes.Length == 0) continue;
            var tag = (dimensions.GetValueOrDefault(i + 1) ?? []).SingleOrDefault(x => x.DefinitionId == scopes[0].DefinitionId);
            if (tag?.ValueId is not { } value || !scopes.Any(x => x.ValueId == value))
            {
                if (line.Debit > 0) throw Conflict("This expense requires an explicitly authorized dimensional budget scope.");
                continue;
            }
            var key = (line.AccountId, value);
            changes[key] = changes.GetValueOrDefault(key) + line.Debit - line.Credit;
        }
        foreach (var (key, amount) in changes.Where(x => x.Value > 0))
        {
            var scopes = hard[key.AccountId].Where(x => x.ValueId == key.ValueId).ToArray();
            var actual = await Actual(company, scopes[0], year.StartDate, year.EndDate, ct);
            var open = await Commitments(company, key.ValueId, periodIds, key.AccountId, DateOnly.MaxValue, ct);
            if (actual + open + amount > scopes.Sum(x => x.Amount))
                throw Conflict("This posting exceeds the selected dimension's approved authority.");
        }
    }

    public async Task CheckProcurement(Guid company, ProcurementDocument document, CancellationToken ct)
    {
        if (!await db.DimensionBudgetSlices.AnyAsync(x => x.CompanyId == company && x.HardLimit, ct)) return;
        var year = await Year(company, document.Date, ct);
        if (year is null) return;
        var hard = (await Slices(company, await Active(company, year, ct), ct)).Where(x => x.Kind == BudgetKind.Inventory && x.Hard).ToArray();
        if (hard.Length == 0) return;
        await db.SaveChangesAsync(ct);
        var ids = await db.Periods.Where(x => x.CompanyId == company && x.FiscalYearId == year.Id).Select(x => x.Id).ToArrayAsync(ct);
        var items = await db.InventoryItems.Where(x => x.CompanyId == company && x.Kind == ItemKind.Stocked).Select(x => x.Id).ToListAsync(ct);
        foreach (var line in document.Lines.Where(x => x.NetAmount > 0 && items.Contains(x.ItemId)))
        {
            var binding = await bindings.Latest(company, DimensionSource.Procurement, line.Id, document.Version, ct);
            var tag = Read(binding).SingleOrDefault()?.Values.SingleOrDefault(x => x.DefinitionId == hard[0].DefinitionId);
            if (tag?.ValueId is not { } value || !hard.Any(x => x.ValueId == value) ||
                !await db.BudgetCommitments.AnyAsync(x => x.CompanyId == company && x.ProcurementLineId == line.Id && x.Amount > 0, ct))
                throw Conflict("A controlled purchase needs an authorized dimension and a Phase 07 commitment reservation.");
            var row = hard.First(x => x.ValueId == value);
            var open = await Commitments(company, value, ids, row.AccountId, DateOnly.MaxValue, ct);
            var actual = await Actual(company, row, year.StartDate, year.EndDate, ct);
            if (open + actual > hard.Where(x => x.ValueId == value).Sum(x => x.Amount))
                throw Conflict("This purchase would exceed dimensional authority.");
        }
    }

    internal async Task<decimal> Commitments(Guid company, Guid value, Guid[]? periods, Guid? account, DateOnly cutoff, CancellationToken ct)
    {
        var rows = await db.BudgetCommitments.AsNoTracking().Where(x => x.CompanyId == company && x.Date <= cutoff &&
            x.ProcurementLineId.HasValue && (periods == null || periods.Contains(x.PeriodId)) &&
            (account == null || x.AccountId == account)).ToListAsync(ct);
        var lines = rows.Select(x => x.ProcurementLineId!.Value).Distinct().ToArray();
        var source = await db.DimensionBindings.AsNoTracking().Where(x => x.CompanyId == company && x.Source == DimensionSource.Procurement &&
            lines.Contains(x.SourceId)).ToListAsync(ct);
        var matching = source.GroupBy(x => x.SourceId).Where(x => Read(x.MaxBy(y => y.Revision)).SelectMany(y => y.Values)
            .Any(y => y.ValueId == value)).Select(x => x.Key).ToHashSet();
        return rows.Where(x => matching.Contains(x.ProcurementLineId!.Value)).Sum(x => x.Amount);
    }

    public async Task<IReadOnlyList<ScopedBudgetRow>> Report(Guid actor, Guid org, Guid company, Guid value, DateOnly cutoff, Guid? version, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        if (!await db.DimensionValues.AnyAsync(x => x.CompanyId == company && x.Id == value, ct)) throw Missing("Budget dimension was not found.");
        var year = await Year(company, cutoff, ct) ?? throw Bad("Cutoff has no fiscal year.");
        Guid[] versions;
        if (version is { } selected)
        {
            if (!await (from v in db.BudgetVersions join b in db.FinancialBudgets on v.BudgetId equals b.Id
                where v.CompanyId == company && b.CompanyId == company && v.Id == selected && b.FiscalYearId == year.Id &&
                    v.EffectiveDate != null select v.Id).AnyAsync(ct)) throw Missing("Published budget version was not found in the selected fiscal year.");
            versions = [selected];
        }
        else versions = await Active(company, year, ct);
        var slices = (await Slices(company, versions, ct)).Where(x => x.ValueId == value).ToArray();
        var periods = await db.Periods.AsNoTracking().Where(x => x.CompanyId == company && x.FiscalYearId == year.Id).ToDictionaryAsync(x => x.Id, ct);
        var output = new List<ScopedBudgetRow>();
        foreach (var row in slices.OrderBy(x => periods[x.PeriodId].Number).ThenBy(x => x.AccountId))
        {
            var period = periods[row.PeriodId];
            output.Add(new(row.VersionId, value, row.AccountId, period.Id, row.Amount, await Actual(company, row,
                period.StartDate, cutoff < period.EndDate ? cutoff : period.EndDate, ct),
                await Commitments(company, value, [period.Id], row.AccountId, cutoff, ct), row.Hard));
        }
        await tx.CommitAsync(ct);
        return output;
    }

    private Task<FiscalYear?> Year(Guid company, DateOnly at, CancellationToken ct) =>
        db.FiscalYears.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.StartDate <= at && x.EndDate >= at, ct);

    private Task<Guid[]> Active(Guid company, FiscalYear year, CancellationToken ct)
    {
        var today = Today(clock);
        var at = today < year.EndDate ? today : year.EndDate;
        return (from v in db.BudgetVersions.AsNoTracking() join b in db.FinancialBudgets on v.BudgetId equals b.Id
            where v.CompanyId == company && b.CompanyId == company && b.FiscalYearId == year.Id && v.State == BudgetState.Active &&
                v.EffectiveDate <= at select v.Id).ToArrayAsync(ct);
    }

    private Task<List<Slice>> Slices(Guid company, Guid[] versions, CancellationToken ct) =>
        (from slice in db.DimensionBudgetSlices.AsNoTracking() join allocation in db.BudgetAllocations on slice.BudgetAllocationId equals allocation.Id
            join value in db.DimensionValues on slice.ValueId equals value.Id
            join version in db.BudgetVersions on allocation.BudgetVersionId equals version.Id
            join budget in db.FinancialBudgets on version.BudgetId equals budget.Id
            where slice.CompanyId == company && allocation.CompanyId == company && value.CompanyId == company && version.CompanyId == company &&
                budget.CompanyId == company && versions.Contains(version.Id)
            select new Slice(version.Id, value.DefinitionId, value.Id, allocation.AccountId, allocation.PeriodId, slice.Amount, slice.HardLimit, budget.Kind)).ToListAsync(ct);

    private async Task<decimal> Actual(Guid company, Slice slice, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from > to) return 0;
        var rows = await ledger.Lines(company, to, from, [slice.AccountId], performance: true)
            .Where(line => db.PostedDimensions.Any(x => x.CompanyId == company && x.JournalLineId == line.LineId && x.ValueId == slice.ValueId))
            .ToListAsync(ct);
        return rows.Where(x => BudgetMath.Counts(slice.Kind, x.OriginalSource ?? x.Source)).Sum(x => BudgetMath.Actual(slice.Kind, x.Debit, x.Credit));
    }
}
