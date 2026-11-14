using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.FinancialDimensions;

namespace RAQM.Api.DTOs.FinancialDimensions;

public sealed record DefinitionRequest(DimensionKind Kind, string Code, string Name, string Description);
public sealed record ValueRequest(Guid DefinitionId, string Code, string Name, string Classification, DateOnly EffectiveDate,
    Guid? ParentId = null, Guid? DepartmentId = null, Guid? BranchId = null, Guid? CostCenterId = null, Guid? ManagerId = null);
public sealed record DimensionDeactivate(DateOnly InactiveOn, string Reason);
public sealed record DimensionRuleRequest(Guid DefinitionId, Guid? AccountId, DimensionMode Mode, Guid? DefaultValueId,
    DateOnly EffectiveDate, IReadOnlyList<Guid>? AllowedValues, string Reason);
public sealed record DimensionPick(Guid DefinitionId, Guid? ValueId);
public sealed record DimensionLine(int Number, IReadOnlyList<DimensionPick> Values);
public sealed record DimensionBindingRequest(Guid SourceVersion, int ExpectedRevision, IReadOnlyList<DimensionLine> Lines, string Reason);
public sealed record ProjectRequest(ValueRequest Value, Guid? CustomerId, DateOnly Start, DateOnly End, string Description, string Evidence);
public sealed record ProjectAction(int Version, ProjectState State, DateOnly Date, string Reason);
public sealed record ProjectForecastRequest(DateOnly At, decimal CostToComplete, decimal RevenueToComplete, string Evidence);
public sealed record BudgetSliceRequest(Guid AllocationId, Guid ValueId, decimal Amount, bool HardLimit);
public sealed record AllocationTarget(Guid CostCenterId, decimal Weight, string? MeasurementEvidence = null, Guid? ProjectValueId = null);
public sealed record CostAllocationRequest(string Key, Guid SourceLineId, DateOnly Date, AllocationMode Mode, AllocationBasis Basis,
    IReadOnlyList<AllocationTarget> Targets, string Reason, string? MeasurementUnit = null, DateOnly? MeasurementDate = null);
public sealed record AllocationAction(Guid Version, string Reason, DateOnly? CorrectionDate = null, bool AllowBackdated = false);
public sealed record EffectiveDimension(Guid DefinitionId, Guid? ValueId, Guid? RuleId, Guid? BindingId, DimensionOrigin Origin);
public sealed record AllocationPlan(IReadOnlyList<JournalDraftLine> Lines, IReadOnlyList<DimensionLine> Dimensions, decimal Amount);
