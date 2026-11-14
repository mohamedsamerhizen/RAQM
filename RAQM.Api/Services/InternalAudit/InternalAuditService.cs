using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Entities.InternalControls;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Services.AuditEvidence;
using RAQM.Api.Services.Governance;
using RAQM.Api.Services.InternalControls;
using static RAQM.Api.Helpers.Governance.GovernanceRules;
using static RAQM.Api.Services.Governance.GovernanceAdministration;

namespace RAQM.Api.Services.InternalAudit;

public sealed class InternalAuditService(RaqmDbContext db, IFinancialTransaction transactions, ControlService controls,
    GovernanceAdministration admin, GovernanceGate gate, GovernanceAudit audit, FinancialAccess access)
{
    private sealed record WorkEvidence(WorkInput Input, int Inherent, int Residual, GovernanceGrant? Grant);

    public Task<GovernanceWork> Create(Guid actor, Guid org, Guid company, WorkInput input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await controls.Auditor(actor, org, company, true, token);
            if (!Enum.IsDefined(input.Kind) || input.DueOn.Year < 1900 || !await gate.CanWrite(scope, input.OwnerId, token))
                throw Bad("Choose a valid work owner, kind and due date.");
            var inherent = RiskScore(input.Impact, input.Likelihood);
            var residual = RiskScore(input.ResidualImpact, input.ResidualLikelihood);
            if (residual > inherent) throw Bad("Residual score cannot exceed the assessed inherent score.");
            if (input.RiskId is { } risk && !await db.Set<GovernanceWork>().AnyAsync(x => x.CompanyId == company && x.Id == risk &&
                x.Kind == GovernanceWorkKind.Risk, token)) throw Missing();
            if (input.ControlId is { } control && !await db.Set<InternalControl>().AnyAsync(x => x.CompanyId == company && x.Id == control, token))
                throw Missing();
            if (input.EvaluationId is { } evaluation && !await db.Set<ControlEvaluation>().AnyAsync(x => x.CompanyId == company && x.Id == evaluation &&
                (input.ControlId == null || x.ControlId == input.ControlId), token)) throw Missing();
            if (input.ParentId is { } parent)
            {
                var prior = await db.Set<GovernanceWork>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == parent, token) ?? throw Missing();
                if (!(prior.Kind == GovernanceWorkKind.Engagement && input.Kind == GovernanceWorkKind.Finding ||
                      prior.Kind == input.Kind && prior.State == GovernanceWorkState.Closed))
                    throw Conflict("Only findings may attach to an engagement or supersede a closed case of the same kind.");
                if (prior.Kind == GovernanceWorkKind.Finding &&
                    (input.ControlId != prior.ControlId || input.RiskId != prior.RiskId || input.EvaluationId != prior.EvaluationId))
                    throw Conflict("A reopened finding must retain its source, control and risk links.");
            }
            GovernanceGrant? grant = null;
            if (input.Kind == GovernanceWorkKind.AccessReview)
            {
                grant = await db.Set<GovernanceGrant>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == input.GrantId, token)
                    ?? throw Missing();
                if (input.OwnerId == grant.UserId || actor == grant.UserId) throw Conflict("Access certification must be assigned independently.");
            }
            else if (input.GrantId.HasValue) throw Bad("Only access reviews may name a scoped grant.");
            var normalized = input with { Title = AccountingRules.Text(input.Title, 150, "Title"), Category = AccountingRules.Text(input.Category, 100, "Category"),
                Process = AccountingRules.Text(input.Process, 100, "Process"), Objective = AccountingRules.Text(input.Objective, 1000, "Objective or root cause"),
                Plan = AccountingRules.Text(input.Plan, 1500, "Mitigation, procedure or corrective action") };
            var row = Scoped(scope, new GovernanceWork { Kind = input.Kind, ControlId = input.ControlId, RiskId = input.RiskId,
                EvaluationId = input.EvaluationId, ParentId = input.ParentId, CreatedBy = actor, OwnerId = input.OwnerId,
                DueOn = input.DueOn, Severity = residual, Title = normalized.Title,
                DetailsJson = JsonSerializer.Serialize(new WorkEvidence(normalized, inherent, residual, grant)) });
            db.Add(row);
            await audit.Append(scope, actor, new("audit", input.ParentId.HasValue ? "WorkLinked" : "WorkPrepared", row.Id,
                "Governance work and scope retained", Related: input.ParentId, Next: row.Kind.ToString()), token);
            return row;
        }, ct);

    public Task<GovernanceWork> Change(Guid actor, Guid org, Guid company, Guid id, string action, WorkAction input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await controls.Auditor(actor, org, company, true, token);
            var row = await db.Set<GovernanceWork>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing();
            if (row.Version != input.Version || row.State == GovernanceWorkState.Closed) throw Conflict("The case changed or is closed.");
            var reason = AccountingRules.Text(input.Reason, 1000, "Work transition evidence");
            var old = row.State;
            if (row.Kind == GovernanceWorkKind.Risk && action is "approve" or "close")
                await access.Resolve(actor, org, company, true, true, token);
            if (action == "approve" && row.State == GovernanceWorkState.Draft && row.CreatedBy != actor && row.OwnerId != actor)
                row.State = GovernanceWorkState.Approved;
            else if (action == "start" && row.State == GovernanceWorkState.Approved && row.OwnerId == actor)
                row.State = GovernanceWorkState.InProgress;
            else if (action == "submit" && row.State == GovernanceWorkState.InProgress && row.OwnerId == actor)
            {
                if (!await db.Set<EvidenceReference>().AnyAsync(x => x.CompanyId == company && x.WorkId == id, token) &&
                    !await db.Set<ControlEvaluation>().AnyAsync(x => x.CompanyId == company && x.EngagementId == id, token))
                    throw Conflict("Retain linked evidence before submitting work.");
                row.State = GovernanceWorkState.Review;
                row.SubmittedBy = actor;
                row.Response = reason;
            }
            else if (action == "return" && row.State == GovernanceWorkState.Review && row.SubmittedBy != actor)
                row.State = GovernanceWorkState.InProgress;
            else if (action == "close" && row.State == GovernanceWorkState.Review && row.OwnerId != actor && row.SubmittedBy != actor)
            {
                if (row.Kind == GovernanceWorkKind.Engagement && await db.Set<ControlEvaluation>().AnyAsync(x => x.CompanyId == company &&
                        x.EngagementId == id && x.ReviewerId == null, token))
                    throw Conflict("Review all linked audit procedures before closure.");
                if (row.Kind == GovernanceWorkKind.Finding && row.EvaluationId is { } failed && row.ControlId is { } control &&
                    await db.Set<InternalControl>().AnyAsync(x => x.CompanyId == company && x.Id == control && x.Check != ControlCheck.Manual, token))
                {
                    var initial = await db.Set<ControlEvaluation>().AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == failed, token);
                    if (!await db.Set<ControlEvaluation>().AnyAsync(x => x.CompanyId == company && x.ControlId == control && x.From <= initial.From &&
                        x.Cutoff >= initial.Cutoff && x.At > initial.At && x.Conclusion == ControlOutcome.Pass &&
                        x.ReviewerId != null && x.ReviewerId != row.SubmittedBy, token))
                        throw Conflict("Independently validate remediation with a passing retest that covers the original period.");
                }
                if (row.Kind == GovernanceWorkKind.AccessReview)
                {
                    var details = JsonSerializer.Deserialize<WorkEvidence>(row.DetailsJson)!;
                    var retainedGrant = details.Grant ?? throw Conflict("The review lacks a retained access assignment.");
                    var grant = await db.Set<GovernanceGrant>().SingleAsync(x => x.CompanyId == company && x.Id == retainedGrant.Id, token);
                    if (actor == grant.UserId) throw Conflict("Privileged users cannot certify their own access.");
                    if (input.RevokeGrant)
                    {
                        await admin.GrantAction(actor, org, company, grant.Id, "revoke", token);
                    }
                    else if (grant.ApprovedAt != retainedGrant.ApprovedAt || grant.RevokedAt != retainedGrant.RevokedAt ||
                        grant.ExpiresAt <= gate.Now || grant.RevokedAt.HasValue || !grant.ApprovedAt.HasValue)
                        throw Conflict("The grant changed or ended. Retake the access inventory.");
                }
                else if (input.RevokeGrant) throw Bad("Only access certification may revoke a scoped grant.");
                row.State = GovernanceWorkState.Closed;
                row.ClosedBy = actor;
            }
            else throw Conflict("The work transition is unavailable or requires independent review.");
            row.Version = Guid.NewGuid();
            await audit.Append(scope, actor, new("audit", "Work:" + action, id, "Work lifecycle evidence retained", Related: row.EvaluationId,
                Prior: old.ToString(), Next: row.State.ToString()), token);
            return row;
        }, ct);

    public Task<EvidenceReference> Evidence(Guid actor, Guid org, Guid company, EvidenceInput input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await controls.Auditor(actor, org, company, true, token);
            if (input.WorkId.HasValue == input.EvaluationId.HasValue ||
                input.RetainThrough < DateOnly.FromDateTime(gate.Now.UtcDateTime))
                throw Bad("Name one open work item or test and a future retention date.");
            if (input.WorkId is { } work && !await db.Set<GovernanceWork>().AnyAsync(x => x.CompanyId == company && x.Id == work &&
                x.State != GovernanceWorkState.Closed, token)) throw Missing();
            if (input.EvaluationId is { } evaluation && !await db.Set<ControlEvaluation>().AnyAsync(x => x.CompanyId == company &&
                x.Id == evaluation && x.ReviewerId == null, token)) throw Missing();
            string hash;
            if (input.SourceType == "PostedJournal" && input.SourceId is { } journalId)
            {
                var journal = await db.Journals.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == company &&
                    x.Id == journalId && x.State == JournalState.Posted, token) ?? throw Missing();
                hash = TradeRules.Hash(new { journal.Id, journal.PayloadHash, Lines = journal.Lines.OrderBy(x => x.Number).ToArray() });
            }
            else if (input.SourceType == "External" && input.SourceId is null && input.Sha256 is { Length: 64 } &&
                     input.Sha256.All(Uri.IsHexDigit)) hash = input.Sha256.ToUpperInvariant();
            else throw Bad("Evidence must name a scoped posted journal or a supplied external SHA-256 digest.");
            var row = Scoped(scope, new EvidenceReference { WorkId = input.WorkId, EvaluationId = input.EvaluationId, SourceId = input.SourceId,
                SourceType = input.SourceType, OwnerId = actor, Name = AccountingRules.Text(input.Name, 160, "Evidence name"),
                Provenance = AccountingRules.Text(input.Provenance, 1000, "Evidence provenance"),
                LegalHold = input.LegalHold, RetainThrough = input.RetainThrough, Sha256 = hash, RecordedAt = AccountingRules.StoredUtcInstant(gate.Now) });
            db.Add(row);
            await audit.Append(scope, actor, new("audit", "EvidenceRegistered", row.Id, "Evidence metadata and digest retained",
                Journal: input.SourceType == "PostedJournal" ? input.SourceId : null, Related: input.WorkId ?? input.EvaluationId), token);
            return row;
        }, ct);
}
