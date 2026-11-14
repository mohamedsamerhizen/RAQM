using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Data.Configurations.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Accruals;
using RAQM.Api.Entities.FinancialClose;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.YearEnd;

namespace RAQM.Api.Data.Configurations.FinancialClose;

public sealed class FinancialCloseConfigurations :
    IEntityTypeConfiguration<ReportDefinition>, IEntityTypeConfiguration<ReportAccountMapping>,
    IEntityTypeConfiguration<RecognitionSchedule>, IEntityTypeConfiguration<RecognitionOccurrence>,
    IEntityTypeConfiguration<PeriodCloseSession>, IEntityTypeConfiguration<PeriodCloseTask>,
    IEntityTypeConfiguration<CloseException>, IEntityTypeConfiguration<YearEndRecord>
{
    private const string Schema = "financial_close";

    private static void Scope<T>(EntityTypeBuilder<T> b, string table) where T : FinancialEvidence
    {
        FinancialMapping.Scope(b, table);
        b.ToTable(table, Schema);
    }

    private static void Link<T, TTarget>(EntityTypeBuilder<T> b, string column)
        where T : FinancialEvidence where TTarget : class, ICompanyFinancialEntity =>
        b.HasOne<TTarget>().WithMany().HasForeignKey("CompanyId", column).HasPrincipalKey("CompanyId", "Id").OnDelete(DeleteBehavior.Restrict);

    private static void Actor<T>(EntityTypeBuilder<T> b, string column) where T : FinancialEvidence =>
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(column).OnDelete(DeleteBehavior.Restrict);

    public void Configure(EntityTypeBuilder<ReportDefinition> b)
    {
        Scope(b, "ReportDefinitions");
        b.HasIndex(x => new { x.CompanyId, x.Revision }).IsUnique();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.CashRulesJson).HasColumnType("jsonb");
        Link<ReportDefinition, LedgerAccount>(b, nameof(ReportDefinition.RetainedEarningsId));
        Actor(b, nameof(ReportDefinition.CreatedBy));
        b.ToTable("ReportDefinitions", Schema, t => t.HasCheckConstraint("CK_ReportDefinition_Revision", "\"Revision\" > 0 AND \"MappingCount\" BETWEEN 0 AND 2000"));
    }

    public void Configure(EntityTypeBuilder<ReportAccountMapping> b)
    {
        Scope(b, "ReportAccountMappings");
        b.HasIndex(x => new { x.CompanyId, x.DefinitionId, x.AccountId }).IsUnique();
        b.HasOne<ReportDefinition>().WithMany(x => x.Mappings).HasForeignKey(x => new { x.CompanyId, x.DefinitionId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        Link<ReportAccountMapping, LedgerAccount>(b, nameof(ReportAccountMapping.AccountId));
        b.ToTable("ReportAccountMappings", Schema, t => t.HasCheckConstraint("CK_ReportMapping_Group", "\"Group\" BETWEEN 0 AND 12"));
    }

    public void Configure(EntityTypeBuilder<RecognitionSchedule> b)
    {
        Scope(b, "RecognitionSchedules");
        b.HasIndex(x => new { x.CompanyId, x.Reference }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.BasisLineId }).IsUnique();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.Reference).HasMaxLength(160);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.TemplateJson).HasColumnType("jsonb");
        Link<RecognitionSchedule, JournalLine>(b, nameof(RecognitionSchedule.BasisLineId));
        Link<RecognitionSchedule, LedgerAccount>(b, nameof(RecognitionSchedule.DebitAccountId));
        Link<RecognitionSchedule, LedgerAccount>(b, nameof(RecognitionSchedule.CreditAccountId));
        Actor(b, nameof(RecognitionSchedule.CreatedBy));
        Actor(b, nameof(RecognitionSchedule.ApprovedBy));
        b.ToTable("RecognitionSchedules", Schema, t => t.HasCheckConstraint("CK_RecognitionSchedule_Shape",
            """ "Total" > 0 AND "Kind" BETWEEN 0 AND 4 AND "State" BETWEEN 0 AND 2 AND ("Kind" IN (2,3))=("BasisLineId" IS NOT NULL) AND ("Kind"=4 OR ("DebitAccountId" IS NOT NULL AND "CreditAccountId" IS NOT NULL AND "DebitAccountId"<>"CreditAccountId")) """));
    }

    public void Configure(EntityTypeBuilder<RecognitionOccurrence> b)
    {
        Scope(b, "RecognitionOccurrences");
        b.HasIndex(x => new { x.CompanyId, x.ScheduleId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Date });
        b.HasIndex(x => new { x.CompanyId, x.JournalId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.ReversalJournalId }).IsUnique();
        b.Property(x => x.ReversalHash).HasMaxLength(64);
        b.HasOne<RecognitionSchedule>().WithMany(x => x.Occurrences).HasForeignKey(x => new { x.CompanyId, x.ScheduleId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        Link<RecognitionOccurrence, JournalEntry>(b, nameof(RecognitionOccurrence.JournalId));
        Link<RecognitionOccurrence, JournalEntry>(b, nameof(RecognitionOccurrence.ReversalJournalId));
        b.ToTable("RecognitionOccurrences", Schema, t => t.HasCheckConstraint("CK_RecognitionOccurrence_Shape",
            """ "Amount" > 0 AND "Number" > 0 AND ("ReverseDue" IS NULL OR "ReverseDue">"Date") AND ("ReversalJournalId" IS NULL OR "JournalId" IS NOT NULL) """));
    }

    public void Configure(EntityTypeBuilder<PeriodCloseSession> b)
    {
        Scope(b, "PeriodCloseSessions");
        b.HasIndex(x => new { x.CompanyId, x.PeriodId }).IsUnique().HasFilter(""" "State" IN (0,1) """);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.ApprovalHash).HasMaxLength(64);
        b.Property(x => x.ApprovalReason).HasMaxLength(500);
        b.Property(x => x.EvidenceJson).HasColumnType("jsonb");
        Link<PeriodCloseSession, AccountingPeriod>(b, nameof(PeriodCloseSession.PeriodId));
        Actor(b, nameof(PeriodCloseSession.CreatedBy));
        Actor(b, nameof(PeriodCloseSession.ApprovedBy));
        Actor(b, nameof(PeriodCloseSession.ClosedBy));
        b.ToTable("PeriodCloseSessions", Schema, t => t.HasCheckConstraint("CK_CloseSession_State",
            """ "State" BETWEEN 0 AND 3 AND ("State" NOT IN(1,2) OR ("ApprovedBy" IS NOT NULL AND "ApprovalHash" IS NOT NULL)) AND ("State"<>2 OR ("ClosedBy" IS NOT NULL AND "ClosedAt" IS NOT NULL AND "EvidenceJson" IS NOT NULL)) """));
    }

    public void Configure(EntityTypeBuilder<PeriodCloseTask> b)
    {
        Scope(b, "PeriodCloseTasks");
        b.HasIndex(x => new { x.CompanyId, x.SessionId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.Title).HasMaxLength(160);
        b.Property(x => x.Evidence).HasMaxLength(1000);
        b.HasOne<PeriodCloseSession>().WithMany(x => x.Tasks).HasForeignKey(x => new { x.CompanyId, x.SessionId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        Actor(b, nameof(PeriodCloseTask.ReviewerId));
        Actor(b, nameof(PeriodCloseTask.CompletedBy));
    }

    public void Configure(EntityTypeBuilder<CloseException> b)
    {
        Scope(b, "CloseExceptions");
        b.Property(x => x.Detail).HasMaxLength(1000);
        b.Property(x => x.Resolution).HasMaxLength(1000);
        Link<CloseException, PeriodCloseSession>(b, nameof(CloseException.SessionId));
        Actor(b, nameof(CloseException.RaisedBy));
        Actor(b, nameof(CloseException.ResolvedBy));
    }

    public void Configure(EntityTypeBuilder<YearEndRecord> b)
    {
        Scope(b, "YearEndRecords");
        b.HasIndex(x => new { x.CompanyId, x.FiscalYearId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.RequestKey }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.JournalId }).IsUnique();
        b.Property(x => x.RequestKey).HasMaxLength(64);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.ClosingLinesJson).HasColumnType("jsonb");
        b.Property(x => x.Reason).HasMaxLength(500);
        Link<YearEndRecord, FiscalYear>(b, nameof(YearEndRecord.FiscalYearId));
        Link<YearEndRecord, PeriodCloseSession>(b, nameof(YearEndRecord.SessionId));
        Link<YearEndRecord, ReportDefinition>(b, nameof(YearEndRecord.DefinitionId));
        Link<YearEndRecord, LedgerAccount>(b, nameof(YearEndRecord.RetainedEarningsId));
        Link<YearEndRecord, JournalEntry>(b, nameof(YearEndRecord.JournalId));
        Actor(b, nameof(YearEndRecord.ClosedBy));
    }
}
