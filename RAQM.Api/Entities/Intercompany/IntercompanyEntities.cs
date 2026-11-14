using RAQM.Api.Entities.CorporateGroups;

namespace RAQM.Api.Entities.Intercompany;

public enum PairKind { Service, Principal, Interest, Settlement, Correction }
public enum PairState { Draft, Approved, Posting, Posted, Cancelled }

public sealed class IntercompanyPolicy : IGroupEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid GroupId { get; set; }
    public Guid CreditorId { get; set; }
    public Guid DebtorId { get; set; }
    public Guid DueFromId { get; set; }
    public Guid DueToId { get; set; }
    public Guid RevenueId { get; set; }
    public Guid ExpenseId { get; set; }
    public Guid? CreditorBankId { get; set; }
    public Guid? DebtorBankId { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public Guid PreparedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string Evidence { get; set; } = "";
}

public sealed class IntercompanyPair : IGroupEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid GroupId { get; set; }
    public Guid PolicyId { get; set; }
    public Guid CreditorId { get; set; }
    public Guid DebtorId { get; set; }
    public Guid? BasisId { get; set; }
    public Guid? CreditorJournalId { get; set; }
    public Guid? DebtorJournalId { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid CreatedBy { get; set; }
    public string Reference { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Currency { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public PairKind Kind { get; set; }
    public PairState State { get; set; }
    public string? PlanJson { get; set; }
    public DateOnly? InterestFrom { get; set; }
    public decimal? InterestAnnualPercent { get; set; }
}

public sealed class ReciprocalReview : IGroupEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid GroupId { get; set; }
    public Guid PolicyId { get; set; }
    public DateOnly Cutoff { get; set; }
    public string SnapshotJson { get; set; } = "";
    public string Evidence { get; set; } = "";
    public Guid ActorId { get; set; }
}
