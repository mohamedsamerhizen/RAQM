using RAQM.Api.Entities.Budgeting;

namespace RAQM.Api.DTOs.Budgeting;

public enum AllocationMethod { Equal, Weights, Percent, Manual }
public sealed record BudgetRequest(Guid FiscalYearId, BudgetKind Kind, string Code, string Name, string Description, decimal ApprovalLimit);
public sealed record AnnualAllocation(Guid AccountId, decimal Total, AllocationMethod Method, IReadOnlyList<decimal>? Values = null);
public sealed record BudgetVersionRequest(string Key, string Evidence, IReadOnlyList<AnnualAllocation>? Lines = null,
    Guid? CopyVersionId = null, bool SeedFromPriorActuals = false);
public sealed record BudgetAction(Guid Version, string Reason, Guid? ReviewerId = null, DateOnly? EffectiveDate = null);
public sealed record BudgetTransferRequest(string Key, Guid ActiveVersionId, Guid FromAccountId, Guid FromPeriodId,
    Guid ToAccountId, Guid ToPeriodId, decimal Amount, string Evidence);
public sealed record PolicyRequest(Guid AccountId, BudgetKind Kind, ControlMode Mode, DateOnly EffectiveDate, decimal WarningPercent, string Reason);
public sealed record OverrideRequest(Guid AccountId, string Intent, decimal MaxExcess, DateOnly Expires, string Reason);
public sealed record BudgetAvailability(Guid AccountId, decimal Budget, decimal Actual, decimal Commitments)
{
    public decimal Available => Budget - Actual - Commitments;
}
