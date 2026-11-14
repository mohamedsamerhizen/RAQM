using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Services.Trading;
using RAQM.Api.Services.CashManagement;
using RAQM.Api.Services.FinancialClose;
using RAQM.Api.Services.FixedAssets;
using RAQM.Api.Services.Inventory;
using RAQM.Api.Services.BudgetControl;
using RAQM.Api.Services.ForeignExchange;
using RAQM.Api.Services.Intercompany;
using RAQM.Api.Services.TaxAccounting;

namespace RAQM.Api.Services.Accounting;
public sealed class PostingValidator(RaqmDbContext db, FinancialAccess access, TimeProvider clock, SubledgerPostingPolicy? subledger = null,
    TreasuryPostingPolicy? treasury = null, ClosePostingPolicy? close = null, AssetPostingPolicy? assets = null,
    InventoryPostingPolicy? inventory = null, BudgetControlService? budget = null, FxPostingPolicy? fx = null,
    IntercompanyPostingPolicy? intercompany = null, TaxPostingPolicy? taxes = null)
{
    public async Task<AccountingPeriod> Validate(Guid actor, Company company, PostingRequest r, CancellationToken ct, bool commitBudgetEvidence = false)
    {
        AccountingRules.Lines(r.Lines, r.Currency, true);
        var taxSource = await (taxes ?? new TaxPostingPolicy(db)).Validate(actor, company, r, ct);
        await (subledger ?? new SubledgerPostingPolicy(db,access)).Validate(actor,company,r,ct,taxSource);
        if (r.Currency != company.BaseCurrency)
            throw new ApiException(400, "Only company base-currency posting is supported.", "currency_mismatch");
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (r.AccountingDate > today || r.DocumentDate > today || r.DocumentDate.Year < 1900)
            throw new ApiException(400, "Future posting/document dates and pre-1900 documents are not supported.", "journal_date");
        if (r.AccountingDate < today)
        {
            if (!r.AllowBackdated)
                throw new ApiException(400, "Backdated posting requires explicit approval and a reason.", "backdate_approval");
            AccountingRules.Text(r.Reason, 500, "Backdating reason");
            await access.Resolve(actor, company.OrganizationId, company.Id, true, true, ct);
        }

        var period = await db.Periods.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company.Id && x.StartDate <= r.AccountingDate && x.EndDate >= r.AccountingDate, ct) ?? throw new ApiException(400, "No accounting period contains the posting date.", "period_missing");
        if (period.State != PeriodState.Open)
            throw new ApiException(409, "The accounting period is closed or locked.", "period_closed");
        await (treasury ?? new TreasuryPostingPolicy(db)).Validate(actor, company, r, ct);
        await (close ?? new ClosePostingPolicy(db)).Validate(actor, company.Id, r, ct);
        await (assets ?? new AssetPostingPolicy(db)).Validate(actor, company.Id, r, ct);
        await (inventory ?? new InventoryPostingPolicy(db)).Validate(actor, company.Id, r, ct);
        await (fx ?? new FxPostingPolicy(db)).Validate(actor, company.Id, r, ct);
        await (intercompany ?? new IntercompanyPostingPolicy(db)).Validate(actor, company, r, ct);
        var accounts = await db.Accounts.AsNoTracking().Where(x => x.CompanyId == company.Id).ToDictionaryAsync(x => x.Id, ct);
        foreach (var line in r.Lines)
        {
            if (!accounts.TryGetValue(line.AccountId, out var account))
                throw new ApiException(400, "A journal account is outside the authorized company.", "account_not_found");
            if (!account.IsActive || !account.IsPosting)
                throw new ApiException(400, "Account must be active and posting-enabled.", "account_ineligible");
            var parents = new HashSet<Guid>
            {
                account.Id
            };
            while (account.ParentId is { } parent)
            {
                if (!parents.Add(parent) || !accounts.TryGetValue(parent, out account) || !account.IsActive)
                    throw new ApiException(400, "Account hierarchy is invalid or inactive.", "account_ineligible");
            }
        }

        if (r.SourceModule == "OpeningBalance")
        {
            var first = await db.FiscalYears.Where(x => x.CompanyId == company.Id).MinAsync(x => (DateOnly? )x.StartDate, ct);
            if (first != r.AccountingDate)
                throw new ApiException(400, "Opening balances must use the first fiscal year's start date.", "opening_date");
            if (!await db.OpeningBatches.AnyAsync(x => x.CompanyId == company.Id && x.Id == r.SourceDocumentId && x.JournalId == r.DraftId, ct))
                throw new ApiException(400, "Opening source batch does not match this journal.", "opening_source");
        }

        if (r.ReversesJournalId is { } originalId)
        {
            var original = await db.Journals.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == company.Id && x.Id == originalId && x.State == JournalState.Posted, ct) ?? throw new ApiException(404, "Original posted journal not found.", "reversal_original");
            if (r.AccountingDate < original.PostingDate)
                throw new ApiException(409, "A reversal cannot precede its original journal.", "reversal_date");
            if (await db.CostAllocationExecutions.AnyAsync(x => x.CompanyId == company.Id &&
                (x.JournalId == originalId || x.CorrectionJournalId == originalId), ct))
                throw new ApiException(409, "Correct financial allocations through the allocation workflow.", "allocation_reversal");
            if (original.ReversesJournalId != null)
                throw new ApiException(409, "Reverse the business correction explicitly; reversal-of-reversal is not supported.", "reversal_chain");
            if (r.SourceModule != "Reversal" || r.SourceDocumentId != originalId || !original.Lines.OrderBy(x => x.Number).Select(x => new JournalDraftLine(x.AccountId, x.Credit, x.Debit, x.Description)).SequenceEqual(r.Lines))
                throw new ApiException(400, "Reversal must exactly negate original lines and retain its source link.", "reversal_lines");
            if (await db.Journals.AnyAsync(x => x.CompanyId == company.Id && x.ReversesJournalId == originalId && x.State == JournalState.Posted, ct))
                throw new ApiException(409, "Journal has already been reversed.", "already_reversed");
        }
        else if (r.SourceModule == "Reversal")
            throw new ApiException(400, "Reversal source requires an original journal.", "reversal_original");
        if (budget is not null) await budget.CheckPosting(actor, company, r, commitBudgetEvidence, ct);
        return period;
    }
}
