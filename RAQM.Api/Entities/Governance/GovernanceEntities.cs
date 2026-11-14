using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.Entities.Governance;

public enum FinancialDuty
{
    SupplierPayment, BankTransfer, ManualJournal, ProcurementApproval, StockAdjustment, AssetDisposal,
    BudgetOverride, PeriodClose, FxRevaluation, TaxProvisionApproval, ConsolidationPublication, SupplierDetails,
    SupplierCreation, SupplierBillApproval, BankConfiguration, BankFinalization, GoodsReceipt, AssetCapitalization,
    BudgetApproval, FxRateApproval, IntercompanyApproval, TaxRuleApproval, TaxProvisionPosting, TaxReturnApproval, AuditWorkspace
}

public enum PolicyMode { Enforce, Monitor, Retired }
public enum GovernancePermission { Execute, Review, Audit }
public enum AuthorizationState { Open, Rejected, Cancelled, Used }
public enum ReviewDecision { Approve, Reject }
public enum GovernanceOutcome { Recorded, Allowed, MonitorFinding, Denied }

public abstract class GovernanceRecord : ICompanyFinancialEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CompanyId { get; set; }
}

public sealed class GovernancePolicy : GovernanceRecord
{
    public FinancialDuty Duty { get; set; }
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid OwnerId { get; set; }
    public int Version { get; set; }
    public PolicyMode Mode { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public string RulesJson { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

public sealed class GovernanceGrant : GovernanceRecord
{
    public Guid UserId { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? RevokedBy { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public FinancialDuty Duty { get; set; }
    public GovernancePermission Permission { get; set; }
    public bool Emergency { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class GovernanceDelegation : GovernanceRecord
{
    public FinancialDuty Duty { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid DelegateId { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class GovernanceAuthorization : GovernanceRecord
{
    public Guid PolicyId { get; set; }
    public FinancialDuty Duty { get; set; }
    public Guid SourceId { get; set; }
    public Guid? SubjectId { get; set; }
    public Guid MakerId { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? UsedBy { get; set; }
    public string Version { get; set; } = "";
    public string IntentHash { get; set; } = "";
    public string? ContextJson { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public AuthorizationState State { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class GovernanceReview : GovernanceRecord
{
    public Guid AuthorizationId { get; set; }
    public Guid ReviewerId { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid? DelegationId { get; set; }
    public int Level { get; set; }
    public ReviewDecision Decision { get; set; }
    public DateTimeOffset At { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class GovernanceException : GovernanceRecord
{
    public Guid AuthorizationId { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public string Reason { get; set; } = "";
    public string CompensatingControl { get; set; } = "";
}

public sealed class GovernanceEvent : GovernanceRecord
{
    public long Sequence { get; set; }
    public Guid ActorId { get; set; }
    public string Category { get; set; } = "";
    public string Type { get; set; } = "";
    public FinancialDuty? Duty { get; set; }
    public Guid SourceId { get; set; }
    public Guid? SubjectId { get; set; }
    public Guid? JournalId { get; set; }
    public Guid? PolicyId { get; set; }
    public Guid? AuthorizationId { get; set; }
    public Guid? RelatedId { get; set; }
    public string? PriorState { get; set; }
    public string? NewState { get; set; }
    public string Correlation { get; set; } = "";
    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public GovernanceOutcome Outcome { get; set; }
    public string Reason { get; set; } = "";
    public string PreviousHash { get; set; } = "";
    public string Hash { get; set; } = "";
}
