using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.CorporateGroups;
using RAQM.Api.Entities.Intercompany;
using RAQM.Api.DTOs.Consolidation;

namespace RAQM.Api.DTOs.CorporateGroups;

public sealed record GroupRequest(Guid ParentId, string Code, string Name, string Description, string Currency, DateOnly StartsOn);
public sealed record OwnershipRequest(Guid InvestorId, Guid InvesteeId, InvestmentKind Kind, decimal Ownership, decimal Voting, DateOnly EffectiveOn, string Evidence);
public sealed record GroupAccountRequest(string Code, string Name, AccountClass Classification, bool Cash, Guid? ParentId);
public sealed record MappingRequest(Guid MemberId, Guid LedgerAccountId, Guid GroupAccountId, DateOnly EffectiveOn, string Evidence);
public sealed record PartnerRequest(Guid CreditorId, Guid DebtorId, Guid DueFromId, Guid DueToId, Guid RevenueId, Guid ExpenseId,
    Guid? CreditorBankId, Guid? DebtorBankId, DateOnly EffectiveOn, string Evidence);
public sealed record PairRequest(Guid PolicyId, PairKind Kind, Guid? BasisId, string Reference, DateOnly Date, decimal Amount, string Evidence,
    bool NoTaxApplicable = false, DateOnly? InterestFrom = null, decimal? InterestAnnualPercent = null);
public sealed record GroupReaderRequest(Guid UserId, bool Granted, string Evidence);
public sealed record GroupRetirementRequest(DateOnly EffectiveOn, string Evidence);
public sealed record GroupEvidenceRequest(string Evidence);
public sealed record TranslationRateRequest(Guid MemberId, Guid? EquityAccountId, DateOnly RateDate, DateOnly EffectiveOn, decimal Rate, string Evidence);
public sealed record WorksheetLine(Guid AccountId, decimal Debit, decimal Credit);
public sealed record WorksheetAdjustment(string Reference, string Evidence, IReadOnlyList<Guid> Companies, IReadOnlyList<WorksheetLine> Lines);
public sealed record GroupRunRequest(DateOnly From, DateOnly Cutoff, Guid? SupersedesId, Guid? TranslationReserveId,
    IReadOnlyList<WorksheetAdjustment>? Adjustments, IReadOnlyList<OwnershipBasis>? OwnershipBases, string AccountingPolicyEvidence);
