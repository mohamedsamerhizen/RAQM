using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Data.Configurations.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.Trading;

namespace RAQM.Api.Data.Configurations.FinancialDimensions;

public sealed class DimensionConfigurations : IEntityTypeConfiguration<DimensionDefinition>, IEntityTypeConfiguration<DimensionValue>,
    IEntityTypeConfiguration<DimensionRule>, IEntityTypeConfiguration<DimensionBinding>, IEntityTypeConfiguration<PostedDimension>,
    IEntityTypeConfiguration<DimensionProject>, IEntityTypeConfiguration<DimensionProjectEvent>,
    IEntityTypeConfiguration<DimensionBudgetSlice>, IEntityTypeConfiguration<DimensionProjectForecast>, IEntityTypeConfiguration<DimensionAllocation>
{
    private const string Schema = "analytics";
    private static void Scope<T>(EntityTypeBuilder<T> b, string name) where T : FinancialEvidence
    {
        FinancialMapping.Scope(b, name);
        b.ToTable(name, Schema);
    }
    private static void Link<T, U>(EntityTypeBuilder<T> b, string property) where T : FinancialEvidence where U : class, ICompanyFinancialEntity =>
        b.HasOne<U>().WithMany().HasForeignKey("CompanyId", property).HasPrincipalKey("CompanyId", "Id").OnDelete(DeleteBehavior.Restrict);
    private static void Actor<T>(EntityTypeBuilder<T> b, string property) where T : FinancialEvidence =>
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(property).OnDelete(DeleteBehavior.Restrict);

    public void Configure(EntityTypeBuilder<DimensionDefinition> b)
    {
        Scope(b, "DimensionDefinitions");
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Kind }).IsUnique().HasFilter("\"Kind\"<5");
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.ToTable("DimensionDefinitions", Schema, t => t.HasCheckConstraint("CK_DimensionDefinition_Kind", """ "Kind" BETWEEN 0 AND 5 """));
    }
    public void Configure(EntityTypeBuilder<DimensionValue> b)
    {
        Scope(b, "DimensionValues");
        Link<DimensionValue, DimensionDefinition>(b, nameof(DimensionValue.DefinitionId));
        foreach (var link in new[] { nameof(DimensionValue.ParentId), nameof(DimensionValue.DepartmentId), nameof(DimensionValue.BranchId), nameof(DimensionValue.CostCenterId) })
            Link<DimensionValue, DimensionValue>(b, link);
        Actor(b, nameof(DimensionValue.ManagerId));
        b.HasIndex(x => new { x.CompanyId, x.DefinitionId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.Classification).HasMaxLength(160);
        b.ToTable("DimensionValues", Schema, t => t.HasCheckConstraint("CK_DimensionValue_Terms", """
            ("InactiveOn" IS NULL OR "InactiveOn">"EffectiveDate") AND ("ParentId" IS NULL OR "ParentId"<>"Id")
            """));
    }
    public void Configure(EntityTypeBuilder<DimensionRule> b)
    {
        Scope(b, "DimensionRules");
        Link<DimensionRule, DimensionDefinition>(b, nameof(DimensionRule.DefinitionId));
        Link<DimensionRule, LedgerAccount>(b, nameof(DimensionRule.AccountId));
        Link<DimensionRule, DimensionValue>(b, nameof(DimensionRule.DefaultValueId));
        b.Property(x => x.AllowedJson).HasColumnType("jsonb");
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasIndex(x => new { x.CompanyId, x.DefinitionId, x.AccountId, x.EffectiveDate }).IsUnique().AreNullsDistinct(false);
        b.ToTable("DimensionRules", Schema, t => t.HasCheckConstraint("CK_DimensionRule_Terms", """
            "Mode" BETWEEN 0 AND 3 AND ("Mode"<>3 OR "DefaultValueId" IS NOT NULL) AND ("Mode"<>2 OR "DefaultValueId" IS NULL)
            """));
    }
    public void Configure(EntityTypeBuilder<DimensionBinding> b)
    {
        Scope(b, "DimensionBindings");
        Actor(b, nameof(DimensionBinding.ActorId));
        b.Property(x => x.ValuesJson).HasColumnType("jsonb");
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasIndex(x => new { x.CompanyId, x.Source, x.SourceId, x.SourceVersion, x.Revision }).IsUnique();
        b.ToTable("DimensionBindings", Schema, t => t.HasCheckConstraint("CK_DimensionBinding_Source", """ "Source" BETWEEN 0 AND 6 AND "Revision">0 """));
    }
    public void Configure(EntityTypeBuilder<PostedDimension> b)
    {
        Scope(b, "PostedDimensions");
        Link<PostedDimension, JournalLine>(b, nameof(PostedDimension.JournalLineId));
        Link<PostedDimension, DimensionDefinition>(b, nameof(PostedDimension.DefinitionId));
        Link<PostedDimension, DimensionValue>(b, nameof(PostedDimension.ValueId));
        Link<PostedDimension, DimensionRule>(b, nameof(PostedDimension.RuleId));
        Link<PostedDimension, DimensionBinding>(b, nameof(PostedDimension.BindingId));
        b.HasIndex(x => new { x.CompanyId, x.JournalLineId, x.DefinitionId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.DefinitionId, x.ValueId });
        b.ToTable("PostedDimensions", Schema, t => t.HasCheckConstraint("CK_PostedDimension_Origin", """
            "Origin" BETWEEN 0 AND 7 AND (("Origin"=7)=("ValueId" IS NULL))
            """));
    }
    public void Configure(EntityTypeBuilder<DimensionProject> b)
    {
        Scope(b, "FinancialProjects");
        Link<DimensionProject, DimensionValue>(b, nameof(DimensionProject.ValueId));
        Link<DimensionProject, TradingParty>(b, nameof(DimensionProject.CustomerId));
        Actor(b, nameof(DimensionProject.OwnerId));
        b.HasIndex(x => new { x.CompanyId, x.ValueId }).IsUnique();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Evidence).HasMaxLength(1000);
        b.ToTable("FinancialProjects", Schema, t => t.HasCheckConstraint("CK_FinancialProject_Window", """ "End">="Start" """));
    }
    public void Configure(EntityTypeBuilder<DimensionProjectEvent> b)
    {
        Scope(b, "FinancialProjectEvents");
        Link<DimensionProjectEvent, DimensionProject>(b, nameof(DimensionProjectEvent.ProjectId));
        Actor(b, nameof(DimensionProjectEvent.ActorId));
        b.HasIndex(x => new { x.CompanyId, x.ProjectId, x.Number }).IsUnique();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.ToTable("FinancialProjectEvents", Schema, t => t.HasCheckConstraint("CK_ProjectEvent_State", """ "State" BETWEEN 0 AND 5 AND "Number">0 """));
    }
    public void Configure(EntityTypeBuilder<DimensionBudgetSlice> b)
    {
        Scope(b, "DimensionBudgetSlices");
        Link<DimensionBudgetSlice, BudgetAllocation>(b, nameof(DimensionBudgetSlice.BudgetAllocationId));
        Link<DimensionBudgetSlice, DimensionValue>(b, nameof(DimensionBudgetSlice.ValueId));
        b.Property(x => x.Amount).HasPrecision(20, 4);
        b.HasIndex(x => new { x.CompanyId, x.BudgetAllocationId, x.ValueId }).IsUnique();
        b.ToTable("DimensionBudgetSlices", Schema, t => t.HasCheckConstraint("CK_DimensionBudgetSlice_Amount", """ "Amount">=0 """));
    }
    public void Configure(EntityTypeBuilder<DimensionProjectForecast> b)
    {
        Scope(b, "ProjectFinancialForecasts");
        Link<DimensionProjectForecast, DimensionProject>(b, nameof(DimensionProjectForecast.ProjectId));
        Actor(b, nameof(DimensionProjectForecast.ApprovedBy));
        b.Property(x => x.Evidence).HasMaxLength(1000);
        foreach (var name in new[] { nameof(DimensionProjectForecast.PostedCost), nameof(DimensionProjectForecast.PostedRevenue),
                     nameof(DimensionProjectForecast.CostToComplete), nameof(DimensionProjectForecast.RevenueToComplete) })
            b.Property(name).HasPrecision(20, 4);
        b.HasIndex(x => new { x.CompanyId, x.ProjectId, x.At });
    }
    public void Configure(EntityTypeBuilder<DimensionAllocation> b)
    {
        Scope(b, "CostAllocationExecutions");
        Link<DimensionAllocation, JournalLine>(b, nameof(DimensionAllocation.SourceLineId));
        Link<DimensionAllocation, JournalEntry>(b, nameof(DimensionAllocation.JournalId));
        Link<DimensionAllocation, JournalEntry>(b, nameof(DimensionAllocation.CorrectionJournalId));
        Actor(b, nameof(DimensionAllocation.CreatedBy));
        Actor(b, nameof(DimensionAllocation.ApprovedBy));
        Actor(b, nameof(DimensionAllocation.PostedBy));
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Hash).HasMaxLength(64);
        b.Property(x => x.InputJson).HasColumnType("jsonb");
        b.Property(x => x.PlanJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.CompanyId, x.Key }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.SourceLineId }).IsUnique().HasFilter("\"State\"=2");
        b.ToTable("CostAllocationExecutions", Schema, t => t.HasCheckConstraint("CK_CostAllocation_State", """
            "Mode" IN (0,1) AND "State" BETWEEN 0 AND 4 AND (("State"=4)=("CorrectedOn" IS NOT NULL)) AND
            ("State" NOT IN(1,2,4) OR ("ApprovedBy" IS NOT NULL AND "ApprovedBy"<>"CreatedBy"))
            """));
    }
}
