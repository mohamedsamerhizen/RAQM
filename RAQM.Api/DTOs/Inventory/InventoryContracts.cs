using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Inventory;

namespace RAQM.Api.DTOs.Inventory;

public sealed record InventoryPolicyRequest(ValuationMethod Method, decimal InvoicePriceTolerance, decimal ApprovalLimit,
    IReadOnlyDictionary<InventoryRole, Guid> Accounts);
public sealed record InventoryCategoryRequest(string Code, string Name);
public sealed record InventoryItemRequest(Guid CategoryId, string Code, string Name, ItemKind Kind, string BaseUnit,
    string PurchaseUnit, string SalesUnit, decimal PurchaseFactor, decimal SalesFactor, Guid? SupplierId, Guid? TaxCodeId,
    string? Description = null, decimal? DefaultPurchasePrice = null, decimal? DefaultSalesPrice = null);
public sealed record WarehouseRequest(string Code, string Name, string Location, Guid? ManagerId);
public sealed record InventoryStatusRequest(bool IsActive, string Reason);
public sealed record ProcurementLineRequest(Guid ItemId, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, Guid? TaxRateId);
public sealed record ProcurementRequest(string Key, bool IsOrder, Guid? RequisitionId, Guid? SupplierId, DateOnly Date,
    string Evidence, IReadOnlyList<ProcurementLineRequest> Lines, DateOnly? ExpectedDelivery = null, string? Department = null,
    Guid? ReplacesDocumentId = null);
public sealed record InventoryAction(Guid Version, string Reason, bool AllowBackdated = false);
public sealed record LegacySaleClassification(string Reason);
public sealed record DiscrepancyRequest(DiscrepancyKind Kind, Guid OrderLineId, Guid? ReceiptId, DateOnly Date, string Evidence);
public sealed record DiscrepancyResolution(string Reason);
public enum LandedCostBasis { Quantity, PurchaseValue }
public sealed record ReceiptQuantity(Guid ReceiptId, decimal Quantity);
public sealed record StockRequest(string Key, StockKind Kind, DateOnly Date, Guid ItemId, Guid WarehouseId, decimal Quantity,
    string Evidence, Guid? DestinationId = null, Guid? OrderLineId = null, Guid? RelatedOperationId = null,
    Guid? TradeDocumentId = null, Guid? TradeLineId = null, Guid? SourceJournalLineId = null, decimal Amount = 0,
    bool Damaged = false, IReadOnlyList<ReceiptQuantity>? ReceiptQuantities = null,
    IReadOnlyList<Guid>? LandedReceipts = null, LandedCostBasis LandedBasis = LandedCostBasis.Quantity);

public sealed record LotChange(Guid Id, Guid Origin, decimal Quantity, decimal Cost);
public sealed record ReceiptClaim(Guid ReceiptId, decimal Quantity, decimal Accrual, decimal InvoiceNet);
public sealed record LandedAllocation(Guid ReceiptId, decimal Weight, decimal Capitalized, decimal Expensed);
public sealed record MovementPlan(Guid WarehouseId, decimal Quantity, decimal Gross, decimal Allowance, decimal Reserved,
    IReadOnlyList<LotChange> Lots);
public sealed record StockPlan(IReadOnlyList<MovementPlan> Movements, IReadOnlyList<JournalDraftLine> Lines,
    decimal Accrual = 0, decimal SourceQuantity = 0, IReadOnlyList<ReceiptClaim>? Claims = null,
    IReadOnlyList<LandedAllocation>? Landed = null);
public sealed record StockBalance(Guid ItemId, Guid WarehouseId, decimal OnHand, decimal Reserved, decimal Gross, decimal Allowance)
{
    public decimal Available => OnHand - Reserved;
    public decimal CarryingValue => Gross - Allowance;
}
