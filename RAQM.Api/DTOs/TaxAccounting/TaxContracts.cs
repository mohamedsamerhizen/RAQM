using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Entities.Trading;

namespace RAQM.Api.DTOs.TaxAccounting;

public sealed record TaxRegistrationRequest(string Code, string Country, string? Region, string Authority, string Number,
    string Residence, string Currency, DateOnly EffectiveOn, string Evidence);
public sealed record TaxControlRequest(TaxControlRole Role, Guid AccountId, string Evidence);
public sealed record TaxRuleRequest(string Group, string? ItemClass, Guid? PartyId, PartyKind Side, int Order, TaxTreatment Treatment,
    Guid? RateId, DateOnly EffectiveOn, DateOnly EndsOn, string Evidence);
public sealed record TaxPricingLine(string Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, bool Inclusive,
    string ItemClass, Guid? AccountId = null, string? ExemptionEvidence = null);
public sealed record TaxQuoteRequest(Guid PartyId, DateOnly Date, string Group, IReadOnlyList<TaxPricingLine> Lines);
public sealed record TaxCommercialRequest(TaxQuoteRequest Quote, DateOnly? DueDate, string Description, string? ExternalReference);
public sealed record AppliedTax(Guid RuleId, Guid? RateId, TaxTreatment Treatment, decimal Rate, decimal Base, decimal Amount, string PolicyEvidence);
public sealed record QuotedTaxLine(int Number, decimal Net, decimal Tax, decimal Total, AppliedTax[] Components, string? ExemptionEvidence);
public sealed record TaxQuote(string Currency, PartyKind Side, Guid PartyId, DateOnly Date, string Group, QuotedTaxLine[] Lines, string SellerName)
{
    public decimal Net => Lines.Sum(x => x.Net);
    public decimal Tax => Lines.Sum(x => x.Tax);
    public decimal Total => Lines.Sum(x => x.Total);
}
public sealed record TaxCommercialResponse(TradeDocumentResponse Document, Guid EvidenceId, TaxQuote Quote);
public sealed record TaxPeriodRequest(DateOnly From, DateOnly To, string Name);
public sealed record TaxOperationRequest(TaxOperationKind Kind, string Reference, DateOnly Date, string Evidence, Guid? PeriodId = null,
    Guid? BillId = null, Guid? BasisId = null, Guid? BankAccountId = null, TaxControlRole? Control = null, decimal Amount = 0,
    decimal Base = 0, decimal Rate = 0, decimal AccountingAdjustments = 0, decimal CarryingValue = 0, decimal TaxBase = 0,
    bool UnderlyingLiability = false, decimal RecognizableBasis = 0, DateOnly? ExpectedReversal = null,
    decimal RecoveryPercent = 100, decimal Threshold = 0, string? PolicyEvidence = null,
    bool PriorOrdinarySettlementsIneligible = false);
public sealed record TaxPostingPlan(Guid Actor, JournalDraftLine[] Lines, Guid? ReversesJournalId, decimal TaxableBase,
    decimal Assessed, decimal Recoverable, decimal Nonrecoverable, decimal Unrecognized,
    Guid[]? PriorOrdinaryAllocationIds = null);
public sealed record TaxReturnRequest(Guid? SupersedesId, string Evidence);
public sealed record DeferredPreviewRequest(decimal CarryingValue, decimal TaxBase, bool UnderlyingLiability, decimal Rate, decimal RecognizableBasis);
