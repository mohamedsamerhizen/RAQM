using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Consolidation;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.DTOs.TaxAccounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.CashManagement;
using RAQM.Api.Entities.Consolidation;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.FixedAssets;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.FinancialDimensions;
using RAQM.Api.Services.Inventory;
using RAQM.Api.Services.MultiCurrency;
using static RAQM.Api.Helpers.Governance.GovernanceRules;

namespace RAQM.Api.Services.ApprovalGovernance;

public sealed class GovernanceSources(RaqmDbContext db, DimensionPosting dimensions)
{
    public static Guid PaymentIdentity(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes("payment:" + key.Trim()))[..16]);

    internal async Task<FinancialIntent> Reversal(Company company, Guid maker, Guid id, ReverseJournalRequest request, CancellationToken ct)
    {
        var original = await db.Journals.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x =>
            x.CompanyId == company.Id && x.Id == id && x.SourceModule == "ManualJournal" &&
            x.State == JournalState.Posted && x.ReversesJournalId == null, ct) ?? throw Missing();
        if (request.PostingDate < original.PostingDate)
            throw Conflict("A reversal cannot precede its original journal.");
        return ReversalIntent(original, maker, JournalService.ReversalRequest(original, request));
    }

    internal static FinancialIntent ReversalIntent(JournalEntry original, Guid maker, PostingRequest request) =>
        new(FinancialDuty.ManualJournal, original.Id, original.Id, maker, TradeRules.Hash(new
        {
            original.Version,
            Posting = AccountingRules.Fingerprint(request),
            Key = AccountingRules.Text(request.IdempotencyKey, 128, "Idempotency key"),
            request.AllowBackdated
        }), original.Lines.Sum(x => x.Debit), original.Currency);

    public async Task<FinancialIntent> Payment(Company company, Guid maker, PaymentRequest request, CancellationToken ct)
    {
        var key = AccountingRules.Text(request.IdempotencyKey, 64, "Idempotency key");
        CurrencyPolicy.Validate(request.Amount, CurrencyPolicy.Units(company.BaseCurrency));
        if (request.Amount <= 0 || request.Allocations is { Count: > 100 } ||
            request.Allocations?.Any(x => x is null || x.Amount <= 0) == true ||
            !await db.TradingParties.AnyAsync(x => x.CompanyId == company.Id && x.Id == request.PartyId &&
                x.Kind == PartyKind.Supplier && x.Currency == company.BaseCurrency, ct) ||
            !await db.TradingAccounts.AnyAsync(x => x.CompanyId == company.Id && x.Role == AccountRole.Cash && x.AccountId == request.CashAccountId, ct))
            throw Conflict("A positive eligible company supplier payment is required.");
        if (request.Allocations is { Count: > 0 } allocations)
        {
            var ids = allocations.Select(x => x.DocumentId).Distinct().ToArray();
            if (ids.Length != allocations.Count || allocations.Sum(x => x.Amount) > request.Amount ||
                await db.TradeDocuments.CountAsync(x => x.CompanyId == company.Id && ids.Contains(x.Id) &&
                    x.PartyId == request.PartyId && x.State == TradeState.Posted && x.Kind == TradeKind.Bill, ct) != ids.Length)
                throw Conflict("Payment allocations must match this supplier's posted bills.");
        }
        return new(FinancialDuty.SupplierPayment, PaymentIdentity(key), request.PartyId, maker, TradeRules.Hash(request),
            request.Amount, company.BaseCurrency);
    }

    public async Task<FinancialIntent> Find(Company company, FinancialDuty duty, Guid id, CancellationToken ct,
        IReadOnlyDictionary<int, EffectiveDimension[]>? attribution = null)
    {
        var c = company.Id;
        FinancialIntent Result(Guid maker, Guid version, decimal amount = 0, Guid? subject = null) =>
            new(duty, id, subject, maker, version.ToString("N"), amount, company.BaseCurrency);
        switch (duty)
        {
            case FinancialDuty.ManualJournal:
                var journal = await db.Journals.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x =>
                    x.CompanyId == c && x.Id == id && x.SourceModule == "ManualJournal" && x.State == JournalState.Draft, ct) ?? throw Missing();
                var posting = JournalService.Request(journal, new PostJournalRequest("governance-intent", journal.Version));
                attribution ??= await dimensions.Prepare(journal.CreatedBy, company, posting, ct);
                var intent = Result(journal.CreatedBy, journal.Version, journal.Lines.Sum(x => x.Debit));
                return attribution.Count == 0 ? intent : intent with
                {
                    Version = TradeRules.Hash(new
                    {
                        journal.Version,
                        Posting = AccountingRules.Fingerprint(posting),
                        Dimensions = attribution.OrderBy(x => x.Key).Select(x => new
                        {
                            Line = x.Key,
                            Values = x.Value.OrderBy(tag => tag.DefinitionId).ToArray()
                        }).ToArray()
                    })
                };
            case FinancialDuty.BankTransfer:
                var transfer = await db.TreasuryMovements.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == c && x.Id == id &&
                    x.Kind == TreasuryMovementKind.Transfer && x.State == TreasuryMovementState.PendingApproval, ct) ?? throw Missing();
                return Result(transfer.CreatedBy, transfer.Version, transfer.Amount, transfer.AccountId);
            case FinancialDuty.ProcurementApproval:
                var purchase = await db.ProcurementDocuments.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == c &&
                    x.Id == id && x.State == ProcurementState.Draft, ct) ?? throw Missing();
                return Result(purchase.CreatedBy, purchase.Version, purchase.Lines.Sum(x => x.NetAmount + x.TaxAmount));
            case FinancialDuty.StockAdjustment:
                var stock = await db.StockOperations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == c && x.Id == id &&
                    (x.Kind == StockKind.Count || x.Kind == StockKind.WriteDown) && x.State == StockState.Approved, ct) ?? throw Missing();
                return Result(stock.CreatedBy, stock.Version, StockPlanning.Plan(stock).Lines.Sum(x => x.Debit), stock.ItemId);
            case FinancialDuty.AssetDisposal:
                var asset = await db.AssetOperations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == c && x.Id == id &&
                    (x.Kind == AssetOperationKind.Sale || x.Kind == AssetOperationKind.WriteOff) && x.State == AssetOperationState.Approved, ct) ?? throw Missing();
                return Result(asset.CreatedBy, asset.Version, AssetSnapshot.Plan(asset).Lines.Sum(x => x.Debit));
            case FinancialDuty.FxRevaluation:
                var fx = await db.FxOperations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == c && x.Id == id &&
                    x.Kind == FxKind.Revaluation && x.State == FxState.Approved, ct) ?? throw Missing();
                return Result(fx.CreatedBy, fx.Version, FxQueries.Plan(fx).Lines.Sum(x => x.Debit));
            case FinancialDuty.PeriodClose:
                var period = await db.Periods.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == c && x.Id == id && x.State == PeriodState.Open, ct)
                    ?? throw Missing();
                return Result(period.UpdatedBy, period.Version);
            case FinancialDuty.BudgetOverride:
                var budget = await db.BudgetOverrides.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == c && x.Id == id && x.UsedAt == null, ct)
                    ?? throw Missing();
                return Result(budget.CreatedBy, budget.Id, budget.MaxExcess, budget.AccountId);
            case FinancialDuty.TaxProvisionApproval:
                var tax = await db.Set<TaxOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == c && x.Id == id &&
                    (x.Kind == TaxOperationKind.CurrentProvision || x.Kind == TaxOperationKind.DeferredAsset || x.Kind == TaxOperationKind.DeferredLiability) &&
                    x.State == TaxEvidenceState.Draft, ct) ?? throw Missing();
                return new(duty, id, tax.RegistrationId, tax.PreparedBy,
                    TradeRules.Hash(JsonSerializer.Deserialize<TaxPostingPlan>(tax.PlanJson!)), tax.Amount, company.BaseCurrency);
            case FinancialDuty.ConsolidationPublication:
                var run = await db.Set<ConsolidationRun>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == c && x.Id == id &&
                    x.State == GroupRunState.Approved, ct) ?? throw Missing();
                return new(duty, id, run.GroupId, run.PreparedBy, run.SourceHash, 0m, company.BaseCurrency);
            default:
                throw Bad("Choose an integrated financial source.");
        }
    }
}
