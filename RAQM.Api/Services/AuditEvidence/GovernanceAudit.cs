using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;

namespace RAQM.Api.Services.AuditEvidence;

public sealed record AuditInput(string Category, string Type, Guid Source, string Reason, GovernanceOutcome Outcome = GovernanceOutcome.Recorded,
    FinancialDuty? Duty = null, Guid? Subject = null, Guid? Journal = null, Guid? Policy = null, Guid? Authorization = null,
    Guid? Related = null, string? Prior = null, string? Next = null);

public sealed record AuditVerification(int Count, long? FirstFailure, string LastHash)
{
    public bool Intact => FirstFailure is null;
}

public sealed class GovernanceAudit(RaqmDbContext db, TimeProvider clock)
{
    public static readonly string Genesis = new('0', 64);

    public async Task RecordDenial(Company scope, Guid actor, AuditInput input, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddMinutes(-1);
        if (await db.Set<GovernanceEvent>().AnyAsync(x => x.CompanyId == scope.Id && x.ActorId == actor &&
            x.SourceId == input.Source && x.Type == "GovernanceDenied" && x.Reason == input.Reason && x.RecordedAt >= since, ct))
            return;
        await Append(scope, actor, input, ct);
    }

    public async Task<GovernanceEvent> Append(Company scope, Guid actor, AuditInput input, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Audit writes require the company financial transaction.");
        var previous = db.Set<GovernanceEvent>().Local.Where(x => x.CompanyId == scope.Id).MaxBy(x => x.Sequence) ??
            await db.Set<GovernanceEvent>().AsNoTracking().Where(x => x.CompanyId == scope.Id)
                .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        var now = AccountingRules.StoredUtcInstant(clock.GetUtcNow());
        var row = new GovernanceEvent { TenantId = scope.TenantId, OrganizationId = scope.OrganizationId, CompanyId = scope.Id,
            Sequence = (previous?.Sequence ?? 0) + 1, PreviousHash = previous?.Hash ?? Genesis, ActorId = actor, Category = input.Category,
            Type = input.Type, Duty = input.Duty, SourceId = input.Source, SubjectId = input.Subject, JournalId = input.Journal,
            PolicyId = input.Policy, AuthorizationId = input.Authorization, RelatedId = input.Related, PriorState = input.Prior,
            NewState = input.Next, Correlation = Activity.Current?.TraceId.ToHexString() ?? "", EffectiveAt = now, RecordedAt = now,
            Outcome = input.Outcome, Reason = input.Reason };
        row.Hash = Hash(row);
        db.Add(row);
        return row;
    }

    public static string Hash(GovernanceEvent e) => TradeRules.Hash(new
    {
        Schema = "raqm.governance.audit.v1", e.Id, e.TenantId, e.OrganizationId, e.CompanyId, e.Sequence, e.ActorId, e.Category, e.Type,
        e.Duty, e.SourceId, e.SubjectId, e.JournalId, e.PolicyId, e.AuthorizationId, e.RelatedId, e.PriorState, e.NewState, e.Correlation,
        EffectiveUtcTicks = e.EffectiveAt.UtcTicks, RecordedUtcTicks = e.RecordedAt.UtcTicks, e.Outcome, e.Reason, e.PreviousHash
    });

    public static AuditVerification Verify(IEnumerable<GovernanceEvent> rows, long first = 1, string? anchor = null)
    {
        var previous = anchor ?? Genesis;
        var expected = first;
        var count = 0;
        (Guid Tenant, Guid Organization, Guid Company)? scope = null;
        foreach (var row in rows)
        {
            scope ??= (row.TenantId, row.OrganizationId, row.CompanyId);
            if (scope != (row.TenantId, row.OrganizationId, row.CompanyId) || row.Sequence != expected || row.PreviousHash != previous || row.Hash != Hash(row))
                return new(count, expected, previous);
            previous = row.Hash;
            expected++;
            count++;
        }
        return new(count, null, previous);
    }
}
