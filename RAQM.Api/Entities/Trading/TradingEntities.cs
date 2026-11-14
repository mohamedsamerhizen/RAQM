using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.Entities.Trading;
public enum PartyKind
{
    Customer,
    Supplier
}

public enum TradeKind
{
    Invoice,
    Bill,
    CustomerCredit,
    SupplierCredit,
    OpeningReceivable,
    OpeningPayable
}

public enum TradeState
{
    Draft,
    Approved,
    Posted,
    Cancelled
}

public enum FundingKind
{
    Payment,
    CreditNote
}

public enum SettlementState
{
    Prepared,
    Posted,
    Reversed
}

public enum AccountRole
{
    Receivable,
    Payable,
    Revenue,
    Expense,
    Cash,
    TaxOutput,
    TaxInput,
    CustomerAdvance,
    SupplierAdvance,
    OpeningEquity
}

public enum SettlementStatus
{
    Unsettled,
    Partial,
    Settled
}

public sealed class TradingParty : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public PartyKind Kind { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? BillingAddress { get; set; }
    public string? TaxIdentifier { get; set; }
    public int PaymentTermsDays { get; set; }
    public decimal? CreditLimit { get; set; }
    public string Currency { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public Guid ControlAccountId { get; set; }
    public Guid DefaultAccountId { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TradingAccountMapping : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid AccountId { get; set; }
    public AccountRole Role { get; set; }
    public DateTimeOffset ActivatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid ActorId { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class TradingTaxCode : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public Guid OutputAccountId { get; set; }
    public Guid InputAccountId { get; set; }
    public Guid CreatedBy { get; set; }
}

public sealed class TradingTaxRate : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TaxCodeId { get; set; }
    public int Version { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly EffectiveTo { get; set; }
    public decimal RatePercent { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TradeDocument : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PartyId { get; set; }
    public TradeKind Kind { get; set; }
    public TradeState State { get; set; }
    public Guid? OriginalDocumentId { get; set; }
    public Guid ControlAccountId { get; set; }
    public string Currency { get; set; } = "";
    public int CurrencyPolicyVersion { get; set; } = 1;
    public int MinorUnits { get; set; }
    public string PartyName { get; set; } = "";
    public string? PartyTaxIdentifier { get; set; }
    public string? BillingAddress { get; set; }
    public DateOnly DocumentDate { get; set; }
    public DateOnly DueDate { get; set; }
    public DateOnly? ScheduledPaymentDate { get; set; }
    public DateOnly? PostingDate { get; set; }
    public string Description { get; set; } = "";
    public string? ExternalReference { get; set; }
    public string? SupplierReferenceKey { get; set; }
    public decimal NetTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public long? Sequence { get; set; }
    public string? Number { get; set; }
    public string? PostKey { get; set; }
    public string? PostRequestHash { get; set; }
    public Guid? JournalId { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? PostedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public List<TradeDocumentLine> Lines { get; set; } = [];
}

public sealed class TradeDocumentLine : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid? OriginalLineId { get; set; }
    public Guid? StockItemId { get; set; }
    public bool CancelUnissuedStock { get; set; }
    // Null denotes a historical line with no classification. Zero denotes a non-stocked invoice line.
    public decimal? StockSalesFactor { get; set; }
    public int Number { get; set; }
    public string Description { get; set; } = "";
    public Guid AccountId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public Guid? TaxRateId { get; set; }
    public Guid? TaxAccountId { get; set; }
    public decimal TaxPercent { get; set; }
    public bool TaxInclusive { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
}

public sealed class SettlementPayment : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PartyId { get; set; }
    public PartyKind PartyKind { get; set; }
    public bool IsRefund { get; set; }
    public Guid? RefundPaymentId { get; set; }
    public Guid? RefundCreditId { get; set; }
    public SettlementState State { get; set; }
    public string Currency { get; set; } = "";
    public Guid CashAccountId { get; set; }
    public Guid? BankAccountId { get; set; }
    public Guid ControlAccountId { get; set; }
    public Guid AdvanceAccountId { get; set; }
    public decimal Amount { get; set; }
    public decimal ControlAmount { get; set; }
    public decimal AdvanceAmount { get; set; }
    public DateOnly PostingDate { get; set; }
    public string Reference { get; set; } = "";
    public string Reason { get; set; } = "";
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string? Number { get; set; }
    public long? Sequence { get; set; }
    public Guid? JournalId { get; set; }
    public Guid? ReversalJournalId { get; set; }
    public DateOnly? ReversalDate { get; set; }
    public string? ReversalKey { get; set; }
    public string? ReversalReason { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? ReversedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PostedAt { get; set; }
}

public sealed class SettlementAllocation : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PartyId { get; set; }
    public PartyKind PartyKind { get; set; }
    public FundingKind FundingKind { get; set; }
    public Guid? PaymentId { get; set; }
    public Guid? CreditDocumentId { get; set; }
    public Guid TargetDocumentId { get; set; }
    public Guid? ReversesAllocationId { get; set; }
    public SettlementState State { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public decimal Amount { get; set; }
    public decimal ControlAmount { get; set; }
    public decimal AdvanceAmount { get; set; }
    public Guid ControlAccountId { get; set; }
    public Guid? AdvanceAccountId { get; set; }
    public Guid? JournalId { get; set; }
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
