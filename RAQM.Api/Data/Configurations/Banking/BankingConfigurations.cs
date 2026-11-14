using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Data.Configurations.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.CashManagement;
using RAQM.Api.Entities.Identity;

namespace RAQM.Api.Data.Configurations.Banking;

public sealed class BankingConfigurations :
    IEntityTypeConfiguration<BankProfile>, IEntityTypeConfiguration<TreasuryAccount>,
    IEntityTypeConfiguration<BankStatement>, IEntityTypeConfiguration<BankStatementLine>,
    IEntityTypeConfiguration<ReconciliationSession>, IEntityTypeConfiguration<BankMatch>, IEntityTypeConfiguration<BankMatchItem>,
    IEntityTypeConfiguration<TreasuryMovement>, IEntityTypeConfiguration<CashCount>
{
    private static void Scope<T>(EntityTypeBuilder<T> b, string table) where T : BankEvidence
    {
        FinancialMapping.Scope(b, table);
        b.ToTable(table, "banking");
    }

    public void Configure(EntityTypeBuilder<BankProfile> b)
    {
        Scope(b, "Banks");
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.Swift).HasMaxLength(11);
        b.Property(x => x.Version).IsConcurrencyToken();
    }

    public void Configure(EntityTypeBuilder<TreasuryAccount> b)
    {
        Scope(b, "TreasuryAccounts");
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.LedgerAccountId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.BankProfileId, x.IdentifierDigest }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.IbanDigest }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.AccountMask).HasMaxLength(12);
        b.Property(x => x.IbanMask).HasMaxLength(12);
        b.Property(x => x.IdentifierDigest).HasMaxLength(64);
        b.Property(x => x.IbanDigest).HasMaxLength(64);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasOne<BankProfile>().WithMany().HasForeignKey(x => new { x.CompanyId, x.BankProfileId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.LedgerAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CustodianId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("TreasuryAccounts", "banking", t => t.HasCheckConstraint("CK_TreasuryAccount_Kind",
            """("Kind"=0 AND "BankProfileId" IS NOT NULL AND ("IdentifierDigest" IS NOT NULL OR "IbanDigest" IS NOT NULL) AND "CustodianId" IS NULL) OR ("Kind" IN(1,2) AND "BankProfileId" IS NULL AND "IdentifierDigest" IS NULL AND "IbanDigest" IS NULL AND ("Kind"=2)=("CustodianId" IS NOT NULL))"""));
    }

    public void Configure(EntityTypeBuilder<BankStatement> b)
    {
        Scope(b, "Statements");
        b.HasAlternateKey(x => new { x.CompanyId, x.BankAccountId, x.Id });
        b.HasIndex(x => new { x.CompanyId, x.BankAccountId, x.ExternalId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.BankAccountId, x.RequestKey }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.BankAccountId, x.Digest }).IsUnique();
        b.Property(x => x.ExternalId).HasMaxLength(160);
        b.Property(x => x.RequestKey).HasMaxLength(64);
        b.Property(x => x.Digest).HasMaxLength(64);
        b.HasOne<TreasuryAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.BankAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ImportedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("Statements", "banking", t => t.HasCheckConstraint("CK_Statement_Dates", "\"Start\" <= \"End\" AND \"LineCount\" BETWEEN 0 AND 5000"));
    }

    public void Configure(EntityTypeBuilder<BankStatementLine> b)
    {
        Scope(b, "StatementLines");
        b.HasIndex(x => new { x.CompanyId, x.StatementId, x.Row }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.StatementId, x.Sequence }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.BankAccountId, x.StableExternalId }).IsUnique();
        b.Property(x => x.Reference).HasMaxLength(160);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.OriginalFields).HasMaxLength(8000);
        b.Property(x => x.StableExternalId).HasMaxLength(160);
        b.HasOne<BankStatement>().WithMany(x => x.Lines).HasForeignKey(x => new { x.CompanyId, x.BankAccountId, x.StatementId }).HasPrincipalKey(x => new { x.CompanyId, x.BankAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("StatementLines", "banking", t => t.HasCheckConstraint("CK_StatementLine_Amount", """ "Amount" <> 0 AND "Row" > 1"""));
    }

    public void Configure(EntityTypeBuilder<ReconciliationSession> b)
    {
        Scope(b, "Sessions");
        b.HasKey(x => x.Id).HasName("PK_Banking_Sessions");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.CompanyId, x.StatementId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.BankAccountId }).IsUnique().HasFilter(""" "State" <> 2""");
        b.HasOne<BankStatement>().WithMany().HasForeignKey(x => new { x.CompanyId, x.BankAccountId, x.StatementId }).HasPrincipalKey(x => new { x.CompanyId, x.BankAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.FinalizedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("Sessions", "banking", t => t.HasCheckConstraint("CK_Reconciliation_State",
            """("State" IN(0,1) AND "FinalizedAt" IS NULL) OR ("State"=2 AND "FinalizedAt" IS NOT NULL AND "FinalizedBy" IS NOT NULL AND "FinalizedBook" IS NOT NULL AND "FinalizedDeposits" IS NOT NULL AND "FinalizedWithdrawals" IS NOT NULL)"""));
    }

    public void Configure(EntityTypeBuilder<BankMatch> b)
    {
        Scope(b, "Matches");
        b.HasIndex(x => new { x.CompanyId, x.StatementLineId }).IsUnique().HasFilter(""" "ReleasedAt" IS NULL""");
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.ReleaseReason).HasMaxLength(500);
        b.HasOne<ReconciliationSession>().WithMany().HasForeignKey(x => new { x.CompanyId, x.SessionId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BankStatementLine>().WithMany().HasForeignKey(x => new { x.CompanyId, x.StatementLineId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ConfirmedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ReleasedBy).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<BankMatchItem> b)
    {
        Scope(b, "MatchItems");
        b.HasIndex(x => new { x.CompanyId, x.JournalLineId }).IsUnique().HasFilter("""NOT "Released" """);
        b.HasOne<BankMatch>().WithMany(x => x.Items).HasForeignKey(x => new { x.CompanyId, x.MatchId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalLine>().WithMany().HasForeignKey(x => new { x.CompanyId, x.JournalLineId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<TreasuryMovement> b)
    {
        Scope(b, "Movements");
        b.HasIndex(x => new { x.CompanyId, x.RequestKey }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.JournalId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.StatementLineId }).IsUnique().HasFilter(""" "State" <> 2""");
        b.HasIndex(x => new { x.CompanyId, x.CashCountId }).IsUnique().HasFilter(""" "State" <> 2""");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.RequestKey).HasMaxLength(64);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.ReversalKey).HasMaxLength(64);
        b.Property(x => x.ReversalHash).HasMaxLength(64);
        b.Property(x => x.ApprovalHash).HasMaxLength(64);
        b.Property(x => x.Reference).HasMaxLength(160);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne<TreasuryAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.AccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TreasuryAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.DestinationId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BankStatementLine>().WithMany().HasForeignKey(x => new { x.CompanyId, x.StatementLineId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CashCount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.CashCountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.OffsetLedgerId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.DebitLedgerId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.CreditLedgerId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.CompanyId, x.JournalId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ReversalJournalId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.PostedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ReversedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("Movements", "banking", t =>
        {
            t.HasCheckConstraint("CK_TreasuryMovement_Amount", """ "Amount" > 0 AND "DebitLedgerId" <> "CreditLedgerId" AND "Kind" BETWEEN 0 AND 6""");
            t.HasCheckConstraint("CK_TreasuryMovement_State", """("State"=0 AND "JournalId" IS NULL AND "ReversalJournalId" IS NULL) OR ("State"=1 AND "JournalId" IS NOT NULL AND "ReversalJournalId" IS NULL) OR ("State"=2 AND "JournalId" IS NOT NULL AND "ReversalJournalId" IS NOT NULL)""");
        });
    }

    public void Configure(EntityTypeBuilder<CashCount> b)
    {
        Scope(b, "CashCounts");
        b.Property(x => x.Evidence).HasMaxLength(500);
        b.HasIndex(x => new { x.CompanyId, x.AccountId, x.CountDate, x.CountedAt });
        b.HasOne<TreasuryAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.AccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CountedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("CashCounts", "banking", t => t.HasCheckConstraint("CK_CashCount_Amount", """ "CountedAmount" >= 0"""));
    }
}
