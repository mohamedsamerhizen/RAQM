using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.Entities.Accruals;

public enum RecognitionKind { AccruedExpense, AccruedRevenue, PrepaidExpense, DeferredRevenue, Recurring }
public enum RecognitionState { Draft, Approved, Cancelled }

public sealed class RecognitionSchedule : FinancialEvidence
{
    public RecognitionKind Kind { get; set; }
    public RecognitionState State { get; set; }
    public string Reference { get; set; } = "";
    public string Name { get; set; } = "";
    public string Currency { get; set; } = "";
    public Guid? BasisLineId { get; set; }
    public Guid? DebitAccountId { get; set; }
    public Guid? CreditAccountId { get; set; }
    public string TemplateJson { get; set; } = "[]";
    public decimal Total { get; set; }
    public int? ReverseAfterDays { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<RecognitionOccurrence> Occurrences { get; set; } = [];
}

public sealed class RecognitionOccurrence : FinancialEvidence
{
    public Guid ScheduleId { get; set; }
    public int Number { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public DateOnly? ReverseDue { get; set; }
    public Guid? JournalId { get; set; }
    public Guid? ReversalJournalId { get; set; }
    public string? ReversalHash { get; set; }
}
