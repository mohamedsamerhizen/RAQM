using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.AuditEvidence;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.Governance;
using static RAQM.Api.Helpers.Governance.GovernanceRules;
using static RAQM.Api.Services.Governance.GovernanceAdministration;

namespace RAQM.Api.Services.ApprovalGovernance;

public sealed class GovernanceApprovalService(RaqmDbContext db, IFinancialTransaction transactions, GovernanceSources sources,
    GovernanceGate gate, GovernanceAdministration admin, GovernanceAudit audit, FinancialAccess access)
{
    public Task<GovernanceAuthorization> Prepare(Guid actor, Guid org, Guid company, AuthorizationInput input, CancellationToken ct) =>
        Prepare(actor, org, company, input, null, ct);

    public Task<GovernanceAuthorization> PrepareReversal(Guid actor, Guid org, Guid company, Guid journal,
        ReverseJournalRequest request, CancellationToken ct) =>
        Prepare(actor, org, company, new(FinancialDuty.ManualJournal, journal, null, request.Reason), request, ct);

    private Task<GovernanceAuthorization> Prepare(Guid actor, Guid org, Guid company, AuthorizationInput input,
        ReverseJournalRequest? reversal, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var policy = await gate.Current(company, input.Duty, gate.Now, token);
            if (policy is null || policy.Mode == PolicyMode.Retired) throw Conflict("No applicable approval policy.");
            var intent = reversal is not null
                ? await sources.Reversal(scope, actor, input.SourceId!.Value, reversal, token)
                : input.Duty == FinancialDuty.SupplierPayment && input.Payment is { } payment && input.SourceId is null
                ? await sources.Payment(scope, actor, payment, token)
                : input.Payment is null && input.SourceId is { } id ? await sources.Find(scope, input.Duty, id, token)
                : throw Bad("Select a trusted source or supply the exact proposed supplier payment.");
            var fingerprint = Fingerprint(intent);
            var open = await db.Set<GovernanceAuthorization>().Where(x => x.CompanyId == company && x.PolicyId == policy.Id &&
                x.Duty == input.Duty && x.SourceId == intent.SourceId && x.State == AuthorizationState.Open).ToListAsync(token);
            foreach (var row in open.Where(x => x.ExpiresAt <= gate.Now)) row.State = AuthorizationState.Cancelled;
            var prior = open.SingleOrDefault(x => x.ExpiresAt > gate.Now && x.IntentHash == fingerprint);
            if (prior is not null) return prior;
            if ((reversal is not null || input.Duty == FinancialDuty.SupplierPayment) &&
                open.Any(x => x.ExpiresAt > gate.Now && x.Version == intent.Version))
                throw Conflict("This proposal already has an open maker authorization.");
            await db.SaveChangesAsync(token);
            var authorization = Scoped(scope, new GovernanceAuthorization { PolicyId = policy.Id, Duty = intent.Duty,
                SourceId = intent.SourceId, SubjectId = intent.SubjectId, MakerId = intent.MakerId, RequestedBy = actor,
                Amount = intent.Amount, Currency = intent.Currency, Version = intent.Version, IntentHash = fingerprint,
                ContextJson = reversal is not null ? JsonSerializer.Serialize(reversal with
                {
                    Reason = AccountingRules.Text(reversal.Reason, 500, "Reversal reason"),
                    IdempotencyKey = AccountingRules.Text(reversal.IdempotencyKey, 128, "Idempotency key")
                })
                    : input.Payment is null ? null : JsonSerializer.Serialize(input.Payment),
                Reason = AccountingRules.Text(input.Reason, 500, "Approval justification"),
                ExpiresAt = AccountingRules.StoredUtcInstant(gate.Now.AddHours(Parse(policy).ExpiryHours)) });
            db.Add(authorization);
            await audit.Append(scope, actor, new("governance", "AuthorizationPrepared", intent.SourceId, "Bound source version prepared",
                Duty: intent.Duty, Subject: intent.SubjectId, Policy: policy.Id, Authorization: authorization.Id, Prior: intent.Version, Next: "Open"), token);
            return authorization;
        }, ct);

    public Task<GovernanceReview> Review(Guid actor, Guid org, Guid company, Guid id, ReviewInput input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var authorization = await Find(company, id, token);
            var now = gate.Now;
            var policy = await gate.Current(company, authorization.Duty, now, token);
            if (policy?.Id != authorization.PolicyId || policy.Mode == PolicyMode.Retired ||
                authorization.State != AuthorizationState.Open || authorization.ExpiresAt <= now || !Enum.IsDefined(input.Decision))
                throw Conflict("The approval request is no longer current.");
            var intent = authorization switch
            {
                { Duty: FinancialDuty.SupplierPayment } => await sources.Payment(scope, authorization.MakerId,
                    JsonSerializer.Deserialize<PaymentRequest>(authorization.ContextJson!)!, token),
                { Duty: FinancialDuty.ManualJournal, ContextJson: { } context } => await sources.Reversal(scope,
                    authorization.MakerId, authorization.SourceId, JsonSerializer.Deserialize<ReverseJournalRequest>(context)!, token),
                _ => await sources.Find(scope, authorization.Duty, authorization.SourceId, token)
            };
            if (Fingerprint(intent) != authorization.IntentHash) throw Conflict("Source changed. Submit the new version for review.");
            var principal = input.DelegationId is { } delegation
                ? await db.Set<GovernanceDelegation>().Where(x => x.CompanyId == company && x.Id == delegation)
                    .Select(x => (Guid?)x.PrincipalId).SingleOrDefaultAsync(token) ?? throw Missing() : actor;
            var rules = Parse(policy);
            if (!Independent(authorization.MakerId, actor, principal) || actor == authorization.RequestedBy || principal == authorization.RequestedBy ||
                !await gate.ReviewerAccess(scope, actor, principal, input.DelegationId, authorization.Duty, rules, now, token) ||
                await gate.SupplierConflict(company, actor, intent, rules, now, token) ||
                principal != actor && await gate.SupplierConflict(company, principal, intent, rules, now, token))
                throw GovernanceGate.Deny(scope, actor, policy, intent, "Reviewer conflicts with the maker, supplier or current authority.");
            var previous = await db.Set<GovernanceReview>().AsNoTracking().Where(x => x.CompanyId == company && x.AuthorizationId == id)
                .OrderBy(x => x.Level).ToListAsync(token);
            if (previous.Any(x => x.ReviewerId == actor || x.PrincipalId == principal || x.ReviewerId == principal || x.PrincipalId == actor))
                throw Conflict("Each approval level requires separate actual and represented actors.");
            var band = Band(rules, intent.Amount);
            if (previous.Count >= band.Reviewers && input.Decision == ReviewDecision.Approve)
                throw Conflict("All required approval levels are already assigned.");
            if (previous.Count >= 5 || input.Decision == ReviewDecision.Approve &&
                (band.RequiredReviewers ?? []).Except(previous.Select(x => x.PrincipalId).Append(principal)).Count() > band.Reviewers - previous.Count - 1)
                throw Conflict("This level must be filled by a remaining named reviewer.");
            var review = Scoped(scope, new GovernanceReview { AuthorizationId = id, ReviewerId = actor, PrincipalId = principal,
                DelegationId = input.DelegationId, Level = previous.Count + 1, Decision = input.Decision, At = AccountingRules.StoredUtcInstant(now),
                Reason = AccountingRules.Text(input.Reason, 500, "Review reason") });
            db.Add(review);
            if (input.Decision == ReviewDecision.Reject) authorization.State = AuthorizationState.Rejected;
            await audit.Append(scope, actor, new("governance", "Authorization:" + input.Decision, intent.SourceId, "Independent review recorded",
                Duty: intent.Duty, Subject: intent.SubjectId, Policy: policy.Id, Authorization: id, Related: review.Id), token);
            return review;
        }, ct);

    public Task<GovernanceAuthorization> Cancel(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var row = await Find(company, id, token);
            if (row.RequestedBy != actor || row.State != AuthorizationState.Open) throw Conflict("Only the requester may cancel an open request.");
            row.State = AuthorizationState.Cancelled;
            await audit.Append(scope, actor, new("governance", "AuthorizationCancelled", row.SourceId, "Open approval request cancelled",
                Duty: row.Duty, Policy: row.PolicyId, Authorization: row.Id, Prior: "Open", Next: "Cancelled"), token);
            return row;
        }, ct);

    public async Task<object> View(Guid actor, Guid org, Guid company, Guid id, Guid? delegation, CancellationToken ct)
    {
        var scope = await access.Resolve(actor, org, company, true, false, ct);
        var row = await db.Set<GovernanceAuthorization>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw Missing();
        var policy = await db.Set<GovernancePolicy>().AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == row.PolicyId, ct);
        var principal = delegation is { } delegated
            ? await db.Set<GovernanceDelegation>().Where(x => x.CompanyId == company && x.Id == delegated)
                .Select(x => (Guid?)x.PrincipalId).SingleOrDefaultAsync(ct) ?? throw Missing() : actor;
        if (actor != row.MakerId && actor != row.RequestedBy &&
            !await gate.ReviewerAccess(scope, actor, principal, delegation, row.Duty, Parse(policy), gate.Now, ct))
        {
            try { await access.Resolve(actor, org, company, false, true, ct); }
            catch (ApiException e) when (e.Status == 403) { throw Missing(); }
        }
        var reviews = await db.Set<GovernanceReview>().AsNoTracking().Where(x => x.CompanyId == company && x.AuthorizationId == id)
            .OrderBy(x => x.Level).ToListAsync(ct);
        return new { Authorization = row, Policy = policy, Reviews = reviews };
    }

    public Task<GovernanceException> RequestException(Guid actor, Guid org, Guid company, Guid id, ExceptionInput input, CancellationToken ct) =>
        admin.Admin(actor, org, company, async (scope, token) =>
        {
            var authorization = await Find(company, id, token);
            var policy = await gate.Current(company, authorization.Duty, gate.Now, token);
            if (policy?.Id != authorization.PolicyId || policy.Mode != PolicyMode.Enforce || !Parse(policy).Waivable ||
                Band(Parse(policy), authorization.Amount).Reviewers == 0 ||
                authorization.State != AuthorizationState.Open || input.ExpiresAt <= gate.Now ||
                input.ExpiresAt > authorization.ExpiresAt || input.ExpiresAt > gate.Now.AddHours(48) ||
                await db.Set<GovernanceException>().AnyAsync(x => x.CompanyId == company && x.AuthorizationId == id, token))
                throw Conflict("Only one scoped, explicitly waivable approval exception is permitted.");
            var row = Scoped(scope, new GovernanceException { AuthorizationId = id, RequestedBy = actor,
                ExpiresAt = AccountingRules.StoredUtcInstant(input.ExpiresAt), Reason = AccountingRules.Text(input.Reason, 500, "Exception reason"),
                CompensatingControl = AccountingRules.Text(input.CompensatingControl, 500, "Compensating evidence") });
            db.Add(row);
            await audit.Append(scope, actor, new("governance", "ExceptionRequested", authorization.SourceId, "Bound review exception requested",
                Duty: authorization.Duty, Policy: policy.Id, Authorization: id, Related: row.Id), token);
            return row;
        }, ct);

    public Task<GovernanceException> ExceptionAction(Guid actor, Guid org, Guid company, Guid id, string action, CancellationToken ct) =>
        admin.Admin(actor, org, company, async (scope, token) =>
        {
            var row = await db.Set<GovernanceException>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing();
            var authorization = await Find(company, row.AuthorizationId, token);
            if (action == "approve" && row.ApprovedAt == null && row.RevokedAt == null && row.ExpiresAt > gate.Now &&
                authorization.State == AuthorizationState.Open && actor != row.RequestedBy && actor != authorization.MakerId)
            {
                row.ApprovedBy = actor;
                row.ApprovedAt = AccountingRules.StoredUtcInstant(gate.Now);
            }
            else if (action == "revoke" && row.RevokedAt == null && row.UsedAt == null) row.RevokedAt = AccountingRules.StoredUtcInstant(gate.Now);
            else throw Conflict("This exception transition is unavailable.");
            await audit.Append(scope, actor, new("governance", "Exception:" + action, authorization.SourceId, "Bound exception transition",
                Duty: authorization.Duty, Policy: authorization.PolicyId, Authorization: authorization.Id, Related: row.Id), token);
            return row;
        }, ct);

    private async Task<GovernanceAuthorization> Find(Guid company, Guid id, CancellationToken ct) =>
        await db.Set<GovernanceAuthorization>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw Missing();
}
