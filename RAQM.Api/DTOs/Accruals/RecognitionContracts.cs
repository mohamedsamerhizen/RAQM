using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accruals;

namespace RAQM.Api.DTOs.Accruals;

public sealed record ScheduleRequest(RecognitionKind Kind, string Name, string Reference, DateOnly FirstDate, int Occurrences,
    int EveryMonths, decimal Total, Guid? DebitAccountId = null, Guid? CreditAccountId = null,
    Guid? BasisLineId = null, IReadOnlyList<JournalDraftLine>? Template = null, int? ReverseAfterDays = null);
public sealed record ScheduleAction(Guid Version, string Reason);
public sealed record RecognitionExecution(DateOnly Through, bool AllowBackdated, string Reason);
public sealed record RecognitionReversal(DateOnly Date, string Key, string Reason, bool AllowBackdated = false);
public sealed record RecognitionPreview(Guid Id, int Number, DateOnly Date, decimal Amount, DateOnly? ReverseDue, IReadOnlyList<JournalDraftLine> Lines);
public sealed record RecognitionResult(Guid OccurrenceId, Guid? JournalId, string? Error);
public sealed record ScheduleReconciliation(Guid Id, decimal Planned, decimal Posted, decimal Reversed, decimal Pending,
    decimal RemainingBasis, decimal ExpectedNet, decimal LedgerNet, decimal Difference);
