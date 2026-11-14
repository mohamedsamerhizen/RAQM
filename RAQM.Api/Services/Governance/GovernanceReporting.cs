using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Entities.InternalControls;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Services.AuditEvidence;
using RAQM.Api.Services.InternalControls;
using static RAQM.Api.Helpers.Governance.GovernanceRules;

namespace RAQM.Api.Services.Governance;

public sealed class GovernanceReporting(RaqmDbContext db, ControlService controls, GovernanceGate gate)
{
    public async Task<object> Overview(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await controls.Auditor(actor, org, company, false, ct);
        var now = gate.Now;
        var date = DateOnly.FromDateTime(now.UtcDateTime);
        var policies = await db.Set<GovernancePolicy>().AsNoTracking().Where(x => x.CompanyId == company && x.ApprovedAt <= now &&
            x.EffectiveAt <= now).ToListAsync(ct);
        var effective = policies.GroupBy(x => x.Duty).Select(x => x.OrderByDescending(y => y.EffectiveAt).ThenByDescending(y => y.Version).First()).ToArray();
        var grants = await db.Set<GovernanceGrant>().AsNoTracking().Where(x => x.CompanyId == company && x.ApprovedAt <= now &&
            x.RevokedAt == null && x.StartsAt <= now && x.ExpiresAt > now).ToListAsync(ct);
        var conflicts = grants.GroupBy(x => (x.UserId, x.Duty)).Where(x =>
            x.Any(y => y.Permission == GovernancePermission.Execute) && x.Any(y => y.Permission == GovernancePermission.Review))
            .Select(x => new { x.Key.UserId, x.Key.Duty }).ToArray();
        var work = await db.Set<GovernanceWork>().AsNoTracking().Where(x => x.CompanyId == company && x.State != GovernanceWorkState.Closed)
            .GroupBy(x => x.Kind).Select(x => new { Kind = x.Key, Count = x.Count(), Overdue = x.Count(y => y.DueOn < date) }).ToListAsync(ct);
        var result = new
        {
            EffectivePolicies = effective,
            PendingAuthorizations = await db.Set<GovernanceAuthorization>().CountAsync(x => x.CompanyId == company &&
                x.State == AuthorizationState.Open && x.ExpiresAt > now, ct),
            ActiveGrants = grants,
            OverlappingDuties = conflicts,
            ExpiredDelegations = await db.Set<GovernanceDelegation>().CountAsync(x => x.CompanyId == company && x.ApprovedAt != null && x.ExpiresAt <= now, ct),
            EmergencyUses = await db.Set<GovernanceEvent>().CountAsync(x => x.CompanyId == company && x.Type == "EmergencyGrantUsed", ct),
            UnreviewedEmergencyUses = await (from e in db.Set<GovernanceEvent>() join grant in db.Set<GovernanceGrant>() on e.RelatedId equals (Guid?)grant.Id
                where e.CompanyId == company && grant.CompanyId == company && e.Type == "EmergencyGrantUsed" && grant.ReviewedBy == null &&
                    (grant.ExpiresAt <= now || grant.RevokedAt != null) select e.Id).CountAsync(ct),
            Denied = await db.Set<GovernanceEvent>().CountAsync(x => x.CompanyId == company && x.Outcome == GovernanceOutcome.Denied, ct),
            ExceptionsUsed = await db.Set<GovernanceException>().CountAsync(x => x.CompanyId == company && x.UsedAt != null, ct),
            Work = work,
            OpenMaterialRisks = await db.Set<GovernanceWork>().CountAsync(x => x.CompanyId == company && x.Kind == GovernanceWorkKind.Risk &&
                x.Severity >= 16 && x.State != GovernanceWorkState.Closed, ct)
        };
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<T>> List<T>(Guid actor, Guid org, Guid company, int page, int size, CancellationToken ct)
        where T : GovernanceRecord
    {
        AccountingRules.Page(page, size);
        await controls.Auditor(actor, org, company, false, ct);
        return await db.Set<T>().AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
    }

    public async Task<object> History(Guid actor, Guid org, Guid company, DateTimeOffset from, DateTimeOffset to,
        GovernanceOutcome? outcome, int page, int size, CancellationToken ct)
    {
        if (from > to || to - from > TimeSpan.FromDays(366) || outcome.HasValue && !Enum.IsDefined(outcome.Value))
            throw Bad("Choose an ordered audit window up to one year.");
        AccountingRules.Page(page, size);
        await controls.Auditor(actor, org, company, false, ct);
        var query = db.Set<GovernanceEvent>().AsNoTracking().Where(x => x.CompanyId == company && x.RecordedAt >= from.ToUniversalTime() &&
            x.RecordedAt <= to.ToUniversalTime() && (outcome == null || x.Outcome == outcome));
        var events = await query.OrderBy(x => x.Sequence).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new { Events = events, Page = page, PageSize = size };
    }

    public async Task<object> Verify(Guid actor, Guid org, Guid company, long first, int size, string? expectedAnchor, CancellationToken ct)
    {
        var (rows, anchor, more) = await Window(actor, org, company, first, size, ct);
        var result = GovernanceAudit.Verify(rows, first, anchor);
        return new { First = first, Anchor = anchor, ExternalAnchorMatched = expectedAnchor is null ? (bool?)null :
            string.Equals(expectedAnchor, anchor, StringComparison.OrdinalIgnoreCase), More = more, Integrity = result };
    }

    public async Task<string> Export(Guid actor, Guid org, Guid company, long first, int size, CancellationToken ct)
    {
        var (rows, _, _) = await Window(actor, org, company, first, size, ct);
        var output = new StringBuilder("sequence,id,recorded_at,actor,category,type,source,policy,authorization,outcome,previous_hash,hash\r\n");
        foreach (var row in rows)
            output.Append(CultureInfo.InvariantCulture, $"{row.Sequence},{row.Id},{row.RecordedAt:O},{row.ActorId},{row.Category},{row.Type},{row.SourceId},{row.PolicyId},{row.AuthorizationId},{row.Outcome},{row.PreviousHash},{row.Hash}\r\n");
        return output.ToString();
    }

    private async Task<(GovernanceEvent[] Rows, string Anchor, bool More)> Window(Guid actor, Guid org, Guid company, long first, int size, CancellationToken ct)
    {
        if (first < 1 || size is < 1 or > 5000) throw Bad("Use a positive first sequence and up to 5000 events.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await controls.Auditor(actor, org, company, false, ct);
        var query = db.Set<GovernanceEvent>().AsNoTracking().Where(x => x.CompanyId == company);
        var anchor = first == 1 ? GovernanceAudit.Genesis : await query.Where(x => x.Sequence == first - 1)
            .Select(x => x.Hash).SingleOrDefaultAsync(ct) ?? throw Missing();
        var rows = await query.Where(x => x.Sequence >= first).OrderBy(x => x.Sequence).Take(size + 1).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return (rows.Take(size).ToArray(), anchor, rows.Length > size);
    }
}
