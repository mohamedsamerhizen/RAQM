using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.SaaS;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.SaaS;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Organizations;
using static RAQM.Api.Services.SaaS.CommercialRules;

namespace RAQM.Api.Services.SaaS;

public sealed partial class CommercialService(RaqmDbContext db, IOrganizationService organizations, PlatformAuthority authority, TimeProvider clock)
{
    internal DateTimeOffset Now => clock.GetUtcNow();

    public async Task<CommercialPlan[]> Catalog(CancellationToken ct) =>
        await db.Set<CommercialPlan>().AsNoTracking().Where(x => x.PublishedAt != null &&
            !db.Set<CommercialPlan>().Any(n => n.Code == x.Code && n.PublishedAt != null && n.Version > x.Version))
            .OrderBy(x => x.Product).ThenBy(x => x.Code).Take(100).ToArrayAsync(ct);

    public async Task<CommercialPlan> PreparePlan(Guid actor, CommercialPlanInput input, CancellationToken ct)
    {
        authority.Require(actor, "Catalog");
        var code = Code(input.Code, 40);
        var units = CurrencyPolicy.Units(input.Currency);
        CurrencyPolicy.Validate(input.Price, units);
        CurrencyPolicy.Validate(input.ExtraSeatPrice, units);
        if (!Enum.IsDefined(input.Interval) || input.TrialDays is < 0 or > 90 || input.GraceDays is < 1 or > 30 ||
            input.CompanyLimit is < 1 or > 1000 || input.SeatLimit is < 1 or > 10000 || input.IncludedSeats < 0 ||
            input.IncludedSeats > input.SeatLimit || input.WarehouseLimit is < 0 or > 10000 ||
            input.IntegrationLimit is < 0 or > 1000 || input.WebhookLimit is < 0 or > 1000 ||
            input.Features is null || input.Features.Length > Features.Length || input.Features.Except(Features).Any())
            throw Invalid("Choose valid plan bounds and supported features.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Advisory(code, 141, ct);
        var version = (await db.Set<CommercialPlan>().Where(x => x.Code == code).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1;
        var row = new CommercialPlan { Code = code, Version = version, Product = Code(input.Product, 40),
            Description = AccountingRules.Text(input.Description, 500, "Plan description"), Currency = input.Currency, Interval = input.Interval,
            Price = input.Price, ExtraSeatPrice = input.ExtraSeatPrice, IncludedSeats = input.IncludedSeats, TrialDays = input.TrialDays, GraceDays = input.GraceDays,
            CompanyLimit = input.CompanyLimit, SeatLimit = input.SeatLimit, WarehouseLimit = input.WarehouseLimit,
            IntegrationLimit = input.IntegrationLimit, WebhookLimit = input.WebhookLimit, Features = input.Features.Distinct().Order().ToArray(),
            CreatedBy = actor };
        _ = Charges(row, row.SeatLimit);
        db.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<CommercialPlan> Publish(Guid actor, Guid id, CancellationToken ct)
    {
        authority.Require(actor, "Catalog");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var code = await db.Set<CommercialPlan>().Where(x => x.Id == id).Select(x => x.Code).SingleOrDefaultAsync(ct) ?? throw Missing();
        await Advisory(code, 141, ct);
        var plan = await db.Set<CommercialPlan>().SingleAsync(x => x.Id == id, ct);
        if (plan.PublishedAt.HasValue) return plan;
        if (plan.CreatedBy == actor || await db.Set<CommercialPlan>().AnyAsync(x => x.Code == code && x.Version > plan.Version && x.PublishedAt != null, ct))
            throw Conflict("An independent operator must publish a current plan version.");
        plan.PublishedBy = actor;
        plan.PublishedAt = Now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return plan;
    }

    public async Task<CommercialAccount> Provision(Guid actor, TenantProvisionInput input, CancellationToken ct)
    {
        var key = AccountingRules.Text(input.Key, 100, "Provisioning key");
        var hash = TradeRules.Hash(input);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Advisory(actor.ToString("N"), 142, ct);
        var prior = await db.Set<ProvisioningReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.RequesterId == actor && x.RequestKey == key, ct);
        if (prior is not null)
        {
            if (prior.Hash != hash) throw Conflict("The provisioning key belongs to another payload.");
            return await Account(prior.TenantId, ct);
        }
        if (input.Company is null) throw Invalid("An initial legal company is required.");
        var contact = AccountingRules.Text(input.BillingContact, 254, "Billing contact");
        if (!MailAddress.TryCreate(contact, out var parsed) || parsed.Address != contact)
            throw Invalid("Use a billing email address without a display name.");
        var plan = await Select(input.PlanId, ct);
        if (input.Trial && (plan.TrialDays == 0 || await db.Set<ProvisioningReceipt>().AnyAsync(x => x.RequesterId == actor && x.Trial, ct)))
            throw Conflict("This identity or plan is ineligible for another trial.");
        var org = await organizations.Create(actor, input.Organization, ct);
        var company = await organizations.CreateCompany(actor, org.Id, input.Company, ct);
        var account = new CommercialAccount { TenantId = org.TenantId, OrganizationId = org.Id, FirstCompanyId = company.Id,
            OwnerId = actor, BillingContact = contact };
        var day = Today(Now);
        var sub = new CommercialSubscription { TenantId = org.TenantId, PlanId = plan.Id, Anchor = day, Start = day,
            End = input.Trial ? day.AddDays(plan.TrialDays) : Boundary(day, 1, plan.Interval),
            AccessUntil = input.Trial ? Midnight(day.AddDays(plan.TrialDays)) : Now.AddDays(plan.GraceDays), Trial = input.Trial };
        db.Add(account);
        db.Add(sub);
        db.Add(new ProvisioningReceipt { TenantId = org.TenantId, RequesterId = actor, RequestKey = key, Hash = hash, Trial = input.Trial });
        Event(org.TenantId, actor, account.Id, "TenantProvisioned", new { org.Id, CompanyId = company.Id });
        if (!sub.Trial)
        {
            var invoice = Invoice(sub, plan, actor, "cycle:0", Charges(plan, 1), sub.Start, sub.End);
            sub.AccessUntil = invoice.Total == 0 ? Midnight(sub.End) : invoice.DueAt;
        }
        Event(org.TenantId, actor, sub.Id, sub.Trial ? "TrialStarted" : "SubscriptionActivated", new { Plan = plan.Id, sub.Start, sub.End });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return account;
    }

    internal async Task<Guid> Owner(Guid actor, Guid org, CancellationToken ct)
    {
        var current = await organizations.Resolve(actor, org, ct);
        if (current.Role != OrganizationRole.Owner) throw new ApiException(403, "Organization owner permission required.", "saas_owner");
        return current.TenantId;
    }

    public async Task<CommercialAccount> Enroll(Guid actor, Guid org, Guid planId, string billingContact, CancellationToken ct)
    {
        var tenant = await Owner(actor, org, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(tenant, ct);
        _ = await Owner(actor, org, ct);
        if (await db.Set<CommercialAccount>().AnyAsync(x => x.TenantId == tenant, ct))
            throw Conflict("The tenant already has a commercial account.");
        var contact = AccountingRules.Text(billingContact, 254, "Billing contact");
        if (!MailAddress.TryCreate(contact, out var parsed) || parsed.Address != contact)
            throw Invalid("Use a billing email address without a display name.");
        var company = await db.Companies.Where(x => x.TenantId == tenant && x.OrganizationId == org)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct)
            ?? throw Conflict("Enroll after creating at least one legal company.");
        var plan = await Select(planId, ct);
        var account = new CommercialAccount { TenantId = tenant, OrganizationId = org, FirstCompanyId = company, OwnerId = actor,
            BillingContact = contact };
        var day = Today(Now);
        var sub = new CommercialSubscription { TenantId = tenant, PlanId = plan.Id, Anchor = day, Start = day,
            End = Boundary(day, 1, plan.Interval) };
        db.Add(account);
        db.Add(sub);
        var invoice = Invoice(sub, plan, actor, "cycle:0", Charges(plan, await Seats(tenant, ct)), sub.Start, sub.End);
        sub.AccessUntil = invoice.Total == 0 ? Midnight(sub.End) : invoice.DueAt;
        Event(tenant, actor, account.Id, "LegacyTenantEnrolled", new { Plan = plan.Id, Start = day });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return account;
    }

    internal async Task Lock(Guid tenant, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM organizations."Tenants" WHERE "Id"={tenant} FOR UPDATE""").ToListAsync(ct);
        if (rows.Count == 0) throw Missing();
    }

    internal Task<int> Advisory(string value, int seed, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({value}, {seed}))", ct);

    internal async Task<CommercialPlan> Select(Guid id, CancellationToken ct)
    {
        var plan = await Plan(id, ct);
        if (db.Database.CurrentTransaction is not null)
            await Advisory(plan.Code, 141, ct);
        if (!plan.PublishedAt.HasValue || await db.Set<CommercialPlan>().AnyAsync(x => x.Code == plan.Code &&
            x.Version > plan.Version && x.PublishedAt != null, ct))
            throw Conflict("Select the latest published plan version.");
        return plan;
    }

    internal async Task<CommercialPlan> Plan(Guid id, CancellationToken ct) =>
        await db.Set<CommercialPlan>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing();

    internal async Task<CommercialAccount> Account(Guid tenant, CancellationToken ct) =>
        await db.Set<CommercialAccount>().SingleOrDefaultAsync(x => x.TenantId == tenant, ct) ?? throw Missing();

    internal async Task<CommercialSubscription> Subscription(Guid tenant, CancellationToken ct) =>
        await db.Set<CommercialSubscription>().SingleOrDefaultAsync(x => x.TenantId == tenant, ct) ?? throw Missing();

    internal Task<int> Seats(Guid tenant, CancellationToken ct) =>
        db.OrganizationMembers.Where(x => x.TenantId == tenant).Select(x => x.UserId).Distinct().CountAsync(ct);

    internal static string Code(string input, int max) => AccountingRules.Text(input, max, "Commercial code").ToUpperInvariant();

    internal void Event(Guid tenant, Guid? actor, Guid source, string type, object evidence) =>
        db.Add(new CommercialEvent { TenantId = tenant, ActorId = actor, SourceId = source, Type = type, At = Now,
            Evidence = JsonSerializer.Serialize(evidence) });

    internal CommercialInvoice Invoice(CommercialSubscription sub, CommercialPlan plan, Guid? actor, string key,
        BillingLine[] lines, DateOnly from, DateOnly to)
    {
        var total = lines.Sum(x => x.Amount);
        CurrencyPolicy.Validate(total, CurrencyPolicy.Units(plan.Currency));
        var row = new CommercialInvoice { TenantId = sub.TenantId, SubscriptionId = sub.Id, PlanId = plan.Id,
            RequestKey = key, Currency = plan.Currency, From = from, To = to, IssuedAt = Now, DueAt = Now.AddDays(plan.GraceDays),
            Total = total, Lines = JsonSerializer.Serialize(lines) };
        db.Add(row);
        Event(sub.TenantId, actor, row.Id, "InvoiceFinalized", new { row.RequestKey, row.Currency, row.Total, row.PlanId });
        return row;
    }
}
