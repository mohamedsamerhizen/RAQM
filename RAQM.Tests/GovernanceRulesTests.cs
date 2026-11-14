using RAQM.Api.DTOs.Governance;
using System.Text.Json;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Services.AuditEvidence;
using Xunit;

namespace RAQM.Tests;

public sealed class GovernanceRulesTests
{
    private static readonly DateTimeOffset Day = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AmountBandsUseInclusiveBoundariesAndRequireDistinctNamedReviewers()
    {
        var user = Guid.NewGuid();
        var rules = new PolicyRules([new(1000, 0), new(10_000, 1, [user]), new(CurrencyPolicy.Maximum, 2)], true, false, false, 0, 24, false);
        GovernanceRules.Validate(FinancialDuty.ProcurementApproval, rules);
        Assert.Equal(0, GovernanceRules.Band(rules, 1000m).Reviewers);
        Assert.Equal(1, GovernanceRules.Band(rules, 1000.01m).Reviewers);
        Assert.Equal(2, GovernanceRules.Band(rules, 10_000.01m).Reviewers);
        Assert.Throws<ApiException>(() => GovernanceRules.Validate(FinancialDuty.ProcurementApproval,
            rules with { Bands = [new(CurrencyPolicy.Maximum, 2, [user, user])] }));
    }

    [Fact]
    public void OptionalExceptionsCannotWaiveJournalIntegrityOrEscalateUnsupportedDuties()
    {
        var rules = new PolicyRules([new(CurrencyPolicy.Maximum, 1)], true, false, false, 0, 24, true);
        GovernanceRules.Validate(FinancialDuty.ProcurementApproval, rules);
        Assert.Throws<ApiException>(() => GovernanceRules.Validate(FinancialDuty.ManualJournal, rules));
        Assert.Throws<ApiException>(() => GovernanceRules.Validate(FinancialDuty.AuditWorkspace, rules));
        Assert.Throws<ApiException>(() => GovernanceRules.Validate(FinancialDuty.ProcurementApproval, rules with { SeparateAssignments = true }));
    }

    [Fact]
    public void DelegatedMakerCannotReviewAndAccessExpiryIsExclusive()
    {
        var maker = Guid.NewGuid();
        var delegateId = Guid.NewGuid();
        Assert.False(GovernanceRules.Independent(maker, delegateId, maker));
        Assert.False(GovernanceRules.Independent(maker, maker, delegateId));
        Assert.True(GovernanceRules.Independent(maker, delegateId, Guid.NewGuid()));
        Assert.True(GovernanceRules.Active(Day, Day, Day.AddHours(4), Day, null));
        Assert.False(GovernanceRules.Active(Day.AddHours(4), Day, Day.AddHours(4), Day, null));
        Assert.False(GovernanceRules.Active(Day, Day, Day.AddHours(4), null, null));
        Assert.False(GovernanceRules.Active(Day, Day, Day.AddHours(4), Day, Day));
    }

    [Fact]
    public void FingerprintChangesWithSourceVersionAmountOrMaker()
    {
        var source = new FinancialIntent(FinancialDuty.ManualJournal, Guid.NewGuid(), null, Guid.NewGuid(), "version-1", 100.00m, "USD");
        Assert.Equal(GovernanceRules.Fingerprint(source), GovernanceRules.Fingerprint(source with { Amount = 100m }));
        Assert.NotEqual(GovernanceRules.Fingerprint(source), GovernanceRules.Fingerprint(source with { Version = "version-2" }));
        Assert.NotEqual(GovernanceRules.Fingerprint(source), GovernanceRules.Fingerprint(source with { Amount = 101m }));
        Assert.NotEqual(GovernanceRules.Fingerprint(source), GovernanceRules.Fingerprint(source with { MakerId = Guid.NewGuid() }));
        Assert.Equal(GovernanceSources.PaymentIdentity("pay-1"), GovernanceSources.PaymentIdentity(" pay-1 "));
        Assert.NotEqual(GovernanceSources.PaymentIdentity("pay-1"), GovernanceSources.PaymentIdentity("pay-2"));
    }

    [Fact]
    public void AuditVerificationFindsChangedEvidenceGapsAndCrossCompanySubstitution()
    {
        var company = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        GovernanceEvent Event(long sequence, string previous) => new()
        {
            CompanyId = company, TenantId = tenant, OrganizationId = organization, Sequence = sequence, PreviousHash = previous,
            ActorId = Guid.NewGuid(), RecordedAt = Day, EffectiveAt = Day, SourceId = Guid.NewGuid(), Type = "PolicyApproved",
            Category = "governance", Reason = "retained decision"
        };
        var first = Event(1, new string('0', 64));
        first.Hash = GovernanceAudit.Hash(first);
        var second = Event(2, first.Hash);
        second.Hash = GovernanceAudit.Hash(second);
        Assert.Equal(new AuditVerification(2, null, second.Hash), GovernanceAudit.Verify([first, second]));
        Assert.True(GovernanceAudit.Verify([second], 2, first.Hash).Intact);
        Assert.Equal(1L, GovernanceAudit.Verify([second]).FirstFailure);
        second.Reason = "changed";
        Assert.Equal(2L, GovernanceAudit.Verify([first, second]).FirstFailure);
        second.Hash = GovernanceAudit.Hash(second);
        second.CompanyId = Guid.NewGuid();
        second.Hash = GovernanceAudit.Hash(second);
        Assert.Equal(2L, GovernanceAudit.Verify([first, second]).FirstFailure);
    }

    [Fact]
    public void RetiredSeparationPolicyDoesNotBlockAssignmentsInALaterWindow()
    {
        var rules = new PolicyRules([new(CurrencyPolicy.Maximum, 1)], true, true, true, 0, 24, false);
        GovernancePolicy[] timeline =
        [
            new() { Version = 1, Mode = PolicyMode.Enforce, EffectiveAt = Day, ApprovedAt = Day, RulesJson = JsonSerializer.Serialize(rules) },
            new() { Version = 2, Mode = PolicyMode.Retired, EffectiveAt = Day.AddDays(5), ApprovedAt = Day.AddDays(1), RulesJson = JsonSerializer.Serialize(rules) }
        ];
        Assert.True(GovernanceRules.SeparationOverlaps(timeline, Day.AddDays(4), Day.AddDays(6)));
        Assert.False(GovernanceRules.SeparationOverlaps(timeline, Day.AddDays(5), Day.AddDays(6)));
    }

    [Fact]
    public void RiskScoresUseOrdinalAssumptionsRatherThanProbabilities()
    {
        Assert.Equal(20, GovernanceRules.RiskScore(5, 4));
        Assert.Equal(6, GovernanceRules.RiskScore(3, 2));
        Assert.Throws<ApiException>(() => GovernanceRules.RiskScore(5, 0));
    }
}
