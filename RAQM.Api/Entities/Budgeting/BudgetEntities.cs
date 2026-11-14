using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.Entities.Budgeting;

public enum BudgetKind { Revenue, Operating, Capital, Inventory }
public enum BudgetState { Draft, Submitted, Reviewed, Approved, Active, Superseded, Rejected }
public enum ControlMode { Off, Warning, Hard }
public enum CommitmentKind { Requisition, Order, Capital }

public sealed class FinancialBudget : FinancialEvidence
{
    public Guid FiscalYearId { get; set; }
    public Guid OwnerId { get; set; }
    public BudgetKind Kind { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal ApprovalLimit { get; set; }
}

public sealed class BudgetVersion : FinancialEvidence
{
    public Guid BudgetId { get; set; }
    public Guid? BasisVersionId { get; set; }
    public Guid? ActiveAtPreparationId { get; set; }
    public int Number { get; set; }
    public BudgetState State { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public Guid? ReviewerId { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateOnly? EffectiveDate { get; set; }
    public DateOnly? ActivatedOn { get; set; }
    public string Key { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Evidence { get; set; } = "";
    public List<BudgetAllocation> Allocations { get; set; } = [];
}

public sealed class BudgetAllocation : FinancialEvidence
{
    public Guid BudgetVersionId { get; set; }
    public Guid AccountId { get; set; }
    public Guid PeriodId { get; set; }
    public decimal Amount { get; set; }
}

public sealed class BudgetPolicy : FinancialEvidence
{
    public Guid AccountId { get; set; }
    public BudgetKind Kind { get; set; }
    public ControlMode Mode { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public decimal WarningPercent { get; set; }
    public Guid CreatedBy { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class BudgetCommitment : FinancialEvidence
{
    public Guid AccountId { get; set; }
    public Guid PeriodId { get; set; }
    public Guid? ProcurementLineId { get; set; }
    public Guid? AssetOperationId { get; set; }
    public Guid? StockOperationId { get; set; }
    public CommitmentKind Kind { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string EventKey { get; set; } = "";
    public Guid ActorId { get; set; }
}

public sealed class BudgetOverride : FinancialEvidence
{
    public Guid AccountId { get; set; }
    public string Intent { get; set; } = "";
    public decimal MaxExcess { get; set; }
    public DateOnly Expires { get; set; }
    public Guid CreatedBy { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset? UsedAt { get; set; }
}
