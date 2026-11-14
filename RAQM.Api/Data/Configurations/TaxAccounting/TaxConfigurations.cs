using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.RegulatoryDocuments;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Entities.Trading;

namespace RAQM.Api.Data.Configurations.TaxAccounting;

internal static class TaxMapping
{
    public static void Scope<T>(EntityTypeBuilder<T> b, string table) where T : TaxRecord
    {
        b.ToTable(table, "tax");
        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<Company>().WithMany().HasForeignKey(x => new { x.TenantId, x.OrganizationId, x.CompanyId })
            .HasPrincipalKey(x => new { x.TenantId, x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }

    public static void Link<T, TParent>(EntityTypeBuilder<T> b, string field) where T : TaxRecord where TParent : class, ICompanyFinancialEntity =>
        b.HasOne<TParent>().WithMany().HasForeignKey(nameof(TaxRecord.CompanyId), field)
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
}

public sealed class TaxRegistrationConfiguration : IEntityTypeConfiguration<TaxRegistration>
{
    public void Configure(EntityTypeBuilder<TaxRegistration> b)
    {
        TaxMapping.Scope(b, "Registrations");
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Country).HasMaxLength(2);
        b.Property(x => x.Currency).HasMaxLength(3);
    }
}

public sealed class TaxControlConfiguration : IEntityTypeConfiguration<TaxControl>
{
    public void Configure(EntityTypeBuilder<TaxControl> b)
    {
        TaxMapping.Scope(b, "Controls");
        TaxMapping.Link<TaxControl, TaxRegistration>(b, nameof(TaxControl.RegistrationId));
        TaxMapping.Link<TaxControl, LedgerAccount>(b, nameof(TaxControl.AccountId));
        b.HasIndex(x => new { x.CompanyId, x.RegistrationId, x.Role }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.AccountId }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_Tax_ControlRole", """ "Role" BETWEEN 0 AND 10 """));
    }
}

public sealed class TaxRuleConfiguration : IEntityTypeConfiguration<TaxRule>
{
    public void Configure(EntityTypeBuilder<TaxRule> b)
    {
        TaxMapping.Scope(b, "Rules");
        TaxMapping.Link<TaxRule, TaxRegistration>(b, nameof(TaxRule.RegistrationId));
        TaxMapping.Link<TaxRule, TradingParty>(b, nameof(TaxRule.PartyId));
        TaxMapping.Link<TaxRule, TradingTaxRate>(b, nameof(TaxRule.RateId));
        b.Property(x => x.Group).HasMaxLength(40);
        b.Property(x => x.ItemClass).HasMaxLength(40);
        b.HasIndex(x => new { x.CompanyId, x.RegistrationId, x.Group, x.Side, x.EffectiveOn });
        b.ToTable(t => t.HasCheckConstraint("CK_Tax_Rule", """ "EffectiveOn"<="EndsOn" AND "Order" BETWEEN 1 AND 20 AND "Treatment" BETWEEN 0 AND 3 AND (("Treatment" IN (0,1)) = ("RateId" IS NOT NULL)) """));
    }
}

public sealed class CommercialTaxEvidenceConfiguration : IEntityTypeConfiguration<CommercialTaxEvidence>
{
    public void Configure(EntityTypeBuilder<CommercialTaxEvidence> b)
    {
        TaxMapping.Scope(b, "CommercialEvidence");
        TaxMapping.Link<CommercialTaxEvidence, TaxRegistration>(b, nameof(CommercialTaxEvidence.RegistrationId));
        TaxMapping.Link<CommercialTaxEvidence, TradeDocument>(b, nameof(CommercialTaxEvidence.DocumentId));
        b.HasIndex(x => new { x.CompanyId, x.DocumentId }).IsUnique();
        b.Property(x => x.SnapshotJson).HasColumnType("jsonb");
    }
}

public sealed class TaxPeriodConfiguration : IEntityTypeConfiguration<TaxPeriod>
{
    public void Configure(EntityTypeBuilder<TaxPeriod> b)
    {
        TaxMapping.Scope(b, "Periods");
        TaxMapping.Link<TaxPeriod, TaxRegistration>(b, nameof(TaxPeriod.RegistrationId));
        b.HasIndex(x => new { x.CompanyId, x.RegistrationId, x.From });
        b.ToTable(t => t.HasCheckConstraint("CK_Tax_Period", """ "From"<="To" """));
    }
}

public sealed class TaxOperationConfiguration : IEntityTypeConfiguration<TaxOperation>
{
    public void Configure(EntityTypeBuilder<TaxOperation> b)
    {
        TaxMapping.Scope(b, "Operations");
        TaxMapping.Link<TaxOperation, TaxRegistration>(b, nameof(TaxOperation.RegistrationId));
        TaxMapping.Link<TaxOperation, TaxPeriod>(b, nameof(TaxOperation.PeriodId));
        TaxMapping.Link<TaxOperation, TradeDocument>(b, nameof(TaxOperation.BillId));
        TaxMapping.Link<TaxOperation, SettlementPayment>(b, nameof(TaxOperation.PaymentId));
        TaxMapping.Link<TaxOperation, TaxOperation>(b, nameof(TaxOperation.BasisId));
        TaxMapping.Link<TaxOperation, JournalEntry>(b, nameof(TaxOperation.JournalId));
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.InputsJson).HasColumnType("jsonb");
        b.Property(x => x.PlanJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.CompanyId, x.RegistrationId, x.Reference }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.RegistrationId, x.Date, x.State });
        b.HasIndex(x => x.JournalId).IsUnique();
        b.HasIndex(x => x.PaymentId).IsUnique();
        b.HasIndex(x => x.BasisId).IsUnique().HasFilter(""" "State"=3 AND "Kind"=9 """);
        b.ToTable(t => t.HasCheckConstraint("CK_Tax_Operation",
            """ "Amount">=0 AND "Kind" BETWEEN 0 AND 9 AND "State" BETWEEN 0 AND 4 AND (("State"=3) = ("JournalId" IS NOT NULL)) """));
    }
}

public sealed class TaxReturnConfiguration : IEntityTypeConfiguration<TaxReturnVersion>
{
    public void Configure(EntityTypeBuilder<TaxReturnVersion> b)
    {
        TaxMapping.Scope(b, "Returns");
        TaxMapping.Link<TaxReturnVersion, TaxRegistration>(b, nameof(TaxReturnVersion.RegistrationId));
        TaxMapping.Link<TaxReturnVersion, TaxPeriod>(b, nameof(TaxReturnVersion.PeriodId));
        TaxMapping.Link<TaxReturnVersion, TaxReturnVersion>(b, nameof(TaxReturnVersion.SupersedesId));
        b.Property(x => x.SnapshotJson).HasColumnType("jsonb");
        b.Property(x => x.SourceHash).HasMaxLength(64);
        b.HasIndex(x => new { x.CompanyId, x.PeriodId, x.Version }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_Tax_Return", """ "Version">0 AND "State" IN(0,1) """));
    }
}

public sealed class RegulatoryDocumentConfiguration : IEntityTypeConfiguration<RegulatoryDocument>
{
    public void Configure(EntityTypeBuilder<RegulatoryDocument> b)
    {
        TaxMapping.Scope(b, "RegulatoryDocuments");
        TaxMapping.Link<RegulatoryDocument, TaxRegistration>(b, nameof(RegulatoryDocument.RegistrationId));
        TaxMapping.Link<RegulatoryDocument, TradeDocument>(b, nameof(RegulatoryDocument.DocumentId));
        TaxMapping.Link<RegulatoryDocument, TradeDocument>(b, nameof(RegulatoryDocument.OriginalDocumentId));
        b.HasIndex(x => new { x.CompanyId, x.DocumentId, x.Version }).IsUnique();
        b.Property(x => x.ContentJson).HasColumnType("jsonb");
        b.Property(x => x.ContentHash).HasMaxLength(64);
        b.ToTable(t => t.HasCheckConstraint("CK_Tax_StructuredVersion", """ "Version">0 """));
    }
}
