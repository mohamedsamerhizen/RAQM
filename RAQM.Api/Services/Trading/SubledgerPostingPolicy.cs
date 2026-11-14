using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.Trading;
public sealed class SubledgerPostingPolicy(RaqmDbContext db, FinancialAccess access)
{
    private static void Match(IReadOnlyList<JournalDraftLine> expected, PostingRequest actual)
    {
        if (!expected.SequenceEqual(actual.Lines))
            throw new ApiException(409, "Financial journal differs from its immutable source amounts/accounts.", "source_payload");
    }

    public async Task Validate(Guid actor, Company company, PostingRequest r, CancellationToken ct, bool authorizedTax = false)
    {
        var companyId = company.Id;
        var mappings = await db.TradingAccounts.AsNoTracking().Where(x => x.CompanyId == companyId).ToListAsync(ct);
        var protectedRows = mappings.Where(x => TradingSetupService.Protected(x.Role) && r.Lines.Any(l => l.AccountId == x.AccountId)).ToList();
        if (authorizedTax && protectedRows.All(x => x.Role is AccountRole.TaxOutput or AccountRole.TaxInput)) return;
        if (Enum.GetValues<TradeKind>().Any(k => TradeRules.Module(k) == r.SourceModule))
        {
            var d = await db.TradeDocuments.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == companyId && x.Id == r.SourceDocumentId, ct) ?? throw new ApiException(403, "Trading source document not found.", "control_source");
            if (d.State != TradeState.Approved || TradeRules.Module(d.Kind) != r.SourceModule || d.Currency != r.Currency || d.DocumentDate != r.DocumentDate)
                throw new ApiException(409, "Trading source must be approved and match posting metadata.", "source_state");
            Match(TradeRules.DocumentPosting(d), r);
            if (TradeRules.IsCredit(d.Kind))
                foreach (var l in d.Lines)
                {
                    var original = await db.TradeLines.AsNoTracking().SingleAsync(x => x.CompanyId == companyId && x.Id == l.OriginalLineId, ct);
                    var used = await (
                        from line in db.TradeLines
                        join header in db.TradeDocuments on line.DocumentId equals header.Id
                        where line.OriginalLineId == original.Id && header.State == TradeState.Posted && header.Id != d.Id
                        select new
                        {
                            line.Quantity,
                            line.NetAmount,
                            line.TaxAmount,
                            line.DiscountAmount
                        }

                    ).ToListAsync(ct);
                    var amount = TradeRules.Credit(original, l.Quantity, used.Sum(x => x.Quantity), used.Sum(x => x.NetAmount), used.Sum(x => x.TaxAmount), used.Sum(x => x.DiscountAmount), d.Currency);
                    if (amount.Net != l.NetAmount || amount.Tax != l.TaxAmount)
                        throw new ApiException(409, "Credit exceeds original remaining components.", "over_credit");
                }

            return;
        }

        if (r.SourceModule == "Settlement.Payment")
        {
            var p = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.Id == r.SourceDocumentId && x.State == SettlementState.Prepared, ct) ?? throw new ApiException(403, "Prepared payment source is required.", "control_source");
            if (p.PostingDate != r.AccountingDate || p.Currency != r.Currency || p.CreatedBy != actor)
                throw new ApiException(409, "Payment source metadata mismatch.", "source_payload");
            Match(TradeRules.PaymentPosting(p), r);
            var s = await TradingSnapshot.Load(db, companyId, ct);
            if (p.IsRefund)
            {
                await access.Resolve(actor, company.OrganizationId, companyId, true, true, ct);
                var kind = p.RefundPaymentId.HasValue ? FundingKind.Payment : FundingKind.CreditNote;
                var id = p.RefundPaymentId ?? p.RefundCreditId!.Value;
                var available = s.Available(kind, id, DateOnly.MaxValue);
                var dated = s.Available(kind, id, p.PostingDate);
                if (p.ControlAmount > Math.Min(available.Control, dated.Control) || p.AdvanceAmount > Math.Min(available.Advance, dated.Advance))
                    throw new ApiException(409, "Refund exceeds available source funding.", "funding_exceeded");
            }
            else if (p.ControlAmount > Math.Max(0, Math.Min(s.ControlBalance(p.PartyId, p.PostingDate), s.ControlBalance(p.PartyId, DateOnly.MaxValue))))
                throw new ApiException(409, "On-account payment exceeds current/date control exposure; use advances.", "payment_classification");
            return;
        }

        if (r.SourceModule == "Settlement.Allocation")
        {
            var a = await db.Allocations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.Id == r.SourceDocumentId && x.State == SettlementState.Prepared && x.ReversesAllocationId == null, ct) ?? throw new ApiException(403, "Prepared advance allocation source required.", "control_source");
            if (a.AdvanceAmount <= 0 || a.EffectiveDate != r.AccountingDate || a.ActorId != actor)
                throw new ApiException(409, "Allocation source mismatch.", "source_payload");
            Match(TradeRules.AllocationPosting(a), r);
            return;
        }

        if (r.SourceModule == "Reversal" && r.ReversesJournalId is { } originalId)
        {
            var original = await db.Journals.AsNoTracking().SingleAsync(x => x.CompanyId == companyId && x.Id == originalId, ct);
            if (original.SourceModule == "Settlement.Payment")
            {
                var p = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.Id == original.SourceDocumentId, ct);
                if (p?.ReversalKey is null || p.ReversalDate != r.AccountingDate || p.ReversedBy != actor || r.IdempotencyKey != TradeRules.Key("ReversePay", p.Id, p.ReversalKey))
                    throw new ApiException(403, "Reverse a payment through its coordinated settlement workflow.", "source_reversal");
                return;
            }

            if (original.SourceModule == "Settlement.Allocation")
            {
                var reverse = await db.Allocations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.ReversesAllocationId == original.SourceDocumentId && x.State == SettlementState.Prepared, ct);
                if (reverse is null || reverse.ActorId != actor || reverse.EffectiveDate != r.AccountingDate || r.IdempotencyKey != TradeRules.Key("Deallocate", reverse.Id, reverse.RequestKey))
                    throw new ApiException(403, "Reverse an allocation through its coordinated workflow.", "source_reversal");
                return;
            }

            if ((original.SourceModule.StartsWith("AR.", StringComparison.Ordinal) || original.SourceModule.StartsWith("AP.", StringComparison.Ordinal)) && await db.TradeDocuments.AnyAsync(d => d.CompanyId == companyId && d.JournalId == original.Id, ct))
                throw new ApiException(403, "Posted invoices/bills are corrected by credit notes, not standalone journal reversal.", "source_reversal");
            if (protectedRows.Count > 0 && protectedRows.All(x => original.PostedAt < x.ActivatedAt))
            {
                await access.Resolve(actor, company.OrganizationId, companyId, true, true, ct);
                if (string.IsNullOrWhiteSpace(r.Reason))
                    throw new ApiException(400, "Legacy control reversal requires a reason.", "legacy_control_activity");
                return; // Exact original negation is still checked by the core engine.
            }
        }

        if (protectedRows.Count > 0 || r.SourceModule.StartsWith("AR.", StringComparison.Ordinal) || r.SourceModule.StartsWith("AP.", StringComparison.Ordinal))
            throw new ApiException(403, "Protected controls require an authorized subledger source.", "control_account_source");
    }
}
