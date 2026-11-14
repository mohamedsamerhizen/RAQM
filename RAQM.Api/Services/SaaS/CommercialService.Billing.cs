using Microsoft.EntityFrameworkCore;
using RAQM.Api.DTOs.SaaS;
using RAQM.Api.Entities.SaaS;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using static RAQM.Api.Services.SaaS.CommercialRules;

namespace RAQM.Api.Services.SaaS;

public sealed partial class CommercialService
{
    public async Task<BillingQuote> PreviewChange(Guid actor, Guid org, Guid planId, bool immediate, CancellationToken ct)
    {
        var tenant = await Owner(actor, org, ct);
        var sub = await Subscription(tenant, ct);
        var next = await Select(planId, ct);
        return await Quote(sub, next, immediate, ct);
    }

    private async Task<BillingQuote> Quote(CommercialSubscription sub, CommercialPlan next, bool immediate, CancellationToken ct)
    {
        var current = await Plan(sub.PlanId, ct);
        var day = Today(Now);
        if (sub.Trial || sub.Cancelled || sub.CancelAtEnd || current.Id == next.Id || day >= sub.End ||
            current.Currency != next.Currency || current.Interval != next.Interval)
            throw Conflict("Change only an eligible paid subscription in the same currency and interval.");
        var effectiveOn = immediate ? day : sub.End;
        var lines = immediate ? Prorate(current, next, await Seats(sub.TenantId, ct), sub.Start, sub.End, day) : [];
        if (immediate && lines.Sum(x => x.Amount) <= 0) throw Conflict("Use a next-cycle change for a downgrade or equal charge.");
        var hash = TradeRules.Hash(new { sub.Version, Plan = next.Id, EffectiveOn = effectiveOn, Lines = lines });
        return new(sub.Version, next.Id, effectiveOn, lines, hash);
    }

    public async Task<CommercialSubscription> ChangePlan(Guid actor, Guid org, CommercialPlanChange input, CancellationToken ct)
    {
        var tenant = await Owner(actor, org, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(tenant, ct);
        _ = await Owner(actor, org, ct);
        await RequireCommercialAccount(tenant, ct);
        var sub = await Subscription(tenant, ct);
        Version(sub, input.ExpectedVersion);
        var next = await Select(input.PlanId, ct);
        var quote = await Quote(sub, next, input.Immediate, ct);
        if (input.Immediate)
        {
            if (quote.Hash != input.QuoteHash) throw Conflict("The quoted dates or charges changed. Obtain a current preview.");
            if (await Open(tenant, ct) != 0) throw Conflict("Settle open commercial charges before an immediate upgrade.");
            var invoice = Invoice(sub, next, actor, $"upgrade:{sub.Version:N}", quote.Lines, quote.EffectiveOn, sub.End);
            sub.PlanId = next.Id;
            sub.NextPlanId = null;
            sub.AccessUntil = invoice.DueAt < Midnight(sub.End) ? invoice.DueAt : Midnight(sub.End);
        }
        else sub.NextPlanId = next.Id;
        sub.Version = Guid.NewGuid();
        Event(tenant, actor, sub.Id, input.Immediate ? "PlanChanged" : "PlanChangeScheduled", new { next.Id, quote.EffectiveOn });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return sub;
    }

    public async Task<CommercialSubscription> ChangeSubscription(Guid actor, Guid org, CommercialAction input, CancellationToken ct)
    {
        var tenant = await Owner(actor, org, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(tenant, ct);
        _ = await Owner(actor, org, ct);
        await RequireCommercialAccount(tenant, ct);
        var sub = await Subscription(tenant, ct);
        Version(sub, input.ExpectedVersion);
        if (input.Action == "convert" && sub.Trial && !sub.Cancelled)
        {
            var plan = await Plan(sub.PlanId, ct);
            sub.Trial = false;
            sub.TrialExpiredRecorded = false;
            sub.Anchor = sub.Start = Today(Now);
            sub.End = Boundary(sub.Anchor, 1, plan.Interval);
            var invoice = Invoice(sub, plan, actor, "cycle:0", Charges(plan, await Seats(tenant, ct)), sub.Start, sub.End);
            sub.AccessUntil = invoice.Total == 0 ? Midnight(sub.End) : invoice.DueAt;
            Event(tenant, actor, sub.Id, "SubscriptionActivated", new { sub.Start, sub.End });
        }
        else if (input.Action == "cancel" && !sub.Cancelled && !sub.CancelAtEnd)
        {
            sub.CancelAtEnd = true;
            sub.NextPlanId = null;
            Event(tenant, actor, sub.Id, "CancellationScheduled", new { sub.End });
        }
        else if (input.Action == "resume" && sub.CancelAtEnd && !sub.Cancelled && Now < Midnight(sub.End))
        {
            sub.CancelAtEnd = false;
            Event(tenant, actor, sub.Id, "CancellationWithdrawn", new { sub.End });
        }
        else if (input.Action == "clear-plan-change" && sub.NextPlanId.HasValue)
        {
            sub.NextPlanId = null;
            Event(tenant, actor, sub.Id, "PlanChangeWithdrawn", new { sub.End });
        }
        else if (input.Action == "renew" && await Advance(sub, actor, ct)) { }
        else throw Conflict("The requested transition is unavailable.");
        sub.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return sub;
    }

    private async Task RequireCommercialAccount(Guid tenant, CancellationToken ct)
    {
        var account = await Account(tenant, ct);
        if (account.State != TenantOperationState.Active || account.SuspendAt <= Now) throw Conflict("The commercial account is restricted.");
    }

    internal async Task<bool> Advance(CommercialSubscription sub, Guid? actor, CancellationToken ct)
    {
        if (sub.Cancelled || Now < Midnight(sub.End)) return false;
        if (sub.CancelAtEnd)
        {
            sub.Cancelled = true;
            sub.AccessUntil = Midnight(sub.End);
            Event(sub.TenantId, actor, sub.Id, "SubscriptionCancelled", new { sub.End });
            return true;
        }
        if (sub.Trial)
        {
            if (sub.TrialExpiredRecorded) return false;
            sub.TrialExpiredRecorded = true;
            Event(sub.TenantId, actor, sub.Id, "TrialExpired", new { sub.End });
            return true;
        }
        if (await Open(sub.TenantId, ct) != 0) return false;
        var plan = await Plan(sub.NextPlanId ?? sub.PlanId, ct);
        if (sub.NextPlanId.HasValue)
            Event(sub.TenantId, actor, sub.Id, "PlanChanged", new { PlanId = plan.Id, EffectiveOn = sub.End });
        sub.PlanId = plan.Id;
        sub.NextPlanId = null;
        sub.Cycle++;
        sub.Start = sub.End;
        sub.End = Boundary(sub.Anchor, sub.Cycle + 1, plan.Interval);
        var invoice = Invoice(sub, plan, actor, $"cycle:{sub.Cycle}", Charges(plan, await Seats(sub.TenantId, ct)), sub.Start, sub.End);
        sub.AccessUntil = invoice.Total == 0 ? Midnight(sub.End) : invoice.DueAt;
        Event(sub.TenantId, actor, sub.Id, "SubscriptionRenewed", new { sub.Start, sub.End, sub.Cycle });
        return true;
    }

    public async Task<bool> Tick(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var dueSuspension = await db.Database.SqlQuery<Guid>($"""
            SELECT t."Id" AS "Value" FROM organizations."Tenants" t
            JOIN saas."Accounts" a ON a."TenantId"=t."Id"
            WHERE a."State"=0 AND a."SuspendAt"<={Now}
            ORDER BY a."SuspendAt",t."Id" FOR UPDATE OF t SKIP LOCKED LIMIT 1
            """).ToListAsync(ct);
        if (dueSuspension.Count != 0)
        {
            var account = await Account(dueSuspension[0], ct);
            if (account.State != TenantOperationState.Active || account.SuspendAt is null || account.SuspendAt > Now)
                return false;
            account.State = TenantOperationState.Suspended;
            Event(account.TenantId, null, account.Id, "TenantSuspended", new { account.SuspendAt });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT t."Id" AS "Value" FROM organizations."Tenants" t
            JOIN saas."Accounts" a ON a."TenantId"=t."Id"
            JOIN saas."Subscriptions" s ON s."TenantId"=t."Id"
            WHERE a."State"=0 AND (a."SuspendAt" IS NULL OR a."SuspendAt">{Now}) AND NOT s."Cancelled" AND s."End"<={Today(Now)}
              AND (s."CancelAtEnd" OR (s."Trial" AND NOT s."TrialExpiredRecorded") OR (NOT s."Trial" AND NOT EXISTS (
                SELECT 1 FROM saas."Invoices" i WHERE i."TenantId"=t."Id" AND i."Total">coalesce((
                  SELECT sum(CASE WHEN m."Kind"=1 THEN -m."Amount" ELSE m."Amount" END)
                  FROM saas."Movements" m WHERE m."InvoiceId"=i."Id" AND m."VerifiedAt" IS NOT NULL),0))))
            ORDER BY s."End",t."Id" FOR UPDATE OF t SKIP LOCKED LIMIT 1
            """).ToListAsync(ct);
        if (ids.Count == 0)
        {
            var overdue = await db.Database.SqlQuery<Guid>($"""
                SELECT i."Id" AS "Value" FROM organizations."Tenants" t
                JOIN saas."Invoices" i ON i."TenantId"=t."Id"
                WHERE i."DueAt"<={Now} AND i."Total">coalesce((
                    SELECT sum(CASE WHEN m."Kind"=1 THEN -m."Amount" ELSE m."Amount" END)
                    FROM saas."Movements" m WHERE m."InvoiceId"=i."Id" AND m."VerifiedAt" IS NOT NULL),0)
                  AND NOT EXISTS(SELECT 1 FROM saas."Events" e WHERE e."TenantId"=t."Id" AND e."Type"='SubscriptionPastDue' AND e."SourceId"=i."Id")
                ORDER BY i."DueAt",i."Id" FOR UPDATE OF t SKIP LOCKED LIMIT 1
                """).ToListAsync(ct);
            if (overdue.Count == 0) return false;
            var invoice = await db.Set<CommercialInvoice>().AsNoTracking().SingleAsync(x => x.Id == overdue[0], ct);
            var recorded = await db.Set<CommercialEvent>().AnyAsync(x => x.TenantId == invoice.TenantId &&
                x.Type == "SubscriptionPastDue" && x.SourceId == invoice.Id, ct);
            if (recorded) return false;
            var movements = await db.Set<CommercialMovement>().AsNoTracking().Where(x => x.TenantId == invoice.TenantId &&
                x.InvoiceId == invoice.Id && x.VerifiedAt != null).ToListAsync(ct);
            if (Outstanding(invoice.Total, movements) <= 0) return false;
            Event(invoice.TenantId, null, invoice.Id, "SubscriptionPastDue", new { invoice.DueAt });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        var sub = await Subscription(ids[0], ct);
        if (!await Advance(sub, null, ct)) return false;
        sub.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<CommercialMovement> PrepareMovement(Guid actor, Guid tenant, CommercialMovementInput input, CancellationToken ct)
    {
        authority.Require(actor, "Billing");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(tenant, ct);
        var invoice = await FindInvoice(tenant, input.InvoiceId, ct);
        if (input.Currency != invoice.Currency || !Enum.IsDefined(input.Kind) || input.Amount <= 0 ||
            (input.Kind == CommercialMovementKind.Refund) != input.BasisId.HasValue) throw Invalid("Use an eligible amount, currency and movement basis.");
        CurrencyPolicy.Validate(input.Amount, CurrencyPolicy.Units(invoice.Currency));
        var reference = Code(input.Reference, 100);
        var evidence = AccountingRules.Text(input.Evidence, 500, "Verified source reference or credit reason");
        var prior = await db.Set<CommercialMovement>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant && x.Reference == reference, ct);
        if (prior is not null)
        {
            if ((prior.InvoiceId, prior.Kind, prior.BasisId, prior.Amount, prior.Evidence) != (input.InvoiceId, input.Kind, input.BasisId, input.Amount, evidence))
                throw Conflict("The external reference already belongs to different evidence.");
            return prior;
        }
        var movement = new CommercialMovement { TenantId = tenant, InvoiceId = invoice.Id, BasisId = input.BasisId, Kind = input.Kind,
            Reference = reference, Amount = input.Amount, Evidence = evidence, PreparedBy = actor };
        await ValidateMovement(movement, invoice, ct);
        db.Add(movement);
        Event(tenant, actor, movement.Id, "CommercialMovementPrepared", new { invoice.Id, movement.Kind, movement.Amount });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return movement;
    }

    public async Task<CommercialMovement> VerifyMovement(Guid actor, Guid tenant, Guid id, CancellationToken ct)
    {
        authority.Require(actor, "Billing");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(tenant, ct);
        var row = await db.Set<CommercialMovement>().SingleOrDefaultAsync(x => x.TenantId == tenant && x.Id == id, ct) ?? throw Missing();
        if (row.VerifiedAt.HasValue) return row;
        if (row.PreparedBy == actor || !authority.Allows(row.PreparedBy, "Billing"))
            throw Conflict("A separate authorized operator must verify the external evidence.");
        var invoice = await FindInvoice(tenant, row.InvoiceId, ct);
        await ValidateMovement(row, invoice, ct);
        row.VerifiedAt = Now;
        row.VerifiedBy = actor;
        Event(tenant, actor, row.Id, row.Kind switch { CommercialMovementKind.Payment => "PaymentVerified",
            CommercialMovementKind.Refund => "RefundVerified", _ => "CreditVerified" }, new { invoice.Id, row.Amount });
        await db.SaveChangesAsync(ct);
        var sub = await Subscription(tenant, ct);
        if (!sub.Trial && !sub.Cancelled)
        {
            var open = await Open(tenant, ct);
            if (open == 0) sub.AccessUntil = Midnight(sub.End);
            else if (row.Kind == CommercialMovementKind.Refund)
                sub.AccessUntil = sub.AccessUntil < invoice.DueAt ? sub.AccessUntil : invoice.DueAt;
            sub.Version = Guid.NewGuid();
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    private async Task ValidateMovement(CommercialMovement row, CommercialInvoice invoice, CancellationToken ct)
    {
        var movements = await db.Set<CommercialMovement>().AsNoTracking().Where(x => x.TenantId == row.TenantId &&
            x.InvoiceId == invoice.Id && x.VerifiedAt != null).ToListAsync(ct);
        if (row.Kind != CommercialMovementKind.Refund)
        {
            if (row.Amount > Outstanding(invoice.Total, movements)) throw Conflict("The amount exceeds the open invoice. Overpayments are not supported.");
            return;
        }
        var basis = movements.SingleOrDefault(x => x.Id == row.BasisId && x.Kind == CommercialMovementKind.Payment) ?? throw Missing();
        if (row.Amount > basis.Amount - movements.Where(x => x.BasisId == basis.Id).Sum(x => x.Amount))
            throw Conflict("The refund exceeds verified net collection on the original payment.");
    }

    internal Task<decimal> Open(Guid tenant, CancellationToken ct) =>
        db.Set<CommercialInvoice>().Where(x => x.TenantId == tenant).SumAsync(i => i.Total -
            db.Set<CommercialMovement>().Where(x => x.TenantId == tenant && x.InvoiceId == i.Id && x.VerifiedAt != null)
                .Sum(x => x.Kind == CommercialMovementKind.Refund ? -x.Amount : x.Amount), ct);

    private async Task<CommercialInvoice> FindInvoice(Guid tenant, Guid id, CancellationToken ct) =>
        await db.Set<CommercialInvoice>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant && x.Id == id, ct) ?? throw Missing();

    private static void Version(CommercialSubscription row, Guid expected)
    {
        if (row.Version != expected) throw Conflict("The subscription changed. Retrieve its latest version.");
    }
}
