using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Trading;

namespace RAQM.Api.Entities.TaxAccounting;

public enum TaxTreatment { Standard, ZeroRated, Exempt, OutsideScope }
public enum TaxControlRole { Output, Input, Nonrecoverable, Withholding, CurrentExpense, CurrentPayable, Prepaid, DeferredAsset, DeferredLiability, DeferredExpense, Clearing }
public enum TaxOperationKind { CurrentProvision, DeferredAsset, DeferredLiability, Pay, Refund, Installment, ApplyPrepaid, ReverseCharge, Withhold, Correction }
public enum TaxEvidenceState { Draft, Approved, Posting, Posted, Cancelled }

public abstract class TaxRecord : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
}

public sealed class TaxRegistration : TaxRecord
{
    public string Code { get; set; } = "";
    public string Country { get; set; } = "";
    public string? Region { get; set; }
    public string Authority { get; set; } = "";
    public string Number { get; set; } = "";
    public string Residence { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Evidence { get; set; } = "";
    public DateOnly EffectiveOn { get; set; }
    public Guid PreparedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
}

public sealed class TaxControl : TaxRecord
{
    public Guid RegistrationId { get; set; }
    public TaxControlRole Role { get; set; }
    public Guid AccountId { get; set; }
    public Guid ActorId { get; set; }
}

public sealed class TaxRule : TaxRecord
{
    public Guid RegistrationId { get; set; }
    public string Group { get; set; } = "";
    public string? ItemClass { get; set; }
    public Guid? PartyId { get; set; }
    public PartyKind Side { get; set; }
    public int Order { get; set; }
    public TaxTreatment Treatment { get; set; }
    public Guid? RateId { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public string Evidence { get; set; } = "";
    public Guid PreparedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
}

public sealed class CommercialTaxEvidence : TaxRecord
{
    public Guid RegistrationId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid ActorId { get; set; }
    public string SnapshotJson { get; set; } = "";
}

public sealed class TaxPeriod : TaxRecord
{
    public Guid RegistrationId { get; set; }
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public string Name { get; set; } = "";
    public Guid OpenedBy { get; set; }
}

public sealed class TaxOperation : TaxRecord
{
    public Guid RegistrationId { get; set; }
    public Guid? PeriodId { get; set; }
    public Guid? BillId { get; set; }
    public Guid? BasisId { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? JournalId { get; set; }
    public Guid? PostingActor { get; set; }
    public TaxOperationKind Kind { get; set; }
    public TaxEvidenceState State { get; set; }
    public DateOnly Date { get; set; }
    public string Reference { get; set; } = "";
    public string Evidence { get; set; } = "";
    public decimal Amount { get; set; }
    public string InputsJson { get; set; } = "";
    public string? PlanJson { get; set; }
    public Guid PreparedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
}

public sealed class TaxReturnVersion : TaxRecord
{
    public Guid RegistrationId { get; set; }
    public Guid PeriodId { get; set; }
    public Guid? SupersedesId { get; set; }
    public int Version { get; set; }
    public TaxEvidenceState State { get; set; }
    public string SourceHash { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public string Evidence { get; set; } = "";
    public Guid PreparedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}
