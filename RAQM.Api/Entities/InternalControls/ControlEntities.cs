using RAQM.Api.Entities.Governance;

namespace RAQM.Api.Entities.InternalControls;

public enum ControlCheck { JournalBalance, TradingReconciliation, BankCoverage, InventoryReadiness, ApprovalEvidence, ExpiredAccess, Manual }
public enum ControlOutcome { Pass, Fail, Indeterminate }
public enum GovernanceWorkKind { Risk, Engagement, Finding, AccessReview }
public enum GovernanceWorkState { Draft, Approved, InProgress, Review, Closed }

public sealed class InternalControl : GovernanceRecord
{
    public string Code { get; set; } = "";
    public int Version { get; set; }
    public string Objective { get; set; } = "";
    public string Process { get; set; } = "";
    public string Frequency { get; set; } = "";
    public string EvidenceRequired { get; set; } = "";
    public bool Preventive { get; set; }
    public bool CloseBlocking { get; set; }
    public ControlCheck Check { get; set; }
    public Guid? RiskId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateOnly EffectiveOn { get; set; }
}

public sealed class ControlEvaluation : GovernanceRecord
{
    public Guid ControlId { get; set; }
    public Guid? EngagementId { get; set; }
    public Guid TesterId { get; set; }
    public Guid? ReviewerId { get; set; }
    public DateOnly From { get; set; }
    public DateOnly Cutoff { get; set; }
    public DateTimeOffset At { get; set; }
    public ControlOutcome Outcome { get; set; }
    public ControlOutcome? Conclusion { get; set; }
    public string Method { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public string? ReviewEvidence { get; set; }
}

public sealed class GovernanceWork : GovernanceRecord
{
    public GovernanceWorkKind Kind { get; set; }
    public GovernanceWorkState State { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public Guid? ControlId { get; set; }
    public Guid? RiskId { get; set; }
    public Guid? EvaluationId { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid OwnerId { get; set; }
    public Guid? SubmittedBy { get; set; }
    public Guid? ClosedBy { get; set; }
    public DateOnly DueOn { get; set; }
    public int Severity { get; set; }
    public string Title { get; set; } = "";
    public string DetailsJson { get; set; } = "";
    public string? Response { get; set; }
}

public sealed class EvidenceReference : GovernanceRecord
{
    public Guid? WorkId { get; set; }
    public Guid? EvaluationId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid? SourceId { get; set; }
    public string SourceType { get; set; } = "";
    public string Name { get; set; } = "";
    public string Provenance { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool LegalHold { get; set; }
    public DateOnly RetainThrough { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
