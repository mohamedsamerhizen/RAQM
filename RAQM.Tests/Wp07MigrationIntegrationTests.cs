using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.SaaS;
using Testcontainers.PostgreSql;
using Xunit;

namespace RAQM.Tests;

[Collection("PostgreSQL"), Trait("Category", "Integration")]
public sealed class Wp07MigrationIntegrationTests
{
    [Fact]
    public async Task PopulatedNineteenToTwentyUpgradePreservesTenantExportAndCase()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        await using var factory = new ApiFactory(postgres.GetConnectionString());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            await db.Database.MigrateAsync("20261113130000_HardBudgetCommitmentAdoption");
            Assert.Equal(19, (await db.Database.GetAppliedMigrationsAsync()).Count());
        }
        using var client = factory.CreateClient();
        var signup = await client.PostAsJsonAsync("/api/v1/auth/onboard", new OnboardRequest(
            $"{Guid.NewGuid():N}@upgrade.example", "Upgrade_Test-Password1!", "Retained tenant", ApiFactory.OnboardingKey));
        Assert.Equal(HttpStatusCode.OK, signup.StatusCode);
        var token = (await signup.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        var me = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/me");
        var org = me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid();
        var actor = me.GetProperty("userId").GetGuid();
        var created = await client.PostAsJsonAsync($"/api/v1/organizations/{org}/companies",
            new CompanyRequest("Retained company", Guid.NewGuid().ToString("N"), "USD", 1, 1));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var company = (await created.Content.ReadFromJsonAsync<Company>())!;
        var csv = "company_id,date,journal_id,account_id,line_id,debit,credit\r\n";
        Guid accountId, exportId, caseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            accountId = Guid.NewGuid(); exportId = Guid.NewGuid(); caseId = Guid.NewGuid();
            var expiry = DateTimeOffset.UtcNow.AddDays(3);
            var opened = DateTimeOffset.UtcNow;
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(csv)));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO saas."Accounts" ("Id","TenantId","OrganizationId","FirstCompanyId","OwnerId","BillingContact","State","LegalHold","QuotaEpoch")
                VALUES ({accountId},{company.TenantId},{org},{company.Id},{actor},'owner@example.com',0,false,0)
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO saas."Exports" ("Id","TenantId","RequestedBy","From","Cutoff","ExpiresAt","Sha256","Companies","Rows","Csv")
                VALUES ({exportId},{company.TenantId},{actor},{new DateOnly(2025, 1, 1)},{new DateOnly(2025, 1, 31)},
                    {expiry},{digest},{new[] { company.Id }},0,{csv})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO saas."Cases" ("Id","TenantId","OpenedBy","Kind","Summary","OpenedAt")
                VALUES ({caseId},{company.TenantId},{actor},'offboarding','Prepared historical case',{opened})
                """);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            await db.Database.MigrateAsync();
            Assert.Equal(22, (await db.Database.GetAppliedMigrationsAsync()).Count());
            var account = await db.Set<CommercialAccount>().SingleAsync(x => x.Id == accountId);
            var export = await db.Set<PortabilityExport>().SingleAsync(x => x.Id == exportId);
            var review = await db.Set<CommercialCase>().SingleAsync(x => x.Id == caseId);
            Assert.Null(account.RetainedExportId);
            Assert.Null(review.ExportId);
            Assert.Equal(csv, export.Csv);
            Assert.Equal(company.TenantId, export.TenantId);
            Assert.Equal(company.Id, Assert.Single(export.Companies));
            account.RetainedExportId = exportId;
            review.ExportId = exportId;
            await db.SaveChangesAsync();
        }
    }
}
