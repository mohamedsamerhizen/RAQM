using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Data.Configurations.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.Trading;

namespace RAQM.Api.Data.Configurations.FixedAssets;

public sealed class AssetConfigurations : IEntityTypeConfiguration<AssetCategory>, IEntityTypeConfiguration<AssetControlAccount>,
    IEntityTypeConfiguration<FixedAsset>, IEntityTypeConfiguration<AssetComponent>, IEntityTypeConfiguration<AssetOperation>,
    IEntityTypeConfiguration<AssetMovement>, IEntityTypeConfiguration<AssetSourceClaim>
{
    private const string Schema = "fixed_assets";
    private static void Scope<T>(EntityTypeBuilder<T> b, string name) where T : FinancialEvidence
    {
        FinancialMapping.Scope(b, name);
        b.ToTable(name, Schema);
    }
    private static void Link<T, TTarget>(EntityTypeBuilder<T> b, string column) where T : FinancialEvidence where TTarget : class, ICompanyFinancialEntity =>
        b.HasOne<TTarget>().WithMany().HasForeignKey("CompanyId", column).HasPrincipalKey("CompanyId", "Id").OnDelete(DeleteBehavior.Restrict);
    private static void Actor<T>(EntityTypeBuilder<T> b, string column) where T : FinancialEvidence =>
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(column).OnDelete(DeleteBehavior.Restrict);

    public void Configure(EntityTypeBuilder<AssetCategory> b)
    {
        Scope(b, "AssetCategories");
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        Link<AssetCategory, LedgerAccount>(b, nameof(AssetCategory.DepreciationExpenseId));
        Link<AssetCategory, LedgerAccount>(b, nameof(AssetCategory.ImpairmentLossId));
        Link<AssetCategory, LedgerAccount>(b, nameof(AssetCategory.GainId));
        Link<AssetCategory, LedgerAccount>(b, nameof(AssetCategory.LossId));
        Actor(b, nameof(AssetCategory.CreatedBy));
        b.ToTable("AssetCategories", Schema, t => t.HasCheckConstraint("CK_AssetCategory_Threshold", "\"Threshold\" >= 0"));
    }

    public void Configure(EntityTypeBuilder<AssetControlAccount> b)
    {
        Scope(b, "AssetControlAccounts");
        b.HasIndex(x => new { x.CompanyId, x.AccountId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.CategoryId, x.Role }).IsUnique();
        b.HasOne<AssetCategory>().WithMany(x => x.Controls).HasForeignKey(x => new { x.CompanyId, x.CategoryId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        Link<AssetControlAccount, LedgerAccount>(b, nameof(AssetControlAccount.AccountId));
        b.ToTable("AssetControlAccounts", Schema, t => t.HasCheckConstraint("CK_AssetControl_Role", "\"Role\" BETWEEN 0 AND 3"));
    }

    public void Configure(EntityTypeBuilder<FixedAsset> b)
    {
        Scope(b, "FixedAssetRegister");
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.CategoryId, x.Status });
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.PurchaseEvidence).HasMaxLength(500);
        b.Property(x => x.AssignmentJson).HasColumnType("jsonb");
        b.Property(x => x.Version).IsConcurrencyToken();
        Link<FixedAsset, AssetCategory>(b, nameof(FixedAsset.CategoryId));
        Link<FixedAsset, TradingParty>(b, nameof(FixedAsset.SupplierId));
        Actor(b, nameof(FixedAsset.CreatedBy));
        b.ToTable("FixedAssetRegister", Schema, t => t.HasCheckConstraint("CK_Asset_Lifecycle",
            """ "Status" BETWEEN 0 AND 5 AND ("Status" >= 2)=("RecognitionDate" IS NOT NULL) AND ("Status" >= 4)=("DisposalDate" IS NOT NULL) AND ("RecognitionDate" IS NULL OR "RecognitionDate">="AcquisitionDate") AND ("Status"<>3 OR "AvailableDate" IS NOT NULL) """));
    }

    public void Configure(EntityTypeBuilder<AssetComponent> b)
    {
        Scope(b, "AssetComponents");
        b.HasIndex(x => new { x.CompanyId, x.AssetId, x.Number }).IsUnique();
        b.Property(x => x.Name).HasMaxLength(160);
        b.HasOne<FixedAsset>().WithMany(x => x.Components).HasForeignKey(x => new { x.CompanyId, x.AssetId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("AssetComponents", Schema, t => t.HasCheckConstraint("CK_AssetComponent_Money",
            """ "Number">0 AND "ProposedCost">0 AND "InitialResidual">=0 AND "InitialResidual"<="ProposedCost" AND "LifeMonths" BETWEEN 0 AND 1200 AND "OpeningDepreciation">=0 AND "OpeningImpairment">=0 AND "OpeningDepreciation"+"OpeningImpairment"<="ProposedCost" """));
    }

    public void Configure(EntityTypeBuilder<AssetOperation> b)
    {
        Scope(b, "AssetOperations");
        b.HasIndex(x => new { x.CompanyId, x.RequestKey }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.JournalId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Sequence }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.PeriodId }).IsUnique().HasFilter(""" "Kind"=3 AND "State"<>3 """);
        b.HasIndex(x => new { x.CompanyId, x.AssetId, x.Date });
        b.HasIndex(x => new { x.CompanyId, x.AssetId }).IsUnique().HasFilter(""" "Kind" IN (10,11) AND "State"=2 """);
        b.HasIndex(x => new { x.CompanyId, x.AssetId }, "IX_AssetOperation_Initial").IsUnique()
            .HasFilter(""" "Kind"=0 AND "State"<>3 """);
        b.Property(x => x.RequestKey).HasMaxLength(64);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.PayloadJson).HasColumnType("jsonb");
        b.Property(x => x.PlanJson).HasColumnType("jsonb");
        b.Property(x => x.Version).IsConcurrencyToken();
        Link<AssetOperation, FixedAsset>(b, nameof(AssetOperation.AssetId));
        Link<AssetOperation, AccountingPeriod>(b, nameof(AssetOperation.PeriodId));
        Link<AssetOperation, JournalEntry>(b, nameof(AssetOperation.JournalId));
        Link<AssetOperation, JournalEntry>(b, nameof(AssetOperation.OpeningJournalId));
        Actor(b, nameof(AssetOperation.CreatedBy));
        Actor(b, nameof(AssetOperation.ApprovedBy));
        Actor(b, nameof(AssetOperation.PostedBy));
        b.ToTable("AssetOperations", Schema, t => t.HasCheckConstraint("CK_AssetOperation_State",
            """ "Kind" BETWEEN 0 AND 11 AND "State" BETWEEN 0 AND 3 AND ("Kind"<>3 OR ("PeriodId" IS NOT NULL AND "AssetId" IS NULL)) AND ("State" NOT IN(1,2) OR ("PlanJson" IS NOT NULL AND "ApprovedBy" IS NOT NULL)) AND ("State"<>2 OR ("Sequence" IS NOT NULL AND "PostedBy" IS NOT NULL AND "PostedAt" IS NOT NULL)) """));
    }

    public void Configure(EntityTypeBuilder<AssetMovement> b)
    {
        Scope(b, "AssetMovements");
        b.HasIndex(x => new { x.CompanyId, x.OperationId, x.ComponentId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.ComponentId, x.DepreciationPeriodId }).IsUnique().HasFilter(""" "DepreciationPeriodId" IS NOT NULL """);
        b.Property(x => x.Units).HasPrecision(16, 6);
        b.Property(x => x.RemainingOverride).HasPrecision(16, 6);
        Link<AssetMovement, AssetOperation>(b, nameof(AssetMovement.OperationId));
        Link<AssetMovement, AssetComponent>(b, nameof(AssetMovement.ComponentId));
        Link<AssetMovement, AccountingPeriod>(b, nameof(AssetMovement.DepreciationPeriodId));
        b.ToTable("AssetMovements", Schema, t => t.HasCheckConstraint("CK_AssetMovement_Units",
            """ "Units" BETWEEN 0 AND 1 AND "DepreciationCharge">=0 AND ("RemainingOverride" IS NULL OR "RemainingOverride">=0) AND ("ResidualOverride" IS NULL OR "ResidualOverride">=0) """));
    }

    public void Configure(EntityTypeBuilder<AssetSourceClaim> b)
    {
        Scope(b, "AssetSourceClaims");
        b.HasIndex(x => new { x.CompanyId, x.JournalLineId }).IsUnique();
        Link<AssetSourceClaim, AssetOperation>(b, nameof(AssetSourceClaim.OperationId));
        Link<AssetSourceClaim, JournalLine>(b, nameof(AssetSourceClaim.JournalLineId));
    }
}
