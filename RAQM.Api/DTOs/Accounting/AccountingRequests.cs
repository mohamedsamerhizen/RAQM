using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.DTOs.Accounting;
public sealed record AccountRequest(string Code, string Name, string? Description, AccountClass Classification, bool IsPosting, Guid? ParentId = null, bool IsContra = false, bool IsActive = true);
public sealed record UpdateAccountRequest(AccountRequest Settings, Guid Version);
public sealed record InitializeAccountsRequest(string Template = "basic-v1");
public sealed record FiscalYearRequest(int StartYear, string Name);
public sealed record PeriodStateRequest(PeriodState State, string Reason, Guid Version);
public sealed record JournalRequest(DateOnly PostingDate, DateOnly DocumentDate, string Description, string? Reference, IReadOnlyList<JournalDraftLine> Lines);
public sealed record UpdateJournalRequest(JournalRequest Draft, Guid Version);
public sealed record PostJournalRequest(string IdempotencyKey, Guid Version, bool AllowBackdated = false, string? Reason = null);
public sealed record ReverseJournalRequest(DateOnly PostingDate, string Reason, string IdempotencyKey, bool AllowBackdated = false);
public sealed record OpeningBalanceRequest(string BatchKey, string Name, DateOnly PostingDate, IReadOnlyList<JournalDraftLine> Balances);
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
