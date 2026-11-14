using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Entities.InternalControls;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Services.Accounting;
using Xunit;

namespace RAQM.Tests;

public sealed partial class AccountingIntegrationTests
{
    [Fact]
    public async Task GovernedManualPostingRequiresDistinctReviewsAndCommitsOneDecisionAndJournal()
    {
        var s = await Arrange();
        using var owner = s.Client;
        var a = await GovernanceOwner(s);
        var b = await GovernanceOwner(s);
        using var reviewer = a.Client;
        using var other = b.Client;
        var root = GovernanceRoot(s);
        var policy = await Activate(s, reviewer, FinancialDuty.ManualJournal, 2);
        var draft = await Draft(s, Sale(s, 12000));
        var url = $"{s.Root}/journals/{draft.Id}/post";
        var post = new PostJournalRequest("controlled-journal", draft.Version);
        await Request(reviewer, HttpMethod.Post, url, post, HttpStatusCode.Conflict);
        var approval = await Gov<GovernanceAuthorization>(owner, root + "/authorizations",
            new AuthorizationInput(FinancialDuty.ManualJournal, draft.Id, null, "Ledger review"));
        Assert.Equal(12000m, approval.Amount);
        Assert.Equal(policy.Id, approval.PolicyId);
        var input = new ReviewInput(ReviewDecision.Approve, null, "Independent check");
        await Request(owner, HttpMethod.Post, $"{root}/authorizations/{approval.Id}/review", input, HttpStatusCode.Conflict);
        var approvals = await Task.WhenAll(new[] { reviewer, other }.Select(x =>
            Gov<GovernanceReview>(x, $"{root}/authorizations/{approval.Id}/review", input)));
        Assert.Equal(new[] { 1, 2 }, approvals.Select(x => x.Level).Order());
        var posted = await Gov<PostingReceipt>(reviewer, url, post);
        Assert.Equal(posted.JournalId, (await Gov<PostingReceipt>(reviewer, url, post)).JournalId);
        Assert.Equal(1, await PostedCount(s.Company.Id));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var retained = await db.Set<GovernanceAuthorization>().SingleAsync(x => x.Id == approval.Id);
        Assert.Equal(AuthorizationState.Used, retained.State);
        var decision = await db.Set<GovernanceEvent>().SingleAsync(x => x.CompanyId == s.Company.Id &&
            x.JournalId == posted.JournalId && x.Type == "GovernanceDecision");
        Assert.Equal(a.Id, decision.ActorId);
        Assert.Equal(approval.Id, decision.AuthorizationId);
        var verified = await owner.GetFromJsonAsync<JsonElement>(root + "/events/verify");
        Assert.True(verified.GetProperty("integrity").GetProperty("intact").GetBoolean());
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand("""UPDATE governance."Events" SET "Reason"='forged' WHERE "Id"=@id""", conn);
        command.Parameters.AddWithValue("id", decision.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task GovernanceCommitGuardRejectsManualPostingWithoutMatchingDecision()
    {
        var s = await Arrange();
        using var owner = s.Client;
        var reviewer = await GovernanceOwner(s);
        using var reviewerClient = reviewer.Client;
        await Activate(s, reviewerClient, FinancialDuty.ManualJournal, 1);
        var sale = Sale(s);
        var draft = await Draft(s, sale);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var access = scope.ServiceProvider.GetRequiredService<FinancialAccess>();
        var validator = scope.ServiceProvider.GetRequiredService<PostingValidator>();
        var engine = new PostingEngine(db, access, validator, TimeProvider.System);
        var request = new PostingRequest(new(s.Company.TenantId, s.Org, s.Company.Id), "ManualJournal", draft.Id,
            "missing-decision", Today, s.Company.BaseCurrency, sale.Lines, null, Today, sale.Description, sale.Reference,
            DraftId: draft.Id, ExpectedVersion: draft.Version);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => engine.PostAsync(s.User, request, default));

        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal("Active governance requires a matching decision in the financial transaction", failure.MessageText);
        Assert.Equal(JournalState.Draft, await db.Journals.Where(x => x.Id == draft.Id).Select(x => x.State).SingleAsync());
        Assert.Equal(0, await PostedCount(s.Company.Id));
    }

    [Fact]
    public async Task ClosedPeriodRollsBackAuthorizationConsumptionAndDecision()
    {
        var s = await Arrange(year: Today.Year - 1);
        using var owner = s.Client;
        var a = await GovernanceOwner(s);
        using var reviewer = a.Client;
        var root = GovernanceRoot(s);
        await Activate(s, reviewer, FinancialDuty.ManualJournal, 1);
        var period = s.Year.Periods[0];
        var draft = await Draft(s, Sale(s, 100, period.StartDate));
        var approval = await Gov<GovernanceAuthorization>(owner, root + "/authorizations",
            new AuthorizationInput(FinancialDuty.ManualJournal, draft.Id, null, "Ledger review"));
        await Gov<GovernanceReview>(reviewer, $"{root}/authorizations/{approval.Id}/review", new ReviewInput(ReviewDecision.Approve, null, "Reviewed"));
        await Request(owner, HttpMethod.Put, $"{s.Root}/periods/{period.Id}/state",
            new PeriodStateRequest(PeriodState.Closed, "Close selected period", period.Version), HttpStatusCode.OK);
        await Request(reviewer, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/post",
            new PostJournalRequest("closed-governed", draft.Version, true, "Backdated but disallowed"), HttpStatusCode.Conflict);
        Assert.Equal(0, await PostedCount(s.Company.Id));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.Equal(AuthorizationState.Open, (await db.Set<GovernanceAuthorization>().SingleAsync(x => x.Id == approval.Id)).State);
        Assert.False(await db.Set<GovernanceEvent>().AnyAsync(x => x.AuthorizationId == approval.Id && x.Type == "GovernanceDecision"));
    }

    [Fact]
    public async Task EditedDraftRequiresNewApprovalAndRetainsTheEarlierPolicyEvidence()
    {
        var s = await Arrange();
        using var owner = s.Client;
        var a = await GovernanceOwner(s);
        using var reviewer = a.Client;
        var root = GovernanceRoot(s);
        var policy = await Activate(s, reviewer, FinancialDuty.ManualJournal, 1);
        var draft = await Draft(s);
        var auth = await Gov<GovernanceAuthorization>(owner, root + "/authorizations",
            new AuthorizationInput(FinancialDuty.ManualJournal, draft.Id, null, "Review version 1"));
        await Gov<GovernanceReview>(reviewer, $"{root}/authorizations/{auth.Id}/review", new ReviewInput(ReviewDecision.Approve, null, "Reviewed"));
        using var edited = await Request(owner, HttpMethod.Put, $"{s.Root}/journals/{draft.Id}",
            new UpdateJournalRequest(Sale(s, 101), draft.Version), HttpStatusCode.OK);
        var revision = (await edited.Content.ReadFromJsonAsync<JournalResponse>())!;
        await Request(reviewer, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/post",
            new PostJournalRequest("changed", revision.Version), HttpStatusCode.Conflict);
        var next = await Gov<GovernanceAuthorization>(owner, root + "/authorizations",
            new AuthorizationInput(FinancialDuty.ManualJournal, draft.Id, null, "Review new version"));
        Assert.Equal(101m, next.Amount);
        Assert.NotEqual(auth.IntentHash, next.IntentHash);
        Assert.Equal(policy.Id, next.PolicyId);
        using var other = (await Arrange()).Client;
        using var denied = await other.GetAsync(root + "/overview");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task CertificationRevokesTheEffectiveGrantEvenForAnOrganizationOwner()
    {
        var s = await Arrange();
        using var owner = s.Client;
        var a = await GovernanceOwner(s);
        var b = await GovernanceOwner(s);
        using var reviewer = a.Client;
        using var executor = b.Client;
        var root = GovernanceRoot(s);
        await Activate(s, reviewer, FinancialDuty.ManualJournal, 0, explicitGrants: true);
        var now = DateTimeOffset.UtcNow;
        var grant = await Gov<GovernanceGrant>(owner, root + "/grants", new GrantInput(b.Id, FinancialDuty.ManualJournal,
            GovernancePermission.Execute, now.AddMinutes(-1), now.AddHours(1), false, "Scoped permission"));
        await Gov<GovernanceGrant>(reviewer, $"{root}/grants/{grant.Id}/approve", new { });
        var first = await Draft(s);
        await Gov<PostingReceipt>(executor, $"{s.Root}/journals/{first.Id}/post", new PostJournalRequest("before-revoke", first.Version));

        var work = await Gov<GovernanceWork>(owner, root + "/work", new WorkInput(GovernanceWorkKind.AccessReview,
            "Review executor", s.User, Today.AddDays(1), "Access", "Financial administration", "Review privilege", "Revoke",
            GrantId: grant.Id));
        work = await Gov<GovernanceWork>(reviewer, $"{root}/work/{work.Id}/approve", new WorkAction(work.Version, "Review accepted"));
        work = await Gov<GovernanceWork>(owner, $"{root}/work/{work.Id}/start", new WorkAction(work.Version, "Review started"));
        await Gov<EvidenceReference>(owner, root + "/evidence", new EvidenceInput(work.Id, null, null, "External",
            "Permission review", "Retained digest of independent review notes", new string('A', 64), false, Today.AddYears(1)));
        work = await Gov<GovernanceWork>(owner, $"{root}/work/{work.Id}/submit", new WorkAction(work.Version, "Recommend revocation"));
        await Request(executor, HttpMethod.Post, $"{root}/work/{work.Id}/close",
            new WorkAction(work.Version, "Self-certification attempt", true), HttpStatusCode.Conflict);
        await Gov<GovernanceWork>(reviewer, $"{root}/work/{work.Id}/close", new WorkAction(work.Version, "Remove assignment", true));
        var second = await Draft(s);
        await Request(executor, HttpMethod.Post, $"{s.Root}/journals/{second.Id}/post",
            new PostJournalRequest("after-revoke", second.Version), HttpStatusCode.Conflict);
        Assert.Equal(1, await PostedCount(s.Company.Id));
    }

    [Fact]
    public async Task EnabledCloseControlRequiresCurrentReviewedLedgerPopulation()
    {
        var s = await Arrange(year: Today.Year - 1);
        using var owner = s.Client;
        var a = await GovernanceOwner(s);
        using var reviewer = a.Client;
        var root = GovernanceRoot(s);
        var period = s.Year.Periods[0];
        await Post(s, await Draft(s, Sale(s, 100, period.StartDate)), backdated: true);
        var control = await Gov<InternalControl>(owner, root + "/controls", new InternalControlInput("GL-BAL", "Posted journals balance",
            "Close", "Monthly", "Posted lines", false, true, ControlCheck.JournalBalance, null, s.User, period.StartDate));
        await Gov<InternalControl>(reviewer, $"{root}/controls/{control.Id}/approve", new { });
        var state = new PeriodStateRequest(PeriodState.Closed, "Reviewed cutoff", period.Version);
        await Request(owner, HttpMethod.Put, $"{s.Root}/periods/{period.Id}/state", state, HttpStatusCode.Conflict);
        await Test();
        await Post(s, await Draft(s, Sale(s, 25, period.StartDate)), backdated: true);
        await Request(owner, HttpMethod.Put, $"{s.Root}/periods/{period.Id}/state", state, HttpStatusCode.Conflict);
        await Test();
        await Request(owner, HttpMethod.Put, $"{s.Root}/periods/{period.Id}/state", state, HttpStatusCode.OK);

        async Task Test()
        {
            var result = await Gov<ControlEvaluation>(owner, $"{root}/controls/{control.Id}/evaluate",
                new EvaluateControlInput(period.StartDate, period.EndDate, null, "Full posted population", null));
            Assert.Equal(ControlOutcome.Pass, result.Outcome);
            await Gov<ControlEvaluation>(reviewer, $"{root}/evaluations/{result.Id}/review",
                new ReviewControlInput(ControlOutcome.Pass, "Independently verified"));
        }
    }

    private async Task<(HttpClient Client, Guid Id)> GovernanceOwner(Setup s)
    {
        var client = fixture.Factory.CreateClient();
        var token = await Gov<TokenResponse>(client, "/api/v1/auth/onboard", new OnboardRequest(
            $"{Guid.NewGuid():N}@governance.example", "Governance_Test-Password1!", "Reviewer org", ApiFactory.OnboardingKey));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        var user = me.GetProperty("userId").GetGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        db.OrganizationMembers.Add(new() { TenantId = s.Company.TenantId, OrganizationId = s.Org, UserId = user, Role = OrganizationRole.Owner });
        await db.SaveChangesAsync();
        return (client, user);
    }

    private static async Task<GovernancePolicy> Activate(Setup s, HttpClient reviewer, FinancialDuty duty, int reviewers, bool explicitGrants = false)
    {
        var root = GovernanceRoot(s);
        var row = await Gov<GovernancePolicy>(s.Client, root + "/policies", new PolicyInput(duty, duty.ToString(), "Enforced test",
            s.User, PolicyMode.Enforce, DateTimeOffset.UtcNow.AddSeconds(-2),
            new PolicyRules([new(CurrencyPolicy.Maximum, reviewers)], true, explicitGrants, false, 0, 24, false)));
        return await Gov<GovernancePolicy>(reviewer, $"{root}/policies/{row.Id}/approve", new { });
    }

    private static string GovernanceRoot(Setup s) => s.Root.Replace("/accounting", "/governance", StringComparison.Ordinal);

    private static async Task<T> Gov<T>(HttpClient client, string path, object request)
    {
        using var response = await Request(client, HttpMethod.Post, path, request, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
