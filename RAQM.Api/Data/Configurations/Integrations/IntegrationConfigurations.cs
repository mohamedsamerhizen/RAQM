using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Entities.Integrations;
using RAQM.Api.Entities.Organizations;

namespace RAQM.Api.Data.Configurations.Integrations;

internal static class IntegrationMap
{
    public static void Scope<T>(EntityTypeBuilder<T> b, string table) where T : IntegrationRecord
    {
        b.ToTable(table, "integration");
        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<Company>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrganizationId, x.CompanyId })
            .HasPrincipalKey(x => new { x.TenantId, x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }

    public static void Link<T, U>(EntityTypeBuilder<T> b, string field) where T : IntegrationRecord where U : IntegrationRecord =>
        b.HasOne<U>().WithMany().HasForeignKey("CompanyId", field).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
}

public sealed class IntegrationApplicationConfiguration : IEntityTypeConfiguration<IntegrationApplication>
{
    public void Configure(EntityTypeBuilder<IntegrationApplication> b)
    {
        IntegrationMap.Scope(b, "Applications");
        b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_Integration_Application", """ "State" BETWEEN 0 AND 3 AND ("State"=0 OR "ApprovedBy" IS NOT NULL) """));
    }
}

public sealed class IntegrationCredentialConfiguration : IEntityTypeConfiguration<IntegrationCredential>
{
    public void Configure(EntityTypeBuilder<IntegrationCredential> b)
    {
        IntegrationMap.Scope(b, "Credentials");
        IntegrationMap.Link<IntegrationCredential, IntegrationApplication>(b, nameof(IntegrationCredential.ApplicationId));
        b.ToTable(t => t.HasCheckConstraint("CK_Integration_Digest", """ octet_length("Digest")=32 """));
    }
}

public sealed class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEvent>
{
    public void Configure(EntityTypeBuilder<OutboxEvent> b)
    {
        IntegrationMap.Scope(b, "Events");
        b.HasIndex(x => new { x.CompanyId, x.Type, x.SourceId, x.SourceVersion, x.Version }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.OccurredAt });
    }
}

public sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookSubscription> b)
    {
        IntegrationMap.Scope(b, "Subscriptions");
        IntegrationMap.Link<WebhookSubscription, IntegrationApplication>(b, nameof(WebhookSubscription.ApplicationId));
        b.HasIndex(x => new { x.CompanyId, x.Active });
    }
}

public sealed class OutboxDeliveryConfiguration : IEntityTypeConfiguration<OutboxDelivery>
{
    public void Configure(EntityTypeBuilder<OutboxDelivery> b)
    {
        IntegrationMap.Scope(b, "Deliveries");
        IntegrationMap.Link<OutboxDelivery, OutboxEvent>(b, nameof(OutboxDelivery.EventId));
        IntegrationMap.Link<OutboxDelivery, WebhookSubscription>(b, nameof(OutboxDelivery.SubscriptionId));
        b.HasIndex(x => new { x.EventId, x.SubscriptionId }).IsUnique();
        b.HasIndex(x => new { x.State, x.DueAt });
        b.HasIndex(x => new { x.State, x.LeaseUntil });
        b.ToTable(t => t.HasCheckConstraint("CK_Integration_DeliveryLease", """ "Attempts">=0 AND "Generation">=0 AND "State" BETWEEN 0 AND 5 AND (("State"=1 AND "LeaseUntil" IS NOT NULL AND "LeaseToken" IS NOT NULL) OR ("State"<>1 AND "LeaseUntil" IS NULL AND "LeaseToken" IS NULL)) """));
    }
}

public sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> b)
    {
        IntegrationMap.Scope(b, "Attempts");
        IntegrationMap.Link<DeliveryAttempt, OutboxDelivery>(b, nameof(DeliveryAttempt.DeliveryId));
        b.HasIndex(x => new { x.DeliveryId, x.Generation, x.Number }).IsUnique();
    }
}

public sealed class InboxBatchConfiguration : IEntityTypeConfiguration<InboxBatch>
{
    public void Configure(EntityTypeBuilder<InboxBatch> b)
    {
        IntegrationMap.Scope(b, "Inbox");
        IntegrationMap.Link<InboxBatch, IntegrationApplication>(b, nameof(InboxBatch.ApplicationId));
        b.Property(x => x.ExternalId).HasMaxLength(100);
        b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.HasIndex(x => new { x.ApplicationId, x.ExternalId }).IsUnique();
    }
}

public sealed class ExportScheduleConfiguration : IEntityTypeConfiguration<ExportSchedule>
{
    public void Configure(EntityTypeBuilder<ExportSchedule> b)
    {
        IntegrationMap.Scope(b, "Schedules");
        IntegrationMap.Link<ExportSchedule, IntegrationApplication>(b, nameof(ExportSchedule.ApplicationId));
        b.HasIndex(x => new { x.Enabled, x.NextRun });
        b.ToTable(t => t.HasCheckConstraint("CK_Integration_Schedule", """ "IntervalHours" BETWEEN 1 AND 720 AND "LookbackDays" BETWEEN 1 AND 90 """));
    }
}

public sealed class ImportedMasterConfiguration : IEntityTypeConfiguration<ImportedMaster>
{
    public void Configure(EntityTypeBuilder<ImportedMaster> b)
    {
        IntegrationMap.Scope(b, "ImportedMasters");
        IntegrationMap.Link<ImportedMaster, IntegrationApplication>(b, nameof(ImportedMaster.ApplicationId));
        IntegrationMap.Link<ImportedMaster, InboxBatch>(b, nameof(ImportedMaster.BatchId));
        b.HasOne<RAQM.Api.Entities.Trading.TradingParty>().WithMany().HasForeignKey(x => new { x.CompanyId, x.PartyId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ApplicationId, x.ExternalId }).IsUnique();
    }
}

public sealed class ExportOccurrenceConfiguration : IEntityTypeConfiguration<ExportOccurrence>
{
    public void Configure(EntityTypeBuilder<ExportOccurrence> b)
    {
        IntegrationMap.Scope(b, "Occurrences");
        IntegrationMap.Link<ExportOccurrence, ExportSchedule>(b, nameof(ExportOccurrence.ScheduleId));
        b.HasIndex(x => new { x.ScheduleId, x.ScheduledAt }).IsUnique();
        b.HasIndex(x => new { x.State, x.DueAt });
        b.HasIndex(x => new { x.ExpiresAt });
    }
}
