using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.Entities.Integrations;
using RAQM.Api.Services.Integrations;
using RAQM.Api.Services.Webhooks;
using RAQM.Api.Services.SaaS;

namespace RAQM.Api.Services.EventProcessing;

public sealed class DeliveryDispatcher(RaqmDbContext db, WebhookTransport transport, IDataProtectionProvider protection, TimeProvider clock)
{
    public const int Limit = 6;
    internal static readonly ActivitySource Traces = new("RAQM.Integrations");
    internal static readonly Meter Metrics = new("RAQM.Integrations");
    private static readonly Counter<long> Attempts = Metrics.CreateCounter<long>("raqm.webhook.attempts");
    private static readonly Histogram<double> Duration = Metrics.CreateHistogram<double>("raqm.webhook.duration", "ms");

    public async Task<OutboxDelivery?> Claim(CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var ids = await db.Database.SqlQuery<Guid>($"""
            WITH candidate AS (
              SELECT d."Id", d."State", d."Attempts" FROM integration."Deliveries" d
              JOIN integration."Subscriptions" s ON s."Id"=d."SubscriptionId" AND s."CompanyId"=d."CompanyId"
              JOIN integration."Applications" a ON a."Id"=s."ApplicationId" AND a."CompanyId"=s."CompanyId"
              WHERE s."Active" AND a."State"=1 AND saas.can_operate_v1(a."TenantId") AND
                 (d."State" IN (0,3) AND d."DueAt"<=clock_timestamp() OR d."State"=1 AND d."LeaseUntil"<=clock_timestamp())
              ORDER BY d."DueAt",d."Id" FOR UPDATE OF d SKIP LOCKED LIMIT 1
            ), claimed AS (
              UPDATE integration."Deliveries" d SET
                "State"=CASE WHEN c."Attempts">={Limit} THEN 4 ELSE 1 END,
                "Attempts"=CASE WHEN c."Attempts">={Limit} THEN c."Attempts" ELSE c."Attempts"+1 END,
                "LeaseUntil"=CASE WHEN c."Attempts">={Limit} THEN NULL ELSE clock_timestamp()+interval '45 seconds' END,
                "LeaseToken"=CASE WHEN c."Attempts">={Limit} THEN NULL ELSE {token} END
              FROM candidate c WHERE d."Id"=c."Id" RETURNING d.*, c."State" AS prior_state,c."Attempts" AS prior_attempts
            ), expired AS (
              INSERT INTO integration."Attempts" ("Id","TenantId","OrganizationId","CompanyId","DeliveryId","Number","Generation","FinishedAt","DurationMs","Outcome")
              SELECT gen_random_uuid(),"TenantId","OrganizationId","CompanyId","Id",prior_attempts,"Generation",clock_timestamp(),0,'lease-expired'
              FROM claimed WHERE prior_state=1 ON CONFLICT DO NOTHING
            ) SELECT "Id" AS "Value" FROM claimed WHERE "State"=1
            """).ToListAsync(ct);
        return ids.Count == 0 ? null : await db.Set<OutboxDelivery>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == ids[0] && x.LeaseToken == token, ct);
    }

    public async Task<bool> Run(CancellationToken ct)
    {
        var delivery = await Claim(ct);
        if (delivery is null) return false;
        using var activity = Traces.StartActivity("webhook.delivery");
        activity?.SetTag("raqm.delivery.id", delivery.Id);
        var watch = Stopwatch.StartNew();
        var subscription = await db.Set<WebhookSubscription>().AsNoTracking().SingleAsync(x => x.CompanyId == delivery.CompanyId && x.Id == delivery.SubscriptionId, ct);
        var active = subscription.Active && await db.Set<IntegrationApplication>().AnyAsync(x => x.CompanyId == delivery.CompanyId &&
            x.Id == subscription.ApplicationId && x.State == ApplicationState.Active, ct);
        if (active && !await TenantBoundary.CanOperate(db, delivery.TenantId, ct))
        {
            await db.Set<OutboxDelivery>().Where(x => x.Id == delivery.Id && x.CompanyId == delivery.CompanyId &&
                x.LeaseToken == delivery.LeaseToken && x.State == DeliveryState.InProgress)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.State, DeliveryState.Retry)
                    .SetProperty(y => y.Attempts, y => y.Attempts - 1).SetProperty(y => y.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(y => y.LeaseToken, (Guid?)null), ct);
            return true;
        }
        int? code = null;
        string outcome;
        if (!active) outcome = "suspended";
        else
        {
            var source = await db.Set<OutboxEvent>().AsNoTracking().SingleAsync(x => x.CompanyId == delivery.CompanyId && x.Id == delivery.EventId, ct);
            try
            {
                var secret = IntegrationAdministration.Protector(protection, subscription.Id).Unprotect(subscription.ProtectedSecret);
                code = await transport.Send(subscription.Endpoint, secret, subscription.SecretVersion, source.Id, delivery.Id,
                    clock.GetUtcNow().ToUnixTimeSeconds(), source.Payload, ct);
                outcome = code is >= 200 and <= 299 ? "delivered" : code is >= 300 and <= 399 ? "redirect-denied" : "http-error";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { outcome = "timeout"; }
            catch (HttpRequestException) { outcome = "network-or-egress-denied"; }
            catch (CryptographicException) { outcome = "secret-unavailable"; }
            catch (RAQM.Api.Helpers.ApiException) { outcome = "endpoint-denied"; }
        }
        await Finish(delivery, code, outcome, watch.ElapsedMilliseconds, ct);
        Attempts.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        Duration.Record(watch.Elapsed.TotalMilliseconds);
        return true;
    }

    public async Task<bool> Finish(OutboxDelivery delivery, int? code, string outcome, long ms, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var state = outcome == "delivered" ? DeliveryState.Delivered : outcome == "suspended" ? DeliveryState.Cancelled :
            delivery.Attempts >= Limit || outcome is "redirect-denied" or "endpoint-denied" ? DeliveryState.DeadLetter : DeliveryState.Retry;
        var delay = Delay(delivery.Attempts);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE integration."Deliveries" SET "State"={(int)state}, "LeaseUntil"=NULL, "LeaseToken"=NULL,
                "DueAt"=clock_timestamp()+make_interval(secs=>{delay})
            WHERE "Id"={delivery.Id} AND "CompanyId"={delivery.CompanyId} AND "State"={(int)DeliveryState.InProgress}
                AND "LeaseToken"={delivery.LeaseToken} AND "LeaseUntil">clock_timestamp()
            """, ct);
        if (affected == 0) return false;
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        db.Add(new DeliveryAttempt { TenantId = delivery.TenantId, OrganizationId = delivery.OrganizationId, CompanyId = delivery.CompanyId,
            DeliveryId = delivery.Id, Number = delivery.Attempts, Generation = delivery.Generation, FinishedAt = now, HttpStatus = code,
            DurationMs = ms, Outcome = outcome });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public static int Delay(int attempts) => 15 * (1 << Math.Clamp(attempts - 1, 0, 6));
}
