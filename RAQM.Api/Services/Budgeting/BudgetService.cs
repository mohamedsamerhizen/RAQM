using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Budgeting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.BudgetControl;
using RAQM.Api.Services.FinancialDimensions;
using static RAQM.Api.Helpers.Budgeting.BudgetMath;
using static RAQM.Api.Services.BudgetControl.BudgetControlService;

namespace RAQM.Api.Services.Budgeting;

public sealed class BudgetService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    BudgetControlService control, TimeProvider clock, DimensionBudgetService? scopedBudgets = null)
{
    public Task<FinancialBudget> Create(Guid actor, Guid org, Guid company, BudgetRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var code = AccountingRules.Text(input.Code, 40, "Budget code").ToUpperInvariant();
            CurrencyPolicy.Validate(input.ApprovalLimit, CurrencyPolicy.Units(scope.BaseCurrency));
            if (!Enum.IsDefined(input.Kind) || input.ApprovalLimit <= 0 ||
                !await db.FiscalYears.AnyAsync(x => x.CompanyId == company && x.Id == input.FiscalYearId, token))
                throw Bad("Select a company fiscal year, budget kind and positive approval limit.");
            if (await db.FinancialBudgets.AnyAsync(x => x.CompanyId == company &&
                (x.Code == code || (x.FiscalYearId == input.FiscalYearId && x.Kind == input.Kind)), token))
                throw Conflict("The company budget code or fiscal year and classification already exists.");
            var budget = Scoped(scope, new FinancialBudget { FiscalYearId = input.FiscalYearId, OwnerId = actor, Code = code,
                Kind = input.Kind, Name = AccountingRules.Text(input.Name, 160, "Name"), ApprovalLimit = input.ApprovalLimit,
                Description = AccountingRules.Text(input.Description, 1000, "Description") });
            db.FinancialBudgets.Add(budget);
            access.Audit(scope, actor, budget.Id, "BudgetCreated");
            return budget;
        }, ct);

    public Task<BudgetVersion> Prepare(Guid actor, Guid org, Guid company, Guid budgetId, BudgetVersionRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var budget = await FindBudget(company, budgetId, token);
            var key = AccountingRules.Text(input.Key, 64, "Version key");
            var evidence = AccountingRules.Text(input.Evidence, 1000, "Version evidence");
            if ((input.SeedFromPriorActuals ? 1 : 0) + (input.CopyVersionId.HasValue ? 1 : 0) + (input.Lines is not null ? 1 : 0) != 1)
                throw Bad("Choose manual allocation, a source version or previous-year actuals.");
            var periods = await Periods(company, budget.FiscalYearId, token);
            var units = CurrencyPolicy.Units(scope.BaseCurrency);
            var rows = new Dictionary<(Guid Account, Guid Period), decimal>();
            if (input.Lines is { } lines)
            {
                if (lines.Count is < 1 or > 200 || lines.Any(x => x is null) || lines.Select(x => x.AccountId).Distinct().Count() != lines.Count)
                    throw Bad("Supply one to 200 distinct budget accounts.");
                foreach (var line in lines)
                {
                    await control.Account(company, line.AccountId, budget.Kind, token);
                    var amounts = Allocate(line, periods.Count, units);
                    for (var i = 0; i < periods.Count; i++) rows.Add((line.AccountId, periods[i].Id), amounts[i]);
                }
            }
            else if (input.CopyVersionId is { } sourceId)
            {
                var source = await FindVersion(company, sourceId, false, token);
                var original = await FindBudget(company, source.BudgetId, token);
                if (original.Kind != budget.Kind) throw Bad("Copy requires the same budget classification.");
                var originalPeriods = (await Periods(company, original.FiscalYearId, token)).ToDictionary(x => x.Id, x => x.Number);
                foreach (var group in source.Allocations.GroupBy(x => x.AccountId))
                {
                    await control.Account(company, group.Key, budget.Kind, token);
                    foreach (var amount in group)
                    {
                        var period = periods.SingleOrDefault(x => x.Number == originalPeriods[amount.PeriodId])
                            ?? throw Conflict("The fiscal period counts differ.");
                        rows.Add((group.Key, period.Id), amount.Amount);
                    }
                }
            }
            else
            {
                var year = await db.FiscalYears.AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == budget.FiscalYearId, token);
                var prior = await db.FiscalYears.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                    x.EndDate == year.StartDate.AddDays(-1), token) ?? throw Conflict("Previous fiscal year does not exist.");
                var accounts = await db.Accounts.AsNoTracking().Where(x => x.CompanyId == company && x.IsActive && x.IsPosting).ToListAsync(token);
                var eligible = accounts.Where(x => Accepts(budget.Kind, x.Classification)).Select(x => x.Id).ToArray();
                if (budget.Kind == BudgetKind.Capital)
                    eligible = await db.AssetControls.Where(x => x.CompanyId == company && x.Role == Entities.FixedAssets.AssetControlRole.Cost &&
                        eligible.Contains(x.AccountId)).Select(x => x.AccountId).ToArrayAsync(token);
                if (budget.Kind == BudgetKind.Inventory)
                    eligible = await db.InventoryAccounts.Where(x => x.CompanyId == company && x.Role == Entities.Inventory.InventoryRole.Inventory &&
                        eligible.Contains(x.AccountId)).Select(x => x.AccountId).ToArrayAsync(token);
                if (eligible.Length > 200) throw Conflict("Prior-actual seeding supports up to 200 eligible accounts.");
                var priorPeriods = (await Periods(company, prior.Id, token)).ToDictionary(x => x.Number);
                foreach (var account in eligible)
                foreach (var period in periods)
                {
                    if (!priorPeriods.TryGetValue(period.Number, out var previous)) throw Conflict("The fiscal period counts differ.");
                    rows.Add((account, period.Id), Math.Max(0, await control.Spending(company, account, budget.Kind,
                        previous.StartDate, previous.EndDate, token)));
                }
            }
            var hash = TradeRules.Hash(input with { Key = key, Evidence = evidence });
            return await SaveDraft(scope, actor, budget, key, hash, evidence, input.CopyVersionId, rows, token);
        }, ct);

    public Task<BudgetVersion> Transfer(Guid actor, Guid org, Guid company, Guid budgetId, BudgetTransferRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var budget = await FindBudget(company, budgetId, token);
            var key = AccountingRules.Text(input.Key, 64, "Transfer key");
            var evidence = AccountingRules.Text(input.Evidence, 1000, "Transfer evidence");
            var hash = TradeRules.Hash(input with { Key = key, Evidence = evidence });
            CurrencyPolicy.Validate(input.Amount, CurrencyPolicy.Units(scope.BaseCurrency));
            var prior = await Existing(company, budgetId, key, hash, token);
            if (prior is not null) return prior;
            var from = (input.FromAccountId, input.FromPeriodId);
            var to = (input.ToAccountId, input.ToPeriodId);
            if (from == to || input.Amount <= 0) throw Bad("Use distinct transfer lines and a positive amount.");
            var active = await FindVersion(company, input.ActiveVersionId, false, token);
            if (active.BudgetId != budget.Id || active.State != BudgetState.Active) throw Conflict("Select the current active budget version.");
            await control.Account(company, to.Item1, budget.Kind, token);
            var periods = await Periods(company, budget.FiscalYearId, token);
            if (!periods.Any(x => x.Id == to.Item2 && x.State == PeriodState.Open) ||
                !periods.Any(x => x.Id == from.Item2 && x.State == PeriodState.Open))
                throw Conflict("Both transfer periods must be open periods of the same budget.");
            var rows = active.Allocations.ToDictionary(x => (x.AccountId, x.PeriodId), x => x.Amount);
            if (rows.GetValueOrDefault(from) < input.Amount) throw Conflict("Source period allocation cannot cover this transfer.");
            rows[from] -= input.Amount;
            rows[to] = rows.GetValueOrDefault(to) + input.Amount;
            return await SaveDraft(scope, actor, budget, key, hash, $"Transfer {input.Amount} from {from} to {to}. {evidence}",
                active.Id, rows, token);
        }, ct);

    public Task<BudgetVersion> Change(Guid actor, Guid org, Guid company, Guid id, string action, BudgetAction input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var row = await FindVersion(company, id, true, token);
            if (row.Version != input.Version) throw Conflict("The budget version changed.");
            var budget = await FindBudget(company, row.BudgetId, token);
            var reason = AccountingRules.Text(input.Reason, 500, "Budget action reason");
            switch (action)
            {
                case "submit" when row.State == BudgetState.Draft && actor == row.CreatedBy:
                    if (input.ReviewerId is not { } reviewer || reviewer == row.CreatedBy)
                        throw Bad("Assign an independent company reviewer.");
                    await access.Resolve(reviewer, org, company, true, false, token);
                    row.ReviewerId = reviewer;
                    row.State = BudgetState.Submitted;
                    break;
                case "review" when row.State == BudgetState.Submitted && actor == row.ReviewerId:
                    row.State = BudgetState.Reviewed;
                    break;
                case "approve" when row.State == BudgetState.Reviewed:
                    await access.Resolve(actor, org, company, true, true, token);
                    if (actor == row.CreatedBy || row.Allocations.Sum(x => x.Amount) > budget.ApprovalLimit)
                        throw Conflict("Independent approval and the budget's approval limit are required.");
                    row.ApprovedBy = actor;
                    row.State = BudgetState.Approved;
                    break;
                case "activate" when row.State == BudgetState.Approved:
                    await access.Resolve(actor, org, company, true, true, token);
                    await Activate(scope, row, budget, input.EffectiveDate, token);
                    break;
                case "reject" when row.State is BudgetState.Submitted or BudgetState.Reviewed or BudgetState.Approved:
                    await access.Resolve(actor, org, company, true, true, token);
                    row.State = BudgetState.Rejected;
                    break;
                default:
                    throw Conflict("Invalid budget action for this actor and state.");
            }
            row.Version = Guid.NewGuid();
            access.Audit(scope, actor, id, $"BudgetVersion:{action}", reason);
            return row;
        }, ct);

    private async Task Activate(Company scope, BudgetVersion row, FinancialBudget budget, DateOnly? effective, CancellationToken ct)
    {
        var year = await db.FiscalYears.AsNoTracking().SingleAsync(x => x.CompanyId == scope.Id && x.Id == budget.FiscalYearId, ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var date = effective ?? today;
        var periods = await Periods(scope.Id, year.Id, ct);
        if (!periods.Any(x => x.StartDate <= date && x.EndDate >= date && x.State == PeriodState.Open))
            throw Conflict("Activation requires an effective date in an open fiscal period.");
        var active = await db.BudgetVersions.Include(x => x.Allocations).SingleOrDefaultAsync(x => x.CompanyId == scope.Id &&
            x.BudgetId == budget.Id && x.State == BudgetState.Active, ct);
        if (row.ActiveAtPreparationId != active?.Id ||
            (active is not null && (row.Number <= active.Number || date < active.EffectiveDate)))
            throw Conflict("A newer or different revision is active. Prepare a revision from the current version.");
        if (active is not null && date > today && year.StartDate <= today)
            throw Conflict("Schedule a future fiscal year's initial version, or activate revisions effective today or earlier.");
        var closed = periods.Where(x => x.State != PeriodState.Open).Select(x => x.Id).ToHashSet();
        var prior = (active?.Allocations ?? []).Where(x => closed.Contains(x.PeriodId)).ToDictionary(x => (x.AccountId, x.PeriodId), x => x.Amount);
        var next = row.Allocations.Where(x => closed.Contains(x.PeriodId)).ToDictionary(x => (x.AccountId, x.PeriodId), x => x.Amount);
        if (prior.Keys.Concat(next.Keys).Any(key => prior.GetValueOrDefault(key) != next.GetValueOrDefault(key)))
            throw Conflict("Publication cannot change a closed fiscal allocation.");
        if (scopedBudgets is not null) await scopedBudgets.ValidateActivation(scope.Id, row.Id, active?.Id, year.Id, ct);
        var accounts = row.Allocations.Select(x => x.AccountId).Distinct().ToArray();
        foreach (var account in accounts) await control.Account(scope.Id, account, budget.Kind, ct);
        var controlled = (await db.BudgetPolicies.AsNoTracking().Where(x => x.CompanyId == scope.Id &&
            x.Kind == budget.Kind && x.EffectiveDate <= today).ToListAsync(ct)).GroupBy(x => x.AccountId)
            .Select(g => g.MaxBy(x => x.EffectiveDate)!).Where(x => x.Mode == ControlMode.Hard);
        foreach (var policy in controlled)
        {
            var availability = await control.Availability(scope.Id, policy, year, date, DateOnly.MaxValue, ct, row);
            if (availability.Available < 0)
                throw Conflict("The revision would reduce authority below actual spending and outstanding commitments.");
        }
        if (active is not null)
        {
            active.State = BudgetState.Superseded;
            active.Version = Guid.NewGuid();
            await db.SaveChangesAsync(ct);
        }
        row.EffectiveDate = date;
        row.ActivatedOn = today;
        row.State = BudgetState.Active;
    }

    public async Task<PageResult<FinancialBudget>> List(Guid actor, Guid org, Guid company, Guid? year, BudgetKind? kind,
        string? search, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        AccountingRules.OptionalText(search, 100);
        await access.Resolve(actor, org, company, false, false, ct);
        var query = db.FinancialBudgets.AsNoTracking().Where(x => x.CompanyId == company);
        if (year.HasValue) query = query.Where(x => x.FiscalYearId == year);
        if (kind.HasValue) query = query.Where(x => x.Kind == kind);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => EF.Functions.ILike(x.Code, "%" + search + "%") ||
            EF.Functions.ILike(x.Name, "%" + search + "%"));
        return new(await query.OrderBy(x => x.Code).Skip((page - 1) * size).Take(size).ToListAsync(ct), await query.CountAsync(ct), page, size);
    }

    public async Task<IReadOnlyList<BudgetVersion>> Versions(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        await FindBudget(company, id, ct);
        return await db.BudgetVersions.AsNoTracking().Where(x => x.CompanyId == company && x.BudgetId == id)
            .OrderByDescending(x => x.Number).Include(x => x.Allocations).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.AccountingAudit.AsNoTracking().Where(x => x.CompanyId == company && x.EntityId == id &&
            (x.Action.StartsWith("Budget") || x.Action.StartsWith("Forecast") || x.Action.StartsWith("Scenario")))
            .OrderBy(x => x.At).ThenBy(x => x.Id)
            .Select(x => new AuditResponse(x.Id, x.EntityId, x.Action, x.ActorId, x.At, x.Reason)).ToListAsync(ct);
    }

    internal async Task<FinancialBudget> FindBudget(Guid company, Guid id, CancellationToken ct) =>
        await db.FinancialBudgets.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw Missing("Budget does not exist.");

    internal async Task<BudgetVersion> FindVersion(Guid company, Guid id, bool track, CancellationToken ct)
    {
        var query = track ? db.BudgetVersions.AsQueryable() : db.BudgetVersions.AsNoTracking();
        return await query.Include(x => x.Allocations).SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw Missing("Budget version does not exist.");
    }

    internal Task<List<AccountingPeriod>> Periods(Guid company, Guid year, CancellationToken ct) =>
        db.Periods.AsNoTracking().Where(x => x.CompanyId == company && x.FiscalYearId == year).OrderBy(x => x.Number).ToListAsync(ct);

    private async Task<BudgetVersion?> Existing(Guid company, Guid budget, string key, string hash, CancellationToken ct)
    {
        var row = await db.BudgetVersions.AsNoTracking().Include(x => x.Allocations).SingleOrDefaultAsync(x => x.CompanyId == company &&
            x.BudgetId == budget && x.Key == key, ct);
        if (row is not null && row.Hash != hash) throw Conflict("The version key was already used for different terms.");
        return row;
    }

    private async Task<BudgetVersion> SaveDraft(Company scope, Guid actor, FinancialBudget budget, string key, string hash,
        string evidence, Guid? basis, Dictionary<(Guid Account, Guid Period), decimal> rows, CancellationToken ct)
    {
        var prior = await Existing(scope.Id, budget.Id, key, hash, ct);
        if (prior is not null) return prior;
        var units = CurrencyPolicy.Units(scope.BaseCurrency);
        CurrencyPolicy.Validate(rows.Values.Sum(), units);
        if (rows.Count == 0 || rows.Count > 4800) throw Bad("Budget allocations must have a bounded nonempty population.");
        var number = 1 + (await db.BudgetVersions.Where(x => x.CompanyId == scope.Id && x.BudgetId == budget.Id)
            .MaxAsync(x => (int?)x.Number, ct) ?? 0);
        var active = await db.BudgetVersions.Where(x => x.CompanyId == scope.Id && x.BudgetId == budget.Id && x.State == BudgetState.Active)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        var row = Scoped(scope, new BudgetVersion { BudgetId = budget.Id, Number = number, CreatedBy = actor,
            Evidence = AccountingRules.Text(evidence, 1000, "Version evidence"), Key = key, Hash = hash, BasisVersionId = basis,
            ActiveAtPreparationId = active });
        row.Allocations = rows.Select(x => Scoped(scope, new BudgetAllocation { BudgetVersionId = row.Id, AccountId = x.Key.Account,
            PeriodId = x.Key.Period, Amount = x.Value })).ToList();
        db.BudgetVersions.Add(row);
        access.Audit(scope, actor, row.Id, "BudgetDraftPrepared");
        return row;
    }
}
