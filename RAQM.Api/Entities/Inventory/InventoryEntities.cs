using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.Entities.Inventory;

public enum ValuationMethod { Fifo, MovingAverage }
public enum ItemKind { Stocked, NonStock, Service }
public enum InventoryRole { Inventory, Grni, InvoiceClearing, Allowance, CostOfSales, Gain, Loss, WriteDown, PriceVariance, OpeningEquity }
public enum ProcurementState { Draft, Approved, Confirmed, Cancelled, Rejected }
public enum StockState { Draft, Approved, Posted, Cancelled }
public enum DiscrepancyKind { ReceiptQuantity, ReceiptDamage, InvoiceQuantity, InvoicePrice, Tax }
public enum StockKind
{
    Receipt, MatchBill, Issue, Transfer, Reserve, Release, Count, Opening, WriteDown,
    ReceiptCorrection, CustomerReturn, SupplierReturn, LandedCost
}

public sealed class InventoryPolicy : FinancialEvidence
{
    public ValuationMethod Method { get; set; }
    public decimal InvoicePriceTolerance { get; set; }
    public decimal ApprovalLimit { get; set; }
}

public sealed class InventoryAccount : FinancialEvidence
{
    public InventoryRole Role { get; set; }
    public Guid AccountId { get; set; }
}

public sealed class InventoryCategory : FinancialEvidence
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class InventoryItem : FinancialEvidence
{
    public Guid CategoryId { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? TaxCodeId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public ItemKind Kind { get; set; }
    public string? Description { get; set; }
    public string BaseUnit { get; set; } = "";
    public string PurchaseUnit { get; set; } = "";
    public string SalesUnit { get; set; } = "";
    public decimal PurchaseFactor { get; set; }
    public decimal SalesFactor { get; set; }
    public decimal? DefaultPurchasePrice { get; set; }
    public decimal? DefaultSalesPrice { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Warehouse : FinancialEvidence
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public Guid? ManagerId { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ProcurementDocument : FinancialEvidence
{
    public bool IsOrder { get; set; }
    public Guid? RequisitionId { get; set; }
    public Guid? ReplacesDocumentId { get; set; }
    public Guid? SupplierId { get; set; }
    public ProcurementState State { get; set; }
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Number { get; set; } = "";
    public string? Department { get; set; }
    public DateOnly Date { get; set; }
    public DateOnly? ExpectedDelivery { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public List<ProcurementLine> Lines { get; set; } = [];
}

public sealed class ProcurementLine : FinancialEvidence
{
    public Guid DocumentId { get; set; }
    public Guid ItemId { get; set; }
    public Guid? TaxRateId { get; set; }
    public decimal Quantity { get; set; }
    public decimal BaseQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
}

public sealed class ProcurementDiscrepancy : FinancialEvidence
{
    public DiscrepancyKind Kind { get; set; }
    public Guid OrderLineId { get; set; }
    public Guid? ReceiptId { get; set; }
    public DateOnly Date { get; set; }
    public string Evidence { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateOnly? ResolutionDate { get; set; }
    public string? Resolution { get; set; }
}

public sealed class StockOperation : FinancialEvidence
{
    public StockKind Kind { get; set; }
    public StockState State { get; set; }
    public DateOnly Date { get; set; }
    public Guid ItemId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid? RelatedOperationId { get; set; }
    public Guid? OrderLineId { get; set; }
    public Guid? TradeDocumentId { get; set; }
    public Guid? TradeLineId { get; set; }
    public Guid? SourceJournalLineId { get; set; }
    public Guid? JournalId { get; set; }
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string? PlanJson { get; set; }
    public decimal? BookQuantity { get; set; }
    public decimal Accrual { get; set; }
    public decimal SourceQuantity { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? PostedBy { get; set; }
    public long? Sequence { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

public sealed class StockMovement : FinancialEvidence
{
    public Guid OperationId { get; set; }
    public Guid ItemId { get; set; }
    public Guid WarehouseId { get; set; }
    public long Sequence { get; set; }
    public int Number { get; set; }
    public DateOnly Date { get; set; }
    public decimal Quantity { get; set; }
    public decimal Gross { get; set; }
    public decimal Allowance { get; set; }
    public decimal Reserved { get; set; }
    public string LotsJson { get; set; } = "[]";
}
