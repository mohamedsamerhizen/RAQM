using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Entities.SaaS;

namespace RAQM.Api.DTOs.SaaS;

public sealed record CommercialPlanInput(string Product, string Code, string Description, string Currency, CommercialInterval Interval,
    decimal Price, decimal ExtraSeatPrice, int IncludedSeats, int TrialDays, int GraceDays, int CompanyLimit, int SeatLimit,
    int WarehouseLimit, int IntegrationLimit, int WebhookLimit, string[] Features);
public sealed record TenantProvisionInput(string Key, Guid PlanId, string Organization, CompanyRequest Company, string BillingContact, bool Trial);
public sealed record LegacyEnrollmentInput(Guid PlanId, string BillingContact);
public sealed record CommercialPlanChange(Guid ExpectedVersion, Guid PlanId, bool Immediate, string? QuoteHash = null);
public sealed record CommercialAction(Guid ExpectedVersion, string Action);
public sealed record CommercialMovementInput(Guid InvoiceId, CommercialMovementKind Kind, Guid? BasisId, decimal Amount, string Currency, string Reference, string Evidence);
public sealed record TenantOperationInput(string Action, string Reason, DateTimeOffset? EffectiveAt = null, DateTimeOffset? RetainUntil = null, bool LegalHold = false,
    Guid? ExportId = null);
public sealed record PortabilityInput(DateOnly From, DateOnly Cutoff);
public sealed record CommercialCaseInput(string Kind, string Summary, Guid? ExportId = null);
public sealed record CommercialCaseAction(string Action, string Reason);
public sealed record BillingLine(string Label, int Quantity, decimal UnitPrice, decimal Amount);
public sealed record BillingQuote(Guid SubscriptionVersion, Guid PlanId, DateOnly EffectiveOn, BillingLine[] Lines, string Hash)
{
    public decimal Total => Lines.Sum(x => x.Amount);
}
