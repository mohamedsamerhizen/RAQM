namespace RAQM.Api.Entities.SaaS;

public enum TenantOperationState { Active, Suspended, Offboarded }
public enum CommercialMovementKind { Payment, Refund, Credit }
public enum CommercialInterval { Monthly, Annual }

public sealed class CommercialPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Product { get; set; } = "";
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public int Version { get; set; }
    public string Currency { get; set; } = "";
    public CommercialInterval Interval { get; set; }
    public decimal Price { get; set; }
    public decimal ExtraSeatPrice { get; set; }
    public int IncludedSeats { get; set; }
    public int TrialDays { get; set; }
    public int GraceDays { get; set; }
    public int CompanyLimit { get; set; }
    public int SeatLimit { get; set; }
    public int WarehouseLimit { get; set; }
    public int IntegrationLimit { get; set; }
    public int WebhookLimit { get; set; }
    public string[] Features { get; set; } = [];
    public Guid CreatedBy { get; set; }
    public Guid? PublishedBy { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

public abstract class TenantCommercialRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
}

public sealed class CommercialAccount : TenantCommercialRecord
{
    public Guid OrganizationId { get; set; }
    public Guid FirstCompanyId { get; set; }
    public Guid OwnerId { get; set; }
    public string BillingContact { get; set; } = "";
    public TenantOperationState State { get; set; }
    public DateTimeOffset? SuspendAt { get; set; }
    public DateTimeOffset? RetainUntil { get; set; }
    public Guid? RetainedExportId { get; set; }
    public bool LegalHold { get; set; }
    public long QuotaEpoch { get; set; }
}

public sealed class CommercialSubscription : TenantCommercialRecord
{
    public Guid PlanId { get; set; }
    public Guid? NextPlanId { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateOnly Anchor { get; set; }
    public int Cycle { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public DateTimeOffset AccessUntil { get; set; }
    public bool Trial { get; set; }
    public bool TrialExpiredRecorded { get; set; }
    public bool CancelAtEnd { get; set; }
    public bool Cancelled { get; set; }
}

public sealed class ProvisioningReceipt : TenantCommercialRecord
{
    public Guid RequesterId { get; set; }
    public string RequestKey { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool Trial { get; set; }
}

public sealed class CommercialInvoice : TenantCommercialRecord
{
    public Guid SubscriptionId { get; set; }
    public Guid PlanId { get; set; }
    public string RequestKey { get; set; } = "";
    public string Currency { get; set; } = "";
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public decimal Total { get; set; }
    public string Lines { get; set; } = "";
}

public sealed class CommercialMovement : TenantCommercialRecord
{
    public Guid InvoiceId { get; set; }
    public Guid? BasisId { get; set; }
    public CommercialMovementKind Kind { get; set; }
    public string Reference { get; set; } = "";
    public string Evidence { get; set; } = "";
    public decimal Amount { get; set; }
    public Guid PreparedBy { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

public sealed class CommercialEvent : TenantCommercialRecord
{
    public string Type { get; set; } = "";
    public Guid SourceId { get; set; }
    public Guid? ActorId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Evidence { get; set; } = "";
}

public sealed class PortabilityExport : TenantCommercialRecord
{
    public Guid RequestedBy { get; set; }
    public DateOnly From { get; set; }
    public DateOnly Cutoff { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Sha256 { get; set; } = "";
    public Guid[] Companies { get; set; } = [];
    public int Rows { get; set; }
    public string? Csv { get; set; }
}

public sealed class CommercialCase : TenantCommercialRecord
{
    public Guid? ExportId { get; set; }
    public Guid OpenedBy { get; set; }
    public Guid? ClosedBy { get; set; }
    public Guid? AssignedTo { get; set; }
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? Resolution { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}
