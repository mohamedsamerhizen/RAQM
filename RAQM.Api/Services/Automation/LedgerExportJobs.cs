using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Integrations;
using RAQM.Api.Entities.Integrations;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.ApiAccess;
using RAQM.Api.Services.EventProcessing;
using RAQM.Api.Services.FinancialReporting;
using RAQM.Api.Services.Integrations;
using static RAQM.Api.Services.Integrations.IntegrationAdministration;

namespace RAQM.Api.Services.Automation;

public sealed class LedgerExportJobs(RaqmDbContext db, IntegrationAdministration admin, FinancialLedger ledger, FinancialAccess access, TimeProvider clock)
{
    private static readonly Histogram<double> JobDuration = DeliveryDispatcher.Metrics.CreateHistogram<double>("raqm.export.duration", "ms");
    public Task<ExportSchedule> Prepare(Guid actor, Guid org, Guid company, Guid application, IntegrationExportScheduleRequest input, CancellationToken ct) =>
        admin.Admin(actor, org, company, async (scope, token) =>
        {
            var app = await admin.Application(company, application, token);
            var start = input.StartsAt.ToUniversalTime();
            if (!app.Scopes.Contains("reports.read") || app.State != ApplicationState.Active || input.IntervalHours is < 1 or > 720 ||
                input.LookbackDays is < 1 or > 90 || start < clock.GetUtcNow().AddMinutes(-5) || start > clock.GetUtcNow().AddDays(90))
                throw Invalid("Choose an active reporting application and a bounded UTC interval and start.");
            var row = Scoped(scope, new ExportSchedule { ApplicationId = application, CreatedBy = actor,
                IntervalHours = input.IntervalHours, LookbackDays = input.LookbackDays, NextRun = start });
            db.Add(row);
            await admin.Audit(scope, actor, row.Id, "LedgerExportPrepared", token);
            return row;
        }, ct);

    public Task<ExportSchedule> Change(Guid actor, Guid org, Guid company, Guid id, string action, CancellationToken ct) =>
        admin.Admin(actor, org, company, async (scope, token) =>
        {
            var row = await db.Set<ExportSchedule>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing();
            if (action == "enable" && actor != row.CreatedBy && (await admin.Application(company, row.ApplicationId, token)).State == ApplicationState.Active)
            {
                row.Enabled = true;
                row.ApprovedBy = actor;
            }
            else if (action == "disable") row.Enabled = false;
            else throw Conflict("Independent enablement or disablement is required.");
            await admin.Audit(scope, actor, id, "LedgerExport:" + action, token);
            return row;
        }, ct);

    public Task<Guid> Manual(Guid actor, Guid org, Guid company, Guid id, DateTimeOffset slot, CancellationToken ct) =>
        admin.Admin(actor, org, company, async (scope, token) =>
        {
            var schedule = await db.Set<ExportSchedule>().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing();
            var utc = slot.ToUniversalTime();
            if (!schedule.Enabled || !schedule.ApprovedBy.HasValue || utc > clock.GetUtcNow() || utc < clock.GetUtcNow().AddDays(-90) ||
                (await admin.Application(company, schedule.ApplicationId, token)).State != ApplicationState.Active)
                throw Conflict("Use an approved active schedule and an elapsed UTC slot.");
            // Match the database timestamp resolution before checking the durable slot key.
            utc = RAQM.Api.Helpers.Accounting.AccountingRules.StoredUtcInstant(utc);
            var existing = await db.Set<ExportOccurrence>().Where(x => x.CompanyId == company && x.ScheduleId == id && x.ScheduledAt == utc)
                .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
            if (existing.HasValue) return existing.Value;
            var row = Scoped(scope, new ExportOccurrence { ScheduleId = id, ScheduledAt = utc, DueAt = clock.GetUtcNow() });
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO integration."Occurrences" ("Id","CompanyId","TenantId","OrganizationId","ScheduleId","ScheduledAt","DueAt","State","Attempts")
                VALUES ({row.Id},{company},{scope.TenantId},{org},{id},{utc},{row.DueAt},0,0)
                ON CONFLICT ("ScheduleId","ScheduledAt") DO NOTHING
                """, token);
            if (inserted == 0)
                return await db.Set<ExportOccurrence>().Where(x => x.CompanyId == company && x.ScheduleId == id && x.ScheduledAt == utc)
                    .Select(x => x.Id).SingleAsync(token);
            await admin.Audit(scope, actor, row.Id, "LedgerExportQueued", token);
            return row.Id;
        }, ct);

    public Task<bool> Recover(Guid actor, Guid org, Guid company, Guid id, bool cancel, CancellationToken ct) =>
        admin.Admin(actor, org, company, async (scope, token) =>
        {
            var now = clock.GetUtcNow();
            var state = cancel ? DeliveryState.Cancelled : DeliveryState.Queued;
            var changed = await db.Set<ExportOccurrence>().Where(x => x.CompanyId == company && x.Id == id &&
                (cancel ? x.State == DeliveryState.Queued || x.State == DeliveryState.Retry || x.State == DeliveryState.InProgress : x.State == DeliveryState.DeadLetter))
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.State, state).SetProperty(y => y.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(y => y.LeaseToken, (Guid?)null).SetProperty(y => y.DueAt, now)
                    .SetProperty(y => y.Attempts, y => cancel ? y.Attempts : 0), token);
            if (changed == 0) throw Conflict("This occurrence cannot change state.");
            await admin.Audit(scope, actor, id, cancel ? "LedgerExportCancelled" : "LedgerExportRequeued", token);
            return true;
        }, ct);

    public async Task<object> Status(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, true, ct);
        return new { Schedules = await db.Set<ExportSchedule>().AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Id).Take(100).ToArrayAsync(ct),
            Occurrences = await db.Set<ExportOccurrence>().Where(x => x.CompanyId == company).OrderByDescending(x => x.ScheduledAt).Take(100)
                .Select(x => new { x.Id, x.ScheduleId, x.ScheduledAt, x.State, x.Attempts, x.Failure, x.ExpiresAt, x.Digest }).ToArrayAsync(ct) };
    }

    public async Task<string> Retrieve(MachineIdentity actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await actor.Require(db, org, company, "reports.read", now, ct);
        return await (from o in db.Set<ExportOccurrence>() join s in db.Set<ExportSchedule>() on o.ScheduleId equals s.Id
            where o.CompanyId == company && s.CompanyId == company && s.ApplicationId == actor.Application && s.Enabled && o.Id == id &&
                o.State == DeliveryState.Delivered && o.ExpiresAt > now select o.Csv).SingleOrDefaultAsync(ct) ?? throw Missing();
    }

    public Task<int> Materialize(CancellationToken ct) => db.Database.ExecuteSqlRawAsync("""
        WITH picked AS (
           SELECT s.* FROM integration."Schedules" s JOIN integration."Applications" a ON a."Id"=s."ApplicationId" AND a."CompanyId"=s."CompanyId"
           WHERE s."Enabled" AND s."ApprovedBy" IS NOT NULL AND s."NextRun"<=clock_timestamp() AND a."State"=1
             AND saas.can_operate_v1(a."TenantId")
           ORDER BY s."NextRun",s."Id" FOR UPDATE OF s SKIP LOCKED LIMIT 10
        ), advanced AS (
           UPDATE integration."Schedules" s SET "NextRun"=p."NextRun"+make_interval(hours=>p."IntervalHours") *
             (floor(extract(epoch FROM (clock_timestamp()-p."NextRun"))/(p."IntervalHours"*3600))+1)
           FROM picked p WHERE s."Id"=p."Id" RETURNING p.*
        )
        INSERT INTO integration."Occurrences" ("Id","TenantId","OrganizationId","CompanyId","ScheduleId","ScheduledAt","DueAt","State","Attempts")
        SELECT gen_random_uuid(),"TenantId","OrganizationId","CompanyId","Id","NextRun",clock_timestamp(),0,0 FROM advanced
        ON CONFLICT ("ScheduleId","ScheduledAt") DO NOTHING
        """, ct);

    public async Task<bool> Run(CancellationToken ct)
    {
        const int limit = 5;
        var token = Guid.NewGuid();
        var ids = await db.Database.SqlQuery<Guid>($"""
            WITH selected AS (
                SELECT o."Id" FROM integration."Occurrences" o JOIN integration."Schedules" s ON s."Id"=o."ScheduleId" AND s."CompanyId"=o."CompanyId"
                JOIN integration."Applications" a ON a."Id"=s."ApplicationId" AND a."CompanyId"=s."CompanyId"
                WHERE s."Enabled" AND a."State"=1 AND saas.can_operate_v1(a."TenantId") AND
                 (o."State" IN(0,3) AND o."DueAt"<=clock_timestamp() OR o."State"=1 AND o."LeaseUntil"<=clock_timestamp())
                ORDER BY o."DueAt",o."Id" FOR UPDATE OF o SKIP LOCKED LIMIT 1
            )
            UPDATE integration."Occurrences" o SET "State"=CASE WHEN o."Attempts">={limit} THEN 4 ELSE 1 END,
              "Attempts"=CASE WHEN o."Attempts">={limit} THEN o."Attempts" ELSE o."Attempts"+1 END,
              "LeaseToken"=CASE WHEN o."Attempts">={limit} THEN NULL ELSE {token} END,
              "LeaseUntil"=CASE WHEN o."Attempts">={limit} THEN NULL ELSE clock_timestamp()+interval '60 seconds' END,
              "Failure"=CASE WHEN o."Attempts">={limit} THEN 'lease-exhausted' ELSE o."Failure" END
            FROM selected WHERE selected."Id"=o."Id" RETURNING o."Id" AS "Value"
            """).ToListAsync(ct);
        if (ids.Count == 0) return false;
        var row = await db.Set<ExportOccurrence>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == ids[0] && x.LeaseToken == token, ct);
        if (row is null) return true;
        using var activity = DeliveryDispatcher.Traces.StartActivity("ledger.export");
        activity?.SetTag("raqm.occurrence.id", row.Id);
        var timer = Stopwatch.StartNew();
        string? csv = null, failure = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await using var read = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, timeout.Token);
            var schedule = await db.Set<ExportSchedule>().AsNoTracking().SingleAsync(x => x.CompanyId == row.CompanyId && x.Id == row.ScheduleId, timeout.Token);
            var cutoff = DateOnly.FromDateTime(row.ScheduledAt.UtcDateTime).AddDays(-1);
            var start = cutoff.AddDays(1 - schedule.LookbackDays);
            var lines = await ledger.Lines(row.CompanyId, cutoff, start).Take(10001).ToArrayAsync(timeout.Token);
            if (lines.Length > 10000) failure = "row-limit";
            else
            {
                var output = new StringBuilder("date,journal_id,account_id,line_id,debit,credit\r\n");
                foreach (var line in lines)
                    output.Append(CultureInfo.InvariantCulture, $"{line.Date:yyyy-MM-dd},{line.JournalId},{line.AccountId},{line.LineId},{line.Debit},{line.Credit}\r\n");
                csv = output.ToString();
            }
            await read.CommitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = "timeout"; }
        catch (Npgsql.NpgsqlException) { failure = "database"; }
        var digest = csv is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));
        var state = csv is not null ? DeliveryState.Delivered : row.Attempts >= limit || failure == "row-limit" ? DeliveryState.DeadLetter : DeliveryState.Retry;
        await using var completion = await db.Database.BeginTransactionAsync(ct);
        _ = await db.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM organizations."Tenants" WHERE "Id"={row.TenantId} FOR UPDATE""").SingleAsync(ct);
        var eligible = await db.Database.SqlQuery<Guid>($"""
            SELECT s."Id" AS "Value" FROM integration."Schedules" s
            JOIN integration."Applications" a ON a."Id"=s."ApplicationId" AND a."CompanyId"=s."CompanyId"
            WHERE s."Id"={row.ScheduleId} AND s."CompanyId"={row.CompanyId} AND s."Enabled" AND
                s."ApprovedBy" IS NOT NULL AND a."State"={(int)ApplicationState.Active} AND
                a."Scopes" @> ARRAY['reports.read']::text[] AND saas.can_operate_v1(a."TenantId")
            FOR SHARE OF s,a
            """).AnyAsync(ct);
        var finished = 0;
        if (eligible)
        {
            var delay = DeliveryDispatcher.Delay(row.Attempts);
            finished = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE integration."Occurrences" SET "State"={(int)state}, "Csv"={csv}, "Digest"={digest},
                    "ExpiresAt"=CASE WHEN {csv}::text IS NULL THEN NULL ELSE clock_timestamp()+interval '7 days' END,
                    "Failure"=COALESCE({failure}::text,"Failure"), "LeaseToken"=NULL, "LeaseUntil"=NULL,
                    "DueAt"=clock_timestamp()+make_interval(secs=>{delay})
                WHERE "Id"={row.Id} AND "CompanyId"={row.CompanyId} AND "State"={(int)DeliveryState.InProgress}
                    AND "LeaseToken"={token} AND "LeaseUntil">clock_timestamp()
                """, ct);
        }
        await completion.CommitAsync(ct);
        JobDuration.Record(timer.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", finished == 0 ? "lease-lost" : state.ToString()));
        return true;
    }

    public Task<int> Purge(CancellationToken ct) => db.Database.ExecuteSqlRawAsync("""
        UPDATE integration."Occurrences" SET "Csv"=NULL WHERE "Id" IN
        (SELECT "Id" FROM integration."Occurrences" WHERE "ExpiresAt"<=clock_timestamp() AND "Csv" IS NOT NULL LIMIT 100)
        """, ct);
}
