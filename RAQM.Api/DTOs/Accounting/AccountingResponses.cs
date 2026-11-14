using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.DTOs.Accounting;
public sealed record CurrencyPolicyResponse(int Version, IReadOnlyDictionary<string, int> Currencies);
public sealed record AccountResponse(Guid Id, string Code, string Name, string? Description, AccountClass Classification, BalanceSide NormalBalance, bool IsContra, Guid? ParentId, bool IsPosting, bool IsActive, Guid Version)
{
    public static AccountResponse From(LedgerAccount x) => new(x.Id, x.Code, x.Name, x.Description, x.Classification, x.NormalBalance, x.IsContra, x.ParentId, x.IsPosting, x.IsActive, x.Version);
}

public sealed record PeriodResponse(Guid Id, Guid FiscalYearId, int Number, DateOnly StartDate, DateOnly EndDate, PeriodState State, Guid Version)
{
    public static PeriodResponse From(AccountingPeriod x) => new(x.Id, x.FiscalYearId, x.Number, x.StartDate, x.EndDate, x.State, x.Version);
}

public sealed record FiscalYearResponse(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, IReadOnlyList<PeriodResponse> Periods);
public sealed record JournalLineResponse(Guid Id, int Number, Guid AccountId, decimal Debit, decimal Credit, string? Description);
public sealed record JournalResponse(Guid Id, JournalState State, string? Number, DateOnly PostingDate, DateOnly DocumentDate, string Description, string? Reference, string Currency, int CurrencyPolicyVersion, int MinorUnits, Guid? PeriodId, string SourceModule, Guid SourceDocumentId, Guid? ReversesJournalId, Guid Version, Guid CreatedBy, DateTimeOffset CreatedAt, Guid UpdatedBy, DateTimeOffset UpdatedAt, Guid? PostedBy, DateTimeOffset? PostedAt, string? PostingReason, IReadOnlyList<JournalLineResponse> Lines)
{
    public static JournalResponse From(JournalEntry x) => new(x.Id, x.State, x.Number, x.PostingDate, x.DocumentDate, x.Description, x.Reference, x.Currency, x.CurrencyPolicyVersion, x.MinorUnits, x.PeriodId, x.SourceModule, x.SourceDocumentId, x.ReversesJournalId, x.Version, x.CreatedBy, x.CreatedAt, x.UpdatedBy, x.UpdatedAt, x.PostedBy, x.PostedAt, x.PostingReason, x.Lines.OrderBy(l => l.Number).Select(l => new JournalLineResponse(l.Id, l.Number, l.AccountId, l.Debit, l.Credit, l.Description)).ToList());
}

public sealed record JournalValidationResponse(Guid JournalId, decimal Debit, decimal Credit, bool Valid);
public sealed record OpeningBatchResponse(Guid Id, string BatchKey, string Name, Guid JournalId, JournalState State);
public sealed record AuditResponse(Guid Id, Guid EntityId, string Action, Guid ActorId, DateTimeOffset At, string? Reason);
public sealed record LedgerTransaction(Guid JournalId, string JournalNumber, Guid LineId, Guid AccountId, int LineNumber, DateOnly PostingDate, DateOnly DocumentDate, string Description, string? Reference, string SourceModule, Guid SourceDocumentId, Guid? ReversesJournalId, decimal Debit, decimal Credit, decimal RunningBalance);
public sealed record AccountLedgerResponse(Guid AccountId, string Currency, DateOnly From, DateOnly To, decimal OpeningBalance, decimal DebitMovement, decimal CreditMovement, decimal ClosingBalance, PageResult<LedgerTransaction> Transactions);
public sealed record TrialBalanceLine(Guid AccountId, string Code, string Name, Guid? ParentId, bool IsPosting, decimal OpeningBalance, decimal DebitMovement, decimal CreditMovement, decimal ClosingBalance, decimal ClosingDebit, decimal ClosingCredit);
public sealed record TrialBalanceResponse(string Currency, DateOnly From, DateOnly To, IReadOnlyList<TrialBalanceLine> Accounts, decimal DebitMovement, decimal CreditMovement, decimal ClosingDebit, decimal ClosingCredit);
