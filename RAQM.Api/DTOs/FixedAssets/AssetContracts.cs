using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.FixedAssets;

namespace RAQM.Api.DTOs.FixedAssets;

public sealed record CategoryRequest(string Code, string Name, bool Depreciable, bool MandatoryClose, decimal Threshold,
    Guid CostId, Guid AccumulatedDepreciationId, Guid DepreciationExpenseId, Guid ImpairmentLossId,
    Guid AccumulatedImpairmentId, Guid GainId, Guid LossId, Guid? ClearingId = null);
public sealed record AssetAssignment(string Location, string? Branch, string? Department, string? CostCenter, Guid? CustodianId,
    PhysicalStatus PhysicalStatus = PhysicalStatus.InUse);
public sealed record ComponentRequest(string Name, decimal Cost, decimal Residual, int LifeMonths,
    decimal OpeningDepreciation = 0, decimal OpeningImpairment = 0);
public sealed record RegisterAssetRequest(Guid CategoryId, string Code, string Name, string Description, DateOnly AcquisitionDate,
    Guid? SupplierId, string PurchaseEvidence, AssetAssignment Assignment, IReadOnlyList<ComponentRequest> Components);
public sealed record AssetOperationRequest(AssetOperationKind Kind, DateOnly Date, string Key, string Reason, string Evidence,
    Guid? AssetId = null, Guid? ComponentId = null, Guid? PeriodId = null, CapitalFunding? Funding = null,
    decimal Amount = 0, Guid? OffsetAccountId = null, IReadOnlyList<Guid>? SourceLines = null,
    int? RemainingMonths = null, decimal? Residual = null, AssetAssignment? Assignment = null,
    Guid? OpeningJournalId = null, IReadOnlyList<Guid>? OpeningAssets = null, DateOnly? HistoricalAvailableDate = null,
    bool ReplacesExistingComponent = false, decimal DisposalCosts = 0, Guid? RepairExpenseId = null, DateOnly? PhysicalDisposalDate = null);
public sealed record AssetAction(Guid Version, string Reason, bool AllowBackdated = false);
public sealed record AssetMovementDraft(Guid ComponentId, Guid? DepreciationPeriodId = null, decimal Cost = 0, decimal Depreciation = 0,
    decimal Impairment = 0, decimal ShadowDepreciation = 0, decimal Units = 0, decimal? RemainingOverride = null,
    decimal? ResidualOverride = null, decimal DepreciationCharge = 0);
public sealed record AssetPlan(IReadOnlyList<JournalDraftLine> Lines, IReadOnlyList<AssetMovementDraft> Movements, IReadOnlyList<Guid> Sources,
    IReadOnlyList<Guid> Assets, string Basis);
public sealed record ComponentBalance(Guid ComponentId, decimal Cost, decimal Depreciation, decimal Impairment,
    decimal ShadowDepreciation, decimal Residual, decimal RemainingMonths, decimal ReversibleImpairment,
    decimal? DepreciationBasis = null, decimal? BasisMonths = null, decimal? ShadowBasis = null, decimal? ShadowBasisMonths = null)
{
    public decimal NetBookValue => Cost - Depreciation - Impairment;
}
public sealed record AssetPosition(Guid Id, Guid CategoryId, string Code, string Name, AssetStatus Status, DateOnly? RecognitionDate,
    DateOnly? AvailableDate, DateOnly? DisposalDate, AssetAssignment Assignment, IReadOnlyList<ComponentBalance> Components,
    DateOnly AcquisitionDate, string Description, string Currency, Guid? SupplierId, string PurchaseEvidence)
{
    public decimal Cost => Components.Sum(x => x.Cost);
    public decimal Depreciation => Components.Sum(x => x.Depreciation);
    public decimal Impairment => Components.Sum(x => x.Impairment);
    public decimal NetBookValue => Components.Sum(x => x.NetBookValue);
}
public sealed record AssetReconciliation(Guid CategoryId, string Code, AssetControlRole Role, Guid AccountId, decimal Register,
    decimal Ledger, decimal Difference, bool Mandatory);
public sealed record AssetSummary(string Group, int Assets, decimal Cost, decimal Depreciation, decimal Impairment, decimal NetBookValue);
public sealed record DepreciationForecast(Guid ComponentId, Guid PeriodId, DateOnly PeriodEnd, decimal Charge, decimal NetBookValue, decimal RemainingMonths);
public sealed record AssetPeriodReport(DateOnly From, DateOnly To, decimal Capitalized, decimal Improvements, decimal Depreciation,
    decimal ImpairmentNet, decimal DisposalCost, decimal Proceeds, decimal DisposalGainLoss, decimal Repairs,
    IReadOnlyList<Guid> ApprovedUncapitalized, IReadOnlyList<Guid> FullyDepreciatedInUse, IReadOnlyList<Guid> NearUsefulLifeEnd,
    IReadOnlyList<AssetSummary> Categories, IReadOnlyList<AssetSummary> Branches, IReadOnlyList<AssetSummary> Departments,
    IReadOnlyList<AssetReconciliation> Reconciliation);
