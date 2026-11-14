using System.Text.Json;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Helpers;

namespace RAQM.Api.Helpers.Governance;

public static class GovernanceRules
{
    public static ApiException Missing() => new(404, "Governance evidence was not found.", "governance_missing");
    public static ApiException Conflict(string message) => new(409, message, "governance_conflict");
    public static ApiException Bad(string message) => new(400, message, "governance_input");
    public static bool Supported(FinancialDuty duty) => duty is >= FinancialDuty.SupplierPayment and <= FinancialDuty.ConsolidationPublication;

    public static void Validate(FinancialDuty duty, PolicyRules rules)
    {
        if (!Supported(duty) || rules is null || rules.Bands is null || rules.Bands.Length is < 1 or > 10 ||
            rules.ExpiryHours is < 1 or > 720 || rules.SupplierConflictDays is < 0 or > 365 ||
            rules.SupplierConflictDays > 0 && duty != FinancialDuty.SupplierPayment ||
            rules.Waivable && duty != FinancialDuty.ProcurementApproval || rules.SeparateAssignments && !rules.RequireExplicitGrants)
            throw Bad("Choose a supported duty and bounded control parameters.");
        decimal prior = -1;
        foreach (var band in rules.Bands)
        {
            if (band is null || band.Through <= prior || band.Through > Accounting.CurrencyPolicy.Maximum ||
                band.Reviewers is < 0 or > 5 || (band.RequiredReviewers?.Distinct().Count() ?? 0) != (band.RequiredReviewers?.Length ?? 0) ||
                (band.RequiredReviewers?.Length ?? 0) > band.Reviewers)
                throw Bad("Approval bands must be ascending with up to five distinct required reviewers.");
            prior = band.Through;
        }
        if (prior != Accounting.CurrencyPolicy.Maximum)
            throw Bad("The final band must cover the maximum supported amount.");
    }

    public static ApprovalBand Band(PolicyRules rules, decimal amount) =>
        rules.Bands.FirstOrDefault(x => amount <= x.Through) ?? throw Conflict("No applicable approval band.");

    public static PolicyRules Parse(GovernancePolicy policy) => JsonSerializer.Deserialize<PolicyRules>(policy.RulesJson)!;
    public static string Fingerprint(FinancialIntent intent) => TradeRules.Hash(intent);
    public static bool Active(DateTimeOffset now, DateTimeOffset start, DateTimeOffset end, DateTimeOffset? approval, DateTimeOffset? revocation) =>
        approval <= now && revocation == null && start <= now && now < end;
    public static bool Independent(Guid maker, Guid reviewer, Guid principal) => maker != reviewer && maker != principal;
    public static bool SeparationOverlaps(IEnumerable<GovernancePolicy> policies, DateTimeOffset start, DateTimeOffset end)
    {
        if (start >= end) return false;
        var timeline = policies.Where(x => x.ApprovedAt.HasValue).OrderBy(x => x.EffectiveAt).ThenBy(x => x.Version).ToArray();
        for (var i = 0; i < timeline.Length; i++)
        {
            var policy = timeline[i];
            var begins = Activation(policy);
            var expires = i + 1 < timeline.Length ? Activation(timeline[i + 1]) : DateTimeOffset.MaxValue;
            if (policy.Mode == PolicyMode.Enforce && begins < end && start < expires && Parse(policy).SeparateAssignments)
                return true;
        }
        return false;

        static DateTimeOffset Activation(GovernancePolicy row) =>
            row.ApprovedAt > row.EffectiveAt ? row.ApprovedAt.Value : row.EffectiveAt;
    }
    public static int RiskScore(int impact, int likelihood)
    {
        if (impact is < 1 or > 5 || likelihood is < 1 or > 5) throw Bad("Use a 1–5 impact and likelihood scale.");
        return impact * likelihood;
    }
}
