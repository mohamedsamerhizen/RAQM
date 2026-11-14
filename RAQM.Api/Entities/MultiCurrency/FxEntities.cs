using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.Entities.MultiCurrency;

public enum FxAccountRole { Receivable, Payable, CustomerAdvance, SupplierAdvance, RealizedGain, RealizedLoss, UnrealizedGain, UnrealizedLoss, TaxInput, TaxOutput }
public enum FxRateType { Spot, Closing }
public enum FxRateState { Draft, Approved, Superseded, Cancelled }
public enum FxState { Draft, Approved, Posted, Cancelled }
public enum FxKind { Invoice, Bill, Settlement, Advance, Credit, BankPurchase, BankSale, Revaluation, RevaluationCorrection }
public enum FxPositionKind { Receivable, Payable, CustomerAdvance, SupplierAdvance, CustomerRefund, SupplierRefund, Bank }

public sealed class FxConfiguration : FinancialEvidence
{
    public string FunctionalCurrency { get; set; } = "";
    public int SpotFallbackDays { get; set; }
    public bool RequireCloseRevaluation { get; set; }
}

public sealed class FxAccount : FinancialEvidence
{
    public Guid AccountId { get; set; }
    public FxAccountRole Role { get; set; }
}

public sealed class FxCurrency : FinancialEvidence
{
    public string Code { get; set; } = "";
}

public sealed class FxRate : FinancialEvidence
{
    public Guid CurrencyId { get; set; }
    public FxRateType Type { get; set; }
    public DateOnly Date { get; set; }
    public decimal ExchangeRate { get; set; }
    public int Revision { get; set; }
    public FxRateState State { get; set; }
    public Guid? ReplacesId { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public string Source { get; set; } = "";
    public string Evidence { get; set; } = "";
}

public sealed class FxPosition : FinancialEvidence
{
    public Guid CurrencyId { get; set; }
    public Guid AccountId { get; set; }
    public Guid? PartyId { get; set; }
    public FxPositionKind Kind { get; set; }
    public string Code { get; set; } = "";
    public DateOnly? DueDate { get; set; }
}

public sealed class FxOperation : FinancialEvidence
{
    public FxKind Kind { get; set; }
    public FxState State { get; set; }
    public DateOnly Date { get; set; }
    public Guid? PositionId { get; set; }
    public Guid? PartyId { get; set; }
    public Guid? OriginalId { get; set; }
    public Guid? JournalId { get; set; }
    public string? ReferenceKey { get; set; }
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string InputJson { get; set; } = "";
    public string? PlanJson { get; set; }
    public long? Sequence { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? PostedBy { get; set; }
}

public sealed class FxEffect : FinancialEvidence
{
    public Guid OperationId { get; set; }
    public Guid PositionId { get; set; }
    public Guid? RateId { get; set; }
    public DateOnly Date { get; set; }
    public long Sequence { get; set; }
    public decimal ForeignDelta { get; set; }
    public decimal BaseDelta { get; set; }
}

public sealed class FxBankStatement : FinancialEvidence
{
    public Guid PositionId { get; set; }
    public DateOnly Cutoff { get; set; }
    public decimal StatementForeign { get; set; }
    public decimal BookForeign { get; set; }
    public decimal BookBase { get; set; }
    public Guid ActorId { get; set; }
    public string Evidence { get; set; } = "";
}
