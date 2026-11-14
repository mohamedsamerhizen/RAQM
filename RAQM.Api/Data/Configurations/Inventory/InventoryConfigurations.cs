using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RAQM.Api.Data.Configurations.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.Identity;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Trading;

namespace RAQM.Api.Data.Configurations.Inventory;

public sealed class InventoryConfigurations : IEntityTypeConfiguration<InventoryPolicy>, IEntityTypeConfiguration<InventoryAccount>,
    IEntityTypeConfiguration<InventoryCategory>, IEntityTypeConfiguration<InventoryItem>, IEntityTypeConfiguration<Warehouse>,
    IEntityTypeConfiguration<ProcurementDocument>, IEntityTypeConfiguration<ProcurementLine>, IEntityTypeConfiguration<StockOperation>,
    IEntityTypeConfiguration<StockMovement>, IEntityTypeConfiguration<ProcurementDiscrepancy>
{
    private const string Schema = "inventory";
    private static void Scope<T>(EntityTypeBuilder<T> b, string name) where T : FinancialEvidence
    {
        FinancialMapping.Scope(b, name);
        b.ToTable(name, Schema);
        foreach (var property in typeof(T).GetProperties().Where(x => x.PropertyType == typeof(decimal) || x.PropertyType == typeof(decimal?)))
            b.Property(property.Name).HasPrecision(24, 6);
    }

    private static void Link<T, TTarget>(EntityTypeBuilder<T> b, string column) where T : FinancialEvidence where TTarget : class, ICompanyFinancialEntity =>
        b.HasOne<TTarget>().WithMany().HasForeignKey("CompanyId", column).HasPrincipalKey("CompanyId", "Id").OnDelete(DeleteBehavior.Restrict);
    private static void Actor<T>(EntityTypeBuilder<T> b, string column) where T : FinancialEvidence =>
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(column).OnDelete(DeleteBehavior.Restrict);

    public void Configure(EntityTypeBuilder<InventoryPolicy> b)
    {
        Scope(b, "Policies");
        b.HasIndex(x => x.CompanyId).IsUnique();
        b.ToTable("Policies", Schema, t => t.HasCheckConstraint("CK_Inventory_Policy", """
            "Method" IN (0,1) AND "InvoicePriceTolerance">=0 AND "ApprovalLimit">0
            """));
    }
    public void Configure(EntityTypeBuilder<InventoryAccount> b)
    {
        Scope(b, "InventoryAccounts");
        Link<InventoryAccount, LedgerAccount>(b, nameof(InventoryAccount.AccountId));
        b.HasIndex(x => new { x.CompanyId, x.Role }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.AccountId }).IsUnique();
        b.ToTable("InventoryAccounts", Schema, t => t.HasCheckConstraint("CK_Inventory_Role", """ "Role" BETWEEN 0 AND 9 """));
    }
    public void Configure(EntityTypeBuilder<InventoryCategory> b)
    {
        Scope(b, "Categories");
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
    }
    public void Configure(EntityTypeBuilder<InventoryItem> b)
    {
        Scope(b, "Items");
        Link<InventoryItem, InventoryCategory>(b, nameof(InventoryItem.CategoryId));
        Link<InventoryItem, TradingParty>(b, nameof(InventoryItem.SupplierId));
        Link<InventoryItem, TradingTaxCode>(b, nameof(InventoryItem.TaxCodeId));
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.ToTable("Items", Schema, t => t.HasCheckConstraint("CK_Inventory_Item", """
            "Kind" BETWEEN 0 AND 2 AND "PurchaseFactor">0 AND "SalesFactor">0 AND "DefaultPurchasePrice">=0 AND "DefaultSalesPrice">=0
            """));
    }
    public void Configure(EntityTypeBuilder<Warehouse> b)
    {
        Scope(b, "Warehouses");
        b.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        Actor(b, nameof(Warehouse.ManagerId));
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(160);
    }
    public void Configure(EntityTypeBuilder<ProcurementDocument> b)
    {
        Scope(b, "ProcurementDocuments");
        Link<ProcurementDocument, ProcurementDocument>(b, nameof(ProcurementDocument.RequisitionId));
        Link<ProcurementDocument, ProcurementDocument>(b, nameof(ProcurementDocument.ReplacesDocumentId));
        Link<ProcurementDocument, TradingParty>(b, nameof(ProcurementDocument.SupplierId));
        Actor(b, nameof(ProcurementDocument.CreatedBy));
        Actor(b, nameof(ProcurementDocument.ApprovedBy));
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.CompanyId, x.RequestKey }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Property(x => x.Number).HasMaxLength(20);
        b.Property(x => x.Department).HasMaxLength(160);
        b.HasIndex(x => new { x.CompanyId, x.RequisitionId }).IsUnique()
            .HasFilter("\"State\" NOT IN (3,4) AND \"RequisitionId\" IS NOT NULL");
        b.HasIndex(x => new { x.CompanyId, x.ReplacesDocumentId }).IsUnique()
            .HasFilter("\"State\" NOT IN (3,4) AND \"ReplacesDocumentId\" IS NOT NULL");
        b.ToTable("ProcurementDocuments", Schema, t => t.HasCheckConstraint("CK_Inventory_Procurement", """
            "State" BETWEEN 0 AND 4 AND ("ExpectedDelivery" IS NULL OR "ExpectedDelivery">="Date") AND
            ("IsOrder" OR "ReplacesDocumentId" IS NULL) AND (("IsOrder" AND "SupplierId" IS NOT NULL) OR
            (NOT "IsOrder" AND "SupplierId" IS NULL AND "RequisitionId" IS NULL AND "State"<>2))
            """));
    }
    public void Configure(EntityTypeBuilder<ProcurementLine> b)
    {
        Scope(b, "ProcurementLines");
        b.HasOne<ProcurementDocument>().WithMany(x => x.Lines).HasForeignKey(x => new { x.CompanyId, x.DocumentId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        Link<ProcurementLine, InventoryItem>(b, nameof(ProcurementLine.ItemId));
        Link<ProcurementLine, TradingTaxRate>(b, nameof(ProcurementLine.TaxRateId));
        b.HasIndex(x => new { x.CompanyId, x.DocumentId, x.ItemId }).IsUnique();
        b.ToTable("ProcurementLines", Schema, t => t.HasCheckConstraint("CK_Inventory_ProcurementLine", """
            "Quantity">0 AND "BaseQuantity">0 AND "UnitPrice">=0 AND "DiscountPercent" BETWEEN 0 AND 100 AND "NetAmount">=0 AND "TaxAmount">=0
            """));
    }
    public void Configure(EntityTypeBuilder<StockOperation> b)
    {
        Scope(b, "Operations");
        Link<StockOperation, InventoryItem>(b, nameof(StockOperation.ItemId));
        Link<StockOperation, Warehouse>(b, nameof(StockOperation.WarehouseId));
        Link<StockOperation, StockOperation>(b, nameof(StockOperation.RelatedOperationId));
        Link<StockOperation, ProcurementLine>(b, nameof(StockOperation.OrderLineId));
        Link<StockOperation, TradeDocument>(b, nameof(StockOperation.TradeDocumentId));
        Link<StockOperation, TradeDocumentLine>(b, nameof(StockOperation.TradeLineId));
        Link<StockOperation, JournalLine>(b, nameof(StockOperation.SourceJournalLineId));
        Link<StockOperation, JournalEntry>(b, nameof(StockOperation.JournalId));
        Actor(b, nameof(StockOperation.CreatedBy));
        Actor(b, nameof(StockOperation.ApprovedBy));
        Actor(b, nameof(StockOperation.PostedBy));
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.PayloadJson).HasColumnType("jsonb");
        b.Property(x => x.PlanJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.CompanyId, x.RequestKey }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.Sequence }).IsUnique();
        b.HasIndex(x => new { x.CompanyId, x.SourceJournalLineId }).IsUnique()
            .HasFilter("\"State\" = 2 AND \"SourceJournalLineId\" IS NOT NULL");
        b.ToTable("Operations", Schema, t => t.HasCheckConstraint("CK_Inventory_Operation", """
            "Kind" BETWEEN 0 AND 12 AND "State" BETWEEN 0 AND 3 AND
            (("State"=2)=("Sequence" IS NOT NULL)) AND
            ("State"<>2 OR ("ApprovedBy" IS NOT NULL AND "PostedBy" IS NOT NULL AND "PlanJson" IS NOT NULL))
            """));
    }
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        Scope(b, "StockMovements");
        Link<StockMovement, StockOperation>(b, nameof(StockMovement.OperationId));
        Link<StockMovement, InventoryItem>(b, nameof(StockMovement.ItemId));
        Link<StockMovement, Warehouse>(b, nameof(StockMovement.WarehouseId));
        b.Property(x => x.LotsJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.CompanyId, x.ItemId, x.WarehouseId, x.Sequence, x.Number });
        b.HasIndex(x => new { x.CompanyId, x.OperationId, x.Number }).IsUnique();
        b.ToTable("StockMovements", Schema, t => t.HasCheckConstraint("CK_Inventory_Movement", """
            "Number">0 AND "Sequence">0 AND ("Quantity"<>0 OR "Gross"<>0 OR "Allowance"<>0 OR "Reserved"<>0)
            """));
    }

    public void Configure(EntityTypeBuilder<ProcurementDiscrepancy> b)
    {
        Scope(b, "ProcurementDiscrepancies");
        Link<ProcurementDiscrepancy, ProcurementLine>(b, nameof(ProcurementDiscrepancy.OrderLineId));
        Link<ProcurementDiscrepancy, StockOperation>(b, nameof(ProcurementDiscrepancy.ReceiptId));
        Actor(b, nameof(ProcurementDiscrepancy.CreatedBy));
        Actor(b, nameof(ProcurementDiscrepancy.ResolvedBy));
        b.Property(x => x.Evidence).HasMaxLength(1000);
        b.Property(x => x.Resolution).HasMaxLength(500);
        b.HasIndex(x => new { x.CompanyId, x.Date, x.ResolutionDate });
        b.ToTable("ProcurementDiscrepancies", Schema, t => t.HasCheckConstraint("CK_Inventory_Discrepancy", """
            "Kind" BETWEEN 0 AND 4 AND ("ResolutionDate" IS NULL)=("ResolvedBy" IS NULL)
              AND ("ResolutionDate" IS NULL)=("Resolution" IS NULL) AND ("ResolutionDate" IS NULL OR "ResolutionDate">="Date")
            """));
    }
}
