using RAQM.Api.Entities.Banking;

namespace RAQM.Api.Entities.CashManagement;

public enum TreasuryMovementKind { Transfer, BankFee, BankInterest, PettyExpense, CountCorrection, CashReceipt, CashPayment }
public enum TreasuryMovementState { PendingApproval, Posted, Reversed }

public sealed class TreasuryMovement : BankEvidence
{
    public TreasuryMovementKind Kind { get; set; }
    public TreasuryMovementState State { get; set; }
    public Guid AccountId { get; set; }
    public Guid? DestinationId { get; set; }
    public Guid? OffsetLedgerId { get; set; }
    public Guid? StatementLineId { get; set; }
    public Guid? CashCountId { get; set; }
    public Guid DebitLedgerId { get; set; }
    public Guid CreditLedgerId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly PostingDate { get; set; }
    public string Reference { get; set; } = "";
    public string Reason { get; set; } = "";
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid? JournalId { get; set; }
    public Guid? ReversalJournalId { get; set; }
    public string? ReversalKey { get; set; }
    public string? ReversalHash { get; set; }
    public string? ApprovalHash { get; set; }
    public Guid? PostedBy { get; set; }
    public Guid? ReversedBy { get; set; }
}

public sealed class CashCount : BankEvidence
{
    public Guid AccountId { get; set; }
    public DateOnly CountDate { get; set; }
    public decimal BookAmount { get; set; }
    public decimal CountedAmount { get; set; }
    public string Evidence { get; set; } = "";
    public Guid CountedBy { get; set; }
    public DateTimeOffset CountedAt { get; set; } = DateTimeOffset.UtcNow;
}
