using RAQM.Api.Entities.Banking;

namespace RAQM.Api.DTOs.Treasury;

public sealed record BookMovement(Guid LineId, Guid JournalId, DateOnly Date, decimal Amount, string Source, string? Reference,
    Guid? PaymentId, Guid? AttributedBankAccountId, string? OriginalSource = null);
public sealed record TreasuryPosition(Guid AccountId, string Code, TreasuryAccountKind Kind, string Currency, decimal Opening,
    decimal Incoming, decimal Outgoing, decimal Closing, decimal BankCharges, decimal TradingReceipts, decimal TradingDisbursements,
    DateOnly? LastReconciledThrough);
public sealed record TreasuryActivity(TreasuryPosition Position, IReadOnlyList<BookMovement> Lines, int Total);
