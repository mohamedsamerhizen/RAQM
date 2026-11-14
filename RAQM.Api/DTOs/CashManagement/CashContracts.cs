using RAQM.Api.Entities.CashManagement;

namespace RAQM.Api.DTOs.CashManagement;

public sealed record TransferRequest(Guid From, Guid To, decimal Amount, DateOnly Date, string Reference, string Reason, string IdempotencyKey);
public sealed record MovementAction(Guid Version, DateOnly Date, string Reason, string IdempotencyKey, bool AllowBackdated = false);
public sealed record CashMovementRequest(Guid AccountId, TreasuryMovementKind Kind, Guid OffsetLedgerId, decimal Amount,
    DateOnly Date, string Reference, string Reason, string IdempotencyKey, bool AllowBackdated = false);
public sealed record AdjustmentRequest(Guid StatementLineId, TreasuryMovementKind Kind, Guid OffsetLedgerId,
    Guid SessionVersion, string Reason, string IdempotencyKey, bool AllowBackdated = false);
public sealed record CashCountRequest(Guid AccountId, DateOnly Date, decimal CountedAmount, string Evidence);
public sealed record CountCorrectionRequest(Guid OffsetLedgerId, string Reason, string IdempotencyKey, bool AllowBackdated = false);
