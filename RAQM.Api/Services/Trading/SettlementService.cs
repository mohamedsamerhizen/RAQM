using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Services.TaxAccounting;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.Trading;
public sealed class SettlementService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions, IPostingEngine engine, TradingPolicy policy, TimeProvider clock, GovernanceGate? governance = null) : ISettlementService
{
    private sealed record Funding(Guid Party, PartyKind Kind, DateOnly Date, Guid Control, Guid? Advance);
    private async Task<Funding> FundingSource(Guid company, FundingKind kind, Guid id, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind))
            throw new ApiException(400, "Invalid funding kind.", "funding_kind");
        if (kind == FundingKind.Payment)
        {
            var p = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id && !x.IsRefund && x.State == SettlementState.Posted, ct) ?? throw new ApiException(404, "Posted funding payment not found.", "funding_not_found");
            return new(p.PartyId, p.PartyKind, p.PostingDate, p.ControlAccountId, p.AdvanceAccountId);
        }

        var d = await db.TradeDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id && x.State == TradeState.Posted, ct);
        if (d is null || !TradeRules.IsCredit(d.Kind))
            throw new ApiException(404, "Posted credit note not found.", "funding_not_found");
        return new(d.PartyId, TradeRules.Side(d.Kind), d.PostingDate!.Value, d.ControlAccountId, null);
    }

    private static void Chronology(TradingSnapshot s, FundingKind kind, Guid funding, Guid? target, DateOnly date, DateOnly earliest)
    {
        if (date < earliest || date < s.LatestFundingEvent(kind, funding) || (target.HasValue && date < s.LatestTargetEvent(target.Value)))
            throw new ApiException(409, "Settlement cannot precede its source/target or later recorded settlement history.", "settlement_chronology");
    }

    private static (decimal Control, decimal Advance) Portion(TradingSnapshot s, FundingKind kind, Guid id, decimal amount, DateOnly date)
    {
        var current = s.Available(kind, id, DateOnly.MaxValue);
        var dated = s.Available(kind, id, date);
        var control = Math.Min(current.Control, dated.Control);
        var advance = Math.Min(current.Advance, dated.Advance);
        if (control < 0 || advance < 0 || amount > control + advance)
            throw new ApiException(409, "Allocation/refund exceeds unallocated funding.", "funding_exceeded");
        var useControl = Math.Min(amount, control);
        return (useControl, amount - useControl);
    }

    private async Task<PaymentResponse> FinishPayment(Company c, Guid actor, SettlementPayment p, bool backdated, string? reason, CancellationToken ct)
    {
        db.Payments.Add(p);
        await db.SaveChangesAsync(ct);
        var receipt = await engine.PostAsync(actor, new(new(c.TenantId, c.OrganizationId, c.Id), "Settlement.Payment", p.Id, TradeRules.Key("Payment", p.Id, p.RequestKey), p.PostingDate, c.BaseCurrency, TradeRules.PaymentPosting(p), null, p.PostingDate, p.IsRefund ? "Counterparty refund" : "Counterparty payment", p.Reference, backdated, reason), ct);
        p.Sequence = (await db.Payments.Where(x => x.CompanyId == c.Id && x.State != SettlementState.Prepared).MaxAsync(x => x.Sequence, ct) ?? 0) + 1;
        p.Number = $"{(p.IsRefund ? "RF" : p.PartyKind == PartyKind.Customer ? "RC" : "SP")}-{p.Sequence:D8}";
        p.State = SettlementState.Posted;
        p.JournalId = receipt.JournalId;
        p.PostedAt = receipt.PostedAt;
        access.Audit(c, actor, p.Id, p.IsRefund ? "CounterpartyRefund" : "CounterpartyPayment", reason);
        await db.SaveChangesAsync(ct);
        return View(p, await TradingSnapshot.Load(db, c.Id, ct));
    }

    public Task<PaymentResponse> ReceiveOrPay(Guid actor, Guid org, Guid company, PaymentRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        AccountingRules.OptionalText(r.Reason, 500);
        var key = AccountingRules.Text(r.IdempotencyKey, 64, "Idempotency key");
        var hash = TradeRules.Hash(r);
        var prior = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.RequestKey == key, t);
        if (prior is not null)
        {
            if (prior.RequestHash != hash)
                throw new ApiException(409, "Payment key payload changed.", "idempotency_conflict");
            return View(prior, await TradingSnapshot.Load(db, company, t));
        }

        var party = await db.TradingParties.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == r.PartyId, t) ?? throw new ApiException(404, "Party not found.", "party_not_found");
        if (party.Currency != c.BaseCurrency)
            throw new ApiException(409, "Party currency differs from company.", "currency_mismatch");
        TradingPolicy.Positive(r.Amount, c.BaseCurrency);
        await policy.Cash(company, r.CashAccountId, t);
        await Banking.BankingPolicy.PaymentAccount(db, company, r.CashAccountId, r.BankAccountId, t);
        await policy.Date(actor, c, r.PostingDate, r.AllowBackdated, r.Reason, t);
        var mappings = await policy.RequiredMappings(company, t);
        var snapshot = await TradingSnapshot.Load(db, company, t);
        var available = Math.Max(0, Math.Min(snapshot.ControlBalance(party.Id, r.PostingDate), snapshot.ControlBalance(party.Id, DateOnly.MaxValue)));
        var control = r.TreatAsAdvance ? 0 : Math.Min(r.Amount, available);
        var p = new SettlementPayment
        {
            TenantId = c.TenantId,
            OrganizationId = org,
            CompanyId = company,
            PartyId = party.Id,
            PartyKind = party.Kind,
            Currency = c.BaseCurrency,
            CashAccountId = r.CashAccountId,
            BankAccountId = r.BankAccountId,
            ControlAccountId = party.ControlAccountId,
            AdvanceAccountId = mappings[party.Kind == PartyKind.Customer ? AccountRole.CustomerAdvance : AccountRole.SupplierAdvance],
            Amount = r.Amount,
            ControlAmount = control,
            AdvanceAmount = r.Amount - control,
            PostingDate = r.PostingDate,
            Reference = AccountingRules.Text(r.Reference, 160, "Reference"),
            RequestKey = key,
            RequestHash = hash,
            Reason = r.Reason ?? "",
            CreatedBy = actor
        };
        if (governance is not null && p.PartyKind == PartyKind.Supplier)
            await governance.EnforcePayment(c, actor, r, p.Id, t);
        await FinishPayment(c, actor, p, r.AllowBackdated, r.Reason, t);
        if (r.Allocations is { } allocations)
        {
            if (allocations.Count > 100 || allocations.Any(x => x is null) || allocations.Select(x => x.DocumentId).Distinct().Count() != allocations.Count)
                throw new ApiException(400, "Use at most 100 distinct allocation targets.", "allocation_targets");
            for (var i = 0; i < allocations.Count; i++)
                await Allocate(actor, org, company, new(FundingKind.Payment, p.Id, allocations[i].DocumentId, allocations[i].Amount, r.PostingDate, $"payment-{p.Id:N}-{i}", r.AllowBackdated, r.Reason), t);
        }

        return View(p, await TradingSnapshot.Load(db, company, t));
    }, ct);
    public Task<PaymentResponse> Refund(Guid actor, Guid org, Guid company, RefundRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        await access.Resolve(actor, org, company, true, true, t);
        var key = AccountingRules.Text(r.IdempotencyKey, 64, "Idempotency key");
        var hash = TradeRules.Hash(r);
        var prior = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.RequestKey == key, t);
        if (prior is not null)
        {
            if (prior.RequestHash != hash)
                throw new ApiException(409, "Refund key payload changed.", "idempotency_conflict");
            return View(prior, await TradingSnapshot.Load(db, company, t));
        }

        var source = await FundingSource(company, r.FundingKind, r.FundingId, t);
        var reason = AccountingRules.Text(r.Reason, 500, "Refund reason");
        TradingPolicy.Positive(r.Amount, c.BaseCurrency);
        await policy.Cash(company, r.CashAccountId, t);
        await Banking.BankingPolicy.PaymentAccount(db, company, r.CashAccountId, r.BankAccountId, t);
        await policy.Date(actor, c, r.PostingDate, r.AllowBackdated, reason, t);
        var snapshot = await TradingSnapshot.Load(db, company, t);
        Chronology(snapshot, r.FundingKind, r.FundingId, null, r.PostingDate, source.Date);
        var parts = Portion(snapshot, r.FundingKind, r.FundingId, r.Amount, r.PostingDate);
        var map = await policy.RequiredMappings(company, t);
        var p = new SettlementPayment
        {
            TenantId = c.TenantId,
            OrganizationId = org,
            CompanyId = company,
            PartyId = source.Party,
            PartyKind = source.Kind,
            IsRefund = true,
            RefundPaymentId = r.FundingKind == FundingKind.Payment ? r.FundingId : null,
            RefundCreditId = r.FundingKind == FundingKind.CreditNote ? r.FundingId : null,
            Currency = c.BaseCurrency,
            CashAccountId = r.CashAccountId,
            BankAccountId = r.BankAccountId,
            ControlAccountId = source.Control,
            AdvanceAccountId = map[source.Kind == PartyKind.Customer ? AccountRole.CustomerAdvance : AccountRole.SupplierAdvance],
            Amount = r.Amount,
            ControlAmount = parts.Control,
            AdvanceAmount = parts.Advance,
            PostingDate = r.PostingDate,
            Reference = $"Refund {r.FundingId:N}",
            Reason = reason,
            RequestKey = key,
            RequestHash = hash,
            CreatedBy = actor
        };
        return await FinishPayment(c, actor, p, r.AllowBackdated, reason, t);
    }, ct);
    public Task<AllocationResponse> Allocate(Guid actor, Guid org, Guid company, AllocationRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        AccountingRules.OptionalText(r.Reason, 500);
        var key = AccountingRules.Text(r.IdempotencyKey, 64, "Idempotency key");
        var hash = TradeRules.Hash(r);
        var prior = await db.Allocations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.RequestKey == key, t);
        if (prior is not null)
        {
            if (prior.RequestHash != hash)
                throw new ApiException(409, "Allocation key payload changed.", "idempotency_conflict");
            return AllocationView(prior);
        }

        TradingPolicy.Positive(r.Amount, c.BaseCurrency);
        await policy.Date(actor, c, r.EffectiveDate, r.AllowBackdated, r.Reason, t);
        var source = await FundingSource(company, r.FundingKind, r.FundingId, t);
        var target = await db.TradeDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == r.DocumentId && x.State == TradeState.Posted, t) ?? throw new ApiException(404, "Posted target document not found.", "document_not_found");
        if (TradeRules.IsCredit(target.Kind) || target.PartyId != source.Party || TradeRules.Side(target.Kind) != source.Kind || target.Currency != c.BaseCurrency || target.ControlAccountId != source.Control)
            throw new ApiException(400, "Allocation source/target must share party, company, currency and control account.", "allocation_scope");
        var snapshot = await TradingSnapshot.Load(db, company, t);
        Chronology(snapshot, r.FundingKind, r.FundingId, target.Id, r.EffectiveDate, source.Date > target.PostingDate ? source.Date : target.PostingDate!.Value);
        if (r.Amount > Math.Min(snapshot.Outstanding(target, r.EffectiveDate), snapshot.Outstanding(target, DateOnly.MaxValue)))
            throw new ApiException(409, "Allocation exceeds document outstanding amount.", "over_allocation");
        var parts = Portion(snapshot, r.FundingKind, r.FundingId, r.Amount, r.EffectiveDate);
        var a = new SettlementAllocation
        {
            TenantId = c.TenantId,
            OrganizationId = org,
            CompanyId = company,
            PartyId = source.Party,
            PartyKind = source.Kind,
            FundingKind = r.FundingKind,
            PaymentId = r.FundingKind == FundingKind.Payment ? r.FundingId : null,
            CreditDocumentId = r.FundingKind == FundingKind.CreditNote ? r.FundingId : null,
            TargetDocumentId = target.Id,
            EffectiveDate = r.EffectiveDate,
            Amount = r.Amount,
            ControlAmount = parts.Control,
            AdvanceAmount = parts.Advance,
            ControlAccountId = source.Control,
            AdvanceAccountId = parts.Advance > 0 ? source.Advance : null,
            RequestKey = key,
            RequestHash = hash,
            ActorId = actor,
            Reason = r.Reason ?? ""
        };
        db.Allocations.Add(a);
        await db.SaveChangesAsync(t);
        if (a.AdvanceAmount > 0)
        {
            var receipt = await engine.PostAsync(actor, new(new(c.TenantId, org, company), "Settlement.Allocation", a.Id, TradeRules.Key("Allocate", a.Id, key), r.EffectiveDate, c.BaseCurrency, TradeRules.AllocationPosting(a), null, r.EffectiveDate, "Apply counterparty advance", target.Number, r.AllowBackdated, r.Reason), t);
            a.JournalId = receipt.JournalId;
        }

        a.State = SettlementState.Posted;
        access.Audit(c, actor, a.Id, "PaymentAllocated", r.Reason);
        await db.SaveChangesAsync(t);
        return AllocationView(a);
    }, ct);
    public Task<AllocationResponse> ReverseAllocation(Guid actor, Guid org, Guid company, Guid id, ReverseAllocationRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        var key = AccountingRules.Text(r.IdempotencyKey, 64, "Idempotency key");
        var hash = TradeRules.Hash(new { id, r });
        var prior = await db.Allocations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.RequestKey == key, t);
        if (prior is not null)
        {
            if (prior.RequestHash != hash)
                throw new ApiException(409, "Reversal key payload changed.", "idempotency_conflict");
            return AllocationView(prior);
        }

        var reason = AccountingRules.Text(r.Reason, 500, "Deallocation reason");
        await policy.Date(actor, c, r.EffectiveDate, r.AllowBackdated, reason, t);
        var original = await db.Allocations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id && x.State == SettlementState.Posted && x.ReversesAllocationId == null, t) ?? throw new ApiException(404, "Original allocation not found.", "allocation_not_found");
        if (original.PaymentId is { } protectedPayment)
            await TaxPostingPolicy.GuardPayment(db, company, protectedPayment, actor, null, t);
        if (await db.Allocations.AnyAsync(x => x.CompanyId == company && x.ReversesAllocationId == id, t))
            throw new ApiException(409, "Allocation already reversed.", "allocation_reversed");
        var snapshot = await TradingSnapshot.Load(db, company, t);
        var funding = original.PaymentId ?? original.CreditDocumentId!.Value;
        Chronology(snapshot, original.FundingKind, funding, original.TargetDocumentId, r.EffectiveDate, original.EffectiveDate);
        var a = new SettlementAllocation
        {
            TenantId = c.TenantId,
            OrganizationId = org,
            CompanyId = company,
            PartyId = original.PartyId,
            PartyKind = original.PartyKind,
            FundingKind = original.FundingKind,
            PaymentId = original.PaymentId,
            CreditDocumentId = original.CreditDocumentId,
            TargetDocumentId = original.TargetDocumentId,
            ReversesAllocationId = id,
            EffectiveDate = r.EffectiveDate,
            Amount = original.Amount,
            ControlAmount = original.ControlAmount,
            AdvanceAmount = original.AdvanceAmount,
            ControlAccountId = original.ControlAccountId,
            AdvanceAccountId = original.AdvanceAccountId,
            RequestKey = key,
            RequestHash = hash,
            ActorId = actor,
            Reason = reason
        };
        db.Allocations.Add(a);
        await db.SaveChangesAsync(t);
        if (original.JournalId is { } journal)
        {
            var posted = await db.Journals.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.CompanyId == company && x.Id == journal, t);
            var receipt = await engine.PostAsync(actor, new(new(c.TenantId, org, company), "Reversal", journal, TradeRules.Key("Deallocate", a.Id, key), r.EffectiveDate, c.BaseCurrency, posted.Lines.OrderBy(x => x.Number).Select(x => new JournalDraftLine(x.AccountId, x.Credit, x.Debit, x.Description)).ToList(), journal, r.EffectiveDate, "Reverse advance allocation", posted.Number, r.AllowBackdated, reason), t);
            a.JournalId = receipt.JournalId;
        }

        a.State = SettlementState.Posted;
        access.Audit(c, actor, a.Id, "AllocationReversed", reason);
        await db.SaveChangesAsync(t);
        return AllocationView(a);
    }, ct);
    public Task<PaymentResponse> ReversePayment(Guid actor, Guid org, Guid company, Guid id, ReversePaymentRequest r, CancellationToken ct) => transactions.Execute(actor, org, company, async (c, t) =>
    {
        await access.Resolve(actor, org, company, true, true, t);
        var reason = AccountingRules.Text(r.Reason, 500, "Reversal reason");
        var key = AccountingRules.Text(r.IdempotencyKey, 64, "Idempotency key");
        var p = await db.Payments.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id && x.State != SettlementState.Prepared, t) ?? throw new ApiException(404, "Payment not found.", "payment_not_found");
        if (p.State == SettlementState.Reversed)
        {
            if (p.ReversalKey != key || p.ReversalDate != r.PostingDate || p.ReversalReason != reason)
                throw new ApiException(409, "Payment already reversed with different intent.", "payment_reversed");
            return View(p, await TradingSnapshot.Load(db, company, t));
        }

        await TaxPostingPolicy.GuardPayment(db, company, id, actor, key, t);
        await policy.Date(actor, c, r.PostingDate, r.AllowBackdated, reason, t);
        if (r.PostingDate < p.PostingDate)
            throw new ApiException(400, "Reversal cannot precede payment.", "settlement_date");
        var snapshot = await TradingSnapshot.Load(db, company, t);
        if (snapshot.Payments.Any(x => x.RefundPaymentId == id && TradeRules.Active(x, DateOnly.MaxValue)))
            throw new ApiException(409, "Reverse related refunds before reversing their funding payment.", "payment_refunds");
        var active = snapshot.Allocations.Where(x => x.PaymentId == id && x.ReversesAllocationId == null && !snapshot.Allocations.Any(y => y.ReversesAllocationId == x.Id)).OrderBy(x => x.EffectiveDate).ThenBy(x => x.Id).ToList();
        foreach (var a in active)
            await ReverseAllocation(actor, org, company, a.Id, new(r.PostingDate, TradeRules.Hash(new { Payment = id, Allocation = a.Id }), reason, r.AllowBackdated), t);
        snapshot = await TradingSnapshot.Load(db, company, t);
        if (!p.IsRefund && r.PostingDate < snapshot.LatestFundingEvent(FundingKind.Payment, id))
            throw new ApiException(409, "Reversal cannot precede settlement history.", "settlement_chronology");
        // Stage intent, not a split commit: deferred contracts require the final reversal link/state.
        p.ReversalDate = r.PostingDate;
        p.ReversalKey = key;
        p.ReversalReason = reason;
        p.ReversedBy = actor;
        p.ReversedAt = AccountingRules.StoredUtcInstant(clock.GetUtcNow());
        await db.SaveChangesAsync(t);
        var original = await db.Journals.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == p.JournalId && x.CompanyId == company, t);
        var receipt = await engine.PostAsync(actor, new(new(c.TenantId, org, company), "Reversal", original.Id, TradeRules.Key("ReversePay", p.Id, key), r.PostingDate, c.BaseCurrency, original.Lines.OrderBy(x => x.Number).Select(x => new JournalDraftLine(x.AccountId, x.Credit, x.Debit, x.Description)).ToList(), original.Id, r.PostingDate, "Reverse counterparty payment", p.Number, r.AllowBackdated, reason), t);
        p.State = SettlementState.Reversed;
        p.ReversalJournalId = receipt.JournalId;
        access.Audit(c, actor, id, "CounterpartyPaymentReversed", reason);
        await db.SaveChangesAsync(t);
        return View(p, await TradingSnapshot.Load(db, company, t));
    }, ct);
    public async Task<PaymentResponse> GetPayment(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        var p = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id && x.State != SettlementState.Prepared, ct) ?? throw new ApiException(404, "Payment not found.", "payment_not_found");
        return View(p, await TradingSnapshot.Load(db, company, ct));
    }

    public async Task<PageResult<PaymentResponse>> Payments(Guid actor, Guid org, Guid company, Guid? party, int page, int pageSize, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        AccountingRules.Page(page, pageSize);
        var q = db.Payments.AsNoTracking().Where(x => x.CompanyId == company && x.State != SettlementState.Prepared);
        if (party.HasValue)
            q = q.Where(x => x.PartyId == party);
        var count = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.PostingDate).ThenByDescending(x => x.Sequence).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var s = await TradingSnapshot.Load(db, company, ct);
        return new(rows.Select(p => View(p, s)).ToList(), count, page, pageSize);
    }

    public async Task<PageResult<AllocationResponse>> Allocations(Guid actor, Guid org, Guid company, Guid? party, int page, int pageSize, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        AccountingRules.Page(page, pageSize);
        var q = db.Allocations.AsNoTracking().Where(x => x.CompanyId == company && x.State == SettlementState.Posted);
        if (party.HasValue)
            q = q.Where(x => x.PartyId == party);
        var count = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.At).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(rows.Select(AllocationView).ToList(), count, page, pageSize);
    }

    public static AllocationResponse AllocationView(SettlementAllocation a) => new(a.Id, a.PartyId, a.PaymentId, a.CreditDocumentId, a.TargetDocumentId, a.ReversesAllocationId, a.EffectiveDate, a.Amount, a.ControlAmount, a.AdvanceAmount, a.JournalId);
    public static PaymentResponse View(SettlementPayment p, TradingSnapshot s)
    {
        var at = DateOnly.MaxValue;
        var allocated = p.IsRefund ? 0 : s.Allocations.Where(x => x.PaymentId == p.Id).Sum(x => TradeRules.AllocationSign(x) * x.Amount);
        var refunded = s.Payments.Where(x => x.RefundPaymentId == p.Id && TradeRules.Active(x, at)).Sum(x => x.Amount);
        var available = p.IsRefund || !TradeRules.Active(p, at) ? (Control: 0m, Advance: 0m) : s.Available(FundingKind.Payment, p.Id, at);
        return new(p.Id, p.PartyId, p.PartyKind, p.IsRefund, p.State, p.Number!, p.Currency, p.Amount, p.ControlAmount, p.AdvanceAmount, allocated, refunded, available.Control + available.Advance, p.PostingDate, p.JournalId!.Value, p.ReversalJournalId, p.ReversalDate, p.BankAccountId);
    }
}
