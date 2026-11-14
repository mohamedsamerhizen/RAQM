using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Services.Identity;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RAQM.Api.Data;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.Organizations;
using Xunit;
namespace RAQM.Tests;

[Collection("PostgreSQL")]
[Trait("Category", "Integration")]
public sealed class WorkflowTests(PostgreSqlFixture fixture)
{
    private const string Password = "Test-Only_Password123!";
    private HttpClient Client() => fixture.Factory.CreateClient();
    private static string Path(Guid org, Guid? company = null) =>
        $"/api/v1/organizations/{org}/companies" + (company is null ? "" : $"/{company}");
    private async Task<(HttpClient Client, TokenResponse Tokens, Guid User, Guid Org)> Owner()
    {
        var client = Client();
        var response = await client.PostAsJsonAsync("/api/v1/auth/onboard",
            new OnboardRequest($"{Guid.NewGuid():N}@test.example", Password, "Test organization", ApiFactory.OnboardingKey));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = (await client.GetFromJsonAsync<JsonElement>("/api/v1/me"));
        return (client, tokens, me.GetProperty("userId").GetGuid(),
            me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid());
    }
    private static async Task<Company> CreateCompany(HttpClient client, Guid org, string? registration = null)
    {
        var response = await client.PostAsJsonAsync(Path(org),
            new CompanyRequest("Legal company", registration ?? Guid.NewGuid().ToString("N"), "IQD", 1, 1));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Company>())!;
    }
    [Fact]
    public async Task OnboardingIsRestrictedAndRejectsInvalidPasswordAtomically()
    {
        using var client = Client();
        var email = $"{Guid.NewGuid():N}@test.example";
        var forbidden = await client.PostAsJsonAsync("/api/v1/auth/onboard", new OnboardRequest(email, Password, "Org", "wrong"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var bad = await client.PostAsJsonAsync("/api/v1/auth/onboard", new OnboardRequest(email, "weak", "Org", ApiFactory.OnboardingKey));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().Users.AnyAsync(x => x.Email == email));
    }
    [Fact]
    public async Task LoginRejectsBadCredentialsAndAcceptsValidPassword()
    {
        using var client = Client();
        var email = $"{Guid.NewGuid():N}@test.example";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/onboard",
            new OnboardRequest(email, Password, "Login Org", ApiFactory.OnboardingKey))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/auth/onboard",
            new OnboardRequest(email, Password, "Duplicate", ApiFactory.OnboardingKey))).StatusCode);
    }
    [Fact]
    public async Task ConcurrentFailedLoginsReachTheConfiguredLockoutThreshold()
    {
        using var client = Client();
        var email = $"{Guid.NewGuid():N}@test.example";
        using var enrolled = await client.PostAsJsonAsync("/api/v1/auth/onboard",
            new OnboardRequest(email, Password, "Lockout Org", ApiFactory.OnboardingKey));
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        using var start = new ManualResetEventSlim();
        var requests = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "wrong"));
        })).ToArray();
        start.Set();
        var replies = await Task.WhenAll(requests);
        try { Assert.All(replies, x => Assert.Equal(HttpStatusCode.Unauthorized, x.StatusCode)); }
        finally { foreach (var reply in replies) reply.Dispose(); }
        using var scope = fixture.Factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().Users.SingleAsync(x => x.Email == email);
        Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow);
        using var correct = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
    }
    [Fact]
    public async Task RefreshRotationDetectsReplayAndRevokesAccessImmediately()
    {
        var owner = await Owner();
        using var client = owner.Client;
        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(owner.Tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.NotEqual(owner.Tokens.RefreshToken, rotated.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(owner.Tokens.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(rotated.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.False(await db.RefreshTokens.AnyAsync(x => x.Hash == owner.Tokens.RefreshToken));
        Assert.True(await db.RefreshTokens.AnyAsync(x => x.Hash == IdentityService.Hash(owner.Tokens.RefreshToken)));
    }
    [Fact]
    public async Task ConcurrentRefreshAllowsOneRotationThenRevokesFamily()
    {
        var owner = await Owner();
        using var client = owner.Client;
        var results = await Task.WhenAll(
            client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(owner.Tokens.RefreshToken)),
            client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(owner.Tokens.RefreshToken)));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }
    [Fact]
    public async Task LogoutInvalidatesAccessAndRefreshTokens()
    {
        var owner = await Owner();
        using var client = owner.Client;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(owner.Tokens.RefreshToken))).StatusCode);
    }
    [Fact]
    public async Task ExpiredSessionRejectsAccessAndRefresh()
    {
        var owner = await Owner();
        using var client = owner.Client;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            await db.Sessions.Where(x => x.UserId == owner.User)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(owner.Tokens.RefreshToken))).StatusCode);
    }
    [Fact]
    public async Task RevokeAllInvalidatesIndependentLoginSessions()
    {
        using var first = Client();
        using var second = Client();
        var email = $"{Guid.NewGuid():N}@test.example";
        var onboard = await first.PostAsJsonAsync("/api/v1/auth/onboard", new OnboardRequest(email, Password, "Org", ApiFactory.OnboardingKey));
        Assert.Equal(HttpStatusCode.OK, onboard.StatusCode);
        var firstToken = (await onboard.Content.ReadFromJsonAsync<TokenResponse>())!;
        var login = await second.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var secondToken = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
        first.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstToken.AccessToken);
        second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondToken.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsync("/api/v1/auth/revoke-all", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/me")).StatusCode);
    }
    [Fact]
    public async Task TenantAndCompanyIsolationFailClosed()
    {
        var a = await Owner(); var b = await Owner();
        using var ac = a.Client; using var bc = b.Client;
        var company = await CreateCompany(ac, a.Org);
        Assert.Equal(HttpStatusCode.NotFound, (await bc.GetAsync(Path(a.Org, company.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bc.GetAsync(Path(b.Org, company.Id))).StatusCode);
        Assert.Empty((await bc.GetFromJsonAsync<List<Company>>(Path(b.Org)))!);
        Assert.Equal(HttpStatusCode.NotFound, (await bc.PutAsJsonAsync(Path(a.Org, company.Id),
            new UpdateCompanyRequest("Attack", "USD", 1, 1, company.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bc.PostAsJsonAsync(Path(a.Org),
            new CompanyRequest("Attack", "X", "USD", 1, 1))).StatusCode);
        Assert.Equal("Legal company", (await ac.GetFromJsonAsync<Company>(Path(a.Org, company.Id)))!.Name);
    }
    [Fact]
    public async Task MembershipAndCompanyRolesControlAccess()
    {
        var a = await Owner(); var b = await Owner();
        using var ac = a.Client; using var bc = b.Client;
        var company = await CreateCompany(ac, a.Org);
        Assert.Equal(HttpStatusCode.BadRequest, (await ac.PutAsJsonAsync(Path(a.Org, company.Id) + "/members",
            new GrantCompanyRequest(b.User, CompanyRole.Manager))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ac.PostAsJsonAsync($"/api/v1/organizations/{a.Org}/members", new AddMemberRequest(b.User))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ac.PostAsJsonAsync($"/api/v1/organizations/{a.Org}/members", new AddMemberRequest(b.User))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bc.GetAsync(Path(a.Org, company.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ac.PutAsJsonAsync(Path(a.Org, company.Id) + "/members",
            new GrantCompanyRequest(b.User, CompanyRole.Reader))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bc.GetAsync(Path(a.Org, company.Id))).StatusCode);
        var update = new UpdateCompanyRequest("Updated", "USD", 4, 1, company.Version);
        Assert.Equal(HttpStatusCode.Forbidden, (await bc.PutAsJsonAsync(Path(a.Org, company.Id), update)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bc.PostAsJsonAsync(Path(a.Org),
            new CompanyRequest("Unauthorized", "N", "USD", 1, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ac.PutAsJsonAsync(Path(a.Org, company.Id) + "/members",
            new GrantCompanyRequest(b.User, CompanyRole.Manager))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bc.PutAsJsonAsync(Path(a.Org, company.Id), update)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ac.PutAsJsonAsync(Path(a.Org, company.Id), update)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ac.DeleteAsync(Path(a.Org, company.Id) + $"/members/{b.User}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bc.GetAsync(Path(a.Org, company.Id))).StatusCode);
    }
    [Fact]
    public async Task CompanyValidationUniquenessAndPersistence()
    {
        var a = await Owner();
        using var ac = a.Client;
        var company = await CreateCompany(ac, a.Org, "Reg-001");
        Assert.Equal(HttpStatusCode.Conflict, (await ac.PostAsJsonAsync(Path(a.Org),
            new CompanyRequest("Duplicate", "reg-001", "USD", 1, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ac.PostAsJsonAsync(Path(a.Org),
            new CompanyRequest("Invalid", "Other", "ZZZ", 2, 29))).StatusCode);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var saved = await db.Companies.SingleAsync(x => x.Id == company.Id);
        Assert.Equal("REG-001", saved.RegistrationNumber);
        Assert.Equal(a.User, saved.CreatedBy);
        Assert.Equal(TimeSpan.Zero, saved.CreatedAt.Offset);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(HttpStatusCode.OK, (await ac.GetAsync("/health/ready")).StatusCode);
    }
    [Fact]
    public async Task DatabaseRejectsCrossTenantForeignKeyAndInvalidFiscalDate()
    {
        var a = await Owner(); var b = await Owner();
        using var ac = a.Client; using var bc = b.Client;
        var company = await CreateCompany(ac, a.Org);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.CompanyMembers.Add(new CompanyMember
            {
                TenantId = company.TenantId,
                OrganizationId = a.Org,
                CompanyId = company.Id,
                UserId = b.User,
                Role = CompanyRole.Manager
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.Companies.Add(new Company
            {
                TenantId = company.TenantId,
                OrganizationId = b.Org,
                Name = "Invalid relationship",
                RegistrationNumber = "INVALID",
                BaseCurrency = "USD",
                CreatedBy = a.User,
                UpdatedBy = a.User
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            var entity = await db.Companies.SingleAsync(x => x.Id == company.Id);
            entity.FiscalYearStartMonth = 2; entity.FiscalYearStartDay = 30;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
    [Fact]
    public async Task AuthenticatedUserCanCreateAnotherIndependentOrganization()
    {
        var a = await Owner();
        using var ac = a.Client;
        var response = await ac.PostAsJsonAsync("/api/v1/organizations", new CreateOrganizationRequest("Second tenant"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var org = (await response.Content.ReadFromJsonAsync<Organization>())!;
        Assert.NotEqual(a.Org, org.Id);
        Assert.Empty((await ac.GetFromJsonAsync<List<Company>>(Path(org.Id)))!);
    }
}


