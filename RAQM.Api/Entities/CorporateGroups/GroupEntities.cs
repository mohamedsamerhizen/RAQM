using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.Entities.CorporateGroups;

public sealed class CorporateGroup : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Currency { get; set; } = "";
    public Guid OwnerId { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly? RetiredOn { get; set; }
    public Guid[] ReportReaders { get; set; } = [];
}

public interface IGroupEvidence : ICompanyFinancialEntity
{
    Guid GroupId { get; set; }
}

public enum InvestmentKind { Controlled, Associate, JointArrangement, Other, Excluded }

public sealed class GroupOwnership : IGroupEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid GroupId { get; set; }
    public Guid InvestorId { get; set; }
    public Guid InvesteeId { get; set; }
    public InvestmentKind Kind { get; set; }
    public decimal Ownership { get; set; }
    public decimal Voting { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public string Evidence { get; set; } = "";
    public Guid ActorId { get; set; }
}

public sealed class GroupAccount : IGroupEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid GroupId { get; set; }
    public Guid? ParentId { get; set; }
    public AccountClass Classification { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Cash { get; set; }
}

public sealed class GroupMapping : IGroupEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid GroupId { get; set; }
    public Guid MemberId { get; set; }
    public Guid LedgerAccountId { get; set; }
    public Guid GroupAccountId { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public Guid PreparedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string Evidence { get; set; } = "";
}
