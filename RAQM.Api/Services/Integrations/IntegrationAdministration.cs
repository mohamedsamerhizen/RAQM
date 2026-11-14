using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Integrations;
using RAQM.Api.Entities.Integrations;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.ApiAccess;
using RAQM.Api.Services.AuditEvidence;
using RAQM.Api.Services.Webhooks;

namespace RAQM.Api.Services.Integrations;

public sealed class IntegrationAdministration(RaqmDbContext db, IFinancialTransaction transaction, FinancialAccess access,
    GovernanceAudit audit, TimeProvider clock, IDataProtectionProvider protection, WebhookTransport transport)
{
    public static ApiException Invalid(string message) => new(400, message, "integration_input");
    public static ApiException Missing() => new(404, "Integration resource not found.", "integration_missing");
    public static ApiException Conflict(string message) => new(409, message, "integration_conflict");

    public Task<IntegrationApplication> Create(Guid actor, Guid org, Guid company, ApplicationRequest input, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var name = AccountingRules.Text(input.Name, 80, "Application name");
            if (input.Scopes is null || input.Scopes.Length == 0 || input.Scopes.Except(MachineIdentity.Scopes).Any())
                throw Invalid("Choose explicit supported integration scopes.");
            var row = Scoped(scope, new IntegrationApplication { Name = name, OwnerId = actor, Scopes = input.Scopes.Distinct().Order().ToArray() });
            db.Add(row);
            await Audit(scope, actor, row.Id, "ApplicationPrepared", token);
            return row;
        }, ct);

    public Task<IntegrationApplication> Change(Guid actor, Guid org, Guid company, Guid id, string action, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var row = await Application(company, id, token);
            switch (action)
            {
                case "approve" when row.State == ApplicationState.Draft && row.OwnerId != actor:
                    row.ApprovedBy = actor;
                    row.State = ApplicationState.Active;
                    break;
                case "suspend" when row.State == ApplicationState.Active:
                    row.State = ApplicationState.Suspended;
                    break;
                case "resume" when row.State == ApplicationState.Suspended && row.OwnerId != actor:
                    row.State = ApplicationState.Active;
                    break;
                case "revoke" when row.State is ApplicationState.Active or ApplicationState.Suspended:
                    row.State = ApplicationState.Revoked;
                    break;
                default: throw Conflict("Invalid or non-independent application transition.");
            }
            await Audit(scope, actor, id, "Application:" + action, token);
            return row;
        }, ct);

    public Task<CredentialIssue> Issue(Guid actor, Guid org, Guid company, Guid id, CredentialRequest input, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var app = await Application(company, id, token);
            var now = clock.GetUtcNow();
            if (app.State != ApplicationState.Active || input.Days is < 1 or > 90 || input.OverlapMinutes is < 0 or > 60 ||
                input.Replace is null && input.OverlapMinutes != 0)
                throw Invalid("Use an active application, a lifetime of 1–90 days and an overlap of up to 60 minutes.");
            if (input.Replace is { } old)
            {
                if (await db.Set<IntegrationCredential>().CountAsync(x => x.CompanyId == company && x.ApplicationId == id &&
                    x.ExpiresAt > now && x.RevokedAt == null, token) > 1)
                    throw Conflict("Allow the earlier overlap to expire or revoke the earlier credential before rotating again.");
                var prior = await db.Set<IntegrationCredential>().SingleOrDefaultAsync(x => x.CompanyId == company && x.ApplicationId == id && x.Id == old &&
                    x.ExpiresAt > now && x.RevokedAt == null, token) ?? throw Missing();
                if (input.OverlapMinutes == 0) prior.RevokedAt = now;
                else if (prior.ExpiresAt > now.AddMinutes(input.OverlapMinutes)) prior.ExpiresAt = now.AddMinutes(input.OverlapMinutes);
            }
            else if (await db.Set<IntegrationCredential>().AnyAsync(x => x.CompanyId == company && x.ApplicationId == id &&
                x.RevokedAt == null && x.ExpiresAt > now, token))
                throw Conflict("Rotate the current credential rather than issuing another active key.");
            var bytes = RandomNumberGenerator.GetBytes(32);
            var row = Scoped(scope, new IntegrationCredential { ApplicationId = id, Digest = SHA256.HashData(bytes), ExpiresAt = now.AddDays(input.Days) });
            db.Add(row);
            var key = $"{row.Id:N}.{Convert.ToHexString(bytes)}";
            CryptographicOperations.ZeroMemory(bytes);
            await Audit(scope, actor, row.Id, "CredentialIssued", token);
            return new CredentialIssue(row.Id, key, row.ExpiresAt);
        }, ct);

    public Task<bool> RevokeKey(Guid actor, Guid org, Guid company, Guid app, Guid key, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var row = await db.Set<IntegrationCredential>().SingleOrDefaultAsync(x => x.CompanyId == company && x.ApplicationId == app && x.Id == key, token)
                ?? throw Missing();
            if (row.RevokedAt.HasValue) return false;
            row.RevokedAt = clock.GetUtcNow();
            await Audit(scope, actor, key, "CredentialRevoked", token);
            return true;
        }, ct);

    public Task<WebhookSecret> Subscribe(Guid actor, Guid org, Guid company, Guid app, WebhookRequest input, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var application = await Application(company, app, token);
            if (application.State != ApplicationState.Active || !application.Scopes.Contains("webhooks.receive") ||
                input.Events is null || input.Events.Length == 0 || input.Events.Except(WebhookTransport.EventTypes).Any())
                throw Invalid("Select an active webhook application and supported event names.");
            if (await db.Set<WebhookSubscription>().CountAsync(x => x.CompanyId == company, token) >= 100 ||
                await db.Set<WebhookSubscription>().CountAsync(x => x.CompanyId == company && x.ApplicationId == app, token) >= 10)
                throw Conflict("The company or application subscription limit has been reached.");
            var endpoint = transport.Endpoint(input.Endpoint);
            var row = Scoped(scope, new WebhookSubscription { ApplicationId = app, CreatedBy = actor, Endpoint = endpoint.AbsoluteUri,
                Events = input.Events.Distinct().Order().ToArray() });
            db.Add(row);
            return await Secret(scope, actor, row, token);
        }, ct);

    public Task<WebhookSecret> RotateWebhook(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var row = await Subscription(company, id, token);
            row.SecretVersion++;
            return await Secret(scope, actor, row, token);
        }, ct);

    public Task<bool> ChangeWebhook(Guid actor, Guid org, Guid company, Guid id, bool active, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var row = await Subscription(company, id, token);
            if (active && (row.CreatedBy == actor || (await Application(company, row.ApplicationId, token)).State != ApplicationState.Active))
                throw Conflict("An independent owner must activate a subscription for an active application.");
            if (active) row.ApprovedBy = actor;
            row.Active = active;
            await Audit(scope, actor, id, active ? "WebhookActivated" : "WebhookSuspended", token);
            return active;
        }, ct);

    public async Task<object> List(Guid actor, Guid org, Guid company, int page, int size, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, true, ct);
        AccountingRules.Page(page, size);
        var apps = await db.Set<IntegrationApplication>().Where(x => x.CompanyId == company).OrderBy(x => x.Id)
            .Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        var ids = apps.Select(x => x.Id).ToArray();
        return new { Applications = apps, Credentials = await db.Set<IntegrationCredential>().Where(x => x.CompanyId == company && ids.Contains(x.ApplicationId))
            .Select(x => new { x.Id, x.ApplicationId, x.ExpiresAt, x.RevokedAt, x.LastUsedAt }).ToArrayAsync(ct),
            Subscriptions = await db.Set<WebhookSubscription>().Where(x => x.CompanyId == company && ids.Contains(x.ApplicationId))
            .Select(x => new { x.Id, x.ApplicationId, x.Active, x.Endpoint, x.Events, x.SecretVersion }).ToArrayAsync(ct) };
    }

    private async Task<WebhookSecret> Secret(Company scope, Guid actor, WebhookSubscription row, CancellationToken ct)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        row.ProtectedSecret = Protector(protection, row.Id).Protect(secret);
        await Audit(scope, actor, row.Id, "WebhookSecretIssued", ct);
        return new(row.Id, row.SecretVersion, secret);
    }

    internal static IDataProtector Protector(IDataProtectionProvider provider, Guid id) => provider.CreateProtector("RAQM.Webhook.v1", id.ToString("N"));
    internal Task Audit(Company company, Guid actor, Guid id, string type, CancellationToken ct) =>
        audit.Append(company, actor, new("integration", type, id, "Integration management action"), ct);
    internal Task<T> Admin<T>(Guid actor, Guid org, Guid company, Func<Company, CancellationToken, Task<T>> action, CancellationToken ct) =>
        transaction.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            return await action(scope, token);
        }, ct);
    internal async Task<IntegrationApplication> Application(Guid company, Guid id, CancellationToken ct) =>
        await db.Set<IntegrationApplication>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw Missing();
    private async Task<WebhookSubscription> Subscription(Guid company, Guid id, CancellationToken ct) =>
        await db.Set<WebhookSubscription>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw Missing();
    internal static T Scoped<T>(Company scope, T row) where T : IntegrationRecord
    {
        row.TenantId = scope.TenantId;
        row.OrganizationId = scope.OrganizationId;
        row.CompanyId = scope.Id;
        return row;
    }
}
