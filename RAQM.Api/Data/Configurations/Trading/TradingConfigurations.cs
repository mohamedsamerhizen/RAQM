using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Data.Configurations.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.Trading;

namespace RAQM.Api.Data.Configurations.Trading;
internal static class TradingMapping
{
    public static void Scope<T>(EntityTypeBuilder<T> b, string table)
        where T : class, ICompanyFinancialEntity
    {
        FinancialMapping.Scope(b, table);
        b.ToTable(table, "trading");
    }
}

public sealed class PartyConfiguration : IEntityTypeConfiguration<TradingParty>
{
    public void Configure(EntityTypeBuilder<TradingParty> b)
    {
        TradingMapping.Scope(b, "Parties");
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.Email).HasMaxLength(254);
        b.Property(x => x.Phone).HasMaxLength(80);
        b.Property(x => x.BillingAddress).HasMaxLength(1000);
        b.Property(x => x.TaxIdentifier).HasMaxLength(80);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.CreditLimit).HasPrecision(20, 4);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.CompanyId, x.Kind, x.Code }).IsUnique();
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ControlAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.DefaultAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("Parties", "trading", t => t.HasCheckConstraint("CK_Party_Configuration", "\"Kind\" IN(0,1) AND \"PaymentTermsDays\" BETWEEN 0 AND 365 AND (\"CreditLimit\" IS NULL OR \"CreditLimit\">=0)"));
    }
}

public sealed class TradingAccountConfiguration : IEntityTypeConfiguration<TradingAccountMapping>
{
    public void Configure(EntityTypeBuilder<TradingAccountMapping> b)
    {
        TradingMapping.Scope(b, "AccountMappings");
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.AccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CompanyId, x.AccountId }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Role }).IsUnique().HasFilter("\"Role\" <> 4");
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("AccountMappings", "trading", t => t.HasCheckConstraint("CK_TradingRole", "\"Role\" BETWEEN 0 AND 9"));
    }
}

public sealed class TaxCodeConfiguration : IEntityTypeConfiguration<TradingTaxCode>
{
    public void Configure(EntityTypeBuilder<TradingTaxCode> b)
    {
        TradingMapping.Scope(b, "TaxCodes");
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.OutputAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.InputAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TaxRateConfiguration : IEntityTypeConfiguration<TradingTaxRate>
{
    public void Configure(EntityTypeBuilder<TradingTaxRate> b)
    {
        TradingMapping.Scope(b, "TaxRates");
        b.Property(x => x.RatePercent).HasPrecision(9, 6);
        b.HasIndex(x => new { x.TaxCodeId, x.Version }).IsUnique();
        b.HasOne<TradingTaxCode>().WithMany().HasForeignKey(x => new { x.CompanyId, x.TaxCodeId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("TaxRates", "trading", t => t.HasCheckConstraint("CK_TaxRate", "\"Version\">0 AND \"RatePercent\" BETWEEN 0 AND 100 AND \"EffectiveFrom\"<=\"EffectiveTo\""));
    }
}

public sealed class TradeDocumentConfiguration : IEntityTypeConfiguration<TradeDocument>
{
    public void Configure(EntityTypeBuilder<TradeDocument> b)
    {
        TradingMapping.Scope(b, "Documents");
        b.HasOne<TradingParty>().WithMany().HasForeignKey(x => new { x.CompanyId, x.PartyId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TradeDocument>().WithMany().HasForeignKey(x => new { x.CompanyId, x.OriginalDocumentId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ControlAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.CompanyId, x.JournalId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.PartyName).HasMaxLength(160);
        b.Property(x => x.PartyTaxIdentifier).HasMaxLength(80);
        b.Property(x => x.BillingAddress).HasMaxLength(1000);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.ExternalReference).HasMaxLength(160);
        b.Property(x => x.SupplierReferenceKey).HasMaxLength(160);
        b.Property(x => x.Number).HasMaxLength(40);
        b.Property(x => x.PostKey).HasMaxLength(64);
        b.Property(x => x.PostRequestHash).HasMaxLength(64);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.CompanyId, x.Kind, x.Sequence }).IsUnique().HasFilter("\"State\"=2");
        b.HasIndex(x => new { x.CompanyId, x.PartyId, x.SupplierReferenceKey }).IsUnique().HasFilter("\"Kind\"=1 AND \"State\"<>3");
        b.HasIndex(x => new { x.CompanyId, x.PartyId, x.Kind }).IsUnique().HasFilter("\"Kind\" IN (4,5) AND \"State\"<>3");
        b.HasIndex(x => new { x.CompanyId, x.PartyId, x.State, x.DueDate });
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ApprovedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.PostedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("Documents", "trading", t =>
        {
            t.HasCheckConstraint("CK_TradeDocument", "\"Kind\" BETWEEN 0 AND 5 AND \"State\" BETWEEN 0 AND 3 AND \"NetTotal\">=0 AND \"TaxTotal\">=0 AND \"Total\">0 AND \"Total\"=\"NetTotal\"+\"TaxTotal\" AND \"DueDate\">=\"DocumentDate\" AND \"CurrencyPolicyVersion\"=1");
            t.HasCheckConstraint("CK_TradeOriginal", "(\"Kind\" IN(2,3)) = (\"OriginalDocumentId\" IS NOT NULL)");
            t.HasCheckConstraint("CK_TradePosted", "\"State\"<>2 OR (\"JournalId\" IS NOT NULL AND \"PostingDate\" IS NOT NULL AND \"Number\" IS NOT NULL AND \"Sequence\">0 AND \"PostKey\" IS NOT NULL AND \"PostedBy\" IS NOT NULL AND \"PostedAt\" IS NOT NULL)");
        });
    }
}

public sealed class TradeLineConfiguration : IEntityTypeConfiguration<TradeDocumentLine>
{
    public void Configure(EntityTypeBuilder<TradeDocumentLine> b)
    {
        TradingMapping.Scope(b, "DocumentLines");
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Quantity).HasPrecision(20, 6);
        b.Property(x => x.StockSalesFactor).HasPrecision(20, 6);
        b.HasOne<RAQM.Api.Entities.Inventory.InventoryItem>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.StockItemId }).HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.UnitPrice).HasPrecision(20, 4);
        b.Property(x => x.DiscountPercent).HasPrecision(9, 6);
        b.Property(x => x.TaxPercent).HasPrecision(9, 6);
        b.HasIndex(x => new { x.DocumentId, x.Number }).IsUnique();
        b.HasOne<TradeDocument>().WithMany(x => x.Lines).HasForeignKey(x => new { x.CompanyId, x.DocumentId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TradeDocumentLine>().WithMany().HasForeignKey(x => new { x.CompanyId, x.OriginalLineId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.AccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.TaxAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TradingTaxRate>().WithMany().HasForeignKey(x => new { x.CompanyId, x.TaxRateId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("DocumentLines", "trading", t => t.HasCheckConstraint("CK_TradeLine", "\"Number\">0 AND \"Quantity\">0 AND \"UnitPrice\">=0 AND \"DiscountPercent\" BETWEEN 0 AND 100 AND \"TaxPercent\" BETWEEN 0 AND 100 AND \"NetAmount\">=0 AND \"TaxAmount\">=0 AND \"Total\"=\"NetAmount\"+\"TaxAmount\""));
    }
}

public sealed class PaymentConfiguration : IEntityTypeConfiguration<SettlementPayment>
{
    public void Configure(EntityTypeBuilder<SettlementPayment> b)
    {
        b.HasOne<RAQM.Api.Entities.Banking.TreasuryAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.BankAccountId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        TradingMapping.Scope(b, "Payments");
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Reference).HasMaxLength(160);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.RequestKey).HasMaxLength(64);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.ReversalKey).HasMaxLength(64);
        b.Property(x => x.ReversalReason).HasMaxLength(500);
        b.Property(x => x.Number).HasMaxLength(40);
        b.HasIndex(x => new { x.CompanyId, x.RequestKey }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Sequence }).IsUnique().HasFilter("\"State\"<>0");
        b.HasOne<TradingParty>().WithMany().HasForeignKey(x => new { x.CompanyId, x.PartyId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SettlementPayment>().WithMany().HasForeignKey(x => new { x.CompanyId, x.RefundPaymentId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TradeDocument>().WithMany().HasForeignKey(x => new { x.CompanyId, x.RefundCreditId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.CompanyId, x.JournalId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ReversalJournalId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.CashAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ControlAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.AdvanceAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ReversedBy).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("Payments", "trading", t =>
        {
            t.HasCheckConstraint("CK_PaymentAmounts", "\"PartyKind\" IN(0,1) AND \"State\" IN(0,1,2) AND \"Amount\">0 AND \"ControlAmount\">=0 AND \"AdvanceAmount\">=0 AND \"Amount\"=\"ControlAmount\"+\"AdvanceAmount\"");
            t.HasCheckConstraint("CK_PaymentRefund", "(NOT \"IsRefund\" AND \"RefundPaymentId\" IS NULL AND \"RefundCreditId\" IS NULL) OR (\"IsRefund\" AND ((\"RefundPaymentId\" IS NOT NULL)::int+(\"RefundCreditId\" IS NOT NULL)::int)=1)");
            t.HasCheckConstraint("CK_PaymentPosted", "\"State\"=0 OR (\"JournalId\" IS NOT NULL AND \"PostedAt\" IS NOT NULL AND \"Sequence\">0 AND \"Number\" IS NOT NULL)");
            t.HasCheckConstraint("CK_PaymentReversed", "\"State\"<>2 OR (\"ReversalJournalId\" IS NOT NULL AND \"ReversalDate\" IS NOT NULL AND \"ReversalKey\" IS NOT NULL AND \"ReversedBy\" IS NOT NULL)");
        });
    }
}

public sealed class AllocationConfiguration : IEntityTypeConfiguration<SettlementAllocation>
{
    public void Configure(EntityTypeBuilder<SettlementAllocation> b)
    {
        TradingMapping.Scope(b, "Allocations");
        b.Property(x => x.RequestKey).HasMaxLength(64);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasIndex(x => new { x.CompanyId, x.RequestKey }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.ReversesAllocationId }).IsUnique().HasFilter("\"ReversesAllocationId\" IS NOT NULL");
        b.HasIndex(x => new { x.CompanyId, x.PartyId, x.EffectiveDate });
        b.HasOne<TradingParty>().WithMany().HasForeignKey(x => new { x.CompanyId, x.PartyId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SettlementPayment>().WithMany().HasForeignKey(x => new { x.CompanyId, x.PaymentId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TradeDocument>().WithMany().HasForeignKey(x => new { x.CompanyId, x.CreditDocumentId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TradeDocument>().WithMany().HasForeignKey(x => new { x.CompanyId, x.TargetDocumentId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SettlementAllocation>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ReversesAllocationId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.CompanyId, x.JournalId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.ControlAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.CompanyId, x.AdvanceAccountId }).HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable("Allocations", "trading", t => t.HasCheckConstraint("CK_Allocation", "\"State\" IN(0,1) AND \"PartyKind\" IN(0,1) AND \"FundingKind\" IN(0,1) AND \"Amount\">0 AND \"ControlAmount\">=0 AND \"AdvanceAmount\">=0 AND \"Amount\"=\"ControlAmount\"+\"AdvanceAmount\" AND ((\"FundingKind\"=0 AND \"PaymentId\" IS NOT NULL AND \"CreditDocumentId\" IS NULL) OR (\"FundingKind\"=1 AND \"CreditDocumentId\" IS NOT NULL AND \"PaymentId\" IS NULL AND \"AdvanceAmount\"=0))"));
    }
}
