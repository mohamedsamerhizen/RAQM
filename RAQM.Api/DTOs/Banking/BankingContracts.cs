using RAQM.Api.Entities.Banking;

namespace RAQM.Api.DTOs.Banking;

public sealed record BankProfileRequest(string Code, string Name, string? Swift);
public sealed record TreasuryAccountRequest(TreasuryAccountKind Kind, string Code, string Name, Guid LedgerAccountId,
    Guid? BankProfileId = null, string? AccountIdentifier = null, string? Iban = null, Guid? CustodianId = null, bool AcknowledgeExistingLedger = false);
public sealed record AccountChange(Guid Version, string Name, bool Active, string Reason, Guid? CustodianId = null);
public sealed record TreasuryAccountResponse(Guid Id, TreasuryAccountKind Kind, string Code, string Name, string Currency, Guid LedgerAccountId,
    Guid? BankProfileId, string? AccountMask, string? IbanMask, Guid? CustodianId, bool Active, Guid Version);
public sealed record StatementColumns(string BookingDate = "booking_date", string? ValueDate = "value_date", string Amount = "amount",
    string Reference = "reference", string Description = "description", string? StableId = null, string? Direction = null);
public sealed record StatementRequest(DateOnly Start, DateOnly End, decimal Opening, decimal Closing, string ExternalId,
    string Csv, string IdempotencyKey, StatementColumns? Columns = null, string Delimiter = ",");
public sealed record ImportIssue(int Row, string Message);
public sealed record StatementRow(int Row, DateOnly BookingDate, DateOnly? ValueDate, decimal Amount, string Reference,
    string Description, string? StableId, string OriginalFields);
public sealed record StatementPreview(IReadOnlyList<StatementRow> Rows, IReadOnlyList<ImportIssue> Errors, IReadOnlyList<ImportIssue> Warnings,
    decimal CalculatedClosing, string Digest);
public sealed record StatementSummary(Guid Id, Guid BankAccountId, DateOnly Start, DateOnly End, decimal Opening, decimal Closing,
    string ExternalId, int LineCount, DateTimeOffset ImportedAt);
public sealed record ConfirmMatch(Guid Version, Guid StatementLineId, IReadOnlyList<Guid> JournalLineIds, string Reason);
public sealed record ReviewAction(Guid Version, string Reason);
public sealed record MatchSuggestion(Guid StatementLineId, Guid JournalLineId, string Confidence, string Reason);
public sealed record OutstandingAge(string Bucket, int StatementLines, int BookLines, decimal Deposits, decimal Withdrawals);
public sealed record ReconciliationView(Guid Id, Guid StatementId, ReconciliationState State, Guid Version, decimal BankClosing,
    decimal BookClosing, decimal DepositsInTransit, decimal OutstandingWithdrawals, decimal Difference, int MatchedLines,
    IReadOnlyList<BankStatementLine> UnmatchedStatement, IReadOnlyList<DTOs.Treasury.BookMovement> UnmatchedBook,
    IReadOnlyList<MatchSuggestion> Suggestions, DateTimeOffset? FinalizedAt, IReadOnlyList<OutstandingAge> Aging);
