using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Trading;
using static RAQM.Api.Services.Inventory.InventoryRegister;
using static RAQM.Api.Helpers.Inventory.StockValuation;

namespace RAQM.Api.Services.Inventory;

public sealed partial class StockPlanning
{
    internal static StockRequest Input(StockOperation operation) => JsonSerializer.Deserialize<StockRequest>(operation.PayloadJson)!;
    internal static StockPlan Plan(StockOperation operation) => JsonSerializer.Deserialize<StockPlan>(operation.PlanJson!)!;

    private async Task<decimal> Receipt(StockRequest input, IQueryable<StockOperation> posted, Guid company, int units, CancellationToken ct)
    {
        var line = await (from l in db.ProcurementLines.AsNoTracking()
            join order in db.ProcurementDocuments.AsNoTracking() on l.DocumentId equals order.Id
            where l.CompanyId == company && order.CompanyId == company && l.Id == input.OrderLineId && l.ItemId == input.ItemId &&
                order.IsOrder && order.State == ProcurementState.Confirmed && order.Date <= input.Date
            select l).SingleOrDefaultAsync(ct) ?? throw Bad("Select a confirmed company PO line for this item.");
        var previous = await posted.Where(x => x.OrderLineId == line.Id &&
            (x.Kind == StockKind.Receipt || x.Kind == StockKind.ReceiptCorrection || x.Kind == StockKind.SupplierReturn)).ToListAsync(ct);
        var received = previous.Where(x => x.Kind == StockKind.Receipt).ToArray();
        var corrections = previous.Where(x => x.Kind != StockKind.Receipt && x.TradeDocumentId == null).ToArray();
        var remaining = line.BaseQuantity - received.Sum(x => Input(x).Quantity) + corrections.Sum(x => x.SourceQuantity);
        if (input.Quantity > remaining) throw Conflict("Goods receipt exceeds the remaining PO quantity.");
        var amount = line.NetAmount - received.Sum(x => x.Accrual) + corrections.Sum(x => x.Accrual);
        return Share(amount, input.Quantity, remaining, units);
    }

    private static async Task<StockOperation> Related(StockRequest input, IQueryable<StockOperation> posted, StockKind kind,
        CancellationToken ct, bool sameWarehouse = true) =>
        await posted.SingleOrDefaultAsync(x => x.Id == input.RelatedOperationId && x.Kind == kind && x.ItemId == input.ItemId &&
            (!sameWarehouse || x.WarehouseId == input.WarehouseId) && x.Date <= input.Date, ct)
        ?? throw Bad($"Select posted {kind} evidence for this item and warehouse.");

    private static async Task<(decimal Quantity, decimal Accrual)> ReceiptUnmatched(IQueryable<StockOperation> posted,
        StockOperation receipt, int units, CancellationToken ct)
    {
        var used = await posted.Where(x => x.ItemId == receipt.ItemId && (x.Kind == StockKind.MatchBill ||
            (x.RelatedOperationId == receipt.Id && (x.Kind == StockKind.ReceiptCorrection ||
                (x.Kind == StockKind.SupplierReturn && x.TradeDocumentId == null))))).ToListAsync(ct);
        var claims = used.SelectMany(x => x.Kind == StockKind.MatchBill ? Plan(x).Claims!.Where(y => y.ReceiptId == receipt.Id) :
            new[] { new ReceiptClaim(receipt.Id, x.SourceQuantity, x.Accrual, 0) }).ToArray();
        return (Input(receipt).Quantity - claims.Sum(x => x.Quantity), Round(receipt.Accrual - claims.Sum(x => x.Accrual), units));
    }

    private async Task<IReadOnlyList<ReceiptClaim>> Match(StockRequest input, InventoryItem item, InventoryPolicy policy, IQueryable<StockOperation> posted,
        Guid clearing, int units, CancellationToken ct)
    {
        var allocation = input.ReceiptQuantities ?? [new(input.RelatedOperationId!.Value, input.Quantity)];
        var (document, line) = await TradeLine(item.CompanyId, input, TradeKind.Bill, TradeState.Approved, ct);
        var shares = Allocate(input.Amount, allocation.Select(x => x.Quantity).ToArray(), units);
        var claims = new List<ReceiptClaim>();
        Guid? orderLineId = null;
        var claimWarehouse = false;
        for (var i = 0; i < allocation.Count; i++)
        {
            var part = allocation[i];
            var receipt = await Related(input with { RelatedOperationId = part.ReceiptId }, posted, StockKind.Receipt, ct, sameWarehouse: false);
            claimWarehouse |= receipt.WarehouseId == input.WarehouseId;
            orderLineId ??= receipt.OrderLineId;
            if (orderLineId != receipt.OrderLineId) throw Bad("A bill line can only match receipts from one PO line.");
            var available = await ReceiptUnmatched(posted, receipt, units, ct);
            if (part.Quantity > available.Quantity) throw Conflict("Invoice quantity exceeds the unmatched goods receipt.");
            claims.Add(new(receipt.Id, part.Quantity, Share(available.Accrual, part.Quantity, available.Quantity, units), shares[i]));
        }
        if (!claimWarehouse) throw Bad("Select a warehouse represented by the matched receipts.");
        var expected = await (from purchaseLine in db.ProcurementLines
            join order in db.ProcurementDocuments on purchaseLine.DocumentId equals order.Id
            where purchaseLine.CompanyId == item.CompanyId && order.CompanyId == item.CompanyId && purchaseLine.Id == orderLineId
            select new { order.SupplierId, purchaseLine.TaxRateId }).SingleAsync(ct);
        if (document.Lines.Count(x => x.AccountId == clearing) != 1)
            throw Bad("Split stocked bill lines into separately referenced bills before receipt matching. This operation posts the complete bill.");
        if (document.PartyId != expected.SupplierId || line.AccountId != clearing || line.TaxRateId != expected.TaxRateId ||
            line.Quantity * item.PurchaseFactor != input.Quantity || line.NetAmount != input.Amount)
            throw Bad("The approved bill must match the supplier, tax version, clearing line, quantity and net amount.");
        if (Math.Abs(input.Amount - claims.Sum(x => x.Accrual)) > policy.InvoicePriceTolerance)
            throw Conflict("The supplier price discrepancy exceeds the configured tolerance.");
        return claims;
    }

    internal static IQueryable<TradeDocumentLine> CancelledSales(RaqmDbContext db, Guid company, Guid saleLine) =>
        from line in db.TradeLines.AsNoTracking() join credit in db.TradeDocuments.AsNoTracking() on line.DocumentId equals credit.Id
        where line.CompanyId == company && credit.CompanyId == company && line.OriginalLineId == saleLine &&
            line.CancelUnissuedStock && credit.Kind == TradeKind.CustomerCredit && credit.State == TradeState.Posted
        select line;

    private async Task CheckIssue(StockRequest input, InventoryItem item, IQueryable<StockOperation> posted, CancellationToken ct)
    {
        var (_, line) = await TradeLine(item.CompanyId, input, TradeKind.Invoice, TradeState.Posted, ct);
        if (line.StockSalesFactor == 0 || (line.StockItemId.HasValue && line.StockItemId != item.Id))
            throw Conflict("The invoice line does not declare this stocked item.");
        if (line.StockSalesFactor == null && await db.AccountingAudit.AnyAsync(a => a.CompanyId == item.CompanyId &&
            a.EntityId == line.Id && a.Action == InventoryReporting.LegacyNonStockAction, ct))
            throw Conflict("This historical invoice line is retained as non-stocked.");
        var issues = await posted.Where(x => x.Kind == StockKind.Issue && x.TradeLineId == line.Id).ToListAsync(ct);
        var cancelled = await CancelledSales(db, item.CompanyId, line.Id).SumAsync(x => x.Quantity, ct);
        if (issues.Any(x => x.ItemId != item.Id) ||
            issues.Sum(x => Input(x).Quantity) + input.Quantity > (line.Quantity - cancelled) * (line.StockSalesFactor ?? item.SalesFactor))
            throw Conflict("The issue exceeds invoice quantity or changes the item for a previously linked line.");
        if (await (from creditLine in db.TradeLines join credit in db.TradeDocuments on creditLine.DocumentId equals credit.Id
            where creditLine.CompanyId == item.CompanyId && credit.CompanyId == item.CompanyId && credit.State == TradeState.Posted &&
                creditLine.OriginalLineId == line.Id && !creditLine.CancelUnissuedStock &&
                !posted.Any(x => x.Kind == StockKind.CustomerReturn && x.TradeDocumentId == credit.Id)
            select creditLine.Id).AnyAsync(ct))
            throw Conflict("An existing commercial credit on the invoice line needs separate reconciliation before inventory linkage.");
    }

    private static async Task CheckReservation(StockRequest input, IQueryable<StockOperation> posted, CancellationToken ct)
    {
        var reserve = await Related(input, posted, StockKind.Reserve, ct);
        var uses = await posted.Where(x => x.RelatedOperationId == reserve.Id &&
            (x.Kind == StockKind.Release || x.Kind == StockKind.Issue)).ToListAsync(ct);
        if (uses.Sum(x => Input(x).Quantity) + input.Quantity > Input(reserve).Quantity)
            throw Conflict("The reservation does not cover the requested quantity.");
    }

    private async Task CheckOpening(Guid company, Guid journalLine, Guid account, StockRequest input, IQueryable<StockOperation> posted,
        CancellationToken ct)
    {
        var source = await (from line in db.JournalLines join journal in db.Journals on line.JournalId equals journal.Id
            where line.CompanyId == company && journal.CompanyId == company && line.Id == journalLine && line.AccountId == account &&
                journal.State == JournalState.Posted && journal.SourceModule == "OpeningBalance" && journal.PostingDate == input.Date &&
                !db.Journals.Any(x => x.CompanyId == company && x.ReversesJournalId == journal.Id && x.State == JournalState.Posted)
            select line).SingleOrDefaultAsync(ct);
        if (source is null || source.Debit != input.Amount || await posted.AnyAsync(x => x.SourceJournalLineId == journalLine, ct))
            throw Conflict("Opening conversion requires an unused, exact posted opening debit to the inventory account.");
    }

    private async Task CheckNewOpening(Guid company, Guid account, StockRequest input, IQueryable<StockOperation> posted, CancellationToken ct)
    {
        if (await (from line in db.JournalLines join journal in db.Journals on line.JournalId equals journal.Id
            where line.CompanyId == company && journal.CompanyId == company && line.AccountId == account && line.Debit > 0 &&
                journal.State == JournalState.Posted && journal.SourceModule == "OpeningBalance" && journal.PostingDate == input.Date &&
                !db.Journals.Any(x => x.CompanyId == company && x.ReversesJournalId == journal.Id && x.State == JournalState.Posted) &&
                !posted.Any(x => x.SourceJournalLineId == line.Id)
            select line.Id).AnyAsync(ct))
            throw Conflict("Convert the existing opening inventory debit before creating new opening value.");
    }

    private async Task<(decimal Gross, decimal Allowance)> CustomerReturn(StockRequest input, InventoryItem item,
        IQueryable<StockOperation> posted, StockOperation original, int units, CancellationToken ct)
    {
        var (credit, line) = await TradeLine(item.CompanyId, input, TradeKind.CustomerCredit, TradeState.Approved, ct);
        var salesFactor = await db.TradeLines.Where(x => x.CompanyId == item.CompanyId && x.Id == original.TradeLineId)
            .Select(x => x.StockSalesFactor).SingleAsync(ct);
        var returnedBase = Quantity(line.Quantity * (salesFactor ?? item.SalesFactor));
        if (line.CancelUnissuedStock || credit.OriginalDocumentId != original.TradeDocumentId || line.OriginalLineId != original.TradeLineId ||
            returnedBase != input.Quantity || credit.Lines.Count != 1)
            throw Bad("Credit must refer to the original sales line and exact return quantity.");
        var previous = await posted.Where(x => x.Kind == StockKind.CustomerReturn && x.RelatedOperationId == original.Id).ToListAsync(ct);
        var remaining = Input(original).Quantity - previous.Sum(x => Input(x).Quantity);
        if (remaining < input.Quantity) throw Conflict("Customer return exceeds the unreturned issue quantity.");
        var movement = await db.StockMovements.AsNoTracking().SingleAsync(x => x.CompanyId == item.CompanyId && x.OperationId == original.Id, ct);
        return (Share(-movement.Gross - previous.Sum(x => x.Accrual), input.Quantity, remaining, units),
            Share(-movement.Allowance - previous.Sum(x => x.SourceQuantity), input.Quantity, remaining, units));
    }

    private async Task<decimal> SupplierCredit(StockRequest input, InventoryItem item, IQueryable<StockOperation> posted,
        StockOperation receipt, Guid clearing, int units, CancellationToken ct)
    {
        var (credit, line) = await TradeLine(item.CompanyId, input, TradeKind.SupplierCredit, TradeState.Approved, ct);
        var match = await posted.SingleOrDefaultAsync(x => x.Kind == StockKind.MatchBill &&
            x.TradeDocumentId == credit.OriginalDocumentId && x.TradeLineId == line.OriginalLineId, ct);
        var claim = match is null ? null : Plan(match).Claims!.SingleOrDefault(x => x.ReceiptId == receipt.Id);
        if (claim is null || line.AccountId != clearing || credit.Lines.Count != 1 || line.Quantity * item.PurchaseFactor != input.Quantity)
            throw Bad("Supplier credit must refer to the matched bill line and exact return quantity.");
        var previous = await (from row in posted join document in db.TradeDocuments on row.TradeDocumentId equals (Guid?)document.Id
            where row.Kind == StockKind.SupplierReturn && row.RelatedOperationId == receipt.Id && document.CompanyId == item.CompanyId &&
                document.OriginalDocumentId == match!.TradeDocumentId
            select row).ToListAsync(ct);
        var remaining = claim.Quantity - previous.Sum(x => Input(x).Quantity);
        var remainingAmount = claim.InvoiceNet - previous.Sum(x => Input(x).Amount);
        if (input.Quantity > remaining || line.NetAmount != input.Amount || line.NetAmount != Share(remainingAmount, input.Quantity, remaining, units))
            throw Conflict("Return exceeds the billed stock or its original proportionate invoice value.");
        return line.NetAmount;
    }

    private async Task<(TradeDocument Document, TradeDocumentLine Line)> TradeLine(Guid company, StockRequest input, TradeKind kind,
        TradeState state, CancellationToken ct)
    {
        var document = await db.TradeDocuments.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == company &&
            x.Id == input.TradeDocumentId && x.Kind == kind && x.State == state && x.DocumentDate <= input.Date &&
            (x.PostingDate == null || x.PostingDate <= input.Date), ct)
            ?? throw Bad($"Select a {state} {kind} in this company.");
        return (document, document.Lines.SingleOrDefault(x => x.Id == input.TradeLineId) ?? throw Bad("Select a line on the linked trade document."));
    }

    private async Task<(Guid Account, IReadOnlyList<MovementPlan> Movements, IReadOnlyList<LandedAllocation> Allocations)> Landed(StockRequest input,
        IQueryable<StockOperation> posted, Guid company, int units, CancellationToken ct)
    {
        var receipts = new List<StockOperation>();
        foreach (var id in input.LandedReceipts ?? [input.RelatedOperationId!.Value])
            receipts.Add(await Related(input with { RelatedOperationId = id }, posted, StockKind.Receipt, ct));
        var earliest = receipts.Min(x => x.Date);
        var source = await (from l in db.JournalLines join j in db.Journals on l.JournalId equals j.Id
            join a in db.Accounts on l.AccountId equals a.Id
            where l.CompanyId == company && j.CompanyId == company && a.CompanyId == company && l.Id == input.SourceJournalLineId &&
                j.State == JournalState.Posted && (j.SourceModule == "AP.Bill" || j.SourceModule == "ManualJournal" || j.SourceModule == "Manual") &&
                j.PostingDate >= earliest && j.PostingDate <= input.Date && a.Classification == AccountClass.Expense &&
                !db.InventoryAccounts.Any(x => x.CompanyId == company && x.AccountId == a.Id) &&
                !db.Journals.Any(x => x.CompanyId == company && x.ReversesJournalId == j.Id && x.State == JournalState.Posted)
            select l).SingleOrDefaultAsync(ct);
        if (source is null || source.Debit != input.Amount || input.Amount <= 0 || input.Quantity != 0 ||
            await posted.AnyAsync(x => x.SourceJournalLineId == input.SourceJournalLineId, ct))
            throw Bad("Landed cost requires an unused, exact posted acquisition expense debit and zero quantity.");
        var rows = await db.StockMovements.AsNoTracking().Where(x => x.CompanyId == company && x.ItemId == input.ItemId)
            .OrderBy(x => x.Sequence).ThenBy(x => x.Number).ToListAsync(ct);
        var origins = receipts.Select(x => x.Id).ToHashSet();
        var outflows = rows.Where(x => x.Quantity < 0 && JsonSerializer.Deserialize<LotChange[]>(x.LotsJson)!.Any(l => origins.Contains(l.Origin)))
            .Select(x => x.OperationId).Distinct().ToArray();
        if (await posted.AnyAsync(x => (outflows.Contains(x.Id) && x.Kind != StockKind.Transfer && x.Kind != StockKind.Issue) ||
            (x.Kind == StockKind.CustomerReturn && x.RelatedOperationId.HasValue && outflows.Contains(x.RelatedOperationId.Value)), ct))
            throw Conflict("Landed cost on previously adjusted or returned receipt stock requires a separate controlled correction.");
        var eligible = rows.GroupBy(x => x.WarehouseId).OrderBy(x => x.Key).SelectMany(group =>
            Lots(group).Where(x => origins.Contains(x.Origin)).Select(x => (Warehouse: group.Key, Lot: x))).ToLookup(x => x.Lot.Origin);
        var weights = receipts.Select(x => input.LandedBasis == LandedCostBasis.Quantity ? Input(x).Quantity : x.Accrual).ToArray();
        var shares = Allocate(input.Amount, weights, units);
        var adjustments = new List<(Guid Warehouse, LotChange Delta)>();
        var allocations = new List<LandedAllocation>();
        for (var i = 0; i < receipts.Count; i++)
        {
            var receipt = receipts[i];
            var lots = eligible[receipt.Id].ToArray();
            var total = Input(receipt).Quantity;
            var remaining = lots.Sum(x => x.Lot.Quantity);
            if (remaining > total) throw Conflict("Receipt provenance does not reconcile.");
            var capitalized = Share(shares[i], remaining, total, units);
            allocations.Add(new(receipt.Id, weights[i], capitalized, shares[i] - capitalized));
            if (capitalized == 0) continue;
            var amounts = Allocate(capitalized, lots.Select(x => x.Lot.Quantity).ToArray(), units);
            adjustments.AddRange(lots.Select((x, j) => (x.Warehouse, x.Lot with { Quantity = 0, Cost = amounts[j] })));
        }
        var movements = adjustments.Where(x => x.Delta.Cost != 0).GroupBy(x => x.Warehouse).Select(group =>
                new MovementPlan(group.Key, 0, group.Sum(x => x.Delta.Cost), 0, 0, group.Select(x => x.Delta).ToArray())).ToArray();
        return (source.AccountId, movements, allocations);
    }
}
