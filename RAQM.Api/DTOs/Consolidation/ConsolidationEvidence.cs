using RAQM.Api.Entities.CorporateGroups;

namespace RAQM.Api.DTOs.Consolidation;

public sealed record OwnershipBasis(Guid MemberId, Guid InvestmentAccountId, Guid ContributedEquityAccountId, string Evidence);
public sealed record CompanyCutoff(Guid CompanyId, string Currency, Guid FiscalYearId, Guid[] PeriodVersions, long JournalSequence);
public sealed record GroupContribution(Guid CompanyId, Guid LedgerAccountId, Guid JournalId, Guid LineId, DateOnly Date, Guid MappingId,
    Guid GroupAccountId, decimal Functional, decimal Rate, Guid? RateId, decimal Translated, bool InternalPair,
    string Source = "", string? OriginalSource = null, string? Reference = null);
public sealed record GroupAdjustmentLine(string Layer, string Reference, Guid GroupAccountId, decimal Signed, string Evidence,
    Guid? CompanyId = null, Guid? SourceId = null, Guid[]? Companies = null);
public sealed record GroupReciprocalEvidence(Guid PolicyId, Guid ReviewId, decimal OpenAmount, Guid[] PostedPairIds);
public sealed record ConsolidationSnapshot(string Currency, string AccountingPolicyEvidence, CompanyCutoff[] Companies, GroupOwnership[] Ownership, GroupAccount[] Accounts,
    GroupContribution[] Contributions, GroupAdjustmentLine[] Adjustments, GroupReciprocalEvidence[] ReciprocalReviews);
