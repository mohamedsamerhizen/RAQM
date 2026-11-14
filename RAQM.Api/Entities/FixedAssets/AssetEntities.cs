using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.Entities.FixedAssets;

public enum AssetStatus { Draft, Approved, Capitalized, InService, Disposed, WrittenOff }
public enum PhysicalStatus { InUse, Idle, UnderMaintenance, Retired }
public enum AssetControlRole { Cost, AccumulatedDepreciation, AccumulatedImpairment, Clearing }
public enum AssetOperationKind { Acquisition, Opening, AvailableForUse, Depreciation, Estimate, Transfer, Improvement, Repair, Impairment, Recovery, Sale, WriteOff }
public enum AssetOperationState { Draft, Approved, Posted, Cancelled }
public enum CapitalFunding { Direct, ExistingCost, ClearBill }

public sealed class AssetCategory : FinancialEvidence
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Depreciable { get; set; }
    public bool MandatoryClose { get; set; }
    public decimal Threshold { get; set; }
    public Guid DepreciationExpenseId { get; set; }
    public Guid ImpairmentLossId { get; set; }
    public Guid GainId { get; set; }
    public Guid LossId { get; set; }
    public Guid CreatedBy { get; set; }
    public List<AssetControlAccount> Controls { get; set; } = [];
}

public sealed class AssetControlAccount : FinancialEvidence
{
    public Guid CategoryId { get; set; }
    public Guid AccountId { get; set; }
    public AssetControlRole Role { get; set; }
}

public sealed class FixedAsset : FinancialEvidence
{
    public Guid CategoryId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Currency { get; set; } = "";
    public DateOnly AcquisitionDate { get; set; }
    public Guid? SupplierId { get; set; }
    public string PurchaseEvidence { get; set; } = "";
    public string AssignmentJson { get; set; } = "{}";
    public AssetStatus Status { get; set; }
    public DateOnly? RecognitionDate { get; set; }
    public DateOnly? AvailableDate { get; set; }
    public DateOnly? DisposalDate { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AssetComponent> Components { get; set; } = [];
}

public sealed class AssetComponent : FinancialEvidence
{
    public Guid AssetId { get; set; }
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public decimal ProposedCost { get; set; }
    public decimal InitialResidual { get; set; }
    public int LifeMonths { get; set; }
    public decimal OpeningDepreciation { get; set; }
    public decimal OpeningImpairment { get; set; }
}

public sealed class AssetOperation : FinancialEvidence
{
    public Guid? AssetId { get; set; }
    public Guid? PeriodId { get; set; }
    public Guid? OpeningJournalId { get; set; }
    public AssetOperationKind Kind { get; set; }
    public AssetOperationState State { get; set; }
    public DateOnly Date { get; set; }
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string? PlanJson { get; set; }
    public Guid? JournalId { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public long? Sequence { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? PostedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PostedAt { get; set; }
}

public sealed class AssetMovement : FinancialEvidence
{
    public Guid OperationId { get; set; }
    public Guid ComponentId { get; set; }
    public Guid? DepreciationPeriodId { get; set; }
    public DateOnly Date { get; set; }
    public long Sequence { get; set; }
    public decimal Cost { get; set; }
    public decimal Depreciation { get; set; }
    public decimal Impairment { get; set; }
    public decimal ShadowDepreciation { get; set; }
    public decimal Units { get; set; }
    public decimal? RemainingOverride { get; set; }
    public decimal? ResidualOverride { get; set; }
    public decimal DepreciationCharge { get; set; }
}

public sealed class AssetSourceClaim : FinancialEvidence
{
    public Guid OperationId { get; set; }
    public Guid JournalLineId { get; set; }
}
