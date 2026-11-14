using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Forecasting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Entities.Forecasting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Forecasting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.BudgetControl;
using RAQM.Api.Services.FinancialReporting;
using static RAQM.Api.Helpers.Budgeting.BudgetMath;
using static RAQM.Api.Services.BudgetControl.BudgetControlService;

namespace RAQM.Api.Services.Forecasting;

public sealed record ForecastComparison(ForecastRow Forecast, decimal? Actual, decimal ForecastVsBudget, decimal? ActualVsForecast)
{
    public decimal? AbsoluteErrorPercent => Actual is { } value && value != 0 ?
        Round(Math.Abs(value - Forecast.Amount) / Math.Abs(value) * 100, 2) : null;
}

public sealed class ForecastService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    BudgetControlService control, CashForecastSource cash, FinancialLedger ledger, TimeProvider clock)
{
    public Task<ForecastVersion> Prepare(Guid actor, Guid org, Guid company, ForecastRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var code = AccountingRules.Text(input.Code, 40, "Forecast code").ToUpperInvariant();
            var key = AccountingRules.Text(input.Key, 64, "Forecast key");
            var name = AccountingRules.Text(input.Name, 160, "Forecast name");
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            if (input.AsOf.Year < 1900 || input.AsOf > today || input.Horizon is not (3 or 12) ||
                input.Drivers is null || input.Drivers.Count is < 1 or > 100 || input.Drivers.Any(x => x is null) ||
                input.Drivers.Select(x => x.AccountId).Distinct().Count() != input.Drivers.Count ||
                input.CustomerDelayDays is < 0 or > 365 || input.SupplierDelayDays is < 0 or > 365)
                throw Bad("Use a valid cutoff, a three or twelve period horizon, distinct forecast drivers and supported timing delays.");
            var units = CurrencyPolicy.Units(scope.BaseCurrency);
            foreach (var driver in input.Drivers)
            {
                _ = ForecastMath.Adjust(0, driver.GrowthPercent, units);
                if (!Enum.IsDefined(driver.Method) || driver.HistoryMonths is < 1 or > 12 ||
                    ((driver.Method == ForecastMethod.Manual) != (driver.Monthly is not null)) ||
                    (driver.Monthly is { } values && values.Count != input.Horizon))
                    throw Bad("Each driver requires a supported method and manual amounts for every forecast bucket when applicable.");
                foreach (var amount in driver.Monthly ?? []) CurrencyPolicy.Validate(Math.Abs(amount), units);
            }
            foreach (var assumption in input.Cash ?? [])
            {
                if (assumption is null) throw Bad("A cash assumption cannot be null.");
                CurrencyPolicy.Validate(Math.Abs(assumption.Amount), units);
            }
            var normalized = input with { Code = code, Key = key, Name = name };
            var hash = TradeRules.Hash(normalized);
            var existing = await Existing(company, key, hash, token);
            if (existing is not null) return existing;
            var sequence = await NextVersion(company, code, input.BasisId, input.AsOf, false, token);
            var start = input.AsOf.AddDays(1);
            var periods = await db.Periods.AsNoTracking().Where(x => x.CompanyId == company && x.EndDate >= start)
                .OrderBy(x => x.StartDate).Take(input.Horizon).ToListAsync(token);
            if (periods.Count != input.Horizon || periods[0].StartDate > start ||
                periods.Zip(periods.Skip(1)).Any(pair => pair.First.EndDate.AddDays(1) != pair.Second.StartDate))
                throw Conflict("Create continuous fiscal periods through the forecast horizon.");
            var chart = (await ledger.Accounts(company, token)).ToDictionary(x => x.Id);
            var rows = new List<ForecastRow>();
            var baselines = new Dictionary<(Guid Period, BudgetKind Kind), BudgetVersion?>();
            foreach (var driver in input.Drivers.OrderBy(x => x.AccountId))
            {
                if (!chart.TryGetValue(driver.AccountId, out var account) || !account.IsPosting || !account.IsActive ||
                    account.Classification is not (AccountClass.Revenue or AccountClass.Expense))
                    throw Bad("Forecast drivers require active company revenue or expense posting accounts.");
                var kind = account.Classification == AccountClass.Revenue ? BudgetKind.Revenue : BudgetKind.Operating;
                var average = driver.Method == ForecastMethod.HistoricalMonthlyAverage ?
                    await Historical(company, kind, driver, input.AsOf, token) : 0;
                for (var i = 0; i < periods.Count; i++)
                {
                    var period = periods[i];
                    if (!baselines.TryGetValue((period.Id, kind), out var published))
                    {
                        var effective = period.StartDate > input.AsOf ? period.StartDate : input.AsOf;
                        published = await control.Published(company, period.FiscalYearId, kind, effective, token, input.AsOf);
                        baselines.Add((period.Id, kind), published);
                    }
                    var bucketStart = period.StartDate < start ? start : period.StartDate;
                    var share = (decimal)(period.EndDate.DayNumber - bucketStart.DayNumber + 1) /
                        (period.EndDate.DayNumber - period.StartDate.DayNumber + 1);
                    var allocation = published?.Allocations.SingleOrDefault(x => x.AccountId == driver.AccountId && x.PeriodId == period.Id);
                    var budget = Round((allocation?.Amount ?? 0) * share, units);
                    if (driver.Method == ForecastMethod.ApprovedBudget && allocation is null)
                        throw Conflict("A budget-based driver needs a published allocation known at the forecast cutoff.");
                    var basis = driver.Method switch
                    {
                        ForecastMethod.Manual => driver.Monthly![i],
                        ForecastMethod.HistoricalMonthlyAverage => Round(average * share, units),
                        _ => budget
                    };
                    rows.Add(new(driver.AccountId, kind, bucketStart, period.EndDate,
                        ForecastMath.Adjust(basis, driver.GrowthPercent, units), budget, basis, published?.Id));
                }
            }
            var (opening, events, warnings) = await cash.Build(company, normalized, periods[^1].EndDate, scope.BaseCurrency, token);
            var buckets = ForecastMath.Project(opening, periods.Select(x => (x.StartDate < start ? start : x.StartDate, x.EndDate)), events);
            var ledgerSequence = await db.Journals.Where(x => x.CompanyId == company && x.State == JournalState.Posted &&
                x.PostingDate <= input.AsOf).MaxAsync(x => x.Sequence, token) ?? 0;
            var snapshot = new ForecastSnapshot(scope.BaseCurrency, input.AsOf, rows, opening, events, buckets, ledgerSequence, warnings);
            return Add(scope, actor, code, name, sequence, key, hash, input.BasisId, null, input.AsOf, input.Horizon, normalized, snapshot);
        }, ct);

    public Task<ForecastVersion> Scenario(Guid actor, Guid org, Guid company, ScenarioRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var code = AccountingRules.Text(input.Code, 40, "Scenario code").ToUpperInvariant();
            var key = AccountingRules.Text(input.Key, 64, "Scenario key");
            var name = AccountingRules.Text(input.Name, 160, "Scenario name");
            var evidence = AccountingRules.Text(input.Evidence, 1000, "Scenario evidence");
            var units = CurrencyPolicy.Units(scope.BaseCurrency);
            foreach (var change in new[] { input.RevenueChange, input.ExpenseChange, input.ProcurementChange })
                _ = ForecastMath.Adjust(0, change, units);
            if (input.CustomerDelayDays is < 0 or > 365) throw Bad("Scenario collection delay must be between 0 and 365 days.");
            var normalized = input with { Code = code, Key = key, Name = name, Evidence = evidence };
            var hash = TradeRules.Hash(normalized);
            var existing = await Existing(company, key, hash, token);
            if (existing is not null) return existing;
            var baseline = await Find(company, input.BaseId, false, token);
            if (baseline.State != ForecastState.Approved || baseline.ScenarioBaseId.HasValue)
                throw Conflict("A scenario requires an approved baseline forecast.");
            var sequence = await NextVersion(company, code, input.BasisId, baseline.AsOf, true, token);
            var scenario = ForecastMath.Scenario(Read(baseline), normalized, units);
            return Add(scope, actor, code, name, sequence, key, hash, input.BasisId, baseline.Id,
                baseline.AsOf, baseline.Horizon, normalized, scenario);
        }, ct);

    public Task<ForecastVersion> Change(Guid actor, Guid org, Guid company, Guid id, string action, ForecastAction input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var row = await Find(company, id, true, token);
            if (row.Version != input.Version || row.State != ForecastState.Draft || actor == row.CreatedBy)
                throw Conflict("Approval or rejection requires a current draft and an independent Owner.");
            row.State = action switch
            {
                "approve" => ForecastState.Approved,
                "reject" => ForecastState.Rejected,
                _ => throw Bad("Invalid forecast action.")
            };
            row.ApprovedBy = actor;
            row.Version = Guid.NewGuid();
            access.Audit(scope, actor, id, $"Forecast:{action}", AccountingRules.Text(input.Reason, 500, "Action reason"));
            return row;
        }, ct);

    public async Task<PageResult<ForecastVersion>> List(Guid actor, Guid org, Guid company, string? code, bool? scenario,
        int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        await access.Resolve(actor, org, company, false, false, ct);
        var query = db.ForecastVersions.AsNoTracking().Where(x => x.CompanyId == company);
        if (code is not null)
        {
            var canonical = AccountingRules.Text(code, 40, "Forecast code").ToUpperInvariant();
            query = query.Where(x => x.Code == canonical);
        }
        if (scenario.HasValue) query = query.Where(x => (x.ScenarioBaseId != null) == scenario);
        return new(await query.OrderByDescending(x => x.AsOf).ThenBy(x => x.Code).ThenByDescending(x => x.Number)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct), await query.CountAsync(ct), page, size);
    }

    public async Task<object> Compare(Guid actor, Guid org, Guid company, Guid id, DateOnly cutoff, Guid? compareId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var row = await Find(company, id, false, ct);
        var snapshot = Read(row);
        var otherId = compareId ?? row.ScenarioBaseId ?? row.BasisId;
        var other = otherId.HasValue ? Read(await Find(company, otherId.Value, false, ct)) : null;
        if (cutoff < row.AsOf || cutoff > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            throw Bad("Comparison cutoff must be between the forecast cutoff and today.");
        var actual = await ledger.Lines(company, cutoff, row.AsOf.AddDays(1),
            snapshot.Rows.Select(x => x.AccountId).Distinct().ToArray(), true).ToListAsync(ct);
        var comparison = snapshot.Rows.Select(f =>
        {
            var amount = f.Start > cutoff ? (decimal?)null :
                actual.Where(x => x.AccountId == f.AccountId && x.Date >= f.Start && x.Date <= f.End && Counts(f.Kind, x.OriginalSource ?? x.Source))
                    .Sum(x => Actual(f.Kind, x.Debit, x.Credit));
            return new ForecastComparison(f, amount, f.Amount - f.Budget, amount - f.Amount);
        }).ToArray();
        var balances = await cash.OpeningCash(company, cutoff, ct);
        var completed = snapshot.CashBuckets.LastOrDefault(x => x.End <= cutoff);
        var cashThroughCutoff = cutoff <= snapshot.CashBuckets[^1].End ?
            snapshot.OpeningCash + snapshot.Cash.Where(x => x.Date <= cutoff).Sum(x => x.Amount) : (decimal?)null;
        decimal Result(ForecastSnapshot source) => source.Rows.Sum(x => x.Kind == BudgetKind.Revenue ? x.Amount : -x.Amount);
        var quarterViews = snapshot.CashBuckets.Chunk(3).Select((group, index) =>
        {
            var first = group[0].Start;
            var last = group[^1].End;
            var drivers = snapshot.Rows.Where(x => x.Start >= first && x.End <= last).ToArray();
            return new
            {
                HorizonQuarter = index + 1, Start = first, End = last,
                Revenue = drivers.Where(x => x.Kind == BudgetKind.Revenue).Sum(x => x.Amount),
                Expenses = drivers.Where(x => x.Kind == BudgetKind.Operating).Sum(x => x.Amount),
                ClosingCash = group[^1].Closing, Shortfall = group.Any(x => x.Shortfall)
            };
        }).ToArray();
        var result = new { Forecast = row, Snapshot = snapshot, Comparison = comparison, Cutoff = cutoff,
            CompareId = otherId, Comparative = other, OperatingResult = Result(snapshot),
            ComparativeOperatingResult = other is null ? (decimal?)null : Result(other),
            ActualCash = balances, ProjectedCashAtCutoff = cashThroughCutoff, CashDifference = balances - cashThroughCutoff,
            LastCompletedBucket = completed?.End, FirstProjectedShortfall = snapshot.CashBuckets.FirstOrDefault(x => x.Shortfall)?.Start,
            QuarterViews = quarterViews };
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<byte[]> Export(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        var row = await Find(company, id, false, ct);
        var snapshot = Read(row);
        var text = new StringBuilder("Type,AccountId,Start,End,Forecast,ApprovedBudget,OpeningCash,Inflows,Outflows,ClosingCash,LowCash,Currency,Version,AsOf\r\n");
        foreach (var line in snapshot.Rows)
            text.Append(FormattableString.Invariant($"Forecast,{line.AccountId},{line.Start:yyyy-MM-dd},{line.End:yyyy-MM-dd},{line.Amount},{line.Budget},,,,,,{snapshot.Currency},{row.Number},{row.AsOf:yyyy-MM-dd}\r\n"));
        foreach (var bucket in snapshot.CashBuckets)
            text.Append(FormattableString.Invariant($"Cash,,{bucket.Start:yyyy-MM-dd},{bucket.End:yyyy-MM-dd},,,{bucket.Opening},{bucket.Inflows},{bucket.Outflows},{bucket.Closing},{bucket.Lowest},{snapshot.Currency},{row.Number},{row.AsOf:yyyy-MM-dd}\r\n"));
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private async Task<decimal> Historical(Guid company, BudgetKind kind, ForecastDriver driver, DateOnly at, CancellationToken ct)
    {
        var periods = await db.Periods.AsNoTracking().Where(x => x.CompanyId == company && x.EndDate <= at)
            .OrderByDescending(x => x.EndDate).Take(driver.HistoryMonths).ToListAsync(ct);
        if (periods.Count != driver.HistoryMonths || periods.Zip(periods.Skip(1)).Any(pair => pair.Second.EndDate.AddDays(1) != pair.First.StartDate))
            throw Conflict("Historical-average driver requires continuous completed fiscal periods.");
        return await control.Spending(company, driver.AccountId, kind, periods[^1].StartDate, periods[0].EndDate, ct) / periods.Count;
    }

    private ForecastVersion Add<T>(Company scope, Guid actor, string code, string name, int sequence, string key, string hash,
        Guid? basis, Guid? scenario, DateOnly at, int horizon, T assumptions, ForecastSnapshot snapshot)
    {
        var row = Scoped(scope, new ForecastVersion { Code = code, Name = name, Number = sequence, RequestKey = key, RequestHash = hash,
            BasisId = basis, ScenarioBaseId = scenario, AsOf = at, Horizon = horizon, CreatedBy = actor,
            AssumptionsJson = JsonSerializer.Serialize(assumptions), SnapshotJson = JsonSerializer.Serialize(snapshot) });
        db.ForecastVersions.Add(row);
        access.Audit(scope, actor, row.Id, scenario.HasValue ? "ScenarioSnapshotPrepared" : "ForecastSnapshotPrepared");
        return row;
    }

    private async Task<int> NextVersion(Guid company, string code, Guid? basis, DateOnly at, bool scenario, CancellationToken ct)
    {
        var latest = await db.ForecastVersions.AsNoTracking().Where(x => x.CompanyId == company && x.Code == code)
            .OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct);
        if (latest?.Id != basis || (latest is not null && (latest.AsOf > at || latest.ScenarioBaseId.HasValue != scenario)))
            throw Conflict("Forecast revision must use the latest version of its own series and an ordered cutoff.");
        return (latest?.Number ?? 0) + 1;
    }

    private async Task<ForecastVersion?> Existing(Guid company, string key, string hash, CancellationToken ct)
    {
        var row = await db.ForecastVersions.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.RequestKey == key, ct);
        if (row is not null && row.RequestHash != hash) throw Conflict("Forecast key belongs to a different request.");
        return row;
    }

    private async Task<ForecastVersion> Find(Guid company, Guid id, bool tracking, CancellationToken ct) =>
        await (tracking ? db.ForecastVersions : db.ForecastVersions.AsNoTracking()).SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw Missing("Forecast version was not found.");

    private static ForecastSnapshot Read(ForecastVersion row) => JsonSerializer.Deserialize<ForecastSnapshot>(row.SnapshotJson)!;
}
