using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.SaaS;
using RAQM.Api.Entities.Integrations;
using RAQM.Api.Entities.SaaS;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Services.FinancialReporting;
using static RAQM.Api.Services.SaaS.CommercialRules;

namespace RAQM.Api.Services.SaaS;

public sealed partial class CommercialService
{
    public async Task<object> Overview(Guid actor, Guid org, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var tenant = await Owner(actor, org, ct);
        var account = await db.Set<CommercialAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant, ct);
        var usage = new
        {
            Companies = await db.Companies.CountAsync(x => x.TenantId == tenant, ct),
            Seats = await Seats(tenant, ct),
            Warehouses = await db.Warehouses.CountAsync(x => x.TenantId == tenant, ct),
            Integrations = await db.Set<IntegrationApplication>().CountAsync(x => x.TenantId == tenant && x.State != ApplicationState.Revoked, ct),
            Webhooks = await db.Set<WebhookSubscription>().CountAsync(x => x.TenantId == tenant, ct),
            CompletedExports = await db.Set<ExportOccurrence>().CountAsync(x => x.TenantId == tenant && x.State == DeliveryState.Delivered, ct)
        };
        if (account is null) return new { TenantId = tenant, Legacy = true, Usage = usage };
        var sub = await Subscription(tenant, ct);
        var plan = await Plan(sub.PlanId, ct);
        var balance = await Open(tenant, ct);
        return new { Account = account, Subscription = sub, Plan = plan, Usage = usage, CommercialStatus = Status(sub, Now, balance),
            Outstanding = balance, CanWrite = !sub.Cancelled && sub.AccessUntil > Now && account.State == TenantOperationState.Active &&
                (!account.SuspendAt.HasValue || account.SuspendAt > Now) };
    }

    public async Task<object> BillingHistory(Guid actor, Guid org, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        var tenant = await Owner(actor, org, ct);
        var invoices = await db.Set<CommercialInvoice>().AsNoTracking().Where(x => x.TenantId == tenant)
            .OrderByDescending(x => x.IssuedAt).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        var ids = invoices.Select(x => x.Id).ToArray();
        var movements = await db.Set<CommercialMovement>().AsNoTracking().Where(x => x.TenantId == tenant && ids.Contains(x.InvoiceId) &&
            x.VerifiedAt != null).OrderBy(x => x.VerifiedAt).Take(1000).ToListAsync(ct);
        return new { Invoices = invoices.Select(x => new { Invoice = x, Number = $"RAQM-{x.Id:N}" }),
            VerifiedMovements = movements.Select(x => new { x.Id, x.InvoiceId, x.Kind, x.Amount, x.VerifiedAt }), Outstanding = await Open(tenant, ct) };
    }

    public async Task<object> OperatorReport(Guid actor, int page, int size, CancellationToken ct)
    {
        authority.Require(actor, "Support");
        AccountingRules.Page(page, size);
        var now = Now;
        var accounts = await db.Set<CommercialAccount>().AsNoTracking().OrderBy(x => x.TenantId).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        var ids = accounts.Select(x => x.TenantId).ToArray();
        var subscriptions = await (from sub in db.Set<CommercialSubscription>()
            join plan in db.Set<CommercialPlan>() on sub.PlanId equals plan.Id
            where ids.Contains(sub.TenantId)
            select new { sub.TenantId, sub.Trial, sub.Cancelled, sub.End, sub.AccessUntil, sub.CancelAtEnd,
                plan.Code, plan.Version, plan.Currency, plan.Price, plan.Interval }).ToListAsync(ct);
        var charges = await db.Set<CommercialInvoice>().Where(x => ids.Contains(x.TenantId)).GroupBy(x => x.Currency)
            .Select(g => new { Currency = g.Key, Booked = g.Sum(x => x.Total) }).ToArrayAsync(ct);
        var collections = await (from m in db.Set<CommercialMovement>() join i in db.Set<CommercialInvoice>() on m.InvoiceId equals i.Id
            where ids.Contains(m.TenantId) && m.VerifiedAt != null
            group m by new { i.Currency, m.Kind } into g select new { g.Key.Currency, g.Key.Kind, Total = g.Sum(x => x.Amount) }).ToArrayAsync(ct);
        var recurring = subscriptions.Where(x => !x.Trial && !x.Cancelled && x.AccessUntil > now)
            .GroupBy(x => x.Currency).Select(g => new { Currency = g.Key, Mrr = g.Sum(x => x.Interval == CommercialInterval.Annual ? x.Price / 12m : x.Price),
                Arr = g.Sum(x => x.Interval == CommercialInterval.Annual ? x.Price : x.Price * 12m) });
        return new { Accounts = accounts, Subscriptions = subscriptions, Charges = charges, VerifiedMovements = collections,
            ContractedBaseRunRateForPage = recurring, LegacyTenants = await db.Tenants.CountAsync(x =>
                !db.Set<CommercialAccount>().Any(a => a.TenantId == x.Id), ct) };
    }

    public async Task<CommercialAccount> Operate(Guid actor, Guid tenant, TenantOperationInput input, CancellationToken ct)
    {
        authority.Require(actor, "Operations");
        var reason = AccountingRules.Text(input.Reason, 500, "Operational reason");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(tenant, ct);
        var account = await Account(tenant, ct);
        switch (input.Action)
        {
            case "suspend" when account.State == TenantOperationState.Active:
                var when = input.EffectiveAt?.ToUniversalTime() ?? Now;
                if (when <= Now) account.State = TenantOperationState.Suspended;
                account.SuspendAt = when;
                Event(tenant, actor, account.Id, when > Now ? "SuspensionScheduled" : "TenantSuspended", new { when, Reason = reason });
                break;
            case "reactivate" when account.State == TenantOperationState.Suspended || account.State == TenantOperationState.Active && account.SuspendAt.HasValue:
                account.State = TenantOperationState.Active;
                account.SuspendAt = null;
                Event(tenant, actor, account.Id, "TenantReactivated", new { Reason = reason });
                break;
            case "offboard" when account.State != TenantOperationState.Offboarded:
                if (!input.RetainUntil.HasValue || input.RetainUntil <= Now || !input.ExportId.HasValue ||
                    !await db.Set<CommercialCase>().AnyAsync(x => x.TenantId == tenant && x.Kind == "offboarding" &&
                        x.ExportId == input.ExportId && x.ClosedAt != null && x.ClosedBy != actor, ct))
                    throw Conflict("Offboarding requires a separately reviewed request naming the export and an explicit retention date.");
                var export = await db.Set<PortabilityExport>().SingleOrDefaultAsync(x => x.TenantId == tenant &&
                    x.Id == input.ExportId.Value && x.ExpiresAt > Now && x.Csv != null, ct)
                    ?? throw Conflict("The reviewed export is no longer available.");
                var retainUntil = input.RetainUntil.Value.ToUniversalTime();
                if (export.ExpiresAt < retainUntil) export.ExpiresAt = retainUntil;
                account.State = TenantOperationState.Offboarded;
                account.SuspendAt = Now;
                account.RetainUntil = retainUntil;
                account.RetainedExportId = export.Id;
                account.LegalHold = input.LegalHold;
                await db.Set<IntegrationApplication>().Where(x => x.TenantId == tenant && x.State != ApplicationState.Draft)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.State, ApplicationState.Revoked), ct);
                await db.Set<IntegrationCredential>().Where(x => x.TenantId == tenant && x.RevokedAt == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.RevokedAt, Now), ct);
                Event(tenant, actor, account.Id, "TenantOffboarded", new { Reason = reason, account.RetainUntil,
                    account.RetainedExportId, export.Sha256, account.LegalHold });
                break;
            default: throw Conflict("The operational transition is unavailable.");
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return account;
    }

    public async Task<CommercialCase> OpenCase(Guid actor, Guid org, CommercialCaseInput input, CancellationToken ct)
    {
        var tenant = await Owner(actor, org, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(tenant, ct);
        _ = await Account(tenant, ct);
        if (input.Kind is not ("billing" or "usage" or "recovery" or "offboarding" or "provisioning"))
            throw Invalid("Choose a supported commercial case category.");
        if (input.Kind == "offboarding")
        {
            if (!input.ExportId.HasValue || !await db.Set<PortabilityExport>().AnyAsync(x => x.TenantId == tenant &&
                x.Id == input.ExportId && x.ExpiresAt > Now && x.Csv != null, ct))
                throw Conflict("An offboarding case must name a current tenant export.");
        }
        else if (input.ExportId.HasValue) throw Invalid("Only offboarding cases can bind an export.");
        var row = new CommercialCase { TenantId = tenant, OpenedBy = actor, Kind = input.Kind, ExportId = input.ExportId,
            Summary = AccountingRules.Text(input.Summary, 500, "Case summary"), OpenedAt = Now };
        db.Add(row);
        Event(tenant, actor, row.Id, "CaseOpened", new { row.Kind, row.ExportId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<CommercialCase> ChangeCase(Guid actor, Guid tenant, Guid id, CommercialCaseAction input, CancellationToken ct)
    {
        authority.Require(actor, "Support");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(tenant, ct);
        var row = await db.Set<CommercialCase>().SingleOrDefaultAsync(x => x.TenantId == tenant && x.Id == id, ct) ?? throw Missing();
        if (row.ClosedAt.HasValue) throw Conflict("The case is already closed.");
        var reason = AccountingRules.Text(input.Reason, 500, "Case reason");
        if (input.Action == "assign" && row.AssignedTo is null) row.AssignedTo = actor;
        else if (input.Action == "close" && row.AssignedTo == actor && row.OpenedBy != actor)
        {
            row.ClosedBy = actor;
            row.ClosedAt = Now;
            row.Resolution = reason;
        }
        else throw Conflict("Only the independently assigned operator can close an open case.");
        Event(tenant, actor, id, "Case:" + input.Action, new { row.Kind, Reason = reason });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<object> Cases(Guid actor, Guid org, CancellationToken ct)
    {
        var tenant = await Owner(actor, org, ct);
        return await db.Set<CommercialCase>().AsNoTracking().Where(x => x.TenantId == tenant)
            .OrderByDescending(x => x.OpenedAt).Take(100).ToArrayAsync(ct);
    }

    public async Task<object> OperatorCases(Guid actor, Guid tenant, CancellationToken ct)
    {
        authority.Require(actor, "Support");
        return await db.Set<CommercialCase>().AsNoTracking().Where(x => x.TenantId == tenant && (x.AssignedTo == null || x.AssignedTo == actor))
            .OrderByDescending(x => x.OpenedAt).Take(100).ToArrayAsync(ct);
    }

    public async Task<object> Export(Guid actor, Guid org, PortabilityInput input, FinancialLedger ledger, CancellationToken ct)
    {
        var tenant = await Owner(actor, org, ct);
        if (input.From.Year < 1900 || input.Cutoff < input.From || input.Cutoff >= Today(Now) ||
            input.Cutoff.DayNumber - input.From.DayNumber > 366)
            throw Invalid("Use a historical cutoff and a window up to 367 days.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Lock(tenant, ct);
        _ = await Owner(actor, org, ct);
        var account = await Account(tenant, ct);
        if (account.State != TenantOperationState.Active || !await TenantBoundary.CanOperate(db, tenant, ct))
            throw Conflict("New portability exports are unavailable while tenant operations are restricted.");
        var ids = await db.Companies.Where(x => x.TenantId == tenant && x.OrganizationId == org)
            .OrderBy(x => x.Id).Select(x => x.Id).Take(51).ToArrayAsync(ct);
        if (ids.Length > 50) throw Conflict("The portability export company limit is 50.");
        var output = new StringBuilder("company_id,date,journal_id,account_id,line_id,debit,credit\r\n");
        var count = 0;
        foreach (var id in ids)
        {
            var lines = await ledger.Lines(id, input.Cutoff, input.From).Take(10001 - count).ToArrayAsync(ct);
            count += lines.Length;
            if (count > 10000) throw Conflict("Narrow the export dates to at most 10,000 ledger rows.");
            foreach (var x in lines)
                output.Append(CultureInfo.InvariantCulture, $"{id},{x.Date:yyyy-MM-dd},{x.JournalId},{x.AccountId},{x.LineId},{x.Debit},{x.Credit}\r\n");
        }
        var csv = output.ToString();
        var row = new PortabilityExport { TenantId = tenant, RequestedBy = actor, From = input.From, Cutoff = input.Cutoff,
            ExpiresAt = Now.AddDays(7), Companies = ids, Rows = count, Csv = csv,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv))) };
        db.Add(row);
        Event(tenant, actor, row.Id, "PortabilityExportCreated", new { row.Cutoff, row.Rows, row.Sha256 });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { row.Id, Scope = "Posted ledger lines only", row.From, row.Cutoff, row.Companies, row.Rows, row.Sha256, row.ExpiresAt };
    }

    public async Task<string> Download(Guid actor, Guid org, Guid id, CancellationToken ct)
    {
        var tenant = await Owner(actor, org, ct);
        var now = Now;
        return await db.Set<PortabilityExport>().Where(x => x.TenantId == tenant && x.Id == id &&
            (x.ExpiresAt > now || db.Set<CommercialAccount>().Any(a => a.TenantId == tenant &&
                a.State == TenantOperationState.Offboarded && a.RetainedExportId == id &&
                (a.LegalHold || a.RetainUntil > now))))
            .Select(x => x.Csv).SingleOrDefaultAsync(ct) ?? throw Missing();
    }

    public Task<int> PurgeExports(CancellationToken ct) => db.Database.ExecuteSqlRawAsync("""
        UPDATE saas."Exports" SET "Csv"=NULL WHERE "Id" IN
        (SELECT e."Id" FROM saas."Exports" e JOIN saas."Accounts" a ON a."TenantId"=e."TenantId"
         WHERE e."ExpiresAt"<=clock_timestamp() AND e."Csv" IS NOT NULL AND NOT a."LegalHold" AND
           (a."RetainedExportId" IS DISTINCT FROM e."Id" OR a."RetainUntil"<=clock_timestamp())
         ORDER BY e."ExpiresAt", e."Id" LIMIT 100 FOR UPDATE OF e SKIP LOCKED FOR SHARE OF a SKIP LOCKED)
        """, ct);
}
