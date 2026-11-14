using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.BudgetControl;
using RAQM.Api.Services.Budgeting;
using RAQM.Api.Services.FinancialReporting;
using static RAQM.Api.Helpers.Budgeting.BudgetMath;

namespace RAQM.Api.Services.FinancialAnalytics;

public sealed record BudgetVariance(Guid AccountId, string Code, int Period, BudgetKind Kind, decimal Budget, decimal Actual,
    decimal Commitments, decimal PriorActual)
{
    public decimal? Available => Kind == BudgetKind.Revenue ? null : Budget - Actual - Commitments;
    public decimal FavorableVariance => Favorable(Kind, Budget, Actual);
    public decimal? FavorablePercent => Budget == 0 ? null : Round(FavorableVariance / Budget * 100, 2);
}

public sealed record BudgetReport(Guid BudgetId, Guid VersionId, int VersionNumber, BudgetKind Kind, string Currency, DateOnly Cutoff,
    DateOnly? ApprovalAsOf, IReadOnlyList<BudgetVariance> Periods, IReadOnlyList<BudgetVariance> Accounts,
    IReadOnlyList<BudgetVariance> QuarterTotals, IReadOnlyList<BudgetVariance> Rollups, decimal YearToDateBudget, decimal AnnualBudget,
    decimal YearToDateActual, decimal FavorableVariance, IReadOnlyList<FinancialLine> Ledger, IReadOnlyList<BudgetCommitment> Events);

public sealed class BudgetReporting(RaqmDbContext db, FinancialAccess access, BudgetService budgets, BudgetControlService control,
    FinancialLedger ledger)
{
    public async Task<BudgetReport> Report(Guid actor, Guid org, Guid company, Guid budgetId, DateOnly cutoff,
        DateOnly? approvalAsOf, Guid? versionId, Guid? account, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var budget = await budgets.FindBudget(company, budgetId, ct);
        var year = await db.FiscalYears.AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == budget.FiscalYearId, ct);
        if (cutoff < year.StartDate || cutoff > year.EndDate || (approvalAsOf.HasValue && versionId.HasValue))
            throw Bad("Select a cutoff inside the fiscal year and one budget comparison basis.");
        var version = versionId.HasValue ? await budgets.FindVersion(company, versionId.Value, false, ct) :
            await control.Published(company, year.Id, budget.Kind, approvalAsOf ?? DateOnly.MaxValue, ct, approvalAsOf)
            ?? throw Missing("No published budget exists at the selected approval cutoff.");
        if (version.BudgetId != budgetId || !version.EffectiveDate.HasValue) throw Bad("Select a published version of this budget.");
        var chart = (await ledger.Accounts(company, ct)).ToDictionary(x => x.Id);
        var ids = version.Allocations.Select(x => x.AccountId).ToHashSet();
        if (budget.Kind is BudgetKind.Revenue or BudgetKind.Operating)
            ids.UnionWith(chart.Values.Where(x => x.IsPosting && Accepts(budget.Kind, x.Classification)).Select(x => x.Id));
        ids.UnionWith(await db.BudgetPolicies.AsNoTracking().Where(x => x.CompanyId == company && x.Kind == budget.Kind)
            .Select(x => x.AccountId).Distinct().ToListAsync(ct));
        if (account.HasValue && !ids.Contains(account.Value)) throw Missing("Account is outside this budget reporting scope.");
        var periods = await budgets.Periods(company, year.Id, ct);
        var actual = (await ledger.Lines(company, cutoff, year.StartDate, ids.ToArray(), true).ToListAsync(ct))
            .Where(x => Counts(budget.Kind, x.OriginalSource ?? x.Source)).ToArray();
        var periodIds = periods.Select(x => x.Id).ToArray();
        var events = await db.BudgetCommitments.AsNoTracking().Where(x => x.CompanyId == company && periodIds.Contains(x.PeriodId) &&
            ids.Contains(x.AccountId) && x.Date <= cutoff).OrderBy(x => x.Date).ThenBy(x => x.Id).ToListAsync(ct);
        var priorYear = await db.FiscalYears.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
            x.EndDate == year.StartDate.AddDays(-1), ct);
        var priorPeriods = priorYear is null ? [] : await budgets.Periods(company, priorYear.Id, ct);
        var prior = priorYear is null ? [] : (await ledger.Lines(company, priorYear.EndDate, priorYear.StartDate, ids.ToArray(), true)
            .ToListAsync(ct)).Where(x => Counts(budget.Kind, x.OriginalSource ?? x.Source)).ToArray();
        var priorByPeriod = priorPeriods.ToDictionary(x => x.Number);
        var budgetByAccount = version.Allocations.ToDictionary(x => (x.AccountId, x.PeriodId), x => x.Amount);
        var currentByPeriod = actual.ToLookup(x => (x.AccountId, periods.Single(p => p.StartDate <= x.Date && p.EndDate >= x.Date).Number));
        var previousByPeriod = prior.ToLookup(x => (x.AccountId, priorPeriods.Single(p => p.StartDate <= x.Date && p.EndDate >= x.Date).Number));
        var commitments = events.ToLookup(x => (x.AccountId, x.PeriodId));
        var rows = ids.OrderBy(id => chart[id].Code).SelectMany(id => periods.Select(period =>
        {
            var key = (id, period.Id);
            var ordinal = (id, period.Number);
            return new BudgetVariance(id, chart[id].Code, period.Number, budget.Kind, budgetByAccount.GetValueOrDefault(key),
                currentByPeriod[ordinal].Sum(x => Actual(budget.Kind, x.Debit, x.Credit)),
                commitments[key].Sum(x => x.Amount),
                priorByPeriod.ContainsKey(period.Number) ? previousByPeriod[ordinal].Sum(x => Actual(budget.Kind, x.Debit, x.Credit)) : 0);
        })).ToArray();
        var accounts = rows.GroupBy(x => x.AccountId).Select(g => Sum(g.Key, chart[g.Key].Code, 0, g)).ToArray();
        var quarter = rows.GroupBy(x => (x.Period - 1) / 3 + 1).OrderBy(x => x.Key)
            .Select(g => Sum(Guid.Empty, $"Q{g.Key}", g.Key, g)).ToArray();
        var ancestors = new Dictionary<Guid, List<BudgetVariance>>();
        foreach (var row in accounts)
        {
            var parent = chart[row.AccountId].ParentId;
            var seen = new HashSet<Guid>();
            while (parent is { } id && chart.TryGetValue(id, out var node) && seen.Add(id))
            {
                if (!ancestors.TryGetValue(id, out var values)) ancestors[id] = values = [];
                values.Add(row);
                parent = node.ParentId;
            }
        }
        var rollups = ancestors.Select(x => Sum(x.Key, chart[x.Key].Code, 0, x.Value)).OrderBy(x => x.Code).ToArray();
        var started = periods.Where(x => x.StartDate <= cutoff).Select(x => x.Number).ToHashSet();
        var ytd = rows.Where(x => started.Contains(x.Period)).Sum(x => x.Budget);
        var booked = rows.Sum(x => x.Actual);
        var result = new BudgetReport(budget.Id, version.Id, version.Number, budget.Kind, scope.BaseCurrency, cutoff, approvalAsOf,
            rows, accounts, quarter, rollups, ytd, rows.Sum(x => x.Budget), booked, Favorable(budget.Kind, ytd, booked),
            account.HasValue ? actual.Where(x => x.AccountId == account.Value).ToArray() : [],
            account.HasValue ? events.Where(x => x.AccountId == account.Value).ToArray() : []);
        await tx.CommitAsync(ct);
        return result;
    }

    private static BudgetVariance Sum(Guid id, string code, int period, IEnumerable<BudgetVariance> rows)
    {
        var items = rows.ToArray();
        return new(id, code, period, items[0].Kind, items.Sum(x => x.Budget), items.Sum(x => x.Actual), items.Sum(x => x.Commitments), items.Sum(x => x.PriorActual));
    }

    public static byte[] Csv(BudgetReport report)
    {
        var text = new StringBuilder("AccountId,Period,Budget,Actual,Commitments,Available,PriorActual,Favorable,FavorablePercent,Currency,Version,ActualCutoff\r\n");
        foreach (var row in report.Periods)
        {
            text.Append(FormattableString.Invariant($"{row.AccountId},{row.Period},{row.Budget},{row.Actual},{row.Commitments},{row.Available},{row.PriorActual},{row.FavorableVariance},{row.FavorablePercent},{report.Currency},{report.VersionNumber},{report.Cutoff:yyyy-MM-dd}\r\n"));
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    public async Task<(IReadOnlyList<CloseFinding> Findings, string Fingerprint)> Check(Guid company, AccountingPeriod period, CancellationToken ct)
    {
        var versions = await (from version in db.BudgetVersions.AsNoTracking() join budget in db.FinancialBudgets on version.BudgetId equals budget.Id
            where version.CompanyId == company && budget.CompanyId == company && budget.FiscalYearId == period.FiscalYearId &&
                version.State != BudgetState.Draft && version.State != BudgetState.Rejected
            select new { version.Id, version.Version, version.State, version.ActivatedOn, version.EffectiveDate }).OrderBy(x => x.Id).ToListAsync(ct);
        var policies = (await db.BudgetPolicies.AsNoTracking().Where(x => x.CompanyId == company && x.EffectiveDate <= period.EndDate)
            .ToListAsync(ct)).GroupBy(x => x.AccountId).Select(x => x.MaxBy(p => p.EffectiveDate)!)
            .Where(x => x.Mode != ControlMode.Off).OrderBy(x => x.AccountId).ToArray();
        if (versions.Count == 0 && policies.Length == 0) return ([], "");
        var year = await control.Year(company, period.StartDate, ct);
        var balances = new List<DTOs.Budgeting.BudgetAvailability>();
        var findings = new List<CloseFinding>();
        foreach (var policy in policies)
        {
            var balance = await control.Availability(company, policy, year, period.EndDate, period.EndDate, ct);
            balances.Add(balance);
            if (balance.Available < 0) findings.Add(new("budget-variance", FindingSeverity.Review, "Controlled account exceeds budget authority.", policy.AccountId));
            if (balance.Commitments > 0) findings.Add(new("budget-commitment", FindingSeverity.Review, "Review outstanding budget commitments at cutoff.", policy.AccountId));
        }
        foreach (var row in versions.Where(x => x.State is BudgetState.Submitted or BudgetState.Reviewed or BudgetState.Approved))
            findings.Add(new("budget-approval", FindingSeverity.Review, "A budget version is awaiting a decision or activation.", row.Id));
        var latest = await db.ForecastVersions.AsNoTracking().Where(x => x.CompanyId == company && x.State == Entities.Forecasting.ForecastState.Approved &&
            x.ScenarioBaseId == null && x.AsOf <= period.EndDate).OrderByDescending(x => x.AsOf).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.AsOf }).FirstOrDefaultAsync(ct);
        if (latest is not null && latest.AsOf < period.StartDate)
            findings.Add(new("budget-forecast-refresh", FindingSeverity.Information, "The approved rolling forecast predates this period.", latest.Id));
        return (findings, TradeRules.Hash(new { Versions = versions, Policies = policies.Select(x => x.Id), Balances = balances, Forecast = latest }));
    }
}
