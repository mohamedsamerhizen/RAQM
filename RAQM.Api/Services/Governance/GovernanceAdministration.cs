using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Governance;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Services.AuditEvidence;
using static RAQM.Api.Helpers.Governance.GovernanceRules;

namespace RAQM.Api.Services.Governance;

public sealed class GovernanceAdministration(RaqmDbContext db, IFinancialTransaction transactions, FinancialAccess access,
    GovernanceGate gate, GovernanceAudit audit)
{
    public Task<GovernancePolicy> Prepare(Guid actor, Guid org, Guid company, PolicyInput input, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            Validate(input.Duty, input.Rules);
            if (!Enum.IsDefined(input.Mode) || input.EffectiveAt < gate.Now.AddMinutes(-1) || input.EffectiveAt > gate.Now.AddYears(1))
                throw Bad("Use a prospective activation within one year.");
            if (!await gate.CanWrite(scope, input.OwnerId, token)) throw Missing();
            foreach (var reviewer in input.Rules.Bands.SelectMany(x => x.RequiredReviewers ?? []).Distinct())
                if (!await gate.CanWrite(scope, reviewer, token)) throw Bad("Named reviewers must have current company financial access.");
            var last = await db.Set<GovernancePolicy>().AsNoTracking().Where(x => x.CompanyId == company && x.Duty == input.Duty)
                .MaxAsync(x => (int?)x.Version, token) ?? 0;
            var row = Scoped(scope, new GovernancePolicy { Duty = input.Duty, Mode = input.Mode, OwnerId = input.OwnerId,
                Code = AccountingRules.Text(input.Code, 40, "Policy code"), Description = AccountingRules.Text(input.Description, 1000, "Policy description"),
                EffectiveAt = AccountingRules.StoredUtcInstant(input.EffectiveAt), Version = last + 1,
                RulesJson = JsonSerializer.Serialize(input.Rules), CreatedBy = actor });
            db.Add(row);
            await audit.Append(scope, actor, new("governance", "PolicyPrepared", row.Id, "Policy version prepared", Duty: row.Duty, Policy: row.Id), token);
            return row;
        }, ct);

    public Task<GovernancePolicy> Approve(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var row = await db.Set<GovernancePolicy>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing();
            if (row.ApprovedBy == actor) return row;
            if (row.ApprovedBy.HasValue || row.CreatedBy == actor) throw Conflict("An independent owner must approve this policy.");
            var impact = await Impact(company, row, token);
            if (impact.ConflictingUsers.Length != 0 && row.Mode == PolicyMode.Enforce)
                throw Conflict("Resolve incompatible explicit duty grants before activation.");
            if (await db.Set<GovernancePolicy>().AnyAsync(x => x.CompanyId == company && x.Duty == row.Duty && x.ApprovedAt != null &&
                (x.Version >= row.Version || x.EffectiveAt >= row.EffectiveAt), token))
                throw Conflict("The new policy must supersede prior approved versions prospectively.");
            row.ApprovedBy = actor;
            row.ApprovedAt = AccountingRules.StoredUtcInstant(gate.Now);
            await audit.Append(scope, actor, new("governance", "PolicyApproved", row.Id, "Independent approval recorded", Duty: row.Duty,
                Policy: row.Id, Prior: "Draft", Next: row.Mode.ToString()), token);
            return row;
        }, ct);

    private async Task<(Guid[] ConflictingUsers, int OpenApprovals)> Impact(Guid company, GovernancePolicy row, CancellationToken ct)
    {
        var grants = await db.Set<GovernanceGrant>().AsNoTracking().Where(x => x.CompanyId == company && x.Duty == row.Duty &&
            x.ApprovedAt != null && x.RevokedAt == null && x.ExpiresAt > row.EffectiveAt).ToListAsync(ct);
        var conflicts = Parse(row).SeparateAssignments ? grants.GroupBy(x => x.UserId).Where(g =>
            g.Any(x => x.Permission == GovernancePermission.Execute && g.Any(y => y.Permission == GovernancePermission.Review &&
                x.StartsAt < y.ExpiresAt && y.StartsAt < x.ExpiresAt))).Select(x => x.Key).Order().ToArray() : [];
        var open = await db.Set<GovernanceAuthorization>().CountAsync(x => x.CompanyId == company && x.Duty == row.Duty &&
            x.State == AuthorizationState.Open && x.ExpiresAt > gate.Now, ct);
        return (conflicts, open);
    }

    public async Task<object> PolicyImpact(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, true, ct);
        var row = await db.Set<GovernancePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw Missing();
        var impact = await Impact(company, row, ct);
        return new { impact.ConflictingUsers, impact.OpenApprovals, row.EffectiveAt, row.Version };
    }

    public Task<GovernanceGrant> RequestGrant(Guid actor, Guid org, Guid company, GrantInput input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            if (actor != input.UserId) await access.Resolve(actor, org, company, true, true, token);
            if (!Enum.IsDefined(input.Permission) || (input.Permission == GovernancePermission.Audit) != (input.Duty == FinancialDuty.AuditWorkspace) ||
                input.Permission != GovernancePermission.Audit && !Supported(input.Duty) ||
                input.Emergency && (input.Permission != GovernancePermission.Execute ||
                    input.Duty is FinancialDuty.PeriodClose or FinancialDuty.ConsolidationPublication or FinancialDuty.TaxProvisionApproval) ||
                input.ExpiresAt <= input.StartsAt || input.ExpiresAt <= gate.Now ||
                input.ExpiresAt - input.StartsAt > (input.Emergency ? TimeSpan.FromHours(4) : TimeSpan.FromDays(366)))
                throw Bad("Use a bounded eligible permission and validity window.");
            await access.Resolve(input.UserId, org, company, input.Permission != GovernancePermission.Audit, false, token);
            var row = Scoped(scope, new GovernanceGrant { UserId = input.UserId, RequestedBy = actor, Duty = input.Duty,
                Permission = input.Permission, StartsAt = AccountingRules.StoredUtcInstant(input.StartsAt),
                ExpiresAt = AccountingRules.StoredUtcInstant(input.ExpiresAt), Emergency = input.Emergency,
                Reason = AccountingRules.Text(input.Reason, 500, "Access justification") });
            db.Add(row);
            await audit.Append(scope, actor, new("access", "GrantRequested", row.Id, "Scoped access requested", Duty: row.Duty, Subject: row.UserId), token);
            return row;
        }, ct);

    public Task<GovernanceGrant> GrantAction(Guid actor, Guid org, Guid company, Guid id, string action, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var row = await db.Set<GovernanceGrant>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing();
            if (action == "approve" && row.ApprovedAt == null && row.RevokedAt == null)
            {
                if (actor == row.RequestedBy || actor == row.UserId || row.ExpiresAt <= gate.Now)
                    throw Conflict("A separate eligible owner must approve an unexpired grant.");
                await access.Resolve(row.UserId, org, company, row.Permission != GovernancePermission.Audit, false, token);
                var policies = await db.Set<GovernancePolicy>().AsNoTracking().Where(x => x.CompanyId == company && x.Duty == row.Duty &&
                    x.ApprovedAt != null && x.EffectiveAt < row.ExpiresAt).ToListAsync(token);
                var now = gate.Now;
                var existing = await db.Set<GovernanceGrant>().AsNoTracking().Where(x => x.CompanyId == company && x.Duty == row.Duty &&
                    x.UserId == row.UserId && x.ApprovedAt != null && x.RevokedAt == null && x.StartsAt < row.ExpiresAt &&
                    row.StartsAt < x.ExpiresAt && x.ExpiresAt > now).ToListAsync(token);
                if (existing.Any(x => x.Permission == row.Permission) || existing.Any(x =>
                    SeparationOverlaps(policies, new[] { x.StartsAt, row.StartsAt, now }.Max(),
                        x.ExpiresAt < row.ExpiresAt ? x.ExpiresAt : row.ExpiresAt)))
                    throw Conflict("The grant overlaps a duplicate or incompatible duty.");
                row.ApprovedBy = actor;
                row.ApprovedAt = AccountingRules.StoredUtcInstant(gate.Now);
            }
            else if (action == "revoke" && row.RevokedAt == null)
            {
                row.RevokedAt = AccountingRules.StoredUtcInstant(gate.Now);
                row.RevokedBy = actor;
            }
            else if (action == "review" && row.Emergency && row.ReviewedBy == null && actor != row.UserId && actor != row.ApprovedBy)
            {
                if (row.RevokedAt == null && row.ExpiresAt > gate.Now ||
                    !await db.Set<GovernanceEvent>().AnyAsync(x => x.CompanyId == company && x.RelatedId == id && x.Type == "EmergencyGrantUsed", token))
                    throw Conflict("Review emergency usage after the grant has ended.");
                row.ReviewedBy = actor;
            }
            else throw Conflict("This access transition is unavailable.");
            await audit.Append(scope, actor, new("access", "Grant:" + action, row.Id, "Scoped access transition", Duty: row.Duty, Subject: row.UserId), token);
            return row;
        }, ct);

    public Task<GovernanceDelegation> Delegate(Guid actor, Guid org, Guid company, DelegationInput input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            if (input.DelegateId == actor || !Supported(input.Duty) || input.StartsAt >= input.ExpiresAt || input.ExpiresAt <= gate.Now ||
                input.ExpiresAt - input.StartsAt > TimeSpan.FromDays(30) || !await gate.CanWrite(scope, input.DelegateId, token))
                throw Bad("Choose a different authorized delegate and a validity window of up to 30 days.");
            if (!(await gate.Grants(company, actor, input.Duty, gate.Now, token)).Any(x => x.Permission == GovernancePermission.Review &&
                x.ExpiresAt >= input.ExpiresAt && !x.Emergency)) throw Conflict("The principal requires a direct review grant covering the delegation.");
            var row = Scoped(scope, new GovernanceDelegation { PrincipalId = actor, DelegateId = input.DelegateId, Duty = input.Duty,
                StartsAt = AccountingRules.StoredUtcInstant(input.StartsAt), ExpiresAt = AccountingRules.StoredUtcInstant(input.ExpiresAt),
                Reason = AccountingRules.Text(input.Reason, 500, "Delegation evidence") });
            db.Add(row);
            await audit.Append(scope, actor, new("access", "DelegationRequested", row.Id, "Scoped delegation requested", Duty: row.Duty, Subject: row.DelegateId), token);
            return row;
        }, ct);

    public Task<GovernanceDelegation> DelegationAction(Guid actor, Guid org, Guid company, Guid id, string action, CancellationToken ct) =>
        Admin(actor, org, company, async (scope, token) =>
        {
            var row = await db.Set<GovernanceDelegation>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing();
            if (action == "approve" && row.ApprovedAt == null && row.RevokedAt == null && row.ExpiresAt > gate.Now &&
                actor != row.PrincipalId && actor != row.DelegateId)
            {
                if (!await gate.CanWrite(scope, row.DelegateId, token) || !(await gate.Grants(company, row.PrincipalId, row.Duty, gate.Now, token))
                    .Any(x => x.Permission == GovernancePermission.Review && !x.Emergency && x.ExpiresAt >= row.ExpiresAt))
                    throw Conflict("The principal and delegate must retain their independent direct access.");
                row.ApprovedBy = actor;
                row.ApprovedAt = AccountingRules.StoredUtcInstant(gate.Now);
            }
            else if (action == "revoke" && row.RevokedAt == null) row.RevokedAt = AccountingRules.StoredUtcInstant(gate.Now);
            else throw Conflict("This delegation transition is unavailable.");
            await audit.Append(scope, actor, new("access", "Delegation:" + action, row.Id, "Scoped delegation transition", Duty: row.Duty, Subject: row.DelegateId), token);
            return row;
        }, ct);

    public async Task<object> Effective(Guid actor, Guid org, Guid company, Guid? user, FinancialDuty duty, CancellationToken ct)
    {
        var scope = await access.Resolve(actor, org, company, false, user.HasValue && user != actor, ct);
        var target = user ?? actor;
        return new { User = target, Duty = duty, HasFinancialWrite = await gate.CanWrite(scope, target, ct),
            Policy = await gate.Current(company, duty, gate.Now, ct), ActiveGrants = await gate.Grants(company, target, duty, gate.Now, ct) };
    }

    internal Task<T> Admin<T>(Guid actor, Guid org, Guid company, Func<Company, CancellationToken, Task<T>> action, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            return await action(scope, token);
        }, ct);

    internal static T Scoped<T>(Company company, T row) where T : GovernanceRecord
    {
        row.TenantId = company.TenantId;
        row.OrganizationId = company.OrganizationId;
        row.CompanyId = company.Id;
        return row;
    }
}
