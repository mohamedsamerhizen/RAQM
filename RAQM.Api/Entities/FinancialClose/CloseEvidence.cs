using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.Entities.FinancialClose;

public enum CloseWorkflowState { Preparing, Approved, Completed, Superseded }

public sealed class PeriodCloseSession : FinancialEvidence
{
    public Guid PeriodId { get; set; }
    public CloseWorkflowState State { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? ClosedBy { get; set; }
    public string? ApprovalHash { get; set; }
    public string? ApprovalReason { get; set; }
    public string? EvidenceJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
    public List<PeriodCloseTask> Tasks { get; set; } = [];
}

public sealed class PeriodCloseTask : FinancialEvidence
{
    public Guid SessionId { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public Guid ReviewerId { get; set; }
    public bool Required { get; set; }
    public string? Evidence { get; set; }
    public Guid? CompletedBy { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class CloseException : FinancialEvidence
{
    public Guid SessionId { get; set; }
    public string Detail { get; set; } = "";
    public bool Blocking { get; set; }
    public Guid RaisedBy { get; set; }
    public Guid? ResolvedBy { get; set; }
    public string? Resolution { get; set; }
    public DateTimeOffset RaisedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}
