using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.Organizations;
using Xunit;

namespace RAQM.Tests;
[Collection("PostgreSQL"), Trait("Category", "Integration")]
public sealed partial class AccountingIntegrationTests(PostgreSqlFixture fixture)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed record Setup(HttpClient Client, Guid User, Guid Org, Company Company, Dictionary<string, AccountResponse> Accounts, FiscalYearResponse Year)
    {
        public string Root => $"/api/v1/organizations/{Org}/companies/{Company.Id}/accounting";
    }

    private static async Task<HttpResponseMessage> Request(HttpClient c, HttpMethod method, string path, object? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        var response = await c.SendAsync(request);
        Assert.True(response.StatusCode == expected, $"{method} {path}: expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response;
    }

    private async Task<Setup> Arrange(string currency = "USD", int? year = null)
    {
        var client = fixture.Factory.CreateClient();
        var onboarding = await Request(client, HttpMethod.Post, "/api/v1/auth/onboard", new OnboardRequest($"{Guid.NewGuid():N}@accounting.example", "Financial_Test-Password1!", "Accounting Org", ApiFactory.OnboardingKey), HttpStatusCode.OK);
        var tokens = (await onboarding.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        var org = me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid();
        var companyResponse = await Request(client, HttpMethod.Post, $"/api/v1/organizations/{org}/companies", new CompanyRequest("Accounting Company", Guid.NewGuid().ToString("N"), currency, 1, 1), HttpStatusCode.Created);
        var company = (await companyResponse.Content.ReadFromJsonAsync<Company>())!;
        var root = $"/api/v1/organizations/{org}/companies/{company.Id}/accounting";
        var accounts = (await (await Request(client, HttpMethod.Post, root + "/accounts/initialize", new InitializeAccountsRequest(), HttpStatusCode.OK)).Content.ReadFromJsonAsync<List<AccountResponse>>())!.ToDictionary(x => x.Code);
        var fiscal = (await (await Request(client, HttpMethod.Post, root + "/fiscal-years", new FiscalYearRequest(year ?? Today.Year, "Fiscal year"), HttpStatusCode.Created)).Content.ReadFromJsonAsync<FiscalYearResponse>())!;
        return new(client, me.GetProperty("userId").GetGuid(), org, company, accounts, fiscal);
    }

    private static JournalRequest Sale(Setup s, decimal amount = 100m, DateOnly? date = null) => new(date ?? Today, date ?? Today, "Cash sale", "REF-1", [new(s.Accounts["1100"].Id, amount, 0, "Cash"), new(s.Accounts["4100"].Id, 0, amount, "Sales")]);
    private static async Task<JournalResponse> Draft(Setup s, JournalRequest? r = null) => (await (await Request(s.Client, HttpMethod.Post, s.Root + "/journals", r ?? Sale(s), HttpStatusCode.Created)).Content.ReadFromJsonAsync<JournalResponse>())!;
    private static async Task<PostingReceipt> Post(Setup s, JournalResponse j, string? key = null, bool backdated = false) => (await (await Request(s.Client, HttpMethod.Post, $"{s.Root}/journals/{j.Id}/post", new PostJournalRequest(key ?? Guid.NewGuid().ToString("N"), j.Version, backdated, backdated ? "Approved initial/backdated entry" : null), HttpStatusCode.OK)).Content.ReadFromJsonAsync<PostingReceipt>())!;
    private async Task<int> PostedCount(Guid company)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().Journals.CountAsync(x => x.CompanyId == company && x.State == JournalState.Posted);
    }

    [Fact]
    public async Task BalancedWorkflowLedgerAndTrialBalanceAgreeWithIndependentExpectations()
    {
        var s = await Arrange();
        using var client = s.Client;
        var sale = await Draft(s);
        await Post(s, sale);
        var expense = await Draft(s, new(Today, Today, "Operating expense", "REF-2", [new(s.Accounts["5100"].Id, 25, 0, null), new(s.Accounts["1100"].Id, 0, 25, null)]));
        await Post(s, expense);
        var trial = (await client.GetFromJsonAsync<TrialBalanceResponse>($"{s.Root}/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}&includeHierarchy=true"))!;
        Assert.Equal(125m, trial.DebitMovement);
        Assert.Equal(125m, trial.CreditMovement);
        Assert.Equal(100m, trial.ClosingDebit);
        Assert.Equal(100m, trial.ClosingCredit);
        Assert.Equal(75m, trial.Accounts.Single(x => x.Code == "1100").ClosingBalance);
        Assert.Equal(-100m, trial.Accounts.Single(x => x.Code == "4100").ClosingBalance);
        Assert.Equal(25m, trial.Accounts.Single(x => x.Code == "5100").ClosingBalance);
        Assert.Equal(75m, trial.Accounts.Single(x => x.Code == "1000").ClosingBalance);
        var ledger = (await client.GetFromJsonAsync<AccountLedgerResponse>($"{s.Root}/accounts/{s.Accounts["1100"].Id}/ledger?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))!;
        Assert.Equal(0m, ledger.OpeningBalance);
        Assert.Equal(100m, ledger.DebitMovement);
        Assert.Equal(25m, ledger.CreditMovement);
        Assert.Equal(75m, ledger.ClosingBalance);
        Assert.Equal(new[] { 100m, 75m }, ledger.Transactions.Items.Select(x => x.RunningBalance));
        var page = (await client.GetFromJsonAsync<AccountLedgerResponse>($"{s.Root}/accounts/{s.Accounts["1100"].Id}/ledger?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}&page=2&pageSize=1"))!;
        Assert.Single(page.Transactions.Items);
        Assert.Equal(75m, page.Transactions.Items[0].RunningBalance);
        var parentLedger = (await client.GetFromJsonAsync<AccountLedgerResponse>($"{s.Root}/accounts/{s.Accounts["1000"].Id}/ledger?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))!;
        Assert.Equal(75m, parentLedger.ClosingBalance);
        Assert.Equal(2, await PostedCount(s.Company.Id));
    }

    [Fact]
    public async Task UnbalancedEntryAndInvalidLineLeaveNoFinancialEffects()
    {
        var s = await Arrange();
        using var client = s.Client;
        var bad = await Draft(s, Sale(s)with { Lines = [new(s.Accounts["1100"].Id, 100, 0, null), new(s.Accounts["4100"].Id, 0, 99, null)] });
        await Request(client, HttpMethod.Post, $"{s.Root}/journals/{bad.Id}/post", new PostJournalRequest("unbalanced", bad.Version), HttpStatusCode.BadRequest);
        await Request(client, HttpMethod.Post, s.Root + "/journals", Sale(s)with { Lines = [new(s.Accounts["1100"].Id, 1, 1, null)] }, HttpStatusCode.BadRequest);
        Assert.Equal(0, await PostedCount(s.Company.Id));
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().Journals.CountAsync(x => x.CompanyId == s.Company.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InactiveAndNonPostingAccountsCannotPost(bool inactive)
    {
        var s = await Arrange();
        using var client = s.Client;
        var account = s.Accounts["1100"];
        if (inactive)
            await Request(client, HttpMethod.Put, $"{s.Root}/accounts/{account.Id}", new UpdateAccountRequest(new(account.Code, account.Name, null, account.Classification, true, account.ParentId, false, false), account.Version), HttpStatusCode.OK);
        var draft = await Draft(s, Sale(s)with { Lines = [new(inactive ? account.Id : s.Accounts["1000"].Id, 100, 0, null), new(s.Accounts["4100"].Id, 0, 100, null)] });
        await Request(client, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/post", new PostJournalRequest("ineligible", draft.Version), HttpStatusCode.BadRequest);
        Assert.Equal(0, await PostedCount(s.Company.Id));
    }

    [Fact]
    public async Task ClosedAndLockedPeriodsRejectPostingAndRecordLifecycle()
    {
        var s = await Arrange(year: Today.Year - 1);
        using var client = s.Client;
        var period = s.Year.Periods.First();
        var closed = (await (await Request(client, HttpMethod.Put, $"{s.Root}/periods/{period.Id}/state", new PeriodStateRequest(PeriodState.Closed, "Month close", period.Version), HttpStatusCode.OK)).Content.ReadFromJsonAsync<PeriodResponse>())!;
        var draft = await Draft(s, Sale(s, date: period.StartDate));
        await Request(client, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/post", new PostJournalRequest("closed", draft.Version, true, "Approved historical probe"), HttpStatusCode.Conflict);
        var reopened = (await (await Request(client, HttpMethod.Put, $"{s.Root}/periods/{period.Id}/state", new PeriodStateRequest(PeriodState.Open, "Authorized correction", closed.Version), HttpStatusCode.OK)).Content.ReadFromJsonAsync<PeriodResponse>())!;
        var locked = (await (await Request(client, HttpMethod.Put, $"{s.Root}/periods/{period.Id}/state", new PeriodStateRequest(PeriodState.Locked, "Final lock", reopened.Version), HttpStatusCode.OK)).Content.ReadFromJsonAsync<PeriodResponse>())!;
        await Request(client, HttpMethod.Put, $"{s.Root}/periods/{period.Id}/state", new PeriodStateRequest(PeriodState.Open, "Attempt reopen", locked.Version), HttpStatusCode.Conflict);
        Assert.Equal(0, await PostedCount(s.Company.Id));
    }

    [Fact]
    public async Task ConcurrentIdempotentPostsProduceOneJournalAndOneLineSet()
    {
        var s = await Arrange();
        using var client = s.Client;
        var draft = await Draft(s);
        var key = Guid.NewGuid().ToString("N");
        var replies = await Task.WhenAll(client.PostAsJsonAsync($"{s.Root}/journals/{draft.Id}/post", new PostJournalRequest(key, draft.Version)), client.PostAsJsonAsync($"{s.Root}/journals/{draft.Id}/post", new PostJournalRequest(key, draft.Version)));
        Assert.All(replies, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        var first = (await replies[0].Content.ReadFromJsonAsync<PostingReceipt>())!;
        var second = (await replies[1].Content.ReadFromJsonAsync<PostingReceipt>())!;
        Assert.Equal(first, second);
        Assert.Equal(1, await PostedCount(s.Company.Id));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.Equal(2, await db.JournalLines.CountAsync(x => x.CompanyId == s.Company.Id));
        await Request(client, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/post", new PostJournalRequest("different-key", draft.Version), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ConcurrentPeriodCloseAndPostingHaveSerializedConsistentOutcome()
    {
        var s = await Arrange(year: Today.Year - 1);
        using var client = s.Client;
        var period = s.Year.Periods.First();
        var draft = await Draft(s, Sale(s, date: period.StartDate));
        var post = client.PostAsJsonAsync($"{s.Root}/journals/{draft.Id}/post", new PostJournalRequest("race", draft.Version, true, "Approved historical race"));
        var close = client.PutAsJsonAsync($"{s.Root}/periods/{period.Id}/state", new PeriodStateRequest(PeriodState.Closed, "Concurrent close", period.Version));
        var responses = await Task.WhenAll(post, close);
        Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
        Assert.Contains(responses[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        Assert.Equal(responses[0].StatusCode == HttpStatusCode.OK ? 1 : 0, await PostedCount(s.Company.Id));
    }

    [Fact]
    public async Task PostedRecordsCannotBeEditedOrDeletedThroughApiOrDirectSql()
    {
        var s = await Arrange();
        using var client = s.Client;
        var draft = await Draft(s);
        await Post(s, draft);
        await Request(client, HttpMethod.Put, $"{s.Root}/journals/{draft.Id}", new UpdateJournalRequest(Sale(s, 999), draft.Version), HttpStatusCode.Conflict);
        await Request(client, HttpMethod.Delete, $"{s.Root}/journals/{draft.Id}", null, HttpStatusCode.Conflict);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var header = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE accounting.\"Journals\" SET \"Description\"='Tampered' WHERE \"Id\"={draft.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, header.SqlState);
        var line = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounting.\"JournalLines\" WHERE \"JournalId\"={draft.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, line.SqlState);
        var lineUpdate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE accounting.\"JournalLines\" SET \"Description\"='Tampered' WHERE \"JournalId\"={draft.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, lineUpdate.SqlState);
        var deleted = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounting.\"Journals\" WHERE \"Id\"={draft.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, deleted.SqlState);
        Assert.Equal(1, await PostedCount(s.Company.Id));
    }

    [Fact]
    public async Task ReversalPreservesOriginalAndPreventsSecondReversal()
    {
        var s = await Arrange();
        using var client = s.Client;
        var draft = await Draft(s);
        await Post(s, draft);
        var original = (await client.GetFromJsonAsync<JournalResponse>($"{s.Root}/journals/{draft.Id}"))!;
        var request = new ReverseJournalRequest(Today, "Correct erroneous sale", "reverse-once");
        var receipt = (await (await Request(client, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/reverse", request, HttpStatusCode.OK)).Content.ReadFromJsonAsync<PostingReceipt>())!;
        await Request(client, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/reverse", request, HttpStatusCode.OK); // same-key retry
        await Request(client, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/reverse", request with { IdempotencyKey = "another-reversal" }, HttpStatusCode.Conflict);
        var originalAfter = (await client.GetFromJsonAsync<JournalResponse>($"{s.Root}/journals/{draft.Id}"))!;
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(originalAfter));
        var reversal = (await client.GetFromJsonAsync<JournalResponse>($"{s.Root}/journals/{receipt.JournalId}"))!;
        Assert.Equal(draft.Id, reversal.ReversesJournalId);
        Assert.Equal(draft.Id, reversal.SourceDocumentId);
        Assert.Equal(100m, reversal.Lines.Single(x => x.AccountId == s.Accounts["1100"].Id).Credit);
        var trial = (await client.GetFromJsonAsync<TrialBalanceResponse>($"{s.Root}/trial-balance?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))!;
        Assert.Equal(0m, trial.ClosingDebit);
        Assert.Equal(0m, trial.ClosingCredit);
        Assert.Equal(2, await PostedCount(s.Company.Id));
    }

    [Fact]
    public async Task CrossTenantCrossCompanyAndReadOnlyPermissionsFailClosed()
    {
        var a = await Arrange();
        var b = await Arrange();
        using var ac = a.Client;
        using var bc = b.Client;
        var draft = await Draft(a);
        await Request(bc, HttpMethod.Get, $"{a.Root}/journals/{draft.Id}", null, HttpStatusCode.NotFound);
        await Request(bc, HttpMethod.Get, $"{b.Root}/journals/{draft.Id}", null, HttpStatusCode.NotFound);
        await Request(ac, HttpMethod.Post, a.Root + "/journals", Sale(a)with { Lines = [new(b.Accounts["1100"].Id, 100, 0, null), new(a.Accounts["4100"].Id, 0, 100, null)] }, HttpStatusCode.BadRequest);
        await Request(ac, HttpMethod.Post, $"/api/v1/organizations/{a.Org}/members", new AddMemberRequest(b.User), HttpStatusCode.NoContent);
        await Request(ac, HttpMethod.Put, $"/api/v1/organizations/{a.Org}/companies/{a.Company.Id}/members", new GrantCompanyRequest(b.User, CompanyRole.Reader), HttpStatusCode.NoContent);
        await Request(bc, HttpMethod.Get, $"{a.Root}/journals/{draft.Id}", null, HttpStatusCode.OK);
        await Request(bc, HttpMethod.Post, $"{a.Root}/journals/{draft.Id}/post", new PostJournalRequest("reader", draft.Version), HttpStatusCode.Forbidden);
        await Request(bc, HttpMethod.Post, a.Root + "/fiscal-years", new FiscalYearRequest(Today.Year + 1, "Denied"), HttpStatusCode.Forbidden);
        Assert.Equal(0, await PostedCount(a.Company.Id));
        await Request(ac, HttpMethod.Put, $"/api/v1/organizations/{a.Org}/companies/{a.Company.Id}/members", new GrantCompanyRequest(b.User, CompanyRole.Manager), HttpStatusCode.NoContent);
        var period = a.Year.Periods.Single(x => x.StartDate <= Today && x.EndDate >= Today);
        await Request(bc, HttpMethod.Put, $"{a.Root}/periods/{period.Id}/state", new PeriodStateRequest(PeriodState.Closed, "Denied manager close", period.Version), HttpStatusCode.Forbidden);
        await Request(bc, HttpMethod.Post, $"{a.Root}/journals/{draft.Id}/post", new PostJournalRequest("manager-post", draft.Version), HttpStatusCode.OK);
    }

    [Fact]
    public async Task HierarchyCyclesAndUsedAccountStructuralChangesAreRejected()
    {
        var s = await Arrange();
        using var client = s.Client;
        var root = s.Accounts["1000"];
        var child = (await (await Request(client, HttpMethod.Post, s.Root + "/accounts", new AccountRequest("1050", "Asset subgroup", null, AccountClass.Asset, false, root.Id), HttpStatusCode.Created)).Content.ReadFromJsonAsync<AccountResponse>())!;
        await Request(client, HttpMethod.Put, $"{s.Root}/accounts/{root.Id}", new UpdateAccountRequest(new(root.Code, root.Name, null, root.Classification, false, child.Id), root.Version), HttpStatusCode.BadRequest);
        var cash = s.Accounts["1100"];
        await Post(s, await Draft(s));
        await Request(client, HttpMethod.Put, $"{s.Root}/accounts/{cash.Id}", new UpdateAccountRequest(new("1199", cash.Name, null, cash.Classification, true, cash.ParentId), cash.Version), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task FiscalOverlapIsRejectedByServiceAndDatabase()
    {
        var s = await Arrange();
        using var client = s.Client;
        await Request(client, HttpMethod.Post, s.Root + "/fiscal-years", new FiscalYearRequest(Today.Year, "Duplicate"), HttpStatusCode.Conflict);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        db.FiscalYears.Add(new FiscalYear { TenantId = s.Company.TenantId, OrganizationId = s.Org, CompanyId = s.Company.Id, CreatedBy = s.User, Name = "Overlapping", StartDate = s.Year.StartDate.AddDays(1), EndDate = s.Year.EndDate });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task OverlappingPeriodRowsFailAtomicallyEvenWithDifferentPeriodNumbers()
    {
        var s = await Arrange();
        using var client = s.Client;
        var next = new DateOnly(Today.Year + 1, 1, 1);
        var yearId = Guid.NewGuid();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            db.FiscalYears.Add(new FiscalYear { Id = yearId, TenantId = s.Company.TenantId, OrganizationId = s.Org, CompanyId = s.Company.Id, Name = "Atomic overlap probe", StartDate = next, EndDate = next.AddYears(1).AddDays(-1), CreatedBy = s.User });
            await db.SaveChangesAsync();
            db.Periods.AddRange(new AccountingPeriod { TenantId = s.Company.TenantId, OrganizationId = s.Org, CompanyId = s.Company.Id, FiscalYearId = yearId, Number = 1, StartDate = next, EndDate = next.AddMonths(2).AddDays(-1), UpdatedBy = s.User }, new AccountingPeriod { TenantId = s.Company.TenantId, OrganizationId = s.Org, CompanyId = s.Company.Id, FiscalYearId = yearId, Number = 2, StartDate = next.AddMonths(1), EndDate = next.AddMonths(3).AddDays(-1), UpdatedBy = s.User });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ExclusionViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }

        using var verifyScope = fixture.Factory.Services.CreateScope();
        Assert.False(await verifyScope.ServiceProvider.GetRequiredService<RaqmDbContext>().FiscalYears.AnyAsync(x => x.Id == yearId));
    }

    [Fact]
    public async Task ClosedPeriodReversalLeavesOriginalPostedAndCreatesNoCorrection()
    {
        var s = await Arrange();
        using var client = s.Client;
        var draft = await Draft(s);
        await Post(s, draft);
        var period = s.Year.Periods.Single(x => x.StartDate <= Today && x.EndDate >= Today);
        // Establish the closed-period prerequisite; this test isolates reversal finality.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            var fixturePeriod = await db.Periods.SingleAsync(x => x.Id == period.Id);
            fixturePeriod.State = PeriodState.Closed;
            await db.SaveChangesAsync();
        }
        await Request(client, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/reverse", new ReverseJournalRequest(Today, "Invalid closed period correction", "closed-reversal"), HttpStatusCode.Conflict);
        Assert.Equal(1, await PostedCount(s.Company.Id));
        var original = (await client.GetFromJsonAsync<JournalResponse>($"{s.Root}/journals/{draft.Id}"))!;
        Assert.Equal(JournalState.Posted, original.State);
        Assert.Null(original.ReversesJournalId);
    }

    [Fact]
    public async Task OpeningBatchPostsThroughEngineAndCannotBeDuplicated()
    {
        var s = await Arrange();
        using var client = s.Client;
        var request = new OpeningBalanceRequest("initial", "Initial migration balances", s.Year.StartDate, [new(s.Accounts["1100"].Id, 1000, 0, null), new(s.Accounts["3100"].Id, 0, 1000, null)]);
        await Request(client, HttpMethod.Post, s.Root + "/opening-balances", request with { Balances = [new(s.Accounts["1100"].Id, 1000, 0, null), new(s.Accounts["3100"].Id, 0, 999, null)] }, HttpStatusCode.BadRequest);
        var batch = (await (await Request(client, HttpMethod.Post, s.Root + "/opening-balances", request, HttpStatusCode.Created)).Content.ReadFromJsonAsync<OpeningBatchResponse>())!;
        await Request(client, HttpMethod.Post, s.Root + "/opening-balances", request with { BatchKey = "second" }, HttpStatusCode.Conflict);
        var journal = (await client.GetFromJsonAsync<JournalResponse>($"{s.Root}/journals/{batch.JournalId}"))!;
        var postRequest = new PostJournalRequest("opening-post", journal.Version, true, "Approved initial conversion");
        await Request(client, HttpMethod.Post, $"{s.Root}/opening-balances/{batch.Id}/post", postRequest, HttpStatusCode.OK);
        await Request(client, HttpMethod.Post, $"{s.Root}/opening-balances/{batch.Id}/post", postRequest, HttpStatusCode.OK);
        Assert.Equal(1, await PostedCount(s.Company.Id));
        var posted = (await client.GetFromJsonAsync<JournalResponse>($"{s.Root}/journals/{batch.JournalId}"))!;
        Assert.Equal("OpeningBalance", posted.SourceModule);
        Assert.Equal(batch.Id, posted.SourceDocumentId);
        await Request(client, HttpMethod.Post, s.Root + "/fiscal-years",
            new FiscalYearRequest(s.Year.StartDate.Year - 1, "Invalid earlier opening anchor"), HttpStatusCode.Conflict);
        var trial = (await client.GetFromJsonAsync<TrialBalanceResponse>($"{s.Root}/trial-balance?from={s.Year.StartDate:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))!;
        Assert.Equal(1000m, trial.ClosingDebit);
        Assert.Equal(1000m, trial.ClosingCredit);
        var afterOpening = s.Year.StartDate.AddDays(1);
        var ledger = (await client.GetFromJsonAsync<AccountLedgerResponse>($"{s.Root}/accounts/{s.Accounts["1100"].Id}/ledger?from={afterOpening:yyyy-MM-dd}&to={afterOpening:yyyy-MM-dd}"))!;
        Assert.Equal(1000m, ledger.OpeningBalance);
        Assert.Equal(1000m, ledger.ClosingBalance);
        Assert.Equal(0m, ledger.DebitMovement);
        Assert.Equal(0, ledger.Transactions.Total);
    }

    [Fact]
    public async Task CompanyFinancialSettingsAreFrozenButPermittedNameUpdatesRemain()
    {
        var s = await Arrange();
        using var client = s.Client;
        await Request(client, HttpMethod.Put, $"/api/v1/organizations/{s.Org}/companies/{s.Company.Id}", new UpdateCompanyRequest("Company", "USD", 4, 1, s.Company.Version), HttpStatusCode.Conflict);
        await Post(s, await Draft(s));
        await Request(client, HttpMethod.Put, $"/api/v1/organizations/{s.Org}/companies/{s.Company.Id}", new UpdateCompanyRequest("Company", "IQD", 1, 1, s.Company.Version), HttpStatusCode.Conflict);
        await Request(client, HttpMethod.Put, $"/api/v1/organizations/{s.Org}/companies/{s.Company.Id}", new UpdateCompanyRequest("New legal name", "USD", 1, 1, s.Company.Version), HttpStatusCode.OK);
    }

    [Fact]
    public async Task MoneyPrecisionAndExplicitBackdatingAreEnforced()
    {
        var date = Today.AddDays(-1);
        var s = await Arrange(year: date.Year);
        using var client = s.Client;
        await Request(client, HttpMethod.Post, s.Root + "/journals", Sale(s, 0.001m, date), HttpStatusCode.BadRequest);
        var draft = await Draft(s, Sale(s, 100, date));
        await Request(client, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/post", new PostJournalRequest("backdate", draft.Version), HttpStatusCode.BadRequest);
        await Post(s, draft, "approved-backdate", true);
        Assert.Equal(1, await PostedCount(s.Company.Id));
    }

    private sealed class FailPostedSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<JournalEntry>().Any(x => x.Entity.State == JournalState.Posted))
                throw new InvalidOperationException("Injected failure after draft/line flush.");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task FailureAfterDraftFlushRollsBackEveryAccountingWrite()
    {
        var s = await Arrange();
        using var client = s.Client;
        var source = Guid.NewGuid();
        await using (var db = new RaqmDbContext(new DbContextOptionsBuilder<RaqmDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(new FailPostedSave()).Options))
        {
            var access = new FinancialAccess(db, new OrganizationService(db));
            var engine = new PostingEngine(db, access, new PostingValidator(db, access, TimeProvider.System), TimeProvider.System);
            var request = new PostingRequest(new(s.Company.TenantId, s.Org, s.Company.Id), "FailureTest", source, "fail-once", Today, "USD", Sale(s).Lines, null, Today, "Rollback test");
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PostAsync(s.User, request, default));
        }

        using var scope = fixture.Factory.Services.CreateScope();
        var verify = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.Equal(0, await verify.Journals.CountAsync(x => x.CompanyId == s.Company.Id));
        Assert.Equal(0, await verify.JournalLines.CountAsync(x => x.CompanyId == s.Company.Id));
        Assert.Equal(0, await PostedCount(s.Company.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFinancialTransactionPreservesPrimaryExceptionAndRollsBackJournal(bool failAtCommit)
    {
        var s = await Arrange();
        using var client = s.Client;
        using var scope = fixture.Factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<RaqmDbContext>();
        var coordinator = services.GetRequiredService<RAQM.Api.Interfaces.Accounting.IFinancialTransaction>();
        var posting = services.GetRequiredService<RAQM.Api.Interfaces.Accounting.IPostingEngine>();
        var primary = new InvalidOperationException("Injected source failure after staging a balanced journal");
        var request = new PostingRequest(new(s.Company.TenantId, s.Org, s.Company.Id), "FailureTest",
            Guid.NewGuid(), "primary-failure", Today, "USD", Sale(s).Lines, null, Today, "Rollback test");

        var thrown = await Record.ExceptionAsync(() => coordinator.Execute(s.User, s.Org, s.Company.Id, async (_, ct) =>
        {
            var receipt = await posting.PostAsync(s.User, request, ct);
            if (!failAtCommit)
                throw primary;

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TEMP TABLE commit_probe (id integer CONSTRAINT commit_probe_unique UNIQUE DEFERRABLE INITIALLY DEFERRED);
                INSERT INTO commit_probe VALUES (1), (1);
                """, ct);
            return receipt;
        }, default));

        if (failAtCommit)
        {
            var postgres = Assert.IsType<PostgresException>(thrown);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal("commit_probe_unique", postgres.ConstraintName);
            Assert.Contains("Commit", postgres.StackTrace);
        }
        else
        {
            Assert.Same(primary, thrown);
            Assert.Contains(nameof(FailedFinancialTransactionPreservesPrimaryExceptionAndRollsBackJournal), primary.StackTrace);
        }
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.False(await db.Journals.AnyAsync(j => j.CompanyId == s.Company.Id));
        Assert.False(await db.JournalLines.AnyAsync(l => l.CompanyId == s.Company.Id));
    }

    [Fact]
    public async Task EngineRejectsIdempotencyPayloadChangesAndForgedTenant()
    {
        var s = await Arrange();
        using var client = s.Client;
        var source = Guid.NewGuid();
        var request = new PostingRequest(new(s.Company.TenantId, s.Org, s.Company.Id), "IntegrationSource", source, "one-key", Today, "USD", Sale(s).Lines, null, Today, "Source posting");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<RAQM.Api.Interfaces.Accounting.IPostingEngine>();
            await engine.PostAsync(s.User, request, default);
        }

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<RAQM.Api.Interfaces.Accounting.IPostingEngine>();
            var error = await Assert.ThrowsAsync<ApiException>(() => engine.PostAsync(s.User, request with { Lines = Sale(s, 200).Lines }, default));
            Assert.Equal("idempotency_conflict", error.Code);
        }

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<RAQM.Api.Interfaces.Accounting.IPostingEngine>();
            var error = await Assert.ThrowsAsync<ApiException>(() => engine.PostAsync(s.User, request with { Scope = request.Scope with { TenantId = Guid.NewGuid() } }, default));
            Assert.Equal(404, error.Status);
        }

        Assert.Equal(1, await PostedCount(s.Company.Id));
    }
}
