using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.AuditEvidence;
using static RAQM.Api.Helpers.Governance.GovernanceRules;

namespace RAQM.Api.Services.ApprovalGovernance;

public sealed record DeniedGovernanceAction(Guid Actor, Guid Organization, Guid Company, FinancialDuty Duty, Guid Source, Guid Policy, string Reason);

public sealed class GovernanceGate(RaqmDbContext db, GovernanceSources sources, FinancialAccess access, GovernanceAudit audit, TimeProvider clock)
{
    internal DateTimeOffset Now => clock.GetUtcNow();

    internal static ApiException Deny(Company company, Guid actor, GovernancePolicy policy, FinancialIntent intent, string reason)
    {
        var failure = Conflict("The company's financial governance requirements are not satisfied.");
        failure.Data[nameof(DeniedGovernanceAction)] = new DeniedGovernanceAction(actor, company.OrganizationId, company.Id,
            intent.Duty, intent.SourceId, policy.Id, reason);
        return failure;
    }

    internal Task<GovernancePolicy?> Current(Guid company, FinancialDuty duty, DateTimeOffset now, CancellationToken ct) =>
        db.Set<GovernancePolicy>().AsNoTracking().Where(x => x.CompanyId == company && x.Duty == duty &&
            x.ApprovedAt != null && x.ApprovedAt <= now && x.EffectiveAt <= now)
            .OrderByDescending(x => x.EffectiveAt).ThenByDescending(x => x.Version).FirstOrDefaultAsync(ct);

    internal Task<List<GovernanceGrant>> Grants(Guid company, Guid actor, FinancialDuty duty, DateTimeOffset now, CancellationToken ct) =>
        db.Set<GovernanceGrant>().AsNoTracking().Where(x => x.CompanyId == company && x.UserId == actor && x.Duty == duty &&
            x.ApprovedAt != null && x.ApprovedAt <= now && x.RevokedAt == null && x.StartsAt <= now && x.ExpiresAt > now)
            .OrderBy(x => x.Emergency).ThenBy(x => x.ExpiresAt).ThenBy(x => x.Id).ToListAsync(ct);

    public async Task Enforce(Company company, Guid actor, FinancialDuty duty, Guid source, CancellationToken ct,
        IReadOnlyDictionary<int, EffectiveDimension[]>? attribution = null)
    {
        var policy = await Current(company.Id, duty, Now, ct);
        if (policy is null || policy.Mode == PolicyMode.Retired) return;
        var intent = await sources.Find(company, duty, source, ct, attribution);
        await Check(company, actor, policy, intent, ct);
    }

    internal async Task EnforceReversal(Company company, Guid actor, PostingRequest request, Guid reversalId, CancellationToken ct)
    {
        var original = await db.Journals.AsNoTracking().Include(x => x.Lines).SingleAsync(x =>
            x.CompanyId == company.Id && x.Id == request.ReversesJournalId, ct);
        if (original.SourceModule != "ManualJournal") return;
        var policy = await Current(company.Id, FinancialDuty.ManualJournal, Now, ct);
        if (policy is null || policy.Mode == PolicyMode.Retired) return;

        var intent = GovernanceSources.ReversalIntent(original, actor, request);
        var maker = await db.Set<GovernanceAuthorization>().Where(x => x.CompanyId == company.Id &&
            x.PolicyId == policy.Id && x.Duty == intent.Duty && x.SourceId == original.Id && x.ContextJson != null &&
            x.Version == intent.Version && x.State == AuthorizationState.Open && x.ExpiresAt > Now)
            .Select(x => (Guid?)x.MakerId).SingleOrDefaultAsync(ct);
        await Check(company, actor, policy, intent with { MakerId = maker ?? actor }, ct,
            related: original.Id, reversalJournal: reversalId);
    }

    public async Task EnforcePayment(Company company, Guid actor, PaymentRequest request, Guid paymentId, CancellationToken ct)
    {
        var policy = await Current(company.Id, FinancialDuty.SupplierPayment, Now, ct);
        if (policy is null || policy.Mode == PolicyMode.Retired) return;
        var id = GovernanceSources.PaymentIdentity(request.IdempotencyKey);
        var candidates = await db.Set<GovernanceAuthorization>().AsNoTracking().Where(x => x.CompanyId == company.Id &&
            x.Duty == FinancialDuty.SupplierPayment && x.PolicyId == policy.Id && x.SourceId == id && x.State == AuthorizationState.Open &&
            x.ExpiresAt > Now).ToListAsync(ct);
        var version = RAQM.Api.Helpers.Trading.TradeRules.Hash(request);
        var matching = candidates.Where(x => x.Version == version).ToArray();
        if (matching.Length > 1) throw Conflict("Cancel competing payment authorizations for this key.");
        var maker = matching.SingleOrDefault()?.MakerId ?? actor;
        var intent = await sources.Payment(company, maker, request, ct);
        await Check(company, actor, policy, intent, ct, paymentId);
    }

    public async Task ObserveSupplierChange(Company company, Guid actor, Guid supplier, CancellationToken ct)
    {
        var policy = await Current(company.Id, FinancialDuty.SupplierPayment, Now, ct);
        if (policy is null || policy.Mode == PolicyMode.Retired || Parse(policy).SupplierConflictDays == 0) return;
        await audit.Append(company, actor, new("financial", "SupplierFinancialDetailsChanged", supplier, "Supplier financial fields changed",
            Duty: FinancialDuty.SupplierDetails, Subject: supplier, Policy: policy.Id), ct);
    }

    internal Task<bool> SupplierConflict(Guid company, Guid actor, FinancialIntent intent, PolicyRules rules, DateTimeOffset now, CancellationToken ct)
    {
        if (rules.SupplierConflictDays == 0 || intent.SubjectId is null) return Task.FromResult(false);
        var since = now.AddDays(-rules.SupplierConflictDays);
        return db.Set<GovernanceEvent>().AnyAsync(x => x.CompanyId == company && x.Type == "SupplierFinancialDetailsChanged" &&
            x.SubjectId == intent.SubjectId && x.ActorId == actor && x.RecordedAt >= since && x.RecordedAt <= now, ct);
    }

    internal async Task<bool> ReviewerAccess(Company company, Guid actor, Guid principal, Guid? delegation, FinancialDuty duty,
        PolicyRules rules, DateTimeOffset now, CancellationToken ct)
    {
        if (!await CanWrite(company, actor, ct) || !await CanWrite(company, principal, ct)) return false;
        var permissions = await Grants(company.Id, principal, duty, now, ct);
        if ((rules.RequireExplicitGrants || delegation.HasValue) && !permissions.Any(x => x.Permission == GovernancePermission.Review && !x.Emergency) ||
            rules.SeparateAssignments && (permissions.Any(x => x.Permission == GovernancePermission.Execute) ||
                (await Grants(company.Id, actor, duty, now, ct)).Any(x => x.Permission == GovernancePermission.Execute))) return false;
        if (delegation is null) return actor == principal;
        return await db.Set<GovernanceDelegation>().AnyAsync(x => x.CompanyId == company.Id && x.Id == delegation && x.Duty == duty &&
            x.PrincipalId == principal && x.DelegateId == actor && x.ApprovedAt != null && x.ApprovedAt <= now && x.RevokedAt == null &&
            x.StartsAt <= now && x.ExpiresAt > now, ct);
    }

    internal async Task<bool> CanWrite(Company company, Guid actor, CancellationToken ct)
    {
        try { await access.Resolve(actor, company.OrganizationId, company.Id, true, false, ct); return true; }
        catch (ApiException e) when (e.Status is 403 or 404) { return false; }
    }

    private async Task Check(Company company, Guid actor, GovernancePolicy policy, FinancialIntent intent, CancellationToken ct,
        Guid? related = null, Guid? reversalJournal = null)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Governance enforcement requires the authoritative transaction.");
        var now = Now;
        var rules = Parse(policy);
        var band = Band(rules, intent.Amount);
        var fingerprint = Fingerprint(intent);
        var authorization = await db.Set<GovernanceAuthorization>().SingleOrDefaultAsync(x => x.CompanyId == company.Id && x.PolicyId == policy.Id &&
            x.Duty == intent.Duty && x.SourceId == intent.SourceId && x.IntentHash == fingerprint &&
            x.State == AuthorizationState.Open && x.ExpiresAt > now, ct);
        if (authorization is not null && authorization.State != AuthorizationState.Open)
            throw Conflict("This authorization is already used within the current operation.");
        var grants = await Grants(company.Id, actor, intent.Duty, now, ct);
        var executor = grants.FirstOrDefault(x => x.Permission == GovernancePermission.Execute);
        var waiver = authorization is null || !rules.Waivable ? null : await db.Set<GovernanceException>().SingleOrDefaultAsync(x =>
            x.CompanyId == company.Id && x.AuthorizationId == authorization.Id && x.ApprovedAt != null && x.ApprovedAt <= now &&
            x.ExpiresAt > now && x.UsedAt == null && x.RevokedAt == null, ct);
        string? reason = null;
        if (rules.RequireExplicitGrants && executor is null || rules.SeparateAssignments && grants.Any(x => x.Permission == GovernancePermission.Review))
            reason = "Execution permission missing or incompatible.";
        else if (rules.IndependentExecution && intent.MakerId == actor)
            reason = "Independent execution required.";
        else if (await SupplierConflict(company.Id, actor, intent, rules, now, ct))
            reason = "Recent supplier financial edit conflicts with execution.";
        else if (reversalJournal.HasValue && authorization is null)
            reason = "A separate authorization for the exact reversal is required.";
        else if (band.Reviewers > 0 && waiver is null)
        {
            if (authorization is null)
                reason = "Current source version needs a governance authorization.";
            else
            {
                var reviews = await db.Set<GovernanceReview>().AsNoTracking().Where(x => x.CompanyId == company.Id &&
                    x.AuthorizationId == authorization.Id && x.Decision == ReviewDecision.Approve).OrderBy(x => x.Level).ToListAsync(ct);
                if (reviews.Count != band.Reviewers || (band.RequiredReviewers ?? []).Except(reviews.Select(x => x.PrincipalId)).Any())
                    reason = "Required independent approval levels are incomplete.";
                else
                    foreach (var review in reviews)
                    {
                        if (!Independent(intent.MakerId, review.ReviewerId, review.PrincipalId) ||
                            !await ReviewerAccess(company, review.ReviewerId, review.PrincipalId, review.DelegationId, intent.Duty, rules, now, ct) ||
                            await SupplierConflict(company.Id, review.ReviewerId, intent, rules, now, ct) ||
                            review.PrincipalId != review.ReviewerId && await SupplierConflict(company.Id, review.PrincipalId, intent, rules, now, ct))
                        {
                            reason = "Retained approval no longer has valid independent authority.";
                            break;
                        }
                    }
            }
        }
        if (reason is not null && policy.Mode == PolicyMode.Enforce)
            throw Deny(company, actor, policy, intent, reason);

        if (reason is null && policy.Mode == PolicyMode.Enforce)
        {
            if (authorization is not null)
            {
                authorization.State = AuthorizationState.Used;
                authorization.UsedBy = actor;
            }
            if (waiver is not null)
            {
                waiver.UsedAt = now;
                await audit.Append(company, actor, new("governance", "ExceptionConsumed", intent.SourceId, "Approved bounded review waiver",
                    GovernanceOutcome.Allowed, intent.Duty, intent.SubjectId, Policy: policy.Id, Authorization: authorization?.Id, Related: waiver.Id), ct);
            }
            if (executor?.Emergency == true)
                await audit.Append(company, actor, new("access", "EmergencyGrantUsed", intent.SourceId, "Time-limited execution permission used",
                    GovernanceOutcome.Allowed, intent.Duty, Related: executor.Id), ct);
        }
        await audit.Append(company, actor, new("financial", "GovernanceDecision", intent.SourceId, reason ?? "Applicable policy evaluated",
            reason is null ? GovernanceOutcome.Allowed : GovernanceOutcome.MonitorFinding, intent.Duty, intent.SubjectId,
            Journal: reversalJournal ?? (intent.Duty == FinancialDuty.ManualJournal ? intent.SourceId : null),
            Policy: policy.Id, Authorization: authorization?.Id,
            Related: related, Prior: intent.Version, Next: policy.Mode == PolicyMode.Enforce ? "Authorized" : "Observed"), ct);
    }
}
