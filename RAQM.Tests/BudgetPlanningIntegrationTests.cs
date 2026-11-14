using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Budgeting;
using RAQM.Api.DTOs.Forecasting;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Entities.Forecasting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.FinancialAnalytics;
using Xunit;

namespace RAQM.Tests;

[Collection("PostgreSQL"), Trait("Category", "Integration")]
public sealed partial class BudgetPlanningIntegrationTests(PostgreSqlFixture fixture)
{
    private sealed record User(HttpClient Client, Guid Id, Guid Org) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed record Setup(User Owner, User Maker, Company Company, FiscalYearResponse Year, Dictionary<string, Guid> Chart) : IDisposable
    {
        public string Root => $"/api/v1/organizations/{Owner.Org}/companies/{Company.Id}";
        public string Budgets => Root + "/budgets";
        public string Forecasts => Root + "/forecasts";
        public string Core => Root + "/accounting";
        public DateOnly Date => Year.StartDate;
        public void Dispose() { Owner.Dispose(); Maker.Dispose(); }
    }

    private static async Task<T> Send<T>(HttpClient client, string path, object input, HttpStatusCode status = HttpStatusCode.OK, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = JsonContent.Create(input) };
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == status, $"{path}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<User> Onboard(ApiFactory? factory = null)
    {
        var client = (factory ?? fixture.Factory).CreateClient();
        var tokens = await Send<TokenResponse>(client, "/api/v1/auth/onboard", new OnboardRequest($"{Guid.NewGuid():N}@planning.example",
            "Planning_Test-Password1!", "Planning Org", ApiFactory.OnboardingKey));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        return new(client, me.GetProperty("userId").GetGuid(), me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid());
    }

    private async Task<Setup> Arrange(ApiFactory? factory = null)
    {
        var owner = await Onboard(factory);
        var maker = await Onboard(factory);
        var company = await Send<Company>(owner.Client, $"/api/v1/organizations/{owner.Org}/companies",
            new CompanyRequest("Planning Company", Guid.NewGuid().ToString("N"), "USD", 4, 1), HttpStatusCode.Created);
        using (var scope = (factory ?? fixture.Factory).Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.OrganizationMembers.Add(new() { TenantId = company.TenantId, OrganizationId = owner.Org, UserId = maker.Id, Role = OrganizationRole.Member });
            db.CompanyMembers.Add(new() { TenantId = company.TenantId, OrganizationId = owner.Org, CompanyId = company.Id,
                UserId = maker.Id, Role = CompanyRole.Manager });
            await db.SaveChangesAsync();
        }
        var root = $"/api/v1/organizations/{owner.Org}/companies/{company.Id}/accounting";
        var chart = (await Send<List<AccountResponse>>(owner.Client, root + "/accounts/initialize", new InitializeAccountsRequest()))
            .ToDictionary(x => x.Code, x => x.Id);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var year = await Send<FiscalYearResponse>(owner.Client, root + "/fiscal-years",
            new FiscalYearRequest(today.Month >= 4 ? today.Year : today.Year - 1, "Non-calendar fiscal year"), HttpStatusCode.Created);
        return new(owner, maker, company, year, chart);
    }

    private static Task<FinancialBudget> Master(Setup s, BudgetKind kind, Guid? year = null) =>
        Send<FinancialBudget>(s.Maker.Client, s.Budgets, new BudgetRequest(year ?? s.Year.Id, kind, $"B-{kind}", $"{kind} budget", "Test budget", 1_000_000));

    private static Task<BudgetVersion> Draft(Setup s, FinancialBudget master, Guid account, decimal amount, string key = "original") =>
        Send<BudgetVersion>(s.Maker.Client, $"{s.Budgets}/{master.Id}/versions", new BudgetVersionRequest(key, "Allocation basis", [new(account, amount, AllocationMethod.Equal)]));

    private static Task<BudgetVersion> Action(Setup s, BudgetVersion row, string action, User? user = null, DateOnly? date = null) =>
        Send<BudgetVersion>((user ?? s.Owner).Client, $"{s.Budgets}/versions/{row.Id}/{action}", new BudgetAction(row.Version,
            "Independent evidence", action == "submit" ? s.Owner.Id : null, date));

    private static async Task<BudgetVersion> Approve(Setup s, BudgetVersion draft)
    {
        var submitted = await Action(s, draft, "submit", s.Maker);
        var reviewed = await Action(s, submitted, "review");
        return await Action(s, reviewed, "approve");
    }

    private static async Task<(FinancialBudget Budget, BudgetVersion Active)> Publish(Setup s, BudgetKind kind, Guid account, decimal total)
    {
        var budget = await Master(s, kind);
        var approved = await Approve(s, await Draft(s, budget, account, total));
        return (budget, await Action(s, approved, "activate", date: s.Date));
    }

    private static Task<BudgetPolicy> Hard(Setup s, BudgetKind kind, Guid account) =>
        Send<BudgetPolicy>(s.Owner.Client, s.Budgets + "/controls", new PolicyRequest(account, kind, ControlMode.Hard, s.Date, 80, "Hard limit"));

    private static Task<JournalResponse> Journal(Setup s, DateOnly date, Guid debit, Guid credit, decimal amount) =>
        Send<JournalResponse>(s.Owner.Client, s.Core + "/journals", new JournalRequest(date, date, "Budget actual",
            null, [new(debit, amount, 0, null), new(credit, 0, amount, null)]), HttpStatusCode.Created);

    private static Task<PostingReceipt> Post(Setup s, JournalResponse journal, string key) =>
        Send<PostingReceipt>(s.Owner.Client, $"{s.Core}/journals/{journal.Id}/post", new PostJournalRequest(key, journal.Version, true, "Historical test"));

    private static async Task<BudgetReport> Report(Setup s, Guid budgetId, Guid account, DateOnly? at = null, string extra = "") =>
        (await s.Owner.Client.GetFromJsonAsync<BudgetReport>($"{s.Budgets}/{budgetId}/report?cutoff={at ?? DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}&account={account}{extra}"))!;

    [Fact]
    public async Task BudgetRequiresAnEligibleAccountAndVersionedIndependentApproval()
    {
        using var s = await Arrange();
        var master = await Master(s, BudgetKind.Operating);
        using var invalid = await s.Maker.Client.PostAsJsonAsync($"{s.Budgets}/{master.Id}/versions",
            new BudgetVersionRequest("wrong", "Wrong class", [new(s.Chart["4100"], 120, AllocationMethod.Equal)]));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var draft = await Draft(s, master, s.Chart["5100"], 1200);
        Assert.Equal(12, draft.Allocations.Count);
        Assert.All(draft.Allocations, x => Assert.Equal(100, x.Amount));
        Assert.Equal(s.Year.Periods.Select(x => x.Id).Order(), draft.Allocations.Select(x => x.PeriodId).Order());
        var submitted = await Action(s, draft, "submit", s.Maker);
        using var stale = await s.Owner.Client.PostAsJsonAsync($"{s.Budgets}/versions/{draft.Id}/review", new BudgetAction(draft.Version, "Stale"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var reviewed = await Action(s, submitted, "review");
        using var unauthorized = await s.Maker.Client.PostAsJsonAsync($"{s.Budgets}/versions/{draft.Id}/approve", new BudgetAction(reviewed.Version, "Unauthorized"));
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        var active = await Action(s, await Action(s, reviewed, "approve"), "activate", date: s.Date);
        Assert.Equal(s.Date, active.EffectiveDate);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""UPDATE planning."BudgetAllocations" SET "Amount"=200 WHERE "BudgetVersionId"=@id""", connection);
        command.Parameters.AddWithValue("id", active.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task BudgetTransitionRouteBindsRejectAndPreservesValidationAndAuthorization()
    {
        using var s = await Arrange();
        var budget = await Master(s, BudgetKind.Operating);
        var draft = await Draft(s, budget, s.Chart["5100"], 1200);
        var submitted = await Action(s, draft, "submit", s.Maker);
        var path = $"{s.Budgets}/versions/{submitted.Id}";
        var request = new BudgetAction(submitted.Version, "Reviewed budget");

        using var invalid = await s.Owner.Client.PostAsJsonAsync($"{path}/unknown-transition", request);
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
        using var unauthorized = await s.Maker.Client.PostAsJsonAsync($"{path}/reject", request);
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);

        var rejected = await Action(s, submitted, "reject");
        Assert.Equal(BudgetState.Rejected, rejected.State);
        Assert.NotEqual(submitted.Version, rejected.Version);
    }

    [Fact]
    public async Task ParallelSpendingChecksAllowOnlyOnePostingAndReversalRestoresAuthority()
    {
        using var s = await Arrange();
        var account = s.Chart["5100"];
        var (budget, _) = await Publish(s, BudgetKind.Operating, account, 100);
        await Hard(s, BudgetKind.Operating, account);
        var drafts = new[] { await Journal(s, s.Date, account, s.Chart["1100"], 70), await Journal(s, s.Date, account, s.Chart["1100"], 70) };
        async Task<(JournalResponse Draft, HttpStatusCode Status)> Attempt(JournalResponse draft)
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Core}/journals/{draft.Id}/post", new PostJournalRequest(
                $"post-{draft.Id:N}", draft.Version, true, "Historical test"));
            return (draft, response.StatusCode);
        }
        var results = await Task.WhenAll(drafts.Select(Attempt));
        Assert.Single(results, x => x.Status == HttpStatusCode.Conflict);
        var posted = Assert.Single(results, x => x.Status == HttpStatusCode.OK).Draft;
        var report = await Report(s, budget.Id, account, s.Date);
        Assert.Equal(70, report.YearToDateActual);
        Assert.Equal(30, Assert.Single(report.Accounts, x => x.AccountId == account).Available);
        await Send<PostingReceipt>(s.Owner.Client, $"{s.Core}/journals/{posted.Id}/reverse", new ReverseJournalRequest(s.Date, "Reversal", "reverse", true));
        Assert.Equal(0, (await Report(s, budget.Id, account, s.Date)).YearToDateActual);
    }

    [Fact]
    public async Task OverrideIsOwnerOnlyBoundToIntentAndConsumedOnlyAtPosting()
    {
        using var s = await Arrange();
        var account = s.Chart["5100"];
        var (budget, _) = await Publish(s, BudgetKind.Operating, account, 100);
        await Hard(s, BudgetKind.Operating, account);
        var draft = await Journal(s, s.Date, account, s.Chart["1100"], 120);
        var input = new OverrideRequest(account, $"journal:{draft.SourceDocumentId:N}:{TradeRules.Hash("override")}", 20,
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2), "Contractual exception");
        using var denied = await s.Maker.Client.PostAsJsonAsync(s.Budgets + "/overrides", input);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var grant = await Send<BudgetOverride>(s.Owner.Client, s.Budgets + "/overrides", input);
        await Send<JournalValidationResponse>(s.Owner.Client, $"{s.Core}/journals/{draft.Id}/validate",
            new PostJournalRequest("override", draft.Version, true, "Historical test"));
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.Null((await db.BudgetOverrides.SingleAsync(x => x.Id == grant.Id)).UsedAt);
        }
        await Post(s, draft, "override");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.NotNull((await db.BudgetOverrides.SingleAsync(x => x.Id == grant.Id)).UsedAt);
        }
        Assert.Equal(-20, Assert.Single((await Report(s, budget.Id, account, s.Date)).Accounts, x => x.AccountId == account).Available);
    }

    [Fact]
    public async Task TransferPreservesOriginalAllocationAndRejectsAStaleCompetingRevisionWithoutPosting()
    {
        using var s = await Arrange();
        var account = s.Chart["5100"];
        var (budget, first) = await Publish(s, BudgetKind.Operating, account, 1200);
        var from = s.Year.Periods[0].Id;
        var to = s.Year.Periods[1].Id;
        var path = $"{s.Budgets}/{budget.Id}/transfers";
        var request = new BudgetTransferRequest("move", first.Id, account, from, account, to, 40, "Shift timing");
        var pending = await Send<BudgetVersion>(s.Maker.Client, path, request);
        var competing = await Approve(s, await Send<BudgetVersion>(s.Maker.Client, path, request with { Key = "other", Amount = 20 }));
        var active = await Action(s, await Approve(s, pending), "activate", date: s.Date);
        Assert.Equal(60, active.Allocations.Single(x => x.PeriodId == from).Amount);
        Assert.Equal(140, active.Allocations.Single(x => x.PeriodId == to).Amount);
        using var stale = await s.Owner.Client.PostAsJsonAsync($"{s.Budgets}/versions/{competing.Id}/activate",
            new BudgetAction(competing.Version, "Stale transfer", EffectiveDate: s.Date));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var historical = await Report(s, budget.Id, account, s.Date, $"&version={first.Id}");
        Assert.Equal(100, Assert.Single(historical.Periods, x => x.AccountId == account && x.Period == 1).Budget);
        Assert.Empty(historical.Ledger);
        var csv = await s.Owner.Client.GetStringAsync($"{s.Budgets}/{budget.Id}/export?cutoff={s.Date:yyyy-MM-dd}");
        Assert.Contains($"{account},1,60", csv);
    }

    [Fact]
    public async Task PriorYearActualsSeedTheMatchingFiscalOrdinal()
    {
        using var s = await Arrange();
        var previous = await Send<FiscalYearResponse>(s.Owner.Client, s.Core + "/fiscal-years", new FiscalYearRequest(s.Date.Year - 1, "Prior"), HttpStatusCode.Created);
        var account = s.Chart["5100"];
        var journal = await Journal(s, previous.Periods[1].StartDate, account, s.Chart["1100"], 87);
        await Post(s, journal, "prior-actual");
        var budget = await Master(s, BudgetKind.Operating);
        var seeded = await Send<BudgetVersion>(s.Maker.Client, $"{s.Budgets}/{budget.Id}/versions",
            new BudgetVersionRequest("seed", "Prior year", SeedFromPriorActuals: true));
        Assert.Equal(87, seeded.Allocations.Single(x => x.AccountId == account && x.PeriodId == s.Year.Periods[1].Id).Amount);
        Assert.Equal(87, seeded.Allocations.Sum(x => x.Amount));
    }

    [Fact]
    public async Task ForeignTenantCannotReadOrWriteBudgetDetails()
    {
        using var s = await Arrange();
        using var other = await Onboard();
        var budget = await Master(s, BudgetKind.Operating);
        using var read = await other.Client.GetAsync($"{s.Budgets}/{budget.Id}/versions");
        using var write = await other.Client.PostAsJsonAsync($"{s.Budgets}/{budget.Id}/versions",
            new BudgetVersionRequest("foreign", "Unauthorized", [new(s.Chart["5100"], 100, AllocationMethod.Equal)]));
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    [Fact]
    public async Task WarningPolicyRetainsEvidenceAndCloseReviewDoesNotBlockAccounting()
    {
        using var s = await Arrange();
        var account = s.Chart["5100"];
        await Publish(s, BudgetKind.Operating, account, 100);
        await Send<BudgetPolicy>(s.Owner.Client, s.Budgets + "/controls", new PolicyRequest(account, BudgetKind.Operating,
            ControlMode.Warning, s.Date, 80, "Review utilization"));
        var draft = await Journal(s, s.Date, account, s.Chart["1100"], 125);
        await Post(s, draft, "warning");
        var available = await s.Owner.Client.GetFromJsonAsync<BudgetAvailability>(
            $"{s.Budgets}/controls/{account}/availability?cutoff={s.Date:yyyy-MM-dd}");
        Assert.Equal(-25, available!.Available);
        var audit = await s.Owner.Client.GetFromJsonAsync<List<AuditResponse>>($"{s.Budgets}/audit/{draft.SourceDocumentId}");
        Assert.Contains(audit!, x => x.Action == "BudgetThresholdReached");
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var check = scope.ServiceProvider.GetRequiredService<BudgetReporting>();
        var period = await db.Periods.SingleAsync(x => x.Id == s.Year.Periods[0].Id);
        var evidence = await check.Check(s.Company.Id, period, default);
        Assert.Contains(evidence.Findings, x => x.Rule == "budget-variance" &&
            x.Severity == RAQM.Api.DTOs.FinancialClose.FindingSeverity.Review);
    }

    [Fact]
    public async Task ForecastRefreshRebuildsKnownActualInputsAndRetainsPriorSnapshot()
    {
        using var s = await Arrange();
        var year = await Send<FiscalYearResponse>(s.Owner.Client, s.Core + "/fiscal-years",
            new FiscalYearRequest(s.Date.Year - 1, "Prior forecast history"), HttpStatusCode.Created);
        var expense = s.Chart["5100"];
        foreach (var period in year.Periods.TakeLast(3))
            await Post(s, await Journal(s, period.StartDate, expense, s.Chart["1100"], 90), $"history-{period.Number}");
        var request = new ForecastRequest("history", "HISTORY", "Expense outlook", year.EndDate, 3,
            [new(expense, ForecastMethod.HistoricalMonthlyAverage, 10)]);
        var first = await Send<ForecastVersion>(s.Maker.Client, s.Forecasts, request);
        var initial = JsonSerializer.Deserialize<ForecastSnapshot>(first.SnapshotJson)!;
        Assert.All(initial.Rows, x => Assert.Equal(99, x.Amount));
        await Post(s, await Journal(s, year.Periods[^1].StartDate, expense, s.Chart["1100"], 30), "backdated-refresh");
        var refreshed = await Send<ForecastVersion>(s.Maker.Client, s.Forecasts, request with { Key = "refresh", BasisId = first.Id });
        Assert.Equal(2, refreshed.Number);
        Assert.All(JsonSerializer.Deserialize<ForecastSnapshot>(refreshed.SnapshotJson)!.Rows, x => Assert.Equal(110, x.Amount));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var retained = await db.ForecastVersions.SingleAsync(x => x.Id == first.Id);
        Assert.All(JsonSerializer.Deserialize<ForecastSnapshot>(retained.SnapshotJson)!.Rows, x => Assert.Equal(99, x.Amount));
    }
}
