using RAQM.Api.Entities.CorporateGroups;

namespace RAQM.Api.Entities.Consolidation;

public enum GroupRunState { Prepared, Approved, Published }

public sealed class GroupTranslationRate : IGroupEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid GroupId { get; set; }
    public Guid MemberId { get; set; }
    public Guid? EquityAccountId { get; set; }
    public DateOnly RateDate { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public decimal Rate { get; set; }
    public Guid PreparedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string Evidence { get; set; } = "";
}

public sealed class ConsolidationRun : IGroupEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid GroupId { get; set; }
    public DateOnly From { get; set; }
    public DateOnly Cutoff { get; set; }
    public Guid? SupersedesId { get; set; }
    public Guid? TranslationReserveId { get; set; }
    public int Version { get; set; }
    public GroupRunState State { get; set; }
    public Guid PreparedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? PublishedBy { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string SourceHash { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public string AdjustmentsJson { get; set; } = "";
}
