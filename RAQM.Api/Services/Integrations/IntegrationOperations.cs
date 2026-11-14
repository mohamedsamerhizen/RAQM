using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.Entities.Integrations;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.EventProcessing;
using static RAQM.Api.Services.Integrations.IntegrationAdministration;

namespace RAQM.Api.Services.Integrations;

public sealed class IntegrationOperations(RaqmDbContext db, FinancialAccess access, IntegrationAdministration admin,
    IntegrationWorkerStatus worker, IConfiguration configuration, TimeProvider clock)
{
    public async Task<object> Status(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, true, ct);
        var now = clock.GetUtcNow();
        var deliveries = db.Set<OutboxDelivery>().Where(x => x.CompanyId == company);
        var oldest = await (from d in deliveries join e in db.Set<OutboxEvent>() on d.EventId equals e.Id
            where e.CompanyId == company && (d.State == DeliveryState.Queued || d.State == DeliveryState.Retry || d.State == DeliveryState.InProgress)
            select (DateTimeOffset?)e.OccurredAt).MinAsync(ct);
        return new
        {
            WorkerEnabledHere = configuration.GetValue<bool>("Integrations:WorkerEnabled"),
            WorkerHeartbeatHere = worker.Last == 0 ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(worker.Last),
            WorkerRecentlyActiveHere = worker.Last != 0 && now.ToUnixTimeSeconds() - worker.Last <= 90,
            OldestPendingSeconds = oldest.HasValue ? Math.Max(0, (now - oldest.Value).TotalSeconds) : (double?)null,
            Deliveries = await deliveries.GroupBy(x => x.State).Select(x => new { State = x.Key, Count = x.Count() }).ToArrayAsync(ct),
            ExpiredLeases = await deliveries.CountAsync(x => x.State == DeliveryState.InProgress && x.LeaseUntil <= now, ct),
            ExportFailures = await db.Set<ExportOccurrence>().CountAsync(x => x.CompanyId == company && x.State == DeliveryState.DeadLetter, ct),
            RecentAttempts = await db.Set<DeliveryAttempt>().AsNoTracking().Where(x => x.CompanyId == company)
                .OrderByDescending(x => x.FinishedAt).Take(100).ToArrayAsync(ct)
        };
    }

    public async Task<object> Events(Guid actor, Guid org, Guid company, int page, int size, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, true, ct);
        RAQM.Api.Helpers.Accounting.AccountingRules.Page(page, size);
        var events = await db.Set<OutboxEvent>().Where(x => x.CompanyId == company).OrderByDescending(x => x.OccurredAt).ThenBy(x => x.Id)
            .Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        var ids = events.Select(x => x.Id).ToArray();
        return new { Events = events, Deliveries = await db.Set<OutboxDelivery>().Where(x => x.CompanyId == company && ids.Contains(x.EventId))
            .OrderBy(x => x.Id).Take(1000).ToArrayAsync(ct) };
    }

    public Task<bool> Replay(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct) =>
        admin.Admin(actor, org, company, async (scope, token) =>
        {
            var now = clock.GetUtcNow();
            var changed = await db.Set<OutboxDelivery>().Where(x => x.CompanyId == company && x.Id == id &&
                (x.State == DeliveryState.DeadLetter || x.State == DeliveryState.Cancelled || x.State == DeliveryState.Delivered))
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.State, DeliveryState.Queued).SetProperty(y => y.Generation, y => y.Generation + 1)
                    .SetProperty(y => y.Attempts, 0).SetProperty(y => y.DueAt, now), token);
            if (changed == 0) throw Conflict("Replay requires a terminal delivery.");
            await admin.Audit(scope, actor, id, "DeliveryReplayed", token);
            return true;
        }, ct);
}
