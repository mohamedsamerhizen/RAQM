using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RAQM.Api.Data;
using RAQM.Api.Entities.Integrations;
using RAQM.Api.Helpers;
using RAQM.Api.Services.SaaS;

namespace RAQM.Api.Services.ApiAccess;

public sealed record MachineIdentity(Guid Company, Guid Organization, Guid Application, Guid Credential)
{
    public const string Scheme = "IntegrationKey";
    public static readonly string[] Scopes = ["invoices.read", "suppliers.read", "master.import", "reports.read", "webhooks.receive"];

    public static MachineIdentity From(ClaimsPrincipal actor) => new(
        Guid.Parse(actor.FindFirstValue("integration_company")!), Guid.Parse(actor.FindFirstValue("integration_org")!),
        Guid.Parse(actor.FindFirstValue("integration_app")!), Guid.Parse(actor.FindFirstValue("integration_key")!));

    public async Task<IntegrationApplication> Require(RaqmDbContext db, Guid org, Guid company, string scope, DateTimeOffset now, CancellationToken ct)
    {
        if (Organization != org || Company != company) throw new ApiException(404, "Integration resource not found.", "integration_scope");
        var application = await db.Set<IntegrationApplication>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
            x.OrganizationId == org && x.Id == Application && x.State == ApplicationState.Active && x.Scopes.Contains(scope) &&
            db.Set<IntegrationCredential>().Any(k => k.CompanyId == company && k.ApplicationId == x.Id && k.Id == Credential &&
                k.RevokedAt == null && k.ExpiresAt > now), ct);
        if (application is null) throw new ApiException(403, "Integration permission required.", "integration_permission");
        if (scope != "reports.read" && !await TenantBoundary.CanOperate(db, application.TenantId, ct))
            throw new ApiException(403, "Tenant integration access is restricted by commercial status.", "saas_restricted");
        return application;
    }
}

public sealed class MachineAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    RaqmDbContext db, TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization;
        if (header.Count != 1 || header[0] is not { } text || !text.StartsWith("RAQM-Key ", StringComparison.Ordinal))
            return AuthenticateResult.NoResult();
        if (text.Length != 106 || !Guid.TryParseExact(text.AsSpan(9, 32), "N", out var id) || text[41] != '.')
            return AuthenticateResult.Fail("Invalid integration credential.");
        byte[] secret;
        try { secret = Convert.FromHexString(text[42..]); }
        catch (FormatException) { return AuthenticateResult.Fail("Invalid integration credential."); }
        var digest = SHA256.HashData(secret);
        CryptographicOperations.ZeroMemory(secret);
        var now = clock.GetUtcNow();
        var ct = Context.RequestAborted;
        var credential = await (from k in db.Set<IntegrationCredential>().AsNoTracking()
            join a in db.Set<IntegrationApplication>().AsNoTracking() on new { k.CompanyId, Id = k.ApplicationId } equals new { a.CompanyId, a.Id }
            where k.Id == id && k.RevokedAt == null && k.ExpiresAt > now && a.State == ApplicationState.Active
            select k).SingleOrDefaultAsync(ct);
        if (credential is null || !CryptographicOperations.FixedTimeEquals(digest, credential.Digest))
            return AuthenticateResult.Fail("Invalid integration credential.");
        var threshold = now.AddMinutes(-1);
        if (credential.LastUsedAt is null || credential.LastUsedAt < threshold)
            await db.Set<IntegrationCredential>().Where(x => x.Id == id && x.RevokedAt == null && x.ExpiresAt > now &&
                (x.LastUsedAt == null || x.LastUsedAt < threshold)).ExecuteUpdateAsync(x => x.SetProperty(k => k.LastUsedAt, now), ct);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new("integration_company", credential.CompanyId.ToString("D")), new("integration_org", credential.OrganizationId.ToString("D")),
            new("integration_app", credential.ApplicationId.ToString("D")), new("integration_key", id.ToString("D"))
        ], MachineIdentity.Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, MachineIdentity.Scheme));
    }
}
