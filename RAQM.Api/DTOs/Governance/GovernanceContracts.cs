using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Entities.InternalControls;

namespace RAQM.Api.DTOs.Governance;

public sealed record ApprovalBand(decimal Through, int Reviewers, Guid[]? RequiredReviewers = null);
public sealed record PolicyRules(ApprovalBand[] Bands, bool IndependentExecution, bool RequireExplicitGrants,
    bool SeparateAssignments, int SupplierConflictDays, int ExpiryHours, bool Waivable);
public sealed record PolicyInput(FinancialDuty Duty, string Code, string Description, Guid OwnerId, PolicyMode Mode,
    DateTimeOffset EffectiveAt, PolicyRules Rules);
public sealed record GrantInput(Guid UserId, FinancialDuty Duty, GovernancePermission Permission, DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt, bool Emergency, string Reason);
public sealed record DelegationInput(Guid DelegateId, FinancialDuty Duty, DateTimeOffset StartsAt, DateTimeOffset ExpiresAt, string Reason);
public sealed record AuthorizationInput(FinancialDuty Duty, Guid? SourceId, PaymentRequest? Payment, string Reason);
public sealed record ReviewInput(ReviewDecision Decision, Guid? DelegationId, string Reason);
public sealed record ExceptionInput(string Reason, string CompensatingControl, DateTimeOffset ExpiresAt);
public sealed record GovernanceReason(string Reason);
public sealed record FinancialIntent(FinancialDuty Duty, Guid SourceId, Guid? SubjectId, Guid MakerId, string Version, decimal Amount, string Currency);
public sealed record InternalControlInput(string Code, string Objective, string Process, string Frequency, string EvidenceRequired, bool Preventive,
    bool CloseBlocking, ControlCheck Check, Guid? RiskId, Guid OwnerId, DateOnly EffectiveOn);
public sealed record EvaluateControlInput(DateOnly From, DateOnly Cutoff, Guid? EngagementId, string Method, string? ManualObservation);
public sealed record ReviewControlInput(ControlOutcome Conclusion, string Evidence);
public sealed record WorkInput(GovernanceWorkKind Kind, string Title, Guid OwnerId, DateOnly DueOn, string Category, string Process,
    string Objective, string Plan, int Impact = 1, int Likelihood = 1, int ResidualImpact = 1, int ResidualLikelihood = 1,
    Guid? ControlId = null, Guid? RiskId = null, Guid? EvaluationId = null, Guid? ParentId = null, Guid? GrantId = null);
public sealed record WorkAction(Guid Version, string Reason, bool RevokeGrant = false);
public sealed record EvidenceInput(Guid? WorkId, Guid? EvaluationId, Guid? SourceId, string SourceType, string Name, string Provenance,
    string Sha256, bool LegalHold, DateOnly RetainThrough);
