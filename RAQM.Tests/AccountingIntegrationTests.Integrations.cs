using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Integrations;
using RAQM.Api.Entities.Integrations;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.EventProcessing;
using RAQM.Api.Services.Automation;
using RAQM.Api.Services.FinancialReporting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.Integrations;
using Xunit;

namespace RAQM.Tests;

public sealed partial class AccountingIntegrationTests
{
    private sealed class FixedClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    [Fact]
    public async Task FinancialRollbackRemovesCapturedOutboxAndSuccessfulRetryIsUnique()
    {
        var s = await Arrange();
        using var client = s.Client;
        var draft = await Draft(s);
        var args = new PostJournalRequest("outbox-atomic", draft.Version);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            var coordinator = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
            var journals = scope.ServiceProvider.GetRequiredService<IJournalService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.Execute<bool>(s.User, s.Org, s.Company.Id,
                async (_, token) =>
                {
                    await journals.Post(s.User, s.Org, s.Company.Id, draft.Id, args, token);
                    Assert.True(await db.Set<OutboxEvent>().AnyAsync(e => e.SourceId == draft.Id, token));
                    throw new InvalidOperationException("Rollback probe");
                }, CancellationToken.None));
            Assert.False(await db.Set<OutboxEvent>().AnyAsync(e => e.SourceId == draft.Id));
        }
        var posted = await Post(s, draft, "outbox-atomic");
        Assert.Equal(posted.JournalId, (await Post(s, draft, "outbox-atomic")).JournalId);
        using var read = fixture.Factory.Services.CreateScope();
        var persisted = await read.ServiceProvider.GetRequiredService<RaqmDbContext>().Set<OutboxEvent>()
            .Where(x => x.CompanyId == s.Company.Id && x.SourceId == posted.JournalId).ToArrayAsync();
        var entry = Assert.Single(persisted);
        Assert.Equal("journal.posted", entry.Type);
        Assert.Equal(1, entry.Version);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""UPDATE integration."Events" SET "Payload"='{}' WHERE "Id"=@id""", connection);
        command.Parameters.AddWithValue("id", entry.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task CompetingWorkersClaimOneDeliveryAndExpiredLeaseRejectsStaleCompletion()
    {
        var s = await Arrange();
        using var client = s.Client;
        var reviewer = await GovernanceOwner(s);
        using var approver = reviewer.Client;
        var root = s.Root.Replace("/accounting", "/integrations", StringComparison.Ordinal);
        var app = await Gov<IntegrationApplication>(client, root + "/applications", new ApplicationRequest("Hook", ["webhooks.receive"]));
        await Gov<IntegrationApplication>(approver, $"{root}/applications/{app.Id}/approve", new { });
        var subscription = await Gov<WebhookSecret>(client, $"{root}/applications/{app.Id}/webhooks",
            new WebhookRequest("https://hooks.example/receiver", ["journal.posted"]));
        using var enabled = await Request(approver, HttpMethod.Put, $"{root}/webhooks/{subscription.Id}", true, HttpStatusCode.OK);
        await Post(s, await Draft(s));
        using var firstScope = fixture.Factory.Services.CreateScope();
        using var secondScope = fixture.Factory.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<DeliveryDispatcher>();
        var second = secondScope.ServiceProvider.GetRequiredService<DeliveryDispatcher>();
        var claims = await Task.WhenAll(first.Claim(CancellationToken.None), second.Claim(CancellationToken.None));
        var won = Assert.Single(claims.OfType<OutboxDelivery>());
        var db = firstScope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        await db.Set<OutboxDelivery>().Where(x => x.Id == won.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var recovered = await second.Claim(CancellationToken.None);
        Assert.NotNull(recovered);
        Assert.Equal(won.Id, recovered.Id);
        Assert.Equal(2, recovered.Attempts);
        Assert.False(await first.Finish(won, 200, "delivered", 1, CancellationToken.None));
        Assert.True(await second.Finish(recovered, 503, "http-error", 10, CancellationToken.None));
        var evidence = await db.Set<DeliveryAttempt>().Where(x => x.DeliveryId == won.Id).OrderBy(x => x.Number).ToListAsync();
        Assert.Equal(new[] { "lease-expired", "http-error" }, evidence.Select(x => x.Outcome));
    }

    [Fact]
    public async Task ExpiredDeliveryCannotFinishWithItsOriginalTokenAndSlowWorkerClock()
    {
        var s = await Arrange();
        using var client = s.Client;
        var reviewer = await GovernanceOwner(s);
        using var approver = reviewer.Client;
        var root = s.Root.Replace("/accounting", "/integrations", StringComparison.Ordinal);
        var app = await Gov<IntegrationApplication>(client, root + "/applications", new ApplicationRequest("Lease", ["webhooks.receive"]));
        await Gov<IntegrationApplication>(approver, $"{root}/applications/{app.Id}/approve", new { });
        var subscription = await Gov<WebhookSecret>(client, $"{root}/applications/{app.Id}/webhooks",
            new WebhookRequest("https://hooks.example/receiver", ["journal.posted"]));
        using var enabled = await Request(approver, HttpMethod.Put, $"{root}/webhooks/{subscription.Id}", true, HttpStatusCode.OK);
        await Post(s, await Draft(s));
        using var scope = fixture.Factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<RaqmDbContext>();
        var dispatcher = new DeliveryDispatcher(db, services.GetRequiredService<RAQM.Api.Services.Webhooks.WebhookTransport>(),
            services.GetRequiredService<IDataProtectionProvider>(), new FixedClock(DateTimeOffset.UtcNow.AddMinutes(-5)));
        var claimed = await dispatcher.Claim(CancellationToken.None);
        Assert.NotNull(claimed);
        await db.Set<OutboxDelivery>().Where(x => x.Id == claimed.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.False(await dispatcher.Finish(claimed, 200, "delivered", 1, CancellationToken.None));
        Assert.Equal(DeliveryState.InProgress, (await db.Set<OutboxDelivery>().AsNoTracking().SingleAsync(x => x.Id == claimed.Id)).State);
        Assert.False(await db.Set<DeliveryAttempt>().AnyAsync(x => x.DeliveryId == claimed.Id));
    }

    [Fact]
    public async Task ScheduledLedgerExportHasOneDurableSlotAndAppOwnedDownload()
    {
        var s = await Arrange();
        using var client = s.Client;
        var reviewer = await GovernanceOwner(s);
        using var approver = reviewer.Client;
        var root = s.Root.Replace("/accounting", "/integrations", StringComparison.Ordinal);
        var machineRoot = s.Root.Replace("/accounting", "/machine", StringComparison.Ordinal);
        var app = await Gov<IntegrationApplication>(client, root + "/applications", new ApplicationRequest("Reporting", ["reports.read"]));
        await Gov<IntegrationApplication>(approver, $"{root}/applications/{app.Id}/approve", new { });
        var key = await Gov<CredentialIssue>(client, $"{root}/applications/{app.Id}/credentials", new CredentialRequest());
        var slot = DateTimeOffset.UtcNow.AddSeconds(-1);
        var schedule = await Gov<ExportSchedule>(client, $"{root}/applications/{app.Id}/schedules", new IntegrationExportScheduleRequest(24, 5, slot));
        using var enabled = await Request(approver, HttpMethod.Put, $"{root}/schedules/{schedule.Id}/enable", null, HttpStatusCode.OK);
        using var a = fixture.Factory.Services.CreateScope();
        using var b = fixture.Factory.Services.CreateScope();
        var jobsA = a.ServiceProvider.GetRequiredService<LedgerExportJobs>();
        var jobsB = b.ServiceProvider.GetRequiredService<LedgerExportJobs>();
        await Task.WhenAll(jobsA.Materialize(CancellationToken.None), jobsB.Materialize(CancellationToken.None));
        var db = a.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var queued = await db.Set<ExportOccurrence>().AsNoTracking().SingleAsync(x => x.ScheduleId == schedule.Id);
        Assert.Equal(queued.Id, await Gov<Guid>(client, $"{root}/schedules/{schedule.Id}/run", slot));
        Assert.True(await jobsB.Run(CancellationToken.None));
        using var machine = fixture.Factory.CreateClient();
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("RAQM-Key", key.Key);
        using var csv = await Request(machine, HttpMethod.Get, $"{machineRoot}/exports/{queued.Id}", null, HttpStatusCode.OK);
        Assert.StartsWith("date,journal_id,account_id,line_id,debit,credit", await csv.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExpiredExportLeaseCannotPublishWithSlowWorkerClockAndCanBeReclaimed()
    {
        var s = await Arrange();
        using var client = s.Client;
        var reviewer = await GovernanceOwner(s);
        using var approver = reviewer.Client;
        var root = s.Root.Replace("/accounting", "/integrations", StringComparison.Ordinal);
        var app = await Gov<IntegrationApplication>(client, root + "/applications", new ApplicationRequest("Lease export", ["reports.read"]));
        await Gov<IntegrationApplication>(approver, $"{root}/applications/{app.Id}/approve", new { });
        var slot = DateTimeOffset.UtcNow.AddSeconds(-1);
        var schedule = await Gov<ExportSchedule>(client, $"{root}/applications/{app.Id}/schedules",
            new IntegrationExportScheduleRequest(24, 5, slot));
        using var enabled = await Request(approver, HttpMethod.Put, $"{root}/schedules/{schedule.Id}/enable", null, HttpStatusCode.OK);
        using var scope = fixture.Factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<RaqmDbContext>();
        var normal = services.GetRequiredService<LedgerExportJobs>();
        Assert.Equal(1, await normal.Materialize(CancellationToken.None));
        var occurrence = await db.Set<ExportOccurrence>().AsNoTracking().SingleAsync(x => x.ScheduleId == schedule.Id);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION integration.wp07_expire_export_lease() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."State"=1 AND OLD."State"<>1 THEN NEW."LeaseUntil"=clock_timestamp()-interval '1 second'; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER wp07_expire_export_lease BEFORE UPDATE ON integration."Occurrences"
              FOR EACH ROW EXECUTE FUNCTION integration.wp07_expire_export_lease();
            """);
        try
        {
            var slow = new LedgerExportJobs(db, services.GetRequiredService<IntegrationAdministration>(),
                services.GetRequiredService<FinancialLedger>(), services.GetRequiredService<FinancialAccess>(),
                new FixedClock(DateTimeOffset.UtcNow.AddMinutes(-5)));
            Assert.True(await slow.Run(CancellationToken.None));
            var retained = await db.Set<ExportOccurrence>().AsNoTracking().SingleAsync(x => x.Id == occurrence.Id);
            Assert.Equal(DeliveryState.InProgress, retained.State);
            Assert.Null(retained.Csv);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER wp07_expire_export_lease ON integration.\"Occurrences\"; DROP FUNCTION integration.wp07_expire_export_lease()");
        }
        Assert.True(await normal.Run(CancellationToken.None));
        var recovered = await db.Set<ExportOccurrence>().AsNoTracking().SingleAsync(x => x.Id == occurrence.Id);
        Assert.Equal(DeliveryState.Delivered, recovered.State);
        Assert.NotNull(recovered.Csv);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimedExportCannotPublishAfterScheduleOrApplicationSuspension(bool application)
    {
        var s = await Arrange();
        using var client = s.Client;
        var reviewer = await GovernanceOwner(s);
        using var approver = reviewer.Client;
        var root = s.Root.Replace("/accounting", "/integrations", StringComparison.Ordinal);
        var app = await Gov<IntegrationApplication>(client, root + "/applications", new ApplicationRequest("Cutover", ["reports.read"]));
        await Gov<IntegrationApplication>(approver, $"{root}/applications/{app.Id}/approve", new { });
        var schedule = await Gov<ExportSchedule>(client, $"{root}/applications/{app.Id}/schedules",
            new IntegrationExportScheduleRequest(24, 5, DateTimeOffset.UtcNow.AddSeconds(-1)));
        using var enabled = await Request(approver, HttpMethod.Put, $"{root}/schedules/{schedule.Id}/enable", null, HttpStatusCode.OK);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var jobs = scope.ServiceProvider.GetRequiredService<LedgerExportJobs>();
        Assert.Equal(1, await jobs.Materialize(CancellationToken.None));
        var occurrence = await db.Set<ExportOccurrence>().AsNoTracking().SingleAsync(x => x.ScheduleId == schedule.Id);
        var triggerSql = application ? """
            CREATE FUNCTION integration.wp07_restrict_export_on_claim() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."State"=1 AND OLD."State"<>1 THEN
                UPDATE integration."Applications" SET "State"=2 WHERE "Id"=(SELECT "ApplicationId" FROM integration."Schedules" WHERE "Id"=NEW."ScheduleId");
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER wp07_restrict_export_on_claim BEFORE UPDATE ON integration."Occurrences"
              FOR EACH ROW EXECUTE FUNCTION integration.wp07_restrict_export_on_claim();
            """ : """
            CREATE FUNCTION integration.wp07_restrict_export_on_claim() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."State"=1 AND OLD."State"<>1 THEN
                UPDATE integration."Schedules" SET "Enabled"=false WHERE "Id"=NEW."ScheduleId";
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER wp07_restrict_export_on_claim BEFORE UPDATE ON integration."Occurrences"
              FOR EACH ROW EXECUTE FUNCTION integration.wp07_restrict_export_on_claim();
            """;
        await db.Database.ExecuteSqlRawAsync(triggerSql);
        try
        {
            Assert.True(await jobs.Run(CancellationToken.None));
            var retained = await db.Set<ExportOccurrence>().AsNoTracking().SingleAsync(x => x.Id == occurrence.Id);
            Assert.Equal(DeliveryState.InProgress, retained.State);
            Assert.Null(retained.Csv);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER wp07_restrict_export_on_claim ON integration.\"Occurrences\"; DROP FUNCTION integration.wp07_restrict_export_on_claim()");
        }
        if (application)
            await db.Set<IntegrationApplication>().Where(x => x.Id == app.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.State, ApplicationState.Active));
        else
            await db.Set<ExportSchedule>().Where(x => x.Id == schedule.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, true));
        await db.Set<ExportOccurrence>().Where(x => x.Id == occurrence.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.True(await jobs.Run(CancellationToken.None));
        Assert.Equal(DeliveryState.Delivered,
            (await db.Set<ExportOccurrence>().AsNoTracking().SingleAsync(x => x.Id == occurrence.Id)).State);
    }

    [Fact]
    public async Task MachineCredentialCannotCrossCompanyOrEnterHumanFinancialRoutes()
    {
        var s = await Arrange();
        using var client = s.Client;
        var reviewer = await GovernanceOwner(s);
        using var approver = reviewer.Client;
        var root = s.Root.Replace("/accounting", "/integrations", StringComparison.Ordinal);
        var machineRoot = s.Root.Replace("/accounting", "/machine", StringComparison.Ordinal);
        var app = await Gov<IntegrationApplication>(client, root + "/applications", new ApplicationRequest("Reader", ["invoices.read"]));
        await Gov<IntegrationApplication>(approver, $"{root}/applications/{app.Id}/approve", new { });
        var key = await Gov<CredentialIssue>(client, $"{root}/applications/{app.Id}/credentials", new CredentialRequest());
        using var machine = fixture.Factory.CreateClient();
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("RAQM-Key", key.Key);
        using var allowed = await Request(machine, HttpMethod.Get, machineRoot + "/invoices", null, HttpStatusCode.OK);
        using var scope = await Request(machine, HttpMethod.Get, machineRoot + "/suppliers", null, HttpStatusCode.Forbidden);
        using var crossed = await Request(machine, HttpMethod.Get, machineRoot.Replace(s.Company.Id.ToString(), Guid.NewGuid().ToString()) + "/invoices",
            null, HttpStatusCode.NotFound);
        using var human = await Request(machine, HttpMethod.Get, s.Root + "/accounts", null, HttpStatusCode.Unauthorized);
        var rotated = await Gov<CredentialIssue>(client, $"{root}/applications/{app.Id}/credentials", new CredentialRequest(30, key.Id));
        using var denied = await Request(machine, HttpMethod.Get, machineRoot + "/invoices", null, HttpStatusCode.Unauthorized);
        machine.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("RAQM-Key", rotated.Key);
        using var restored = await Request(machine, HttpMethod.Get, machineRoot + "/invoices", null, HttpStatusCode.OK);
    }
}
