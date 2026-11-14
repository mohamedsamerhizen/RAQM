using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Data.Configurations.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Entities.Trading;

namespace RAQM.Api.Data.Configurations.MultiCurrency;

public sealed class FxConfigurations : IEntityTypeConfiguration<FxConfiguration>, IEntityTypeConfiguration<FxAccount>,
    IEntityTypeConfiguration<FxCurrency>, IEntityTypeConfiguration<FxRate>, IEntityTypeConfiguration<FxPosition>,
    IEntityTypeConfiguration<FxOperation>, IEntityTypeConfiguration<FxEffect>, IEntityTypeConfiguration<FxBankStatement>
{
    private const string Schema = "foreign_exchange";
    private static void Scope<T>(EntityTypeBuilder<T> b, string table) where T : FinancialEvidence
    {
        FinancialMapping.Scope(b, table);
        b.ToTable(table, Schema);
    }
    private static void Link<T, TTarget>(EntityTypeBuilder<T> b, string column) where T : FinancialEvidence where TTarget : class, ICompanyFinancialEntity =>
        b.HasOne<TTarget>().WithMany().HasForeignKey("CompanyId", column).HasPrincipalKey("CompanyId", "Id").OnDelete(DeleteBehavior.Restrict);
    private static void Actor<T>(EntityTypeBuilder<T> b, string column) where T : FinancialEvidence =>
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(column).OnDelete(DeleteBehavior.Restrict);

    public void Configure(EntityTypeBuilder<FxConfiguration> b)
    {
        Scope(b, "FxConfigurations");
        b.Property(x => x.FunctionalCurrency).HasMaxLength(3);
        b.HasIndex(x => x.CompanyId).IsUnique();
        b.ToTable("FxConfigurations", Schema, t => t.HasCheckConstraint("CK_FxConfiguration_Fallback", """ "SpotFallbackDays" BETWEEN 0 AND 7 """));
    }
    public void Configure(EntityTypeBuilder<FxAccount> b)
    {
        Scope(b, "FxAccounts");
        Link<FxAccount, LedgerAccount>(b, nameof(FxAccount.AccountId));
        b.HasIndex(x => new { x.CompanyId, x.AccountId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Role }).IsUnique();
        b.ToTable("FxAccounts", Schema, t => t.HasCheckConstraint("CK_FxAccount_Role", """ "Role" BETWEEN 0 AND 9 """));
    }
    public void Configure(EntityTypeBuilder<FxCurrency> b)
    {
        Scope(b, "FxCurrencies");
        b.Property(x => x.Code).HasMaxLength(3);
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
    }
    public void Configure(EntityTypeBuilder<FxRate> b)
    {
        Scope(b, "FxRates");
        Link<FxRate, FxCurrency>(b, nameof(FxRate.CurrencyId));
        Link<FxRate, FxRate>(b, nameof(FxRate.ReplacesId));
        Actor(b, nameof(FxRate.CreatedBy));
        Actor(b, nameof(FxRate.ApprovedBy));
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.ExchangeRate).HasPrecision(20, 10);
        b.Property(x => x.Source).HasMaxLength(160);
        b.Property(x => x.Evidence).HasMaxLength(1000);
        b.HasIndex(x => new { x.CompanyId, x.CurrencyId, x.Type, x.Date, x.Revision }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.CurrencyId, x.Type, x.Date }).IsUnique().HasFilter("\"State\"=1");
        b.ToTable("FxRates", Schema, t => t.HasCheckConstraint("CK_FxRate_State", """
            "Type" IN(0,1) AND "State" BETWEEN 0 AND 3 AND "ExchangeRate">0 AND "Revision">0
              AND ("State" IN(0,3) OR ("ApprovedBy" IS NOT NULL AND "CreatedBy"<>"ApprovedBy"))
            """));
    }
    public void Configure(EntityTypeBuilder<FxPosition> b)
    {
        Scope(b, "FxPositions");
        Link<FxPosition, FxCurrency>(b, nameof(FxPosition.CurrencyId));
        Link<FxPosition, LedgerAccount>(b, nameof(FxPosition.AccountId));
        Link<FxPosition, TradingParty>(b, nameof(FxPosition.PartyId));
        b.Property(x => x.Code).HasMaxLength(64);
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.AccountId }).IsUnique().HasFilter("\"Kind\"=6");
        b.ToTable("FxPositions", Schema, t => t.HasCheckConstraint("CK_FxPosition_Type", """
            "Kind" BETWEEN 0 AND 6 AND (("Kind"=6)=("PartyId" IS NULL)) AND (("Kind"=6)=("DueDate" IS NULL))
            """));
    }
    public void Configure(EntityTypeBuilder<FxOperation> b)
    {
        Scope(b, "FxOperations");
        Link<FxOperation, FxPosition>(b, nameof(FxOperation.PositionId));
        Link<FxOperation, TradingParty>(b, nameof(FxOperation.PartyId));
        Link<FxOperation, FxOperation>(b, nameof(FxOperation.OriginalId));
        Link<FxOperation, JournalEntry>(b, nameof(FxOperation.JournalId));
        Actor(b, nameof(FxOperation.CreatedBy));
        Actor(b, nameof(FxOperation.ApprovedBy));
        Actor(b, nameof(FxOperation.PostedBy));
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.InputJson).HasColumnType("jsonb");
        b.Property(x => x.PlanJson).HasColumnType("jsonb");
        b.Property(x => x.ReferenceKey).HasMaxLength(160);
        b.Property(x => x.RequestKey).HasMaxLength(64);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.HasIndex(x => new { x.CompanyId, x.RequestKey }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Sequence }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.PartyId, x.ReferenceKey }).IsUnique()
            .HasFilter("\"Kind\" IN(0,1) AND \"State\"<>3");
        b.HasIndex(x => new { x.CompanyId, x.Date }).IsUnique().HasFilter("\"Kind\"=7 AND \"State\"=2 AND \"OriginalId\" IS NULL");
        b.HasIndex(x => new { x.CompanyId, x.OriginalId, x.Kind }).IsUnique().HasFilter("\"Kind\" IN(4,7,8) AND \"State\"=2");
        b.ToTable("FxOperations", Schema, t => t.HasCheckConstraint("CK_FxOperation_State", """
            "Kind" BETWEEN 0 AND 8 AND "State" BETWEEN 0 AND 3 AND (("State"=2)=("Sequence" IS NOT NULL))
              AND ("State"<>2 OR "PostedBy" IS NOT NULL) AND ("State" NOT IN (1,2) OR
                ("ApprovedBy" IS NOT NULL AND "ApprovedBy"<>"CreatedBy" AND "PlanJson" IS NOT NULL))
            """));
    }
    public void Configure(EntityTypeBuilder<FxEffect> b)
    {
        Scope(b, "FxEffects");
        Link<FxEffect, FxOperation>(b, nameof(FxEffect.OperationId));
        Link<FxEffect, FxPosition>(b, nameof(FxEffect.PositionId));
        Link<FxEffect, FxRate>(b, nameof(FxEffect.RateId));
        b.Property(x => x.BaseDelta).HasPrecision(20, 4);
        b.Property(x => x.ForeignDelta).HasPrecision(20, 4);
        b.HasIndex(x => new { x.CompanyId, x.PositionId, x.Date, x.Sequence });
        b.HasIndex(x => new { x.CompanyId, x.OperationId, x.PositionId }).IsUnique();
        b.ToTable("FxEffects", Schema, t => t.HasCheckConstraint("CK_FxEffect_Value", """
            "Sequence">0 AND ("BaseDelta"<>0 OR "ForeignDelta"<>0)
            """));
    }
    public void Configure(EntityTypeBuilder<FxBankStatement> b)
    {
        Scope(b, "FxBankStatements");
        Link<FxBankStatement, FxPosition>(b, nameof(FxBankStatement.PositionId));
        Actor(b, nameof(FxBankStatement.ActorId));
        b.Property(x => x.StatementForeign).HasPrecision(20, 4);
        b.Property(x => x.BookForeign).HasPrecision(20, 4);
        b.Property(x => x.BookBase).HasPrecision(20, 4);
        b.Property(x => x.Evidence).HasMaxLength(1000);
        b.HasIndex(x => new { x.CompanyId, x.PositionId, x.Cutoff }).IsUnique();
        b.ToTable("FxBankStatements", Schema, t => t.HasCheckConstraint("CK_FxBankStatement_Amounts", """
            "StatementForeign">=0 AND "BookForeign">=0 AND "BookBase">=0
            """));
    }
}
