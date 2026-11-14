using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.DTOs.SaaS;
using RAQM.Api.DTOs.Integrations;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.SaaS;
using RAQM.Api.Entities.Integrations;
using RAQM.Api.Services.SaaS;
using RAQM.Api.Services.Automation;
using Xunit;

namespace RAQM.Tests;

public sealed partial class AccountingIntegrationTests
{
    [Fact]
    public async Task ConcurrentProvisioningAndQuotaRetainOneTenantAndOneRemainingCompany()
    {
        var owner = await Arrange();
        var second = await Arrange();
        using var ownerClient = owner.Client;
        using var secondClient = second.Client;
        await using var ops = Operators(owner, second);
        using var maker = AsOperator(ops, owner);
        using var reviewer = AsOperator(ops, second);
        var plan = await Publish(maker, reviewer, 100m, []);
        var input = ProvisionInput(plan.Id, true);
        var accepted = await Task.WhenAll(Gov<CommercialAccount>(ownerClient, "/api/v1/saas/provision", input),
            Gov<CommercialAccount>(ownerClient, "/api/v1/saas/provision", input));
        Assert.Equal(accepted[0].TenantId, accepted[1].TenantId);
        Assert.Equal(accepted[0].FirstCompanyId, accepted[1].FirstCompanyId);
        using var duplicateTrial = await Request(ownerClient, HttpMethod.Post, "/api/v1/saas/provision",
            input with { Key = "second-trial" }, HttpStatusCode.Conflict);
        var org = accepted[0].OrganizationId;
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => ownerClient.PostAsJsonAsync(
            $"/api/v1/organizations/{org}/companies", new CompanyRequest("Additional", Guid.NewGuid().ToString("N"), "USD", 1, 1))));
        try
        {
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var result in results) result.Dispose(); }
        var legacy = await ownerClient.GetFromJsonAsync<JsonElement>($"/api/v1/organizations/{owner.Org}/commercial");
        Assert.True(legacy.GetProperty("legacy").GetBoolean());
        using var denied = await Request(secondClient, HttpMethod.Get, $"/api/v1/organizations/{org}/commercial", null, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task VerifiedCollectionsAndRefundsRemainOutsideTheSubscriberLedger()
    {
        var owner = await Arrange();
        var second = await Arrange();
        using var ownerClient = owner.Client;
        using var secondClient = second.Client;
        await using var ops = Operators(owner, second);
        using var maker = AsOperator(ops, owner);
        using var reviewer = AsOperator(ops, second);
        var plan = await Publish(maker, reviewer, 100m, []);
        var account = await Gov<CommercialAccount>(ownerClient, "/api/v1/saas/provision", ProvisionInput(plan.Id, false));
        var billing = $"/api/v1/organizations/{account.OrganizationId}/commercial/billing";
        var before = await ownerClient.GetFromJsonAsync<JsonElement>(billing);
        var invoice = before.GetProperty("invoices")[0].GetProperty("invoice").GetProperty("id").GetGuid();
        var route = $"/api/v1/platform-commercial/tenants/{account.TenantId}/movements";
        var paymentInput = new CommercialMovementInput(invoice, CommercialMovementKind.Payment, null, 60m, "USD", "BANK-001", "Bank statement reference");
        var payment = await Gov<CommercialMovement>(maker, route, paymentInput);
        Assert.Equal(payment.Id, (await Gov<CommercialMovement>(maker, route, paymentInput)).Id);
        using var self = await Request(maker, HttpMethod.Post, $"{route}/{payment.Id}/verify", new { }, HttpStatusCode.Conflict);
        using var noRole = await Request(ownerClient, HttpMethod.Post, $"{route}/{payment.Id}/verify", new { }, HttpStatusCode.Forbidden);
        await Gov<CommercialMovement>(reviewer, $"{route}/{payment.Id}/verify", new { });
        var refund = await Gov<CommercialMovement>(maker, route, new CommercialMovementInput(invoice, CommercialMovementKind.Refund,
            payment.Id, 10m, "USD", "BANK-002", "Verified refund statement reference"));
        await Gov<CommercialMovement>(reviewer, $"{route}/{refund.Id}/verify", new { });
        var after = await ownerClient.GetFromJsonAsync<JsonElement>(billing);
        Assert.Equal(50m, after.GetProperty("outstanding").GetDecimal());
        Assert.Equal(0, await PostedCount(account.FirstCompanyId));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""UPDATE saas."Invoices" SET "Total"=0 WHERE "Id"=@id""", connection);
        command.Parameters.AddWithValue("id", invoice);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task OperationalSuspensionDeniesJournalWritesAndReactivationPreservesTheDraft()
    {
        var owner = await Arrange();
        var second = await Arrange();
        using var ownerClient = owner.Client;
        using var secondClient = second.Client;
        await using var ops = Operators(owner, second);
        using var maker = AsOperator(ops, owner);
        using var reviewer = AsOperator(ops, second);
        var plan = await Publish(maker, reviewer, 0m, []);
        var account = await Gov<CommercialAccount>(ownerClient, "/api/v1/saas/provision", ProvisionInput(plan.Id, false));
        var path = $"/api/v1/organizations/{account.OrganizationId}/companies/{account.FirstCompanyId}/accounting";
        var chart = (await Gov<List<AccountResponse>>(ownerClient, path + "/accounts/initialize", new InitializeAccountsRequest()))
            .ToDictionary(x => x.Code, x => x.Id);
        using var fiscal = await Request(ownerClient, HttpMethod.Post, path + "/fiscal-years", new FiscalYearRequest(Today.Year, "Fiscal"), HttpStatusCode.Created);
        using var prepared = await Request(ownerClient, HttpMethod.Post, path + "/journals",
            new JournalRequest(Today, Today, "Sale", null, [new(chart["1100"], 200, 0, null), new(chart["4100"], 0, 200, null)]),
            HttpStatusCode.Created);
        var draft = (await prepared.Content.ReadFromJsonAsync<JournalResponse>())!;
        var operations = $"/api/v1/platform-commercial/tenants/{account.TenantId}/operations";
        await Gov<CommercialAccount>(reviewer, operations, new TenantOperationInput("suspend", "Reviewed operational restriction"));
        using var read = await Request(ownerClient, HttpMethod.Get, path + "/accounts", null, HttpStatusCode.OK);
        var post = new PostJournalRequest("commercial-post", draft.Version);
        using var blocked = await Request(ownerClient, HttpMethod.Post, $"{path}/journals/{draft.Id}/post", post, HttpStatusCode.Forbidden);
        Assert.Equal(0, await PostedCount(account.FirstCompanyId));
        await Gov<CommercialAccount>(reviewer, operations, new TenantOperationInput("reactivate", "Reviewed recovery"));
        await Gov<PostingReceipt>(ownerClient, $"{path}/journals/{draft.Id}/post", post);
        Assert.Equal(1, await PostedCount(account.FirstCompanyId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OffboardingRetainsItsReviewedExportThroughRetentionAndLegalHold(bool legalHold)
    {
        var owner = await Arrange();
        var reviewerOwner = await Arrange();
        using var ownerClient = owner.Client;
        using var reviewerClient = reviewerOwner.Client;
        await using var ops = Operators(owner, reviewerOwner);
        using var maker = AsOperator(ops, owner);
        using var reviewer = AsOperator(ops, reviewerOwner);
        var plan = await Publish(maker, reviewer, 0m, []);
        var account = await Gov<CommercialAccount>(ownerClient, "/api/v1/saas/provision", ProvisionInput(plan.Id, false));
        var commercial = $"/api/v1/organizations/{account.OrganizationId}/commercial";
        var created = await Gov<JsonElement>(ownerClient, commercial + "/portability",
            new PortabilityInput(Today.AddDays(-2), Today.AddDays(-1)));
        var exportId = created.GetProperty("id").GetGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        await db.Set<PortabilityExport>().Where(x => x.Id == exportId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(2)));
        var caseRow = await Gov<CommercialCase>(ownerClient, commercial + "/cases",
            new CommercialCaseInput("offboarding", "Reviewed export request", exportId));
        var casePath = $"/api/v1/platform-commercial/tenants/{account.TenantId}/cases/{caseRow.Id}";
        await Gov<CommercialCase>(reviewer, casePath, new CommercialCaseAction("assign", "Assigned for review"));
        await Gov<CommercialCase>(reviewer, casePath, new CommercialCaseAction("close", "Reviewed prepared export"));
        var unreviewed = await Gov<JsonElement>(ownerClient, commercial + "/portability",
            new PortabilityInput(Today.AddDays(-3), Today.AddDays(-1)));
        var unreviewedId = unreviewed.GetProperty("id").GetGuid();
        var retention = DateTimeOffset.UtcNow.AddDays(30);
        using (var missing = await Request(maker, HttpMethod.Post,
            $"/api/v1/platform-commercial/tenants/{account.TenantId}/operations",
            new TenantOperationInput("offboard", "Missing reviewed artifact", RetainUntil: retention, LegalHold: legalHold),
            HttpStatusCode.Conflict)) { }
        using (var mismatched = await Request(maker, HttpMethod.Post,
            $"/api/v1/platform-commercial/tenants/{account.TenantId}/operations",
            new TenantOperationInput("offboard", "Unreviewed artifact", RetainUntil: retention, LegalHold: legalHold, ExportId: unreviewedId),
            HttpStatusCode.Conflict)) { }
        var offboarded = await Gov<CommercialAccount>(maker,
            $"/api/v1/platform-commercial/tenants/{account.TenantId}/operations",
            new TenantOperationInput("offboard", "Approved offboarding", RetainUntil: retention, LegalHold: legalHold, ExportId: exportId));
        Assert.Equal(TenantOperationState.Offboarded, offboarded.State);
        Assert.Equal(exportId, offboarded.RetainedExportId);
        Assert.Equal(exportId, caseRow.ExportId);
        var retained = await db.Set<PortabilityExport>().AsNoTracking().SingleAsync(x => x.Id == exportId);
        var persistedAccount = await db.Set<CommercialAccount>().AsNoTracking().SingleAsync(x => x.TenantId == account.TenantId);
        Assert.Equal(exportId, persistedAccount.RetainedExportId);
        Assert.True(retained.ExpiresAt >= persistedAccount.RetainUntil);
        using var download = await Request(ownerClient, HttpMethod.Get, $"{commercial}/portability/{exportId}", null, HttpStatusCode.OK);
        Assert.Contains("company_id,date,journal_id", await download.Content.ReadAsStringAsync());
        await db.Set<PortabilityExport>().Where(x => x.Id == exportId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await scope.ServiceProvider.GetRequiredService<CommercialService>().PurgeExports(CancellationToken.None);
        Assert.NotNull((await db.Set<PortabilityExport>().AsNoTracking().SingleAsync(x => x.Id == exportId)).Csv);
        using var afterExpiry = await Request(ownerClient, HttpMethod.Get, $"{commercial}/portability/{exportId}", null, HttpStatusCode.OK);
        Assert.Equal(await download.Content.ReadAsStringAsync(), await afterExpiry.Content.ReadAsStringAsync());
        await db.Set<CommercialAccount>().Where(x => x.TenantId == account.TenantId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.RetainUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await scope.ServiceProvider.GetRequiredService<CommercialService>().PurgeExports(CancellationToken.None);
        var afterRetention = await db.Set<PortabilityExport>().AsNoTracking().SingleAsync(x => x.Id == exportId);
        if (legalHold)
        {
            Assert.NotNull(afterRetention.Csv);
            using var held = await Request(ownerClient, HttpMethod.Get, $"{commercial}/portability/{exportId}", null, HttpStatusCode.OK);
        }
        else
        {
            Assert.Null(afterRetention.Csv);
            using var expired = await Request(ownerClient, HttpMethod.Get, $"{commercial}/portability/{exportId}", null, HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task ClaimedExportCannotPublishAcrossTenantSuspensionAndRecoversAfterReactivation()
    {
        var owner = await Arrange();
        var second = await Arrange();
        using var ownerClient = owner.Client;
        using var secondClient = second.Client;
        await using var ops = Operators(owner, second);
        using var maker = AsOperator(ops, owner);
        using var reviewer = AsOperator(ops, second);
        var plan = await Publish(maker, reviewer, 0m, ["integrations", "exports"]);
        var account = await Gov<CommercialAccount>(ownerClient, "/api/v1/saas/provision", ProvisionInput(plan.Id, false));
        var root = $"/api/v1/organizations/{account.OrganizationId}/companies/{account.FirstCompanyId}/integrations";
        var companyRoot = root[..^"/integrations".Length];
        using var fiscalResponse = await Request(ownerClient, HttpMethod.Post, companyRoot + "/accounting/fiscal-years",
            new RAQM.Api.DTOs.Accounting.FiscalYearRequest(2025, "Export query fixture"), HttpStatusCode.Created);
        var fiscal = (await fiscalResponse.Content.ReadFromJsonAsync<RAQM.Api.DTOs.Accounting.FiscalYearResponse>())!;
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        db.OrganizationMembers.Add(new OrganizationMember { TenantId = account.TenantId,
            OrganizationId = account.OrganizationId, UserId = second.User, Role = OrganizationRole.Owner });
        await db.SaveChangesAsync();
        var app = await Gov<IntegrationApplication>(ownerClient, root + "/applications",
            new ApplicationRequest("Tenant cutover", ["reports.read"]));
        await Gov<IntegrationApplication>(secondClient, $"{root}/applications/{app.Id}/approve", new { });
        var schedule = await Gov<ExportSchedule>(ownerClient, $"{root}/applications/{app.Id}/schedules",
            new IntegrationExportScheduleRequest(24, 5, DateTimeOffset.UtcNow.AddSeconds(-1)));
        using var enabled = await Request(secondClient, HttpMethod.Put, $"{root}/schedules/{schedule.Id}/enable", null, HttpStatusCode.OK);
        var jobs = scope.ServiceProvider.GetRequiredService<LedgerExportJobs>();
        Assert.Equal(1, await jobs.Materialize(CancellationToken.None));
        var occurrence = await db.Set<ExportOccurrence>().AsNoTracking().SingleAsync(x => x.ScheduleId == schedule.Id);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION integration.wp07_suspend_tenant_on_claim() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."State"=1 AND OLD."State"<>1 THEN
                UPDATE saas."Accounts" SET "State"=1 WHERE "TenantId"=NEW."TenantId";
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER wp07_suspend_tenant_on_claim BEFORE UPDATE ON integration."Occurrences"
              FOR EACH ROW EXECUTE FUNCTION integration.wp07_suspend_tenant_on_claim();
            """);
        try
        {
            Assert.True(await jobs.Run(CancellationToken.None));
            var retained = await db.Set<ExportOccurrence>().AsNoTracking().SingleAsync(x => x.Id == occurrence.Id);
            Assert.Equal(DeliveryState.InProgress, retained.State);
            Assert.Null(retained.Csv);
            var reporting = companyRoot + "/financial-reporting";
            var query = new { kind = 0, fiscalYearId = fiscal.Id };
            using var read = await Request(ownerClient, HttpMethod.Post, reporting + "/query", query, HttpStatusCode.OK);
            using var export = await Request(ownerClient, HttpMethod.Post, reporting + "/export/csv", query, HttpStatusCode.Forbidden);
            Assert.Contains("report_export_restricted", await export.Content.ReadAsStringAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER wp07_suspend_tenant_on_claim ON integration.\"Occurrences\"; DROP FUNCTION integration.wp07_suspend_tenant_on_claim()");
        }
        await Gov<CommercialAccount>(maker, $"/api/v1/platform-commercial/tenants/{account.TenantId}/operations",
            new TenantOperationInput("reactivate", "Reviewed recovery"));
        await db.Set<ExportOccurrence>().Where(x => x.Id == occurrence.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.True(await jobs.Run(CancellationToken.None));
        Assert.Equal(DeliveryState.Delivered,
            (await db.Set<ExportOccurrence>().AsNoTracking().SingleAsync(x => x.Id == occurrence.Id)).State);
    }

    [Fact]
    public async Task CompetingCommercialWorkersRenewOneCycleAndRetainOneCommercialEvent()
    {
        var owner = await Arrange();
        var second = await Arrange();
        using var ownerClient = owner.Client;
        using var secondClient = second.Client;
        await using var ops = Operators(owner, second);
        using var maker = AsOperator(ops, owner);
        using var reviewer = AsOperator(ops, second);
        var plan = await Publish(maker, reviewer, 0m, []);
        var account = await Gov<CommercialAccount>(ownerClient, "/api/v1/saas/provision", ProvisionInput(plan.Id, false));
        using var first = fixture.Factory.Services.CreateScope();
        using var secondScope = fixture.Factory.Services.CreateScope();
        var db = first.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var day = Today;
        var anchor = day.AddMonths(-1);
        await db.Set<CommercialSubscription>().Where(x => x.TenantId == account.TenantId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Start, anchor).SetProperty(y => y.Anchor, anchor).SetProperty(y => y.End, day));
        await Task.WhenAll(first.ServiceProvider.GetRequiredService<CommercialService>().Tick(CancellationToken.None),
            secondScope.ServiceProvider.GetRequiredService<CommercialService>().Tick(CancellationToken.None));
        Assert.Equal(1, await db.Set<CommercialInvoice>().CountAsync(x => x.TenantId == account.TenantId && x.RequestKey == "cycle:1"));
        Assert.Equal(1, await db.Set<CommercialEvent>().CountAsync(x => x.TenantId == account.TenantId && x.Type == "SubscriptionRenewed"));
    }

    private ApiFactory Operators(Setup one, Setup two)
    {
        var settings = new Dictionary<string, string?>();
        foreach (var role in new[] { "Catalog", "Billing", "Operations", "Support" })
        {
            settings[$"SaaS:Operators:{role}:0"] = one.User.ToString("D");
            settings[$"SaaS:Operators:{role}:1"] = two.User.ToString("D");
        }
        return new ApiFactory(fixture.ConnectionString, extra: settings);
    }

    private static HttpClient AsOperator(ApiFactory factory, Setup owner)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = owner.Client.DefaultRequestHeaders.Authorization;
        return client;
    }

    private static async Task<CommercialPlan> Publish(HttpClient maker, HttpClient reviewer, decimal price, string[] features)
    {
        var draft = await Gov<CommercialPlan>(maker, "/api/v1/platform-commercial/plans", new CommercialPlanInput("RAQM",
            Guid.NewGuid().ToString("N"), "Integration test", "USD", CommercialInterval.Monthly, price, 0m, 1, 14, 7, 2, 5, 5, 5, 5, features));
        return await Gov<CommercialPlan>(reviewer, $"/api/v1/platform-commercial/plans/{draft.Id}/publish", new { });
    }

    private static TenantProvisionInput ProvisionInput(Guid plan, bool trial) => new("create-managed", plan, "Managed organization",
        new CompanyRequest("Initial company", Guid.NewGuid().ToString("N"), "USD", 1, 1), "billing@example.com", trial);
}
