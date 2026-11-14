using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.InternalControls;
using RAQM.Api.Entities.Organizations;

namespace RAQM.Api.Data.Configurations.Governance;

internal static class GovernanceMapping
{
    public static void Scope<T>(EntityTypeBuilder<T> b, string name) where T : GovernanceRecord
    {
        b.ToTable(name, "governance");
        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<Company>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrganizationId, x.CompanyId })
            .HasPrincipalKey(x => new { x.TenantId, x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }

    public static void Link<T, TParent>(EntityTypeBuilder<T> b, string field) where T : GovernanceRecord where TParent : class, ICompanyFinancialEntity =>
        b.HasOne<TParent>().WithMany().HasForeignKey(nameof(GovernanceRecord.CompanyId), field)
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);

    public static void User<T>(EntityTypeBuilder<T> b, string field) where T : GovernanceRecord =>
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(field).OnDelete(DeleteBehavior.Restrict);
}

public sealed class GovernancePolicyConfiguration : IEntityTypeConfiguration<GovernancePolicy>
{
    public void Configure(EntityTypeBuilder<GovernancePolicy> b)
    {
        GovernanceMapping.Scope(b, "Policies");
        GovernanceMapping.User(b, nameof(GovernancePolicy.OwnerId));
        GovernanceMapping.User(b, nameof(GovernancePolicy.CreatedBy));
        GovernanceMapping.User(b, nameof(GovernancePolicy.ApprovedBy));
        b.HasIndex(x => new { x.CompanyId, x.Duty, x.Version }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Duty, x.EffectiveAt });
        b.Property(x => x.RulesJson).HasColumnType("jsonb");
        b.ToTable(t => t.HasCheckConstraint("CK_Gov_Policy", """ "Version">0 AND "Mode" BETWEEN 0 AND 2 """));
    }
}
public sealed class GovernanceGrantConfiguration : IEntityTypeConfiguration<GovernanceGrant>
{
    public void Configure(EntityTypeBuilder<GovernanceGrant> b)
    {
        GovernanceMapping.Scope(b, "Grants");
        GovernanceMapping.User(b, nameof(GovernanceGrant.UserId));
        GovernanceMapping.User(b, nameof(GovernanceGrant.ApprovedBy));
        b.HasIndex(x => new { x.CompanyId, x.UserId, x.Duty, x.Permission, x.ExpiresAt });
        b.ToTable(t => t.HasCheckConstraint("CK_Gov_Grant", """ "ExpiresAt">"StartsAt" AND "Permission" BETWEEN 0 AND 2 AND (NOT "Emergency" OR "Permission"=0) """));
    }
}
public sealed class GovernanceDelegationConfiguration : IEntityTypeConfiguration<GovernanceDelegation>
{
    public void Configure(EntityTypeBuilder<GovernanceDelegation> b)
    {
        GovernanceMapping.Scope(b, "Delegations");
        GovernanceMapping.User(b, nameof(GovernanceDelegation.PrincipalId));
        GovernanceMapping.User(b, nameof(GovernanceDelegation.DelegateId));
        b.HasIndex(x => new { x.CompanyId, x.DelegateId, x.Duty, x.ExpiresAt });
        b.ToTable(t => t.HasCheckConstraint("CK_Gov_Delegation", """ "ExpiresAt">"StartsAt" AND "PrincipalId"<>"DelegateId" """));
    }
}
public sealed class GovernanceAuthorizationConfiguration : IEntityTypeConfiguration<GovernanceAuthorization>
{
    public void Configure(EntityTypeBuilder<GovernanceAuthorization> b)
    {
        GovernanceMapping.Scope(b, "Authorizations");
        GovernanceMapping.Link<GovernanceAuthorization, GovernancePolicy>(b, nameof(GovernanceAuthorization.PolicyId));
        GovernanceMapping.User(b, nameof(GovernanceAuthorization.MakerId));
        b.HasIndex(x => new { x.CompanyId, x.PolicyId, x.Duty, x.SourceId, x.IntentHash }).IsUnique().HasFilter(""" "State"=0 """);
        b.Property(x => x.IntentHash).HasMaxLength(64);
        b.Property(x => x.ContextJson).HasColumnType("jsonb");
        b.Property(x => x.Version).HasMaxLength(64);
        b.ToTable(t => t.HasCheckConstraint("CK_Gov_Auth", """ "Amount">=0 AND "State" BETWEEN 0 AND 3 AND (("State"=3)=("UsedBy" IS NOT NULL)) """));
    }
}
public sealed class GovernanceReviewConfiguration : IEntityTypeConfiguration<GovernanceReview>
{
    public void Configure(EntityTypeBuilder<GovernanceReview> b)
    {
        GovernanceMapping.Scope(b, "Reviews");
        GovernanceMapping.Link<GovernanceReview, GovernanceAuthorization>(b, nameof(GovernanceReview.AuthorizationId));
        GovernanceMapping.Link<GovernanceReview, GovernanceDelegation>(b, nameof(GovernanceReview.DelegationId));
        GovernanceMapping.User(b, nameof(GovernanceReview.ReviewerId));
        GovernanceMapping.User(b, nameof(GovernanceReview.PrincipalId));
        b.HasIndex(x => new { x.CompanyId, x.AuthorizationId, x.ReviewerId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.AuthorizationId, x.PrincipalId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.AuthorizationId, x.Level }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_Gov_Review", """ "Level" BETWEEN 1 AND 5 AND "Decision" IN(0,1) """));
    }
}
public sealed class GovernanceExceptionConfiguration : IEntityTypeConfiguration<GovernanceException>
{
    public void Configure(EntityTypeBuilder<GovernanceException> b)
    {
        GovernanceMapping.Scope(b, "Exceptions");
        GovernanceMapping.Link<GovernanceException, GovernanceAuthorization>(b, nameof(GovernanceException.AuthorizationId));
        b.HasIndex(x => new { x.CompanyId, x.AuthorizationId }).IsUnique();
    }
}
public sealed class GovernanceEventConfiguration : IEntityTypeConfiguration<GovernanceEvent>
{
    public void Configure(EntityTypeBuilder<GovernanceEvent> b)
    {
        GovernanceMapping.Scope(b, "Events");
        GovernanceMapping.Link<GovernanceEvent, GovernancePolicy>(b, nameof(GovernanceEvent.PolicyId));
        GovernanceMapping.Link<GovernanceEvent, GovernanceAuthorization>(b, nameof(GovernanceEvent.AuthorizationId));
        GovernanceMapping.Link<GovernanceEvent, JournalEntry>(b, nameof(GovernanceEvent.JournalId));
        GovernanceMapping.User(b, nameof(GovernanceEvent.ActorId));
        b.HasIndex(x => new { x.CompanyId, x.Sequence }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Type, x.SubjectId, x.RecordedAt });
        b.Property(x => x.Hash).HasMaxLength(64);
        b.Property(x => x.PreviousHash).HasMaxLength(64);
        b.ToTable(t => t.HasCheckConstraint("CK_Gov_Event", """ "Sequence">0 AND length("Hash")=64 AND length("PreviousHash")=64 """));
    }
}
public sealed class GovernanceWorkConfiguration : IEntityTypeConfiguration<GovernanceWork>
{
    public void Configure(EntityTypeBuilder<GovernanceWork> b)
    {
        GovernanceMapping.Scope(b, "Work");
        GovernanceMapping.Link<GovernanceWork, GovernanceWork>(b, nameof(GovernanceWork.ParentId));
        GovernanceMapping.Link<GovernanceWork, GovernanceWork>(b, nameof(GovernanceWork.RiskId));
        GovernanceMapping.Link<GovernanceWork, InternalControl>(b, nameof(GovernanceWork.ControlId));
        GovernanceMapping.Link<GovernanceWork, ControlEvaluation>(b, nameof(GovernanceWork.EvaluationId));
        b.Property(x => x.DetailsJson).HasColumnType("jsonb");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.CompanyId, x.Kind, x.State, x.DueOn });
        b.ToTable(t => t.HasCheckConstraint("CK_Gov_Work", """ "Kind" BETWEEN 0 AND 3 AND "State" BETWEEN 0 AND 4 AND "Severity" BETWEEN 1 AND 25 """));
    }
}
public sealed class InternalControlConfiguration : IEntityTypeConfiguration<InternalControl>
{
    public void Configure(EntityTypeBuilder<InternalControl> b)
    {
        GovernanceMapping.Scope(b, "Controls");
        GovernanceMapping.Link<InternalControl, GovernanceWork>(b, nameof(InternalControl.RiskId));
        b.HasIndex(x => new { x.CompanyId, x.Code, x.Version }).IsUnique();
    }
}
public sealed class ControlEvaluationConfiguration : IEntityTypeConfiguration<ControlEvaluation>
{
    public void Configure(EntityTypeBuilder<ControlEvaluation> b)
    {
        GovernanceMapping.Scope(b, "Evaluations");
        GovernanceMapping.Link<ControlEvaluation, InternalControl>(b, nameof(ControlEvaluation.ControlId));
        GovernanceMapping.Link<ControlEvaluation, GovernanceWork>(b, nameof(ControlEvaluation.EngagementId));
        b.Property(x => x.ResultJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.CompanyId, x.ControlId, x.Cutoff, x.At });
        b.ToTable(t => t.HasCheckConstraint("CK_Gov_Eval", """ "From"<="Cutoff" AND "Outcome" BETWEEN 0 AND 2 AND ("Conclusion" IS NULL OR "Conclusion" BETWEEN 0 AND 2) """));
    }
}
public sealed class EvidenceReferenceConfiguration : IEntityTypeConfiguration<EvidenceReference>
{
    public void Configure(EntityTypeBuilder<EvidenceReference> b)
    {
        GovernanceMapping.Scope(b, "Evidence");
        GovernanceMapping.Link<EvidenceReference, GovernanceWork>(b, nameof(EvidenceReference.WorkId));
        GovernanceMapping.Link<EvidenceReference, ControlEvaluation>(b, nameof(EvidenceReference.EvaluationId));
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.HasIndex(x => new { x.CompanyId, x.WorkId, x.EvaluationId, x.RecordedAt });
    }
}
