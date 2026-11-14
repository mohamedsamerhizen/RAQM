using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.Entities.FinancialDimensions;

public enum DimensionKind { Branch, Department, CostCenter, Project, BusinessUnit, Custom }
public enum DimensionMode { Optional, Required, Prohibited, Fixed }
public enum DimensionOrigin { Explicit, Source, AccountDefault, CompanyDefault, Fixed, Reversal, Allocation, Unassigned }
public enum DimensionSource { Manual, Trading, Procurement, Stock, Asset, Recognition, ForeignExchange }
public enum ProjectState { Draft, Approved, Active, Suspended, Completed, Closed }
public enum AllocationMode { Analytical, Financial }
public enum AllocationBasis { Percent, Amount, Statistical }
public enum AllocationState { Draft, Approved, Executed, Cancelled, Corrected }

public sealed class DimensionDefinition : FinancialEvidence
{
    public DimensionKind Kind { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class DimensionValue : FinancialEvidence
{
    public Guid DefinitionId { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? CostCenterId { get; set; }
    public Guid? ManagerId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Classification { get; set; } = "";
    public DateOnly EffectiveDate { get; set; }
    public DateOnly? InactiveOn { get; set; }
}

public sealed class DimensionRule : FinancialEvidence
{
    public Guid DefinitionId { get; set; }
    public Guid? AccountId { get; set; }
    public Guid? DefaultValueId { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DimensionMode Mode { get; set; }
    public string AllowedJson { get; set; } = "[]";
    public string Reason { get; set; } = "";
}

public sealed class DimensionBinding : FinancialEvidence
{
    public DimensionSource Source { get; set; }
    public Guid SourceId { get; set; }
    public Guid SourceVersion { get; set; }
    public int Revision { get; set; }
    public Guid ActorId { get; set; }
    public string ValuesJson { get; set; } = "[]";
    public string Reason { get; set; } = "";
}

public sealed class PostedDimension : FinancialEvidence
{
    public Guid JournalLineId { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid? ValueId { get; set; }
    public Guid? RuleId { get; set; }
    public Guid? BindingId { get; set; }
    public DimensionOrigin Origin { get; set; }
}

public sealed class DimensionProject : FinancialEvidence
{
    public Guid ValueId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid OwnerId { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public string Description { get; set; } = "";
    public string Evidence { get; set; } = "";
}

public sealed class DimensionProjectEvent : FinancialEvidence
{
    public Guid ProjectId { get; set; }
    public Guid ActorId { get; set; }
    public ProjectState State { get; set; }
    public int Number { get; set; }
    public DateOnly Date { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class DimensionBudgetSlice : FinancialEvidence
{
    public Guid BudgetAllocationId { get; set; }
    public Guid ValueId { get; set; }
    public decimal Amount { get; set; }
    public bool HardLimit { get; set; }
}

public sealed class DimensionProjectForecast : FinancialEvidence
{
    public Guid ProjectId { get; set; }
    public Guid ApprovedBy { get; set; }
    public DateOnly At { get; set; }
    public decimal PostedCost { get; set; }
    public decimal PostedRevenue { get; set; }
    public decimal CostToComplete { get; set; }
    public decimal RevenueToComplete { get; set; }
    public string Evidence { get; set; } = "";
}

public sealed class DimensionAllocation : FinancialEvidence
{
    public Guid SourceLineId { get; set; }
    public Guid? JournalId { get; set; }
    public Guid? CorrectionJournalId { get; set; }
    public DateOnly Date { get; set; }
    public DateOnly? CorrectedOn { get; set; }
    public AllocationMode Mode { get; set; }
    public AllocationState State { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? PostedBy { get; set; }
    public string Key { get; set; } = "";
    public string Hash { get; set; } = "";
    public string InputJson { get; set; } = "";
    public string PlanJson { get; set; } = "";
}
