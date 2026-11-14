using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.Accounting;
using Xunit;

namespace RAQM.Tests;

public sealed partial class AccountingIntegrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task R01_002_ReversalCannotPrecedeItsOriginal(int validDayOffset)
    {
        var s = await Arrange(year: Today.Year - 1);
        using var client = s.Client;
        var earlier = s.Year.Periods[0].EndDate;
        var date = earlier.AddDays(1);
        var draft = await Draft(s, Sale(s, 100m, date));
        await Post(s, draft, backdated: true);
        using var response = await client.PostAsJsonAsync($"{s.Root}/journals/{draft.Id}/reverse",
            new ReverseJournalRequest(earlier, "Test chronology", "before-original", true));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var earlierLines = await db.JournalLines.Where(l => l.CompanyId == s.Company.Id &&
            db.Journals.Any(j => j.Id == l.JournalId && j.CompanyId == l.CompanyId && j.State == JournalState.Posted &&
                j.PostingDate <= earlier)).ToListAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict && earlierLines.Count == 0,
            $"D-1 HTTP {(int)response.StatusCode}, cutoff debits {earlierLines.Sum(l => l.Debit)}, cutoff credits {earlierLines.Sum(l => l.Credit)}, original {draft.Id} dated {date}");
        await Request(client, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/reverse",
            new ReverseJournalRequest(date.AddDays(validDayOffset), "Valid correction", "valid-date", true), HttpStatusCode.OK);
        Assert.Equal(2, await PostedCount(s.Company.Id));
    }

    [Fact]
    public async Task R04_001_GovernedJournalRequiresIndependentReversalAuthorization()
    {
        var s = await Arrange();
        using var owner = s.Client;
        var checker = await GovernanceOwner(s);
        using var reviewer = checker.Client;
        await Activate(s, reviewer, FinancialDuty.ManualJournal, 1);
        var draft = await Draft(s);
        await ApproveJournal(s, reviewer, draft.Id);
        await Request(reviewer, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/post",
            new PostJournalRequest("approved-original", draft.Version), HttpStatusCode.OK);
        using var denied = await reviewer.PostAsJsonAsync($"{s.Root}/journals/{draft.Id}/reverse",
            new ReverseJournalRequest(Today, "Separate correction intent", "no-reversal-approval"));
        var count = await PostedCount(s.Company.Id);
        Assert.True(denied.StatusCode == HttpStatusCode.Conflict && count == 1,
            $"Reversal HTTP {(int)denied.StatusCode}, {count} posted journals, original {draft.Id}");
        using (var guardScope = fixture.Factory.Services.CreateScope())
        {
            var services = guardScope.ServiceProvider;
            var guardDb = services.GetRequiredService<RaqmDbContext>();
            var access = services.GetRequiredService<FinancialAccess>();
            var validator = services.GetRequiredService<PostingValidator>();
            var engine = new PostingEngine(guardDb, access, validator, TimeProvider.System);
            var original = await guardDb.Journals.AsNoTracking().Include(j => j.Lines).SingleAsync(j => j.Id == draft.Id);
            var direct = new PostingRequest(new(s.Company.TenantId, s.Org, s.Company.Id), "Reversal", original.Id,
                "guard-probe", Today, s.Company.BaseCurrency, original.Lines.OrderBy(l => l.Number)
                    .Select(l => new JournalDraftLine(l.AccountId, l.Credit, l.Debit, l.Description)).ToList(),
                original.Id, Today, "Attempt without decision", original.Number, Reason: "Test database guard");
            var failure = await Assert.ThrowsAsync<PostgresException>(() => engine.PostAsync(checker.Id, direct, default));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            Assert.Equal("Active governance requires a matching reversal authorization", failure.MessageText);
            Assert.Equal(1, await PostedCount(s.Company.Id));
        }
        var proposal = new ReverseJournalRequest(Today, "Separate correction intent", "authorized-reversal");
        var root = GovernanceRoot(s);
        var authorization = await Gov<GovernanceAuthorization>(owner, $"{root}/authorizations/journal-reversals/{draft.Id}", proposal);
        await Gov<GovernanceReview>(reviewer, $"{root}/authorizations/{authorization.Id}/review",
            new ReviewInput(ReviewDecision.Approve, null, "Independent reversal review"));
        var reversalPath = $"{s.Root}/journals/{draft.Id}/reverse";
        await Request(reviewer, HttpMethod.Post, reversalPath, proposal with { Reason = "Different purpose" }, HttpStatusCode.Conflict);
        await Request(owner, HttpMethod.Post, reversalPath, proposal, HttpStatusCode.Conflict);
        var reversal = await Gov<PostingReceipt>(reviewer, reversalPath, proposal);
        Assert.Equal(reversal.JournalId, (await Gov<PostingReceipt>(reviewer, reversalPath, proposal)).JournalId);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var retained = await db.Set<GovernanceAuthorization>().SingleAsync(x => x.Id == authorization.Id);
        Assert.Equal(AuthorizationState.Used, retained.State);
        var decision = await db.Set<GovernanceEvent>().SingleAsync(x => x.CompanyId == s.Company.Id &&
            x.JournalId == reversal.JournalId && x.AuthorizationId == authorization.Id);
        Assert.Equal(draft.Id, decision.RelatedId);
        Assert.Equal(2, await PostedCount(s.Company.Id));
    }

    [Fact]
    public async Task R03_003_ApprovedJournalCannotPostAfterMaterialRebind()
    {
        var s = await Arrange();
        using var owner = s.Client;
        var checker = await GovernanceOwner(s);
        using var reviewer = checker.Client;
        await Activate(s, reviewer, FinancialDuty.ManualJournal, 1);
        var root = s.Root.Replace("/accounting", "/dimensions", StringComparison.Ordinal);
        var definition = await Gov<DimensionDefinition>(owner, root + "/definitions",
            new DefinitionRequest(DimensionKind.CostCenter, "COST", "Cost center", "Approval evidence"));
        var before = await Gov<DimensionValue>(owner, root + "/values",
            new ValueRequest(definition.Id, "A", "A", "Cost", Today));
        var after = await Gov<DimensionValue>(owner, root + "/values",
            new ValueRequest(definition.Id, "B", "B", "Cost", Today));
        var draft = await Draft(s);
        var bindingPath = $"{root}/sources/{DimensionSource.Manual}/{draft.Id}";
        await Request(owner, HttpMethod.Post, bindingPath,
            new DimensionBindingRequest(draft.Version, 0, [new(1, [new(definition.Id, before.Id)])], "Original attribution"), HttpStatusCode.OK);
        await ApproveJournal(s, reviewer, draft.Id);
        await Request(owner, HttpMethod.Post, bindingPath,
            new DimensionBindingRequest(draft.Version, 1, [new(1, [new(definition.Id, after.Id)])], "Reassigned attribution"), HttpStatusCode.OK);
        using var denied = await reviewer.PostAsJsonAsync($"{s.Root}/journals/{draft.Id}/post",
            new PostJournalRequest("stale-attribution", draft.Version));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var tags = await db.PostedDimensions.Where(x => x.CompanyId == s.Company.Id).Select(x => x.ValueId).ToListAsync();
        var count = await PostedCount(s.Company.Id);
        Assert.True(denied.StatusCode == HttpStatusCode.Conflict && count == 0 && tags.Count == 0,
            $"Post HTTP {(int)denied.StatusCode}, {count} posted journals, approved {before.Id}, rebound {after.Id}, retained tags [{string.Join(", ", tags)}]");
        await ApproveJournal(s, reviewer, draft.Id);
        await Request(reviewer, HttpMethod.Post, $"{s.Root}/journals/{draft.Id}/post",
            new PostJournalRequest("fresh-attribution", draft.Version), HttpStatusCode.OK);
        Assert.Equal(after.Id, await db.PostedDimensions.Where(x => x.CompanyId == s.Company.Id)
            .Select(x => x.ValueId).SingleAsync());
    }

    private static async Task ApproveJournal(Setup s, HttpClient reviewer, Guid journal)
    {
        var root = GovernanceRoot(s);
        var authorization = await Gov<GovernanceAuthorization>(s.Client, root + "/authorizations",
            new AuthorizationInput(FinancialDuty.ManualJournal, journal, null, "Review journal intent"));
        await Gov<GovernanceReview>(reviewer, $"{root}/authorizations/{authorization.Id}/review",
            new ReviewInput(ReviewDecision.Approve, null, "Independent review"));
    }
}
