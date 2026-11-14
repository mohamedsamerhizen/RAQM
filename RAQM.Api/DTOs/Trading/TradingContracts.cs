using RAQM.Api.Entities.Trading;

namespace RAQM.Api.DTOs.Trading;
public sealed record ControlAccountRequest(Guid Receivable, Guid Payable, Guid Revenue, Guid Expense, Guid TaxOutput, Guid TaxInput, Guid CustomerAdvance, Guid SupplierAdvance, Guid OpeningEquity, IReadOnlyList<Guid> CashAccounts, bool AcknowledgeLegacyActivity, string Reason);
public sealed record ControlAccountResponse(Guid AccountId, AccountRole Role, DateTimeOffset ActivatedAt);
public sealed record PartyRequest(PartyKind Kind, string Code, string Name, string? Email, string? Phone, string? BillingAddress, string? TaxIdentifier, int PaymentTermsDays, decimal? CreditLimit, Guid ControlAccountId, Guid DefaultAccountId, bool IsActive = true);
public sealed record UpdatePartyRequest(PartyRequest Party, Guid Version);
public sealed record PartyResponse(Guid Id, PartyKind Kind, string Code, string Name, string? Email, string? Phone, string? BillingAddress, string? TaxIdentifier, int PaymentTermsDays, decimal? CreditLimit, string Currency, Guid ControlAccountId, Guid DefaultAccountId, bool IsActive, Guid Version);
public sealed record TaxCodeRequest(string Code, string Name, Guid OutputAccountId, Guid InputAccountId, bool IsActive = true);
public sealed record TaxCodeResponse(Guid Id, string Code, string Name, Guid OutputAccountId, Guid InputAccountId, bool IsActive);
public sealed record TaxRateRequest(DateOnly EffectiveFrom, DateOnly EffectiveTo, decimal RatePercent);
public sealed record TaxRateResponse(Guid Id, Guid TaxCodeId, int Version, DateOnly EffectiveFrom, DateOnly EffectiveTo, decimal RatePercent);
public sealed record TradeLineRequest(string Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent = 0, Guid? AccountId = null, Guid? TaxRateId = null, bool TaxInclusive = false, Guid? OriginalLineId = null, Guid? StockItemId = null, bool CancelUnissuedStock = false);
public sealed record TradeDocumentRequest(Guid PartyId, TradeKind Kind, DateOnly DocumentDate, DateOnly? DueDate, string Description, string? ExternalReference, IReadOnlyList<TradeLineRequest> Lines, Guid? OriginalDocumentId = null, DateOnly? ScheduledPaymentDate = null);
public sealed record UpdateTradeDocumentRequest(TradeDocumentRequest Document, Guid Version);
public sealed record DocumentTransitionRequest(Guid Version, string Reason);
public sealed record ApplyFundingRequest(FundingKind Kind, Guid FundingId, decimal Amount);
public sealed record PostTradeDocumentRequest(Guid Version, DateOnly PostingDate, string IdempotencyKey, bool AllowBackdated = false, string? Reason = null, bool AllocateCreditToOriginal = true, IReadOnlyList<ApplyFundingRequest>? ApplyFunding = null);
public sealed record TradeLineResponse(Guid Id, int Number, string Description, Guid AccountId, Guid? OriginalLineId, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal DiscountAmount, Guid? TaxRateId, decimal TaxPercent, bool TaxInclusive, decimal NetAmount, decimal TaxAmount, decimal Total, bool CancelUnissuedStock = false);
public sealed record TradeDocumentResponse(Guid Id, Guid PartyId, TradeKind Kind, TradeState FinancialState, SettlementStatus SettlementStatus, string? Number, DateOnly DocumentDate, DateOnly DueDate, DateOnly? ScheduledPaymentDate, DateOnly? PostingDate, string Currency, string PartyName, string? PartyTaxIdentifier, string? BillingAddress, string Description, string? ExternalReference, Guid? OriginalDocumentId, decimal NetTotal, decimal TaxTotal, decimal Total, decimal Allocated, decimal Outstanding, Guid? JournalId, Guid Version, Guid CreatedBy, Guid? ApprovedBy, Guid? PostedBy, DateTimeOffset CreatedAt, DateTimeOffset? PostedAt, IReadOnlyList<TradeLineResponse> Lines);
public sealed record PaymentAllocationRequest(Guid DocumentId, decimal Amount);
public sealed record PaymentRequest(Guid PartyId, Guid CashAccountId, decimal Amount, DateOnly PostingDate, string Reference, string IdempotencyKey, bool TreatAsAdvance = false, bool AllowBackdated = false, string? Reason = null, IReadOnlyList<PaymentAllocationRequest>? Allocations = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? BankAccountId = null);
public sealed record RefundRequest(FundingKind FundingKind, Guid FundingId, Guid CashAccountId, decimal Amount, DateOnly PostingDate, string IdempotencyKey, string Reason, bool AllowBackdated = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? BankAccountId = null);
public sealed record ReversePaymentRequest(DateOnly PostingDate, string IdempotencyKey, string Reason, bool AllowBackdated = false);
public sealed record AllocationRequest(FundingKind FundingKind, Guid FundingId, Guid DocumentId, decimal Amount, DateOnly EffectiveDate, string IdempotencyKey, bool AllowBackdated = false, string? Reason = null);
public sealed record ReverseAllocationRequest(DateOnly EffectiveDate, string IdempotencyKey, string Reason, bool AllowBackdated = false);
public sealed record PaymentResponse(Guid Id, Guid PartyId, PartyKind PartyKind, bool IsRefund, SettlementState FinancialState, string Number, string Currency, decimal Amount, decimal ControlAmount, decimal AdvanceAmount, decimal Allocated, decimal Refunded, decimal Unapplied, DateOnly PostingDate, Guid JournalId, Guid? ReversalJournalId, DateOnly? ReversalDate,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? BankAccountId = null);
public sealed record AllocationResponse(Guid Id, Guid PartyId, Guid? PaymentId, Guid? CreditDocumentId, Guid TargetDocumentId, Guid? ReversesAllocationId, DateOnly EffectiveDate, decimal Amount, decimal ControlAmount, decimal AdvanceAmount, Guid? JournalId);
public sealed record AgingItem(Guid DocumentId, string Number, DateOnly DueDate, decimal Outstanding, int DaysOverdue, string Bucket);
public sealed record StatementEntry(DateOnly Date, string Type, Guid SourceId, string Reference, decimal Amount, decimal Debit, decimal Credit, decimal AdvanceMovement, decimal Balance);
public sealed record PartyStatement(Guid PartyId, PartyKind Kind, string Currency, DateOnly From, DateOnly To, decimal OpeningControlBalance, decimal ClosingControlBalance, decimal OutstandingDocuments, decimal UnappliedCredits, decimal UnappliedControlPayments, decimal Advances, IReadOnlyList<StatementEntry> Entries, IReadOnlyList<AgingItem> Aging);
public sealed record ControlReconciliation(string Name, decimal GeneralLedger, decimal Subledger, decimal Difference);
public sealed record TradingReconciliation(DateOnly AsOf, string Currency, IReadOnlyList<ControlReconciliation> Controls, decimal Payments, decimal NetAllocations, decimal Refunded, decimal Unapplied, decimal PaymentConservationDifference, decimal CreditNotes, decimal CreditAllocations, decimal CreditRefunds, decimal UnappliedCredits, decimal CreditConservationDifference, ControlReconciliation PaymentCash, bool Agrees);
public sealed record PartyAging(Guid PartyId, string Name, PartyKind Kind, decimal Total, IReadOnlyList<AgingItem> Documents);
public sealed record AgingResponse(DateOnly AsOf, string Currency, IReadOnlyList<PartyAging> Parties);
