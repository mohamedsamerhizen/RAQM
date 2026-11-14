using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.CashManagement;

namespace RAQM.Api.Helpers.CashManagement;

public static class TreasuryPosting
{
    public static string Source(TreasuryMovementKind kind) => $"Treasury.{kind}";
    public static IReadOnlyList<JournalDraftLine> Lines(TreasuryMovement movement) =>
        [new(movement.DebitLedgerId, movement.Amount, 0, "Treasury debit"),
         new(movement.CreditLedgerId, 0, movement.Amount, "Treasury credit")];
}
