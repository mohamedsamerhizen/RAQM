using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Budgeting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Budgeting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.FinancialReporting;
using RAQM.Api.Services.FinancialDimensions;
using static RAQM.Api.Helpers.Budgeting.BudgetMath;

namespace RAQM.Api.Services.BudgetControl;

public sealed partial class BudgetControlService(RaqmDbContext db, FinancialLedger ledger, FinancialAccess access,
    IFinancialTransaction transactions, TimeProvider clock, GovernanceGate? governance = null,
    DimensionBudgetService? scopedBudgets = null)
{
    internal static T Scoped<T>(Company scope, T evidence) where T : FinancialEvidence
    {
        evidence.CompanyId = scope.Id;
        evidence.OrganizationId = scope.OrganizationId;
        evidence.TenantId = scope.TenantId;
        return evidence;
    }

    public async Task<LedgerAccount> Account(Guid company, Guid accountId, BudgetKind kind, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == accountId, ct);
        if (account is not { IsActive: true, IsPosting: true } || !Accepts(kind, account.Classification) ||
            (kind == BudgetKind.Inventory && !await db.InventoryAccounts.AnyAsync(x => x.CompanyId == company && x.AccountId == accountId &&
                x.Role == InventoryRole.Inventory, ct)) ||
            (kind == BudgetKind.Capital && !await db.AssetControls.AnyAsync(x => x.CompanyId == company && x.AccountId == accountId &&
                x.Role == AssetControlRole.Cost, ct)))
            throw Bad("Use an active posting account of the budget category and company.");
        return account;
    }

    public Task<BudgetPolicy> Configure(Guid actor, Guid org, Guid company, PolicyRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            await Account(company, request.AccountId, request.Kind, token);
            if (request.Kind == BudgetKind.Revenue || !Enum.IsDefined(request.Mode) || request.WarningPercent is <= 0 or > 100 ||
                Round(request.WarningPercent, 4) != request.WarningPercent || request.EffectiveDate.Year < 1900)
                throw Bad("Supply an expenditure policy, effective date and warning threshold from 0 to 100 percent.");
            if (await db.BudgetPolicies.AnyAsync(x => x.CompanyId == company && x.AccountId == request.AccountId &&
                x.EffectiveDate >= request.EffectiveDate, token))
                throw Conflict("Policy revisions must follow the previous effective date.");
            var row = Scoped(scope, new BudgetPolicy { AccountId = request.AccountId, Kind = request.Kind, Mode = request.Mode,
                EffectiveDate = request.EffectiveDate, WarningPercent = request.WarningPercent, CreatedBy = actor,
                Reason = AccountingRules.Text(request.Reason, 500, "Policy reason") });
            db.BudgetPolicies.Add(row);
            if (request.Kind == BudgetKind.Inventory && request.Mode != ControlMode.Off)
            {
                var adopted = await AdoptProcurement(scope, actor, row, token);
                await db.SaveChangesAsync(token);
                if (scopedBudgets is not null)
                    foreach (var document in adopted)
                        await scopedBudgets.CheckProcurement(company, document, token);
                if (request.Mode == ControlMode.Hard)
                {
                    var years = await db.FiscalYears.AsNoTracking().Where(x => x.CompanyId == company &&
                        x.EndDate >= request.EffectiveDate).ToListAsync(token);
                    foreach (var year in years)
                    {
                        var date = ControlDate(year, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
                        var available = await Availability(company, row, year, date, DateOnly.MaxValue, token);
                        if (available.Available < 0)
                            throw Conflict("Hard policy activation requires authority for retained actual spending and adopted open commitments.");
                    }
                }
            }
            access.Audit(scope, actor, row.Id, "BudgetPolicyConfigured", row.Reason);
            return row;
        }, ct);

    public Task<BudgetOverride> Grant(Guid actor, Guid org, Guid company, OverrideRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            CurrencyPolicy.Validate(request.MaxExcess, CurrencyPolicy.Units(scope.BaseCurrency));
            var intent = AccountingRules.Text(request.Intent, 150, "Budget intent");
            var date = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            if (request.MaxExcess == 0 || request.Expires < date || request.Expires.DayNumber - date.DayNumber > 30 ||
                !await db.BudgetPolicies.AnyAsync(x => x.CompanyId == company && x.AccountId == request.AccountId, token))
                throw Bad("Override requires a configured account, positive ceiling and expiry within 30 days.");
            if (await db.BudgetOverrides.AnyAsync(x => x.CompanyId == company && x.Intent == intent && x.AccountId == request.AccountId, token))
                throw Conflict("An override already exists for the account and intent.");
            var row = Scoped(scope, new BudgetOverride { AccountId = request.AccountId, Intent = intent, MaxExcess = request.MaxExcess,
                Expires = request.Expires, CreatedBy = actor, Reason = AccountingRules.Text(request.Reason, 500, "Override reason") });
            db.BudgetOverrides.Add(row);
            access.Audit(scope, actor, row.Id, "BudgetOverrideGranted", row.Reason);
            return row;
        }, ct);

    public async Task<BudgetPolicy?> Policy(Guid company, Guid account, DateOnly date, CancellationToken ct) =>
        await db.BudgetPolicies.AsNoTracking().Where(x => x.CompanyId == company && x.AccountId == account && x.EffectiveDate <= date)
            .OrderByDescending(x => x.EffectiveDate).FirstOrDefaultAsync(ct);

    internal async Task<BudgetVersion?> Published(Guid company, Guid year, BudgetKind kind, DateOnly asOf, CancellationToken ct,
        DateOnly? knownOn = null) =>
        await (from version in db.BudgetVersions.AsNoTracking() join budget in db.FinancialBudgets on version.BudgetId equals budget.Id
            where version.CompanyId == company && budget.CompanyId == company && budget.FiscalYearId == year && budget.Kind == kind &&
                version.EffectiveDate != null && version.EffectiveDate <= asOf && (knownOn == null || version.ActivatedOn <= knownOn)
            orderby version.EffectiveDate descending, version.Number descending
            select version).Include(x => x.Allocations).FirstOrDefaultAsync(ct);

    internal async Task<decimal> Spending(Guid company, Guid account, BudgetKind kind, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var rows = await ledger.Lines(company, end, start, [account], performance: true).ToListAsync(ct);
        return rows.Where(x => Counts(kind, x.OriginalSource ?? x.Source)).Sum(x => Actual(kind, x.Debit, x.Credit));
    }

    internal async Task<BudgetAvailability> Availability(Guid company, BudgetPolicy policy, FiscalYear year,
        DateOnly policyDate, DateOnly cutoff, CancellationToken ct, BudgetVersion? proposed = null)
    {
        var version = proposed ?? await Published(company, year.Id, policy.Kind, policyDate, ct);
        var committed = await (from movement in db.BudgetCommitments.AsNoTracking()
            join period in db.Periods on movement.PeriodId equals period.Id
            where movement.CompanyId == company && period.CompanyId == company && movement.AccountId == policy.AccountId &&
                period.FiscalYearId == year.Id && movement.Date <= cutoff select movement.Amount).SumAsync(ct);
        var actual = await Spending(company, policy.AccountId, policy.Kind, year.StartDate,
            cutoff < year.EndDate ? cutoff : year.EndDate, ct);
        return new(policy.AccountId, version?.Allocations.Where(x => x.AccountId == policy.AccountId).Sum(x => x.Amount) ?? 0, actual, committed);
    }

    public async Task CheckPosting(Guid actor, Company scope, PostingRequest request, bool commitEvidence, CancellationToken ct)
    {
        var ids = request.Lines.Select(x => x.AccountId).Distinct().ToArray();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (!await db.BudgetPolicies.AnyAsync(x => x.CompanyId == scope.Id && ids.Contains(x.AccountId) &&
            x.EffectiveDate <= today, ct)) return;
        var source = request.SourceModule;
        if (request.ReversesJournalId is { } original)
            source = await db.Journals.Where(x => x.CompanyId == scope.Id && x.Id == original).Select(x => x.SourceModule).SingleAsync(ct);
        var year = await Year(scope.Id, request.AccountingDate, ct);
        var policyDate = ControlDate(year, today);
        foreach (var group in request.Lines.GroupBy(x => x.AccountId))
        {
            var accountId = group.Key;
            var amount = group.Sum(x => x.Debit - x.Credit);
            if (amount <= 0) continue;
            var policy = await Policy(scope.Id, accountId, policyDate, ct);
            if (policy is null || policy.Mode == ControlMode.Off || !Counts(policy.Kind, source)) continue;
            var released = request.ReversesJournalId.HasValue ? 0 : -await (from movement in db.BudgetCommitments
                join period in db.Periods on movement.PeriodId equals period.Id
                where movement.CompanyId == scope.Id && period.CompanyId == scope.Id && movement.AccountId == accountId &&
                    period.FiscalYearId == year.Id && movement.Amount < 0 &&
                    ((request.SourceModule == "Inventory.Receipt" && movement.StockOperationId == request.SourceDocumentId) ||
                     ((request.SourceModule == "FixedAssets.Acquisition" || request.SourceModule == "FixedAssets.Improvement") &&
                      movement.AssetOperationId == request.SourceDocumentId))
                select movement.Amount).SumAsync(ct);
            await Enforce(scope, actor, policy, year, policyDate, amount,
                $"journal:{request.SourceDocumentId:N}:{TradeRules.Hash(request.IdempotencyKey.Trim())}", request.SourceDocumentId, ct, released, commitEvidence);
        }
    }

    internal Task<FiscalYear> Year(Guid company, DateOnly date, CancellationToken ct) =>
        RequiredYear(company, date, ct);

    private static DateOnly ControlDate(FiscalYear year, DateOnly today) => today < year.EndDate ? today : year.EndDate;

    private async Task<FiscalYear> RequiredYear(Guid company, DateOnly date, CancellationToken ct) =>
        await db.FiscalYears.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.StartDate <= date && x.EndDate >= date, ct)
            ?? throw Conflict("No fiscal year contains this budget control date.");

    internal async Task Enforce(Company scope, Guid actor, BudgetPolicy policy, FiscalYear year, DateOnly date,
        decimal increase, string intent, Guid subject, CancellationToken ct, decimal alreadyReleased = 0, bool commitEvidence = true)
    {
        if (increase <= alreadyReleased || policy.Mode == ControlMode.Off) return;
        // Include every committed and posted effect in the fiscal year when authorizing new exposure.
        // A backdated action must not spend funds already used by a later posting.
        var availability = await Availability(scope.Id, policy, year, date, DateOnly.MaxValue, ct);
        var excess = Math.Max(0, Math.Min(increase - alreadyReleased, increase - availability.Available));
        if (excess > 0 && policy.Mode == ControlMode.Hard)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var grant = await db.BudgetOverrides.SingleOrDefaultAsync(x => x.CompanyId == scope.Id && x.AccountId == policy.AccountId &&
                x.Intent == intent && x.UsedAt == null && x.Expires >= today && x.MaxExcess >= excess, ct);
            if (grant is null || grant.UsedAt.HasValue)
                throw new RAQM.Api.Helpers.ApiException(409,
                    FormattableString.Invariant($"Budget for account {policy.AccountId} is exceeded by {excess}. Intent {intent}."), "budget_exceeded");
            if (commitEvidence)
            {
                if (governance is not null) await governance.Enforce(scope, actor, FinancialDuty.BudgetOverride, grant.Id, ct);
                grant.UsedAt = clock.GetUtcNow();
                access.Audit(scope, actor, grant.Id, "BudgetOverrideConsumed", grant.Reason);
            }
        }
        if (commitEvidence && (excess > 0 || availability.Actual + availability.Commitments + increase >= availability.Budget * (policy.WarningPercent / 100)))
            access.Audit(scope, actor, subject, "BudgetThresholdReached",
                FormattableString.Invariant($"Account {policy.AccountId}; available {availability.Available}; increase {increase}; intent {intent}."));
    }

    public async Task<object> Settings(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return new
        {
            Policies = await db.BudgetPolicies.AsNoTracking().Where(x => x.CompanyId == company)
                .OrderBy(x => x.AccountId).ThenBy(x => x.EffectiveDate).ToListAsync(ct),
            Overrides = await db.BudgetOverrides.AsNoTracking().Where(x => x.CompanyId == company)
                .OrderBy(x => x.AccountId).ThenBy(x => x.Intent).ToListAsync(ct)
        };
    }

    public async Task<BudgetAvailability> Inquiry(Guid actor, Guid org, Guid company, Guid account, DateOnly cutoff, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var year = await Year(company, cutoff, ct);
        var policy = await Policy(company, account, cutoff, ct) ?? throw Missing("No policy exists for the account at cutoff.");
        var result = await Availability(company, policy, year, cutoff, cutoff, ct);
        await transaction.CommitAsync(ct);
        return result;
    }
}
