using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Trading;

namespace RAQM.Api.Services.Trading;
// Read-time derivation only. No cached outstanding/settled balance is persisted.
public sealed class TradingSnapshot
{
    public List<TradeDocument> Documents { get; init; } = [];
    public List<SettlementPayment> Payments { get; init; } = [];
    public List<SettlementAllocation> Allocations { get; init; } = [];

    public static async Task<TradingSnapshot> Load(RaqmDbContext db, Guid company, CancellationToken ct) => new()
    {
        Documents = await db.TradeDocuments.AsNoTracking().Where(x => x.CompanyId == company && x.State == TradeState.Posted).ToListAsync(ct),
        Payments = await db.Payments.AsNoTracking().Where(x => x.CompanyId == company && x.State != SettlementState.Prepared).ToListAsync(ct),
        Allocations = await db.Allocations.AsNoTracking().Where(x => x.CompanyId == company && x.State == SettlementState.Posted).ToListAsync(ct)
    };
    public decimal AppliedTo(Guid document, DateOnly at) => Allocations.Where(x => x.TargetDocumentId == document && x.EffectiveDate <= at).Sum(x => TradeRules.AllocationSign(x) * x.Amount);
    public decimal Outstanding(TradeDocument d, DateOnly at) => d.State != TradeState.Posted || d.PostingDate > at ? 0 : TradeRules.IsCredit(d.Kind) ? Available(FundingKind.CreditNote, d.Id, at).Control : d.Total - AppliedTo(d.Id, at);
    public (decimal Control, decimal Advance) Available(FundingKind kind, Guid id, DateOnly at)
    {
        var allocations = Allocations.Where(a => a.EffectiveDate <= at && (kind == FundingKind.Payment ? a.PaymentId == id : a.CreditDocumentId == id)).ToList();
        var refunds = Payments.Where(p => p.IsRefund && TradeRules.Active(p, at) && (kind == FundingKind.Payment ? p.RefundPaymentId == id : p.RefundCreditId == id)).ToList();
        decimal control, advance;
        if (kind == FundingKind.Payment)
        {
            var payment = Payments.SingleOrDefault(x => x.Id == id && !x.IsRefund && TradeRules.Active(x, at));
            control = payment?.ControlAmount ?? 0;
            advance = payment?.AdvanceAmount ?? 0;
        }
        else
        {
            control = Documents.Where(x => x.Id == id && TradeRules.IsCredit(x.Kind) && x.PostingDate <= at).Select(x => x.Total).SingleOrDefault();
            advance = 0;
        }

        return (control - allocations.Sum(x => TradeRules.AllocationSign(x) * x.ControlAmount) - refunds.Sum(x => x.ControlAmount), advance - allocations.Sum(x => TradeRules.AllocationSign(x) * x.AdvanceAmount) - refunds.Sum(x => x.AdvanceAmount));
    }

    public decimal UnallocatedControl(Guid party, DateOnly at) => Payments.Where(p => p.PartyId == party && !p.IsRefund && TradeRules.Active(p, at)).Sum(p => Available(FundingKind.Payment, p.Id, at).Control);
    public decimal Advances(Guid party, DateOnly at) => Payments.Where(p => p.PartyId == party && !p.IsRefund && TradeRules.Active(p, at)).Sum(p => Available(FundingKind.Payment, p.Id, at).Advance);
    public decimal OpenDocuments(Guid party, DateOnly at) => Documents.Where(d => d.PartyId == party && !TradeRules.IsCredit(d.Kind) && d.PostingDate <= at).Sum(d => Outstanding(d, at));
    public decimal UnappliedCredits(Guid party, DateOnly at) => Documents.Where(d => d.PartyId == party && TradeRules.IsCredit(d.Kind) && d.PostingDate <= at).Sum(d => Available(FundingKind.CreditNote, d.Id, at).Control);
    public decimal ControlBalance(Guid party, DateOnly at) => OpenDocuments(party, at) - UnappliedCredits(party, at) - UnallocatedControl(party, at);
    public DateOnly LatestFundingEvent(FundingKind kind, Guid id)
    {
        var dates = Allocations.Where(x => kind == FundingKind.Payment ? x.PaymentId == id : x.CreditDocumentId == id).Select(x => x.EffectiveDate).Concat(Payments.Where(x => x.IsRefund && (kind == FundingKind.Payment ? x.RefundPaymentId == id : x.RefundCreditId == id)).SelectMany(x => new DateOnly? [] { x.PostingDate, x.ReversalDate }.Where(d => d.HasValue).Select(d => d!.Value)));
        return dates.DefaultIfEmpty(DateOnly.MinValue).Max();
    }

    public DateOnly LatestTargetEvent(Guid id) => Allocations.Where(x => x.TargetDocumentId == id).Select(x => x.EffectiveDate).DefaultIfEmpty(DateOnly.MinValue).Max();
}
