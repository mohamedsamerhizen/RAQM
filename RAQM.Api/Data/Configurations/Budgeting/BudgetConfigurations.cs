using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Data.Configurations.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Entities.Forecasting;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.Inventory;

namespace RAQM.Api.Data.Configurations.Budgeting;

public sealed class BudgetConfigurations : IEntityTypeConfiguration<FinancialBudget>, IEntityTypeConfiguration<BudgetVersion>,
    IEntityTypeConfiguration<BudgetAllocation>, IEntityTypeConfiguration<BudgetPolicy>, IEntityTypeConfiguration<BudgetCommitment>,
    IEntityTypeConfiguration<BudgetOverride>, IEntityTypeConfiguration<ForecastVersion>
{
    private const string Schema = "planning";

    private static void Scope<T>(EntityTypeBuilder<T> b, string name) where T : FinancialEvidence
    {
        FinancialMapping.Scope(b, name);
        b.ToTable(name, Schema);
    }
    private static void Link<T, TTarget>(EntityTypeBuilder<T> b, string column) where T : FinancialEvidence where TTarget : class, ICompanyFinancialEntity =>
        b.HasOne<TTarget>().WithMany().HasForeignKey("CompanyId", column).HasPrincipalKey("CompanyId", "Id").OnDelete(DeleteBehavior.Restrict);
    private static void Actor<T>(EntityTypeBuilder<T> b, string column) where T : FinancialEvidence =>
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(column).OnDelete(DeleteBehavior.Restrict);

    public void Configure(EntityTypeBuilder<FinancialBudget> b)
    {
        Scope(b, "FinancialBudgets");
        Link<FinancialBudget, FiscalYear>(b, nameof(FinancialBudget.FiscalYearId));
        Actor(b, nameof(FinancialBudget.OwnerId));
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.ApprovalLimit).HasPrecision(20, 4);
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.FiscalYearId, x.Kind }).IsUnique();
        b.ToTable("FinancialBudgets", Schema, t => t.HasCheckConstraint("CK_Budget_Kind", """
            "Kind" BETWEEN 0 AND 3 AND "ApprovalLimit">0
            """));
    }

    public void Configure(EntityTypeBuilder<BudgetVersion> b)
    {
        Scope(b, "BudgetVersions");
        Link<BudgetVersion, FinancialBudget>(b, nameof(BudgetVersion.BudgetId));
        Link<BudgetVersion, BudgetVersion>(b, nameof(BudgetVersion.BasisVersionId));
        Link<BudgetVersion, BudgetVersion>(b, nameof(BudgetVersion.ActiveAtPreparationId));
        Actor(b, nameof(BudgetVersion.CreatedBy));
        Actor(b, nameof(BudgetVersion.ReviewerId));
        Actor(b, nameof(BudgetVersion.ApprovedBy));
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.Hash).HasMaxLength(64);
        b.Property(x => x.Evidence).HasMaxLength(1000);
        b.HasIndex(x => new { x.CompanyId, x.BudgetId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.BudgetId, x.Key }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.BudgetId }).IsUnique().HasFilter("\"State\"=4");
        b.ToTable("BudgetVersions", Schema, t => t.HasCheckConstraint("CK_BudgetVersion_State", """
            "Number">0 AND "State" BETWEEN 0 AND 6 AND (("State" IN (4,5))=("EffectiveDate" IS NOT NULL))
              AND ("ActivatedOn" IS NULL)=("EffectiveDate" IS NULL)
              AND ("State" NOT IN (3,4,5) OR ("ApprovedBy" IS NOT NULL AND "ApprovedBy"<>"CreatedBy"))
            """));
    }

    public void Configure(EntityTypeBuilder<BudgetAllocation> b)
    {
        Scope(b, "BudgetAllocations");
        b.HasOne<BudgetVersion>().WithMany(x => x.Allocations).HasForeignKey(x => new { x.CompanyId, x.BudgetVersionId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        Link<BudgetAllocation, LedgerAccount>(b, nameof(BudgetAllocation.AccountId));
        Link<BudgetAllocation, AccountingPeriod>(b, nameof(BudgetAllocation.PeriodId));
        b.Property(x => x.Amount).HasPrecision(20, 4);
        b.HasIndex(x => new { x.CompanyId, x.BudgetVersionId, x.AccountId, x.PeriodId }).IsUnique();
        b.ToTable("BudgetAllocations", Schema, t => t.HasCheckConstraint("CK_BudgetAllocation_Amount", """ "Amount">=0 """));
    }

    public void Configure(EntityTypeBuilder<BudgetPolicy> b)
    {
        Scope(b, "BudgetPolicies");
        Link<BudgetPolicy, LedgerAccount>(b, nameof(BudgetPolicy.AccountId));
        Actor(b, nameof(BudgetPolicy.CreatedBy));
        b.Property(x => x.WarningPercent).HasPrecision(9, 4);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasIndex(x => new { x.CompanyId, x.AccountId, x.EffectiveDate }).IsUnique();
        b.ToTable("BudgetPolicies", Schema, t => t.HasCheckConstraint("CK_BudgetPolicy_Mode", """
            "Kind" BETWEEN 1 AND 3 AND "Mode" BETWEEN 0 AND 2 AND "WarningPercent">0 AND "WarningPercent"<=100
            """));
    }

    public void Configure(EntityTypeBuilder<BudgetCommitment> b)
    {
        Scope(b, "BudgetCommitmentEvents");
        Link<BudgetCommitment, LedgerAccount>(b, nameof(BudgetCommitment.AccountId));
        Link<BudgetCommitment, AccountingPeriod>(b, nameof(BudgetCommitment.PeriodId));
        Link<BudgetCommitment, ProcurementLine>(b, nameof(BudgetCommitment.ProcurementLineId));
        Link<BudgetCommitment, AssetOperation>(b, nameof(BudgetCommitment.AssetOperationId));
        Link<BudgetCommitment, StockOperation>(b, nameof(BudgetCommitment.StockOperationId));
        Actor(b, nameof(BudgetCommitment.ActorId));
        b.Property(x => x.Amount).HasPrecision(20, 4);
        b.Property(x => x.EventKey).HasMaxLength(100);
        b.HasIndex(x => new { x.CompanyId, x.EventKey }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.AccountId, x.PeriodId, x.Date });
        b.ToTable("BudgetCommitmentEvents", Schema, t => t.HasCheckConstraint("CK_BudgetCommitment_Source", """
            "Amount"<>0 AND (("Kind" IN (0,1) AND "ProcurementLineId" IS NOT NULL AND "AssetOperationId" IS NULL)
              OR ("Kind"=2 AND "ProcurementLineId" IS NULL AND "AssetOperationId" IS NOT NULL AND "StockOperationId" IS NULL))
            """));
    }

    public void Configure(EntityTypeBuilder<BudgetOverride> b)
    {
        Scope(b, "BudgetOverrideGrants");
        Link<BudgetOverride, LedgerAccount>(b, nameof(BudgetOverride.AccountId));
        Actor(b, nameof(BudgetOverride.CreatedBy));
        b.Property(x => x.MaxExcess).HasPrecision(20, 4);
        b.Property(x => x.Intent).HasMaxLength(150);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasIndex(x => new { x.CompanyId, x.Intent, x.AccountId }).IsUnique();
        b.ToTable("BudgetOverrideGrants", Schema, t => t.HasCheckConstraint("CK_BudgetOverride_Amount", """ "MaxExcess">0 """));
    }

    public void Configure(EntityTypeBuilder<ForecastVersion> b)
    {
        Scope(b, "ForecastVersions");
        Link<ForecastVersion, ForecastVersion>(b, nameof(ForecastVersion.BasisId));
        Link<ForecastVersion, ForecastVersion>(b, nameof(ForecastVersion.ScenarioBaseId));
        Actor(b, nameof(ForecastVersion.CreatedBy));
        Actor(b, nameof(ForecastVersion.ApprovedBy));
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.RequestKey).HasMaxLength(64);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.AssumptionsJson).HasColumnType("jsonb");
        b.Property(x => x.SnapshotJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.CompanyId, x.Code, x.Number }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.RequestKey }).IsUnique();
        b.ToTable("ForecastVersions", Schema, t => t.HasCheckConstraint("CK_Forecast_State", """
            "Number">0 AND "Horizon" IN (3,12) AND "State" BETWEEN 0 AND 2
              AND ("State"<>1 OR ("ApprovedBy" IS NOT NULL AND "CreatedBy"<>"ApprovedBy"))
            """));
    }
}
