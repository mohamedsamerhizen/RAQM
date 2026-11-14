using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.FinancialReporting;
using RAQM.Api.Services.ProjectAccounting;
using static RAQM.Api.Helpers.FinancialDimensions.DimensionMath;

namespace RAQM.Api.Services.AnalyticalReporting;

public sealed record DimensionalLine(FinancialLine Ledger, IReadOnlyList<DimensionPick> Tags, Guid? AllocationId,
    DateOnly Date, decimal SignedAmount, IReadOnlyList<EffectiveDimension>? Evidence = null);
public sealed record DimensionalTotal(Guid? ValueId, string Label, Guid? SecondaryValueId, decimal Revenue, decimal Expense,
    decimal Other, decimal ManagementRevenue, decimal ManagementExpense, bool IsRollup = false)
{
    public decimal Contribution => Revenue - Expense;
    public decimal ManagementContribution => ManagementRevenue - ManagementExpense;
}

public sealed class DimensionReporting(RaqmDbContext db, FinancialAccess access, FinancialLedger ledger, ProjectService projects, TimeProvider clock)
{
    public async Task<object> Report(Guid actor, Guid org, Guid company, Guid definition, DateOnly from, DateOnly to, Guid? secondary,
        Guid? drillValue, bool includeUnassignedDrill, bool includeNonNominal, bool management, DateOnly? compareFrom, DateOnly? compareTo,
        int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        if (from.Year < 1900 || to < from || to > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) ||
            (compareFrom.HasValue != compareTo.HasValue) || compareFrom > compareTo || compareTo > to || definition == secondary)
            throw Bad("Use ordered accounting windows and different dimension definitions.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var defs = await db.DimensionDefinitions.AsNoTracking().Where(x => x.CompanyId == company).ToDictionaryAsync(x => x.Id, ct);
        if (!defs.ContainsKey(definition) || secondary.HasValue && !defs.ContainsKey(secondary.Value)) throw Missing("Dimension was not found.");
        var values = await db.DimensionValues.AsNoTracking().Where(x => x.CompanyId == company).ToDictionaryAsync(x => x.Id, ct);
        if (drillValue is { } requested && (!values.TryGetValue(requested, out var picked) || picked.DefinitionId != definition))
            throw Missing("Dimension value was not found.");
        var chart = (await ledger.Accounts(company, ct)).ToDictionary(x => x.Id, x => x.Classification);
        var current = await Load(company, from, to, includeNonNominal, management, chart, ct);
        var comparison = compareFrom.HasValue ? await Load(company, compareFrom.Value, compareTo!.Value, includeNonNominal, management, chart, ct) : [];
        var descendants = drillValue.HasValue ? AccountingRules.Descendants(drillValue.Value, values.ToDictionary(x => x.Key, x => x.Value.ParentId)) : null;
        var drill = current.Where(x =>
        {
            var tag = Pick(x, definition);
            return descendants is not null ? tag.HasValue && descendants.Contains(tag.Value) : !includeUnassignedDrill || tag is null;
        }).OrderBy(x => x.Date).ThenBy(x => x.Ledger.Sequence).ThenBy(x => x.Ledger.Number).ToArray();
        var result = new
        {
            Currency = scope.BaseCurrency, From = from, To = to, ComparisonFrom = compareFrom, ComparisonTo = compareTo,
            Definition = defs[definition], Secondary = secondary.HasValue ? defs[secondary.Value] : null,
            ManagementView = management, CompanyScopeOnlyBalances = true,
            Groups = Totals(current, definition, secondary, values, chart), ComparativeGroups = Totals(comparison, definition, secondary, values, chart),
            Reconciliation = new
            {
                Direct = current.Where(x => x.AllocationId == null).Sum(x => x.SignedAmount),
                Assigned = current.Where(x => x.AllocationId == null && Pick(x, definition) != null).Sum(x => x.SignedAmount),
                Unassigned = current.Where(x => x.AllocationId == null && Pick(x, definition) == null).Sum(x => x.SignedAmount),
                AnalyticalRedistributionNet = current.Where(x => x.AllocationId != null).Sum(x => x.SignedAmount)
            },
            Drill = new PageResult<DimensionalLine>(drill.Skip((page - 1) * size).Take(size).ToArray(), drill.Length, page, size)
        };
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<DimensionalLine[]> Load(Guid company, DateOnly from, DateOnly to, bool all, bool management,
        IReadOnlyDictionary<Guid, AccountClass> chart, CancellationToken ct)
    {
        var posted = (await ledger.Lines(company, to, from, performance: !all).ToListAsync(ct))
            .Where(x => all || (FinancialMath.Nominal(chart[x.AccountId]) && FinancialMath.OperatingHistory(x))).ToArray();
        var ids = posted.Select(x => x.LineId).ToArray();
        var tags = (await db.PostedDimensions.AsNoTracking().Where(x => x.CompanyId == company && ids.Contains(x.JournalLineId)).ToListAsync(ct))
            .ToLookup(x => x.JournalLineId);
        var result = posted.Select(x => new DimensionalLine(x, tags[x.LineId].OrderBy(t => t.DefinitionId)
            .Select(t => new DimensionPick(t.DefinitionId, t.ValueId)).ToArray(), null, x.Date, FinancialMath.Signed(x),
            tags[x.LineId].OrderBy(t => t.DefinitionId).Select(t =>
                new EffectiveDimension(t.DefinitionId, t.ValueId, t.RuleId, t.BindingId, t.Origin)).ToArray())).ToList();
        if (!management) return result.ToArray();
        var allocations = await db.CostAllocationExecutions.AsNoTracking().Where(x => x.CompanyId == company && x.Mode == AllocationMode.Analytical &&
            (x.State == AllocationState.Executed || x.State == AllocationState.Corrected) &&
            (x.Date >= from && x.Date <= to || x.CorrectedOn >= from && x.CorrectedOn <= to)).OrderBy(x => x.Date).ThenBy(x => x.Id).ToListAsync(ct);
        var sources = allocations.Select(x => x.SourceLineId).Distinct().ToArray();
        if (sources.Length == 0) return result.ToArray();
        var originals = await ledger.Lines(company, to).Where(x => sources.Contains(x.LineId)).ToDictionaryAsync(x => x.LineId, ct);
        foreach (var allocation in allocations)
        {
            var plan = JsonSerializer.Deserialize<AllocationPlan>(allocation.PlanJson)!;
            var original = originals[allocation.SourceLineId];
            Add(allocation.Date, 1);
            if (allocation.CorrectedOn is { } corrected) Add(corrected, -1);

            void Add(DateOnly date, int sign)
            {
                if (date < from || date > to) return;
                foreach (var line in plan.Dimensions)
                {
                    var journal = plan.Lines[line.Number - 1];
                    result.Add(new(original, line.Values, allocation.Id, date, sign * (journal.Debit - journal.Credit)));
                }
            }
        }
        return result.ToArray();
    }

    private static Guid? Pick(DimensionalLine row, Guid definition) => row.Tags.SingleOrDefault(x => x.DefinitionId == definition)?.ValueId;

    public static IReadOnlyList<DimensionalTotal> Totals(IReadOnlyList<DimensionalLine> rows, Guid definition, Guid? secondary,
        IReadOnlyDictionary<Guid, DimensionValue> values, IReadOnlyDictionary<Guid, AccountClass> chart)
    {
        var groups = rows.GroupBy(x => (Primary: Pick(x, definition), Secondary: secondary.HasValue ? Pick(x, secondary.Value) : null)).ToArray();
        var totals = groups.Select(x => Sum(x.Key.Primary, x.Key.Secondary, x, false)).ToList();
        var hierarchy = values.Values.Where(x => x.DefinitionId == definition).ToDictionary(x => x.Id, x => x.ParentId);
        foreach (var root in hierarchy.Keys.Where(x => hierarchy.ContainsValue(x)).Order())
        {
            var descendants = AccountingRules.Descendants(root, hierarchy);
            var relevant = rows.Where(x => Pick(x, definition) is { } tag && descendants.Contains(tag)).ToArray();
            if (relevant.Length == 0) continue;
            foreach (var group in relevant.GroupBy(x => secondary.HasValue ? Pick(x, secondary.Value) : null))
                totals.Add(Sum(root, group.Key, group, true));
        }
        return totals.OrderBy(x => x.IsRollup).ThenBy(x => x.Label, StringComparer.Ordinal).ThenBy(x => x.SecondaryValueId).ToArray();

        DimensionalTotal Sum(Guid? value, Guid? second, IEnumerable<DimensionalLine> source, bool rollup)
        {
            var material = source.ToArray();
            var direct = material.Where(x => x.AllocationId == null).ToArray();
            decimal Amount(IEnumerable<DimensionalLine> lines, AccountClass classification) =>
                lines.Where(x => chart[x.Ledger.AccountId] == classification).Sum(x => x.SignedAmount);
            return new(value, value.HasValue ? values[value.Value].Code : "Unassigned", second,
                -Amount(direct, AccountClass.Revenue), Amount(direct, AccountClass.Expense),
                direct.Where(x => !FinancialMath.Nominal(chart[x.Ledger.AccountId])).Sum(x => x.SignedAmount),
                -Amount(material, AccountClass.Revenue), Amount(material, AccountClass.Expense), rollup);
        }
    }

    public async Task<(IReadOnlyList<CloseFinding> Findings, string Fingerprint)> Check(Guid company, AccountingPeriod period, CancellationToken ct)
    {
        if (!await db.DimensionDefinitions.AnyAsync(x => x.CompanyId == company, ct)) return ([], "");
        var pending = await db.CostAllocationExecutions.AsNoTracking().Where(x => x.CompanyId == company && x.Date <= period.EndDate &&
            x.State == AllocationState.Approved).OrderBy(x => x.Id).ToListAsync(ct);
        var findings = pending.Select(x => new CloseFinding("dimension-allocation-pending", FindingSeverity.Review,
            "An approved allocation has not executed at the close cutoff.", x.Id)).ToList();
        var events = await (from project in db.FinancialProjects join e in db.FinancialProjectEvents on project.Id equals e.ProjectId
            where project.CompanyId == company && e.CompanyId == company && e.Date <= period.EndDate
            select new { project.Id, project.ValueId, e.State, e.Number }).ToListAsync(ct);
        foreach (var project in events.GroupBy(x => x.Id).Select(x => x.MaxBy(y => y.Number)!)
            .Where(x => x.State is ProjectState.Suspended or ProjectState.Completed or ProjectState.Closed).OrderBy(x => x.Id))
        {
            var open = await projects.OpenPurchases(company, project.ValueId, ct);
            if (open > 0) findings.Add(new("dimension-project-commitment", FindingSeverity.Review, "An inactive project has unresolved purchase commitments.", project.Id));
        }
        if (findings.Count == 0) return ([], "");
        return (findings, TradeRules.Hash(new { Findings = findings, Allocations = pending.Select(x => new { x.Id, x.Version }) }));
    }
}
