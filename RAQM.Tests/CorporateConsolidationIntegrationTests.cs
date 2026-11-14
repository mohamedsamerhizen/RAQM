using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.DTOs.Consolidation;
using RAQM.Api.DTOs.CorporateGroups;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.Consolidation;
using RAQM.Api.Entities.CorporateGroups;
using RAQM.Api.Entities.Intercompany;
using RAQM.Api.Entities.Organizations;
using Xunit;

namespace RAQM.Tests;

[Collection("PostgreSQL"), Trait("Category", "Integration")]
public sealed class CorporateConsolidationIntegrationTests(PostgreSqlFixture fixture)
{
    private sealed record User(HttpClient Client, Guid Id, Guid Org) : IDisposable { public void Dispose() => Client.Dispose(); }
    private sealed record CompanySetup(Company Entity, FiscalYearResponse Year, Dictionary<string, Guid> Accounts)
    {
        public string Core => $"/api/v1/organizations/{Entity.OrganizationId}/companies/{Entity.Id}/accounting";
        public Guid Id => Entity.Id;
    }

    private sealed record Setup(User Owner, User Reviewer, CompanySetup Parent, CompanySetup Child, CorporateGroup Group) : IDisposable
    {
        public string Root => $"/api/v1/organizations/{Owner.Org}/corporate-groups/{Group.Id}";
        public string Pairs => Root + "/intercompany/pairs";
        public string Runs => Root + "/consolidation/runs";
        public DateOnly Date => Parent.Year.StartDate;
        public DateOnly Cutoff => Parent.Year.Periods[0].EndDate;
        public void Dispose() { Owner.Dispose(); Reviewer.Dispose(); }
    }

    [Fact]
    public async Task LoanInterestAndRepaymentRetainReciprocalExposureWithoutDuplicateIncome()
    {
        using var s = await Arrange();
        var left = await GroupBank(s, s.Parent, "LEFT");
        var right = await GroupBank(s, s.Child, "RIGHT");
        var partners = s.Root + "/intercompany/partners";
        var policy = await Send<IntercompanyPolicy>(s.Owner.Client, partners, new PartnerRequest(s.Parent.Id, s.Child.Id,
            s.Parent.Accounts["1200"], s.Child.Accounts["2100"], s.Parent.Accounts["4100"], s.Child.Accounts["5100"],
            left.LedgerAccountId, right.LedgerAccountId, s.Date, "Reviewed group loan"));
        await Send<IntercompanyPolicy>(s.Reviewer.Client, $"{partners}/{policy.Id}/approve", new { });
        var principal = await PostPair(new PairRequest(policy.Id, PairKind.Principal, null, "LOAN", s.Date, 50000, "Bank transfer evidence"));
        var interest = await PostPair(new PairRequest(policy.Id, PairKind.Interest, principal.Id, "INT", s.Date.AddDays(10), 68.49m,
            "Reviewed actual/365", InterestFrom: s.Date.AddDays(1), InterestAnnualPercent: 5));
        var repayment = await PostPair(new PairRequest(policy.Id, PairKind.Settlement, principal.Id, "REPAY", s.Date.AddDays(11),
            20000, "Verified repayment reference"));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var journals = new[] { principal.CreditorJournalId, interest.CreditorJournalId, repayment.CreditorJournalId };
        var lines = await db.JournalLines.Where(x => x.CompanyId == s.Parent.Id && journals.Contains(x.JournalId)).ToArrayAsync();
        Assert.Equal(68.49m, lines.Where(x => x.AccountId == s.Parent.Accounts["4100"]).Sum(x => x.Credit));
        Assert.Equal(-30000m, lines.Where(x => x.AccountId == left.LedgerAccountId).Sum(x => x.Debit - x.Credit));
        var evidence = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{partners}/{policy.Id}/reconciliation?cutoff={s.Cutoff:yyyy-MM-dd}");
        var balance = evidence.GetProperty("balance");
        Assert.Equal(30068.49m, balance.GetProperty("dueFrom").GetDecimal());
        Assert.Equal(30068.49m, balance.GetProperty("dueTo").GetDecimal());
        Assert.True(balance.GetProperty("agrees").GetBoolean());

        async Task<IntercompanyPair> PostPair(PairRequest request)
        {
            var draft = await Send<IntercompanyPair>(s.Owner.Client, s.Pairs, request);
            await Send<IntercompanyPair>(s.Reviewer.Client, $"{s.Pairs}/{draft.Id}/approve", new { });
            return await Send<IntercompanyPair>(s.Owner.Client, $"{s.Pairs}/{draft.Id}/post", new { });
        }
    }

    private static async Task<TreasuryAccountResponse> GroupBank(Setup s, CompanySetup company, string code)
    {
        var root = company.Core.Replace("/accounting", "/banking", StringComparison.Ordinal);
        var profile = await Send<BankProfile>(s.Owner.Client, root + "/banks", new BankProfileRequest(code, code, "ABCDUS33"));
        return await Send<TreasuryAccountResponse>(s.Owner.Client, root + "/accounts", new TreasuryAccountRequest(
            TreasuryAccountKind.Bank, code, code, company.Accounts["1100"], profile.Id, "123456789"));
    }

    [Fact]
    public async Task ReciprocalPostingIsExactlyOnceAndEachCompanyKeepsAnIndependentJournal()
    {
        using var s = await Arrange();
        var policy = await Policy(s);
        var pair = await Draft(s, policy);
        await Send<IntercompanyPair>(s.Reviewer.Client, $"{s.Pairs}/{pair.Id}/approve", new { });
        var posts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            Send<IntercompanyPair>(s.Owner.Client, $"{s.Pairs}/{pair.Id}/post", new { })));
        Assert.Equal(posts[0].CreditorJournalId, posts[1].CreditorJournalId);
        Assert.Equal(posts[0].DebtorJournalId, posts[1].DebtorJournalId);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var journals = await db.Journals.Include(x => x.Lines).Where(x => x.SourceDocumentId == pair.Id).ToArrayAsync();
        Assert.Equal(2, journals.Length);
        Assert.All(journals, j => Assert.Equal(0m, j.Lines.Sum(x => x.Debit - x.Credit)));
        Assert.Equal(1000m, journals.Single(x => x.CompanyId == s.Parent.Id).Lines.Single(x => x.AccountId == s.Parent.Accounts["1200"]).Debit);
        Assert.Equal(1000m, journals.Single(x => x.CompanyId == s.Child.Id).Lines.Single(x => x.AccountId == s.Child.Accounts["2100"]).Credit);
        var rec = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Root}/intercompany/partners/{policy.Id}/reconciliation?cutoff={s.Cutoff:yyyy-MM-dd}");
        Assert.Equal(1000m, rec.GetProperty("balance").GetProperty("expected").GetDecimal());
        Assert.True(rec.GetProperty("balance").GetProperty("agrees").GetBoolean());
        var correction = await Send<IntercompanyPair>(s.Owner.Client, s.Pairs, new PairRequest(policy.Id, PairKind.Correction,
            pair.Id, "S-0001-CORRECTION", s.Date.AddDays(1), 1000m, "Exact service correction"));
        await Send<IntercompanyPair>(s.Reviewer.Client, $"{s.Pairs}/{correction.Id}/approve", new { });
        correction = await Send<IntercompanyPair>(s.Owner.Client, $"{s.Pairs}/{correction.Id}/post", new { });
        var reversalIds = await db.Journals.AsNoTracking().Where(x => x.Id == correction.CreditorJournalId || x.Id == correction.DebtorJournalId)
            .Select(x => x.ReversesJournalId).ToArrayAsync();
        Assert.Equal(2, reversalIds.Length);
        Assert.Contains(posts[0].CreditorJournalId, reversalIds);
        Assert.Contains(posts[0].DebtorJournalId, reversalIds);
        var corrected = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Root}/intercompany/partners/{policy.Id}/reconciliation?cutoff={s.Cutoff:yyyy-MM-dd}");
        Assert.Equal(0m, corrected.GetProperty("balance").GetProperty("expected").GetDecimal());
        Assert.True(corrected.GetProperty("balance").GetProperty("agrees").GetBoolean());
    }

    [Fact]
    public async Task AClosedSecondCompanyRollsBackTheFirstJournalAndStagedPair()
    {
        using var s = await Arrange();
        var policy = await Policy(s);
        var pair = await Draft(s, policy);
        await Send<IntercompanyPair>(s.Reviewer.Client, $"{s.Pairs}/{pair.Id}/approve", new { });
        await Close(s, new[] { s.Parent, s.Child }.OrderBy(x => x.Id).Last());
        using var failure = await s.Owner.Client.PostAsJsonAsync($"{s.Pairs}/{pair.Id}/post", new { });
        Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.False(await db.Journals.AnyAsync(x => x.SourceDocumentId == pair.Id));
        var row = await db.Set<IntercompanyPair>().SingleAsync(x => x.Id == pair.Id);
        Assert.Equal(PairState.Approved, row.State);
        Assert.Null(row.PlanJson);
        Assert.Null(row.CreditorJournalId);
        Assert.Null(row.DebtorJournalId);
    }

    [Fact]
    public async Task PublishedConsolidationEliminatesOnlyInternalActivityAndFreezesSourceEvidence()
    {
        using var s = await Arrange();
        var policy = await Policy(s);
        var pair = await Draft(s, policy);
        await Send<IntercompanyPair>(s.Reviewer.Client, $"{s.Pairs}/{pair.Id}/approve", new { });
        await Send<IntercompanyPair>(s.Owner.Client, $"{s.Pairs}/{pair.Id}/post", new { });
        await Send<ReciprocalReview>(s.Reviewer.Client, $"{s.Root}/intercompany/partners/{policy.Id}/reconciliation?cutoff={s.Cutoff:yyyy-MM-dd}",
            new GroupEvidenceRequest("Matched reciprocal invoice and bill."));
        await Journal(s, s.Parent, "1100", "3100", 5000);
        await Journal(s, s.Parent, "1300", "1100", 1000);
        await Journal(s, s.Child, "1100", "3100", 1000);
        await Journal(s, s.Parent, "1100", "4100", 200);
        var parentClosed = await Close(s, s.Parent);
        await Close(s, s.Child);

        var input = new GroupRunRequest(s.Date, s.Cutoff, null, null, [],
            [new OwnershipBasis(s.Child.Id, s.Parent.Accounts["1300"], s.Child.Accounts["3100"], "Initial capitalization")],
            "Both companies use aligned historical cost and accrual policies. Reviewed all internal flows.");
        using var missing = await s.Owner.Client.PostAsJsonAsync(s.Runs, input);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Contains("mapping", await missing.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        foreach (var (code, classification) in new[] { ("1100", AccountClass.Asset), ("1200", AccountClass.Asset), ("1300", AccountClass.Asset),
            ("2100", AccountClass.Liability), ("3100", AccountClass.Equity), ("4100", AccountClass.Revenue), ("5100", AccountClass.Expense) })
        {
            var account = await Send<GroupAccount>(s.Owner.Client, s.Root + "/accounts", new GroupAccountRequest(code, code, classification, code == "1100", null));
            foreach (var company in new[] { s.Parent, s.Child }.Where(x => x.Accounts.ContainsKey(code)))
            {
                var map = await Send<GroupMapping>(s.Owner.Client, s.Root + "/mappings", new MappingRequest(company.Id, company.Accounts[code], account.Id,
                    s.Date, "Verified chart crosswalk"));
                await Send<GroupMapping>(s.Reviewer.Client, $"{s.Root}/mappings/{map.Id}/approve", new { });
            }
        }
        var run = await Send<ConsolidationRun>(s.Owner.Client, s.Runs, input);
        var prepared = JsonSerializer.Deserialize<ConsolidationSnapshot>(run.SnapshotJson)!;
        Assert.Equal(4, prepared.Adjustments.Count(x => x.Layer == "Elimination" && x.SourceId == pair.Id));
        await Send<ConsolidationRun>(s.Reviewer.Client, $"{s.Runs}/{run.Id}/approve", new { });
        await Send<ConsolidationRun>(s.Reviewer.Client, $"{s.Runs}/{run.Id}/publish", new { });
        var report = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Runs}/{run.Id}/report");
        Assert.Equal(5200m, report.GetProperty("balanceSheet").GetProperty("assets").GetDecimal());
        Assert.Equal(5000m, report.GetProperty("balanceSheet").GetProperty("equity").GetDecimal());
        Assert.Equal(0m, report.GetProperty("balanceSheet").GetProperty("difference").GetDecimal());
        Assert.Equal(200m, report.GetProperty("incomeStatement").GetProperty("revenue").GetDecimal());
        Assert.Equal(0m, report.GetProperty("incomeStatement").GetProperty("expense").GetDecimal());
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand("""UPDATE groups."ConsolidationRuns" SET "SnapshotJson"='{}' WHERE "Id"=@id""", conn);
        command.Parameters.AddWithValue("id", run.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        var replacement = await Send<ConsolidationRun>(s.Owner.Client, s.Runs, input with { SupersedesId = run.Id });
        await Send<ConsolidationRun>(s.Reviewer.Client, $"{s.Runs}/{replacement.Id}/approve", new { });
        var reopened = await Send<PeriodResponse>(s.Owner.Client, $"{s.Parent.Core}/periods/{parentClosed.Id}/state",
            new PeriodStateRequest(PeriodState.Open, "Controlled backdated correction", parentClosed.Version), HttpStatusCode.OK, HttpMethod.Put);
        await Journal(s, s.Parent, "1100", "4100", 50);
        await Send<PeriodResponse>(s.Owner.Client, $"{s.Parent.Core}/periods/{parentClosed.Id}/state",
            new PeriodStateRequest(PeriodState.Closed, "Corrected cutoff", reopened.Version), HttpStatusCode.OK, HttpMethod.Put);
        using var stale = await s.Reviewer.Client.PostAsJsonAsync($"{s.Runs}/{replacement.Id}/publish", new { });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var historical = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Runs}/{run.Id}/report");
        Assert.Equal(200m, historical.GetProperty("incomeStatement").GetProperty("revenue").GetDecimal());
    }

    [Fact]
    public async Task GroupReportGrantsDoNotPermitOperationalAccessOrCrossTenantLookup()
    {
        using var s = await Arrange();
        using var reader = await Onboard();
        using var outsider = await Onboard();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.OrganizationMembers.Add(new() { TenantId = s.Parent.Entity.TenantId, OrganizationId = s.Owner.Org, UserId = reader.Id,
                Role = OrganizationRole.Member });
            await db.SaveChangesAsync();
        }
        using var before = await reader.Client.GetAsync(s.Runs);
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        await Send<CorporateGroup>(s.Owner.Client, s.Root + "/readers", new GroupReaderRequest(reader.Id, true, "Group reporting mandate"));
        using var after = await reader.Client.GetAsync(s.Runs);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        using var accounts = await reader.Client.GetAsync(s.Child.Core + "/accounts");
        Assert.Equal(HttpStatusCode.NotFound, accounts.StatusCode);
        using var foreign = await outsider.Client.GetAsync(s.Runs);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private async Task<Setup> Arrange()
    {
        var owner = await Onboard();
        var reviewer = await Onboard();
        var parent = await CreateCompany(owner, "Parent");
        var child = await CreateCompany(owner, "Child");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.OrganizationMembers.Add(new() { TenantId = parent.Entity.TenantId, OrganizationId = owner.Org, UserId = reviewer.Id,
                Role = OrganizationRole.Owner });
            await db.SaveChangesAsync();
        }
        var date = parent.Year.StartDate;
        var root = $"/api/v1/organizations/{owner.Org}/corporate-groups";
        var group = await Send<CorporateGroup>(owner.Client, root, new GroupRequest(parent.Id, "ROOT", "Group", "Test group", "USD", date));
        await Send<GroupOwnership>(owner.Client, $"{root}/{group.Id}/ownership",
            new OwnershipRequest(parent.Id, child.Id, InvestmentKind.Controlled, 100m, 100m, date, "Subscription agreement"));
        parent.Accounts["1200"] = (await Send<AccountResponse>(owner.Client, parent.Core + "/accounts",
            new AccountRequest("1200", "Intercompany due from", null, AccountClass.Asset, true), HttpStatusCode.Created)).Id;
        parent.Accounts["1300"] = (await Send<AccountResponse>(owner.Client, parent.Core + "/accounts",
            new AccountRequest("1300", "Investment in subsidiary", null, AccountClass.Asset, true), HttpStatusCode.Created)).Id;
        return new(owner, reviewer, parent, child, group);
    }

    private async Task<User> Onboard()
    {
        var client = fixture.Factory.CreateClient();
        var token = await Send<TokenResponse>(client, "/api/v1/auth/onboard",
            new OnboardRequest($"{Guid.NewGuid():N}@group.example", "Consolidation_Test-Password1!", "Group Org", ApiFactory.OnboardingKey));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        return new(client, me.GetProperty("userId").GetGuid(), me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid());
    }

    private static async Task<CompanySetup> CreateCompany(User user, string name)
    {
        var root = $"/api/v1/organizations/{user.Org}/companies";
        var company = await Send<Company>(user.Client, root, new CompanyRequest(name, Guid.NewGuid().ToString("N"), "USD", 1, 1), HttpStatusCode.Created);
        var core = $"{root}/{company.Id}/accounting";
        var accounts = (await Send<List<AccountResponse>>(user.Client, core + "/accounts/initialize", new InitializeAccountsRequest()))
            .ToDictionary(x => x.Code, x => x.Id);
        var year = await Send<FiscalYearResponse>(user.Client, core + "/fiscal-years", new FiscalYearRequest(DateTime.UtcNow.Year - 1, "Group year"),
            HttpStatusCode.Created);
        return new(company, year, accounts);
    }

    private static async Task<IntercompanyPolicy> Policy(Setup s)
    {
        var path = s.Root + "/intercompany/partners";
        var p = await Send<IntercompanyPolicy>(s.Owner.Client, path, new PartnerRequest(s.Parent.Id, s.Child.Id,
            s.Parent.Accounts["1200"], s.Child.Accounts["2100"], s.Parent.Accounts["4100"], s.Child.Accounts["5100"], null, null, s.Date, "Agreement"));
        return await Send<IntercompanyPolicy>(s.Reviewer.Client, $"{path}/{p.Id}/approve", new { });
    }

    private static Task<IntercompanyPair> Draft(Setup s, IntercompanyPolicy p) => Send<IntercompanyPair>(s.Owner.Client,
        s.Pairs, new PairRequest(p.Id, PairKind.Service, null, "S-0001", s.Date, 1000m, "Documented no-tax service", true));

    private static async Task Journal(Setup s, CompanySetup c, string debit, string credit, decimal amount)
    {
        var draft = await Send<JournalResponse>(s.Owner.Client, c.Core + "/journals", new JournalRequest(s.Date, s.Date,
            "Group example basis", null, [new(c.Accounts[debit], amount, 0, null), new(c.Accounts[credit], 0, amount, null)]), HttpStatusCode.Created);
        await Send<PostingReceipt>(s.Owner.Client, $"{c.Core}/journals/{draft.Id}/post",
            new PostJournalRequest(Guid.NewGuid().ToString("N"), draft.Version, true, "Historical company evidence"));
    }

    private static async Task<PeriodResponse> Close(Setup s, CompanySetup c)
    {
        var period = c.Year.Periods[0];
        return await Send<PeriodResponse>(s.Owner.Client, $"{c.Core}/periods/{period.Id}/state",
            new PeriodStateRequest(PeriodState.Closed, "Group aligned cutoff", period.Version), HttpStatusCode.OK, HttpMethod.Put);
    }

    private static async Task<T> Send<T>(HttpClient client, string path, object payload, HttpStatusCode status = HttpStatusCode.OK,
        HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == status, $"{path} expected {status}, received {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
