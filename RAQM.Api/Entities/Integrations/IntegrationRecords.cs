using RAQM.Api.Entities.Accounting;
namespace RAQM.Api.Entities.Integrations;

public abstract class IntegrationRecord : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
}

public enum ApplicationState { Draft, Active, Suspended, Revoked }
public enum DeliveryState { Queued, InProgress, Delivered, Retry, DeadLetter, Cancelled }
public enum InboxState { Received, Approved, Processed, Cancelled }

public sealed class IntegrationApplication : IntegrationRecord
{
    public string Name { get; set; } = "";
    public string[] Scopes { get; set; } = [];
    public ApplicationState State { get; set; }
    public Guid OwnerId { get; set; }
    public Guid? ApprovedBy { get; set; }
}

public sealed class IntegrationCredential : IntegrationRecord
{
    public Guid ApplicationId { get; set; }
    public byte[] Digest { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class OutboxEvent : IntegrationRecord
{
    public Guid SourceId { get; set; }
    public Guid SourceVersion { get; set; }
    public Guid? CausationId { get; set; }
    public string Type { get; set; } = "";
    public int Version { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Correlation { get; set; } = "";
    public string Payload { get; set; } = "";
}

public sealed class WebhookSubscription : IntegrationRecord
{
    public Guid ApplicationId { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string Endpoint { get; set; } = "";
    public string[] Events { get; set; } = [];
    public string ProtectedSecret { get; set; } = "";
    public int SecretVersion { get; set; } = 1;
    public bool Active { get; set; }
}

public sealed class OutboxDelivery : IntegrationRecord
{
    public Guid EventId { get; set; }
    public Guid SubscriptionId { get; set; }
    public DeliveryState State { get; set; }
    public int Attempts { get; set; }
    public int Generation { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? LeaseToken { get; set; }
}

public sealed class DeliveryAttempt : IntegrationRecord
{
    public Guid DeliveryId { get; set; }
    public int Number { get; set; }
    public int Generation { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public int? HttpStatus { get; set; }
    public long DurationMs { get; set; }
    public string Outcome { get; set; } = "";
}

public sealed class InboxBatch : IntegrationRecord
{
    public Guid ApplicationId { get; set; }
    public string ExternalId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Input { get; set; } = "";
    public InboxState State { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string? Result { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class ExportSchedule : IntegrationRecord
{
    public Guid ApplicationId { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; }
    public int LookbackDays { get; set; }
    public DateTimeOffset NextRun { get; set; }
}

public sealed class ImportedMaster : IntegrationRecord
{
    public Guid ApplicationId { get; set; }
    public Guid BatchId { get; set; }
    public string ExternalId { get; set; } = "";
    public Guid PartyId { get; set; }
}

public sealed class ExportOccurrence : IntegrationRecord
{
    public Guid ScheduleId { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? LeaseToken { get; set; }
    public int Attempts { get; set; }
    public DeliveryState State { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Csv { get; set; }
    public string? Digest { get; set; }
    public string? Failure { get; set; }
}
