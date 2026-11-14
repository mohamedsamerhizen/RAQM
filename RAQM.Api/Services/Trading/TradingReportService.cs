using System.Data;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.Trading;
public sealed class TradingReportService(RaqmDbContext db, FinancialAccess access, TradingPolicy policy) : ITradingReportService
{
    private sealed record Event(DateOnly Date, DateTimeOffset At, string Type, Guid Id, string Ref, decimal Amount, decimal Debit, decimal Credit, decimal Advance);
    private static void Dates(DateOnly from, DateOnly to)
    {
        if (from.Year < 1900 || to < from)
            throw new ApiException(400, "Invalid report dates.", "report_dates");
    }

    private static List<AgingItem> Age(TradingSnapshot s, Guid party, DateOnly at) => s.Documents.Where(x => x.PartyId == party && !TradeRules.IsCredit(x.Kind) && x.PostingDate <= at).Select(x => new AgingItem(x.Id, x.Number!, x.DueDate, s.Outstanding(x, at), Math.Max(0, at.DayNumber - x.DueDate.DayNumber), TradeRules.AgingBucket(at.DayNumber - x.DueDate.DayNumber))).Where(x => x.Outstanding > 0).OrderBy(x => x.DueDate).ThenBy(x => x.DocumentId).ToList();
    public async Task<PartyStatement> Statement(Guid actor, Guid org, Guid company, Guid id, DateOnly from, DateOnly to, CancellationToken ct)
    {
        Dates(from, to);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var c = await access.Resolve(actor, org, company, false, false, ct);
        var party = await db.TradingParties.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw new ApiException(404, "Party not found.", "party_not_found");
        var s = await TradingSnapshot.Load(db, company, ct);
        var events = new List<Event>();
        foreach (var d in s.Documents.Where(x => x.PartyId == id && x.PostingDate <= to))
        {
            var debit = (party.Kind == PartyKind.Customer) != TradeRules.IsCredit(d.Kind);
            events.Add(new(d.PostingDate!.Value, d.PostedAt!.Value, d.Kind.ToString(), d.Id, d.Number!, d.Total, debit ? d.Total : 0, debit ? 0 : d.Total, 0));
        }

        foreach (var p in s.Payments.Where(x => x.PartyId == id && x.PostingDate <= to))
        {
            var credit = (party.Kind == PartyKind.Customer) != p.IsRefund;
            var advance = (p.IsRefund ? -1 : 1) * p.AdvanceAmount;
            events.Add(new(p.PostingDate, p.PostedAt!.Value, p.IsRefund ? "Refund" : "Payment", p.Id, p.Number!, p.Amount, credit ? 0 : p.ControlAmount, credit ? p.ControlAmount : 0, advance));
            if (p.State == SettlementState.Reversed && p.ReversalDate <= to)
                events.Add(new(p.ReversalDate!.Value, p.ReversedAt!.Value, "PaymentReversal", p.Id, p.Number!, -p.Amount, credit ? p.ControlAmount : 0, credit ? 0 : p.ControlAmount, -advance));
        }

        foreach (var a in s.Allocations.Where(x => x.PartyId == id && x.EffectiveDate <= to))
        {
            var sign = TradeRules.AllocationSign(a);
            var credit = (party.Kind == PartyKind.Customer) == (sign > 0);
            events.Add(new(a.EffectiveDate, a.At, a.ReversesAllocationId.HasValue ? "Deallocation" : "Allocation", a.Id, a.TargetDocumentId.ToString(), a.Amount * sign, credit ? 0 : a.AdvanceAmount, credit ? a.AdvanceAmount : 0, -a.AdvanceAmount * sign));
        }

        decimal Delta(Event e) => party.Kind == PartyKind.Customer ? e.Debit - e.Credit : e.Credit - e.Debit;
        var opening = events.Where(x => x.Date < from).Sum(Delta);
        var balance = opening;
        var lines = events.Where(x => x.Date >= from).OrderBy(x => x.Date).ThenBy(x => x.At).ThenBy(x => x.Id).Select(e =>
        {
            balance += Delta(e);
            return new StatementEntry(e.Date, e.Type, e.Id, e.Ref, e.Amount, e.Debit, e.Credit, e.Advance, balance);
        }).ToList();
        if (balance != s.ControlBalance(id, to))
            throw new ApiException(409, "Statement activity and subledger control balance disagree.", "statement_inconsistent");
        var result = new PartyStatement(id, party.Kind, c.BaseCurrency, from, to, opening, balance, s.OpenDocuments(id, to), s.UnappliedCredits(id, to), s.UnallocatedControl(id, to), s.Advances(id, to), lines, Age(s, id, to));
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<AgingResponse> Aging(Guid actor, Guid org, Guid company, PartyKind? kind, DateOnly asOf, CancellationToken ct)
    {
        Dates(new(1900, 1, 1), asOf);
        if (kind.HasValue && !Enum.IsDefined(kind.Value))
            throw new ApiException(400, "Invalid party kind.", "party_kind");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var c = await access.Resolve(actor, org, company, false, false, ct);
        var parties = await db.TradingParties.AsNoTracking().Where(x => x.CompanyId == company && (!kind.HasValue || x.Kind == kind)).OrderBy(x => x.Code).ToListAsync(ct);
        var s = await TradingSnapshot.Load(db, company, ct);
        var rows = parties.Select(p =>
        {
            var items = Age(s, p.Id, asOf);
            return new PartyAging(p.Id, p.Name, p.Kind, items.Sum(x => x.Outstanding), items);
        }).ToList();
        await tx.CommitAsync(ct);
        return new(asOf, c.BaseCurrency, rows);
    }

    public async Task<TradingReconciliation> Reconcile(Guid actor, Guid org, Guid company, DateOnly at, CancellationToken ct)
    {
        Dates(new(1900, 1, 1), at);
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var c = await access.Resolve(actor, org, company, false, false, ct);
        var map = await policy.RequiredMappings(company, ct);
        var s = await TradingSnapshot.Load(db, company, ct);
        var parties = await db.TradingParties.AsNoTracking().Where(x => x.CompanyId == company).Select(x => new { x.Id, x.Kind }).ToListAsync(ct);
        var ids = map.Values.ToArray();
        var ledger = await (
            from l in db.JournalLines
            join j in db.Journals on l.JournalId equals j.Id
            where l.CompanyId == company && j.CompanyId == company && j.State == JournalState.Posted && j.PostingDate <= at && ids.Contains(l.AccountId)group l by l.AccountId into g
                select new
                {
                    Account = g.Key,
                    Balance = g.Sum(x => x.Debit - x.Credit)
                }

        ).ToDictionaryAsync(x => x.Account, x => x.Balance, ct);
        var controls = new List<ControlReconciliation>();
        foreach (var(role, kind, advance, negative)in new[]
        {
            (AccountRole.Receivable, PartyKind.Customer, false, false),
            (AccountRole.Payable, PartyKind.Supplier, false, true),
            (AccountRole.CustomerAdvance, PartyKind.Customer, true, true),
            (AccountRole.SupplierAdvance, PartyKind.Supplier, true, false)
        }

        )
        {
            var gl = ledger.GetValueOrDefault(map[role]) * (negative ? -1 : 1);
            var expected = parties.Where(x => x.Kind == kind).Sum(p => advance ? s.Advances(p.Id, at) : s.ControlBalance(p.Id, at));
            controls.Add(new(role.ToString(), gl, expected, gl - expected));
        }

        var active = s.Payments.Where(x => TradeRules.Active(x, at)).ToList();
        var total = active.Where(x => !x.IsRefund).Sum(x => x.Amount);
        var allocated = s.Allocations.Where(x => x.PaymentId.HasValue && x.EffectiveDate <= at).Sum(x => TradeRules.AllocationSign(x) * x.Amount);
        var refunded = active.Where(x => x.IsRefund && x.RefundPaymentId.HasValue).Sum(x => x.Amount);
        var unapplied = active.Where(x => !x.IsRefund).Sum(x =>
        {
            var a = s.Available(FundingKind.Payment, x.Id, at);
            return a.Control + a.Advance;
        });
        var creditTotal = s.Documents.Where(x => TradeRules.IsCredit(x.Kind) && x.PostingDate <= at).Sum(x => x.Total);
        var creditApplied = s.Allocations.Where(x => x.CreditDocumentId.HasValue && x.EffectiveDate <= at).Sum(x => TradeRules.AllocationSign(x) * x.Amount);
        var creditRefunded = active.Where(x => x.IsRefund && x.RefundCreditId.HasValue).Sum(x => x.Amount);
        var creditUnapplied = parties.Sum(x => s.UnappliedCredits(x.Id, at));
        var cashIds = await db.TradingAccounts.Where(x => x.CompanyId == company && x.Role == AccountRole.Cash).Select(x => x.AccountId).ToArrayAsync(ct);
        var cash = await (
            from l in db.JournalLines
            join j in db.Journals on l.JournalId equals j.Id
            where l.CompanyId == company && j.CompanyId == company && j.State == JournalState.Posted && j.PostingDate <= at && cashIds.Contains(l.AccountId) && (j.SourceModule == "Settlement.Payment" || (j.ReversesJournalId != null && db.Journals.Any(o => o.Id == j.ReversesJournalId && o.CompanyId == company && o.SourceModule == "Settlement.Payment")))select (decimal? )(l.Debit - l.Credit)).SumAsync(ct) ?? 0m;
        var expectedCash = active.Sum(p => (p.PartyKind == PartyKind.Customer ? 1 : -1) * (p.IsRefund ? -1 : 1) * p.Amount);
        var cashComparison = new ControlReconciliation("SettlementCash", cash, expectedCash, cash - expectedCash);
        var difference = total - allocated - refunded - unapplied;
        var creditDifference = creditTotal - creditApplied - creditRefunded - creditUnapplied;
        var valid = s.Documents.Where(x => !TradeRules.IsCredit(x.Kind) && x.PostingDate <= at).All(x => s.Outstanding(x, at) >= 0) && active.Where(x => !x.IsRefund).All(x =>
        {
            var a = s.Available(FundingKind.Payment, x.Id, at);
            return a.Control >= 0 && a.Advance >= 0;
        }) && s.Documents.Where(x => TradeRules.IsCredit(x.Kind) && x.PostingDate <= at).All(x => s.Available(FundingKind.CreditNote, x.Id, at).Control >= 0);
        if (tx is not null) await tx.CommitAsync(ct);
        return new(at, c.BaseCurrency, controls, total, allocated, refunded, unapplied, difference, creditTotal, creditApplied, creditRefunded, creditUnapplied, creditDifference, cashComparison, valid && difference == 0 && creditDifference == 0 && cashComparison.Difference == 0 && controls.All(x => x.Difference == 0));
    }

    public async Task<IReadOnlyList<AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid entity, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.AccountingAudit.AsNoTracking().Where(x => x.CompanyId == company && x.EntityId == entity).OrderBy(x => x.At).ThenBy(x => x.Id).Select(x => new AuditResponse(x.Id, x.EntityId, x.Action, x.ActorId, x.At, x.Reason)).ToListAsync(ct);
    }
}
