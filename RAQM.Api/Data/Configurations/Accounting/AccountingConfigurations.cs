using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.Organizations;

namespace RAQM.Api.Data.Configurations.Accounting;
internal static class FinancialMapping
{
    public static void Scope<T>(EntityTypeBuilder<T> b, string table)
        where T : class, ICompanyFinancialEntity
    {
        b.ToTable(table, "accounting");
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<Company>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrganizationId, x.CompanyId }).HasPrincipalKey(x => new { x.TenantId, x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AccountConfiguration : IEntityTypeConfiguration<LedgerAccount>
{
    public void Configure(EntityTypeBuilder<LedgerAccount> b)
    {
        FinancialMapping.Scope(b, "Accounts");
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ParentId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("Accounts", "accounting", t =>
        {
            t.HasCheckConstraint("CK_Account_Class", "\"Classification\" BETWEEN 0 AND 4 AND \"NormalBalance\" IN (0,1)");
            t.HasCheckConstraint("CK_Account_Code", "\"Code\" ~ '^[A-Z0-9][A-Z0-9._-]{0,31}$'");
            t.HasCheckConstraint("CK_Account_Normal", "(\"NormalBalance\" = 0) = ((\"Classification\" IN (0,4)) <> \"IsContra\")");
        });
    }
}

public sealed class FiscalYearConfiguration : IEntityTypeConfiguration<FiscalYear>
{
    public void Configure(EntityTypeBuilder<FiscalYear> b)
    {
        FinancialMapping.Scope(b, "FiscalYears");
        b.Property(x => x.Name).HasMaxLength(160);
        b.HasIndex(x => new { x.CompanyId, x.StartDate }).IsUnique();
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("FiscalYears", "accounting", t => t.HasCheckConstraint("CK_FiscalYear_Dates", "\"StartDate\" <= \"EndDate\""));
    }
}

public sealed class AccountingPeriodConfiguration : IEntityTypeConfiguration<AccountingPeriod>
{
    public void Configure(EntityTypeBuilder<AccountingPeriod> b)
    {
        FinancialMapping.Scope(b, "Periods");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasOne<FiscalYear>().WithMany().HasForeignKey(x => new { x.CompanyId, x.FiscalYearId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FiscalYearId, x.Number }).IsUnique();
        b.ToTable("Periods", "accounting", t => t.HasCheckConstraint("CK_Period_Config", "\"StartDate\" <= \"EndDate\" AND \"Number\" BETWEEN 1 AND 12 AND \"State\" BETWEEN 0 AND 2"));
    }
}

public sealed class JournalConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> b)
    {
        FinancialMapping.Scope(b, "Journals");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Reference).HasMaxLength(160);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Number).HasMaxLength(32);
        b.Property(x => x.SourceModule).HasMaxLength(50);
        b.Property(x => x.IdempotencyKey).HasMaxLength(128);
        b.Property(x => x.PayloadHash).HasMaxLength(64);
        b.Property(x => x.PostingReason).HasMaxLength(500);
        b.HasIndex(x => new { x.CompanyId, x.Sequence }).IsUnique().HasFilter("\"State\" = 1");
        b.HasIndex(x => new { x.CompanyId, x.IdempotencyKey }).IsUnique().HasFilter("\"State\" = 1");
        b.HasIndex(x => new { x.CompanyId, x.SourceModule, x.SourceDocumentId }).IsUnique().HasFilter("\"State\" = 1");
        b.HasIndex(x => new { x.CompanyId, x.ReversesJournalId }).IsUnique().HasFilter("\"ReversesJournalId\" IS NOT NULL AND \"State\" = 1");
        b.HasIndex(x => new { x.CompanyId, x.PostingDate, x.Sequence });
        b.HasOne<AccountingPeriod>().WithMany().HasForeignKey(x => new { x.CompanyId, x.PeriodId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ReversesJournalId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.PostedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("Journals", "accounting", t =>
        {
            t.HasCheckConstraint("CK_Journal_State", "\"State\" IN (0,1) AND \"CurrencyPolicyVersion\" = 1 AND \"MinorUnits\" BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_Journal_Posted", "\"State\" = 0 OR (\"PeriodId\" IS NOT NULL AND \"Sequence\" > 0 AND \"Number\" IS NOT NULL AND \"IdempotencyKey\" IS NOT NULL AND \"PayloadHash\" IS NOT NULL AND \"PostedBy\" IS NOT NULL AND \"PostedAt\" IS NOT NULL)");
            t.HasCheckConstraint("CK_Journal_Source", "length(trim(\"SourceModule\")) > 0 AND \"SourceDocumentId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
    }
}

public sealed class JournalLineConfiguration : IEntityTypeConfiguration<JournalLine>
{
    public void Configure(EntityTypeBuilder<JournalLine> b)
    {
        FinancialMapping.Scope(b, "JournalLines");
        b.Property(x => x.Debit).HasPrecision(20, 4);
        b.Property(x => x.Credit).HasPrecision(20, 4);
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => new { x.JournalId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.AccountId, x.JournalId });
        b.HasOne<JournalEntry>().WithMany(x => x.Lines).HasForeignKey(x => new { x.CompanyId, x.JournalId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.AccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("JournalLines", "accounting", t => t.HasCheckConstraint("CK_Line_Amounts", "\"Number\" > 0 AND ((\"Debit\" > 0 AND \"Credit\" = 0) OR (\"Credit\" > 0 AND \"Debit\" = 0))"));
    }
}

public sealed class OpeningBalanceConfiguration : IEntityTypeConfiguration<OpeningBalanceBatch>
{
    public void Configure(EntityTypeBuilder<OpeningBalanceBatch> b)
    {
        FinancialMapping.Scope(b, "OpeningBatches");
        b.Property(x => x.BatchKey).HasMaxLength(128);
        b.Property(x => x.Name).HasMaxLength(160);
        b.HasIndex(x => x.CompanyId).IsUnique(); // one initial opening batch per company
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.CompanyId, x.JournalId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AccountingAuditConfiguration : IEntityTypeConfiguration<AccountingAuditEvent>
{
    public void Configure(EntityTypeBuilder<AccountingAuditEvent> b)
    {
        FinancialMapping.Scope(b, "AuditEvents");
        b.Property(x => x.Action).HasMaxLength(50);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasIndex(x => new { x.CompanyId, x.EntityId, x.At });
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
}
