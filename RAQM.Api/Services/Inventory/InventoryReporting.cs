using System.Data;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.Accounting;
using static RAQM.Api.Helpers.Inventory.StockValuation;

namespace RAQM.Api.Services.Inventory;

public sealed record InventoryControlBalance(InventoryRole Role, decimal Expected, decimal Ledger)
{
    public decimal Difference => Ledger - Expected;
}
public sealed record ReceiptBalance(Guid ReceiptId, Guid? OrderLineId, DateOnly Date, decimal Quantity, decimal UnmatchedQuantity,
    decimal Grni, int AgeDays);
public sealed record FulfillmentBalance(Guid InvoiceId, Guid InvoiceLineId, Guid? ItemId, DateOnly InvoicePostingDate,
    decimal InvoiceQuantity, decimal Issued, decimal Returned, IReadOnlyList<Guid> Issues, bool LaterIssue)
{
    public decimal Remaining => InvoiceQuantity - Cancelled - Issued;
    public decimal Cancelled { get; init; }
    public decimal CostOfSales { get; init; }
    public bool CostEvidenceAgrees { get; init; }
}
public sealed record InventoryCloseEvidence(IReadOnlyList<CloseFinding> Findings, string Fingerprint);

public sealed class InventoryReporting(RaqmDbContext db, FinancialAccess access,
    RAQM.Api.Interfaces.Accounting.IFinancialTransaction transactions)
{
    internal const string LegacyNonStockAction = "LegacyInvoiceLineConfirmedNonStock";

    public Task<Guid> ConfirmLegacyNonStock(Guid actor, Guid org, Guid company, Guid lineId, string reason, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var line = await (from l in db.TradeLines join d in db.TradeDocuments on l.DocumentId equals d.Id
                where l.CompanyId == company && d.CompanyId == company && l.Id == lineId && l.StockSalesFactor == null &&
                    d.Kind == TradeKind.Invoice && d.State == TradeState.Posted
                select l.Id).SingleOrDefaultAsync(token);
            if (line == Guid.Empty) throw InventoryRegister.Missing("Select an unclassified historical invoice line.");
            if (await db.StockOperations.AnyAsync(x => x.CompanyId == company && x.TradeLineId == lineId &&
                x.Kind == StockKind.Issue && x.State != StockState.Cancelled, token))
                throw InventoryRegister.Conflict("This line already has inventory issue evidence.");
            if (!await db.AccountingAudit.AnyAsync(x => x.CompanyId == company && x.EntityId == lineId &&
                x.Action == LegacyNonStockAction, token))
            {
                access.Audit(scope, actor, lineId, LegacyNonStockAction,
                    RAQM.Api.Helpers.Accounting.AccountingRules.Text(reason, 500, "Classification evidence"));
            }
            return lineId;
        }, ct);

    private async Task<(List<StockMovement> Movements, List<StockOperation> Posted)> Load(Guid company, DateOnly at, CancellationToken ct) =>
        (await db.StockMovements.AsNoTracking().Where(x => x.CompanyId == company && x.Date <= at)
            .OrderBy(x => x.Sequence).ThenBy(x => x.Number).ToListAsync(ct),
         await db.StockOperations.AsNoTracking().Where(x => x.CompanyId == company && x.Date <= at && x.State == StockState.Posted)
            .OrderBy(x => x.Sequence).ToListAsync(ct));

    internal static ReceiptBalance[] Receipts(IEnumerable<StockOperation> operations, DateOnly at)
    {
        var rows = operations.ToArray();
        var claims = rows.Where(x => x.Kind is StockKind.MatchBill or StockKind.ReceiptCorrection ||
            (x.Kind == StockKind.SupplierReturn && x.TradeDocumentId == null)).SelectMany(x =>
            x.Kind == StockKind.MatchBill ? StockPlanning.Plan(x).Claims! :
                [new ReceiptClaim(x.RelatedOperationId!.Value, x.SourceQuantity, x.Accrual, 0)]).ToLookup(x => x.ReceiptId);
        return rows.Where(x => x.Kind == StockKind.Receipt).Select(x => new ReceiptBalance(x.Id, x.OrderLineId, x.Date,
            StockPlanning.Input(x).Quantity, StockPlanning.Input(x).Quantity - claims[x.Id].Sum(y => y.Quantity),
            x.Accrual - claims[x.Id].Sum(y => y.Accrual), at.DayNumber - x.Date.DayNumber)).ToArray();
    }

    private async Task<InventoryControlBalance[]> Reconcile(Guid company, DateOnly at, IEnumerable<StockMovement> movements,
        IEnumerable<ReceiptBalance> receipts, CancellationToken ct)
    {
        var map = await db.InventoryAccounts.AsNoTracking().Where(x => x.CompanyId == company && (int)x.Role <= 3).ToListAsync(ct);
        var ids = map.Select(x => x.AccountId).ToArray();
        var signed = await (from line in db.JournalLines join journal in db.Journals on line.JournalId equals journal.Id
            where line.CompanyId == company && journal.CompanyId == company && journal.State == JournalState.Posted &&
                journal.PostingDate <= at && ids.Contains(line.AccountId)
            group line by line.AccountId into lines
            select new { Id = lines.Key, Value = lines.Sum(x => x.Debit - x.Credit) }).ToDictionaryAsync(x => x.Id, x => x.Value, ct);
        return map.OrderBy(x => x.Role).Select(x => new InventoryControlBalance(x.Role, x.Role switch
        {
            InventoryRole.Inventory => movements.Sum(y => y.Gross),
            InventoryRole.Grni => -receipts.Sum(y => y.Grni),
            InventoryRole.Allowance => -movements.Sum(y => y.Allowance),
            _ => 0
        }, signed.GetValueOrDefault(x.AccountId))).ToArray();
    }

    private async Task<FulfillmentBalance[]> Fulfillment(Guid company, DateOnly at, IReadOnlyList<StockOperation> operations,
        IReadOnlyList<StockMovement> movements, CancellationToken ct)
    {
        var sales = await (from line in db.TradeLines.AsNoTracking()
            join document in db.TradeDocuments.AsNoTracking() on line.DocumentId equals document.Id
            where line.CompanyId == company && document.CompanyId == company &&
                document.Kind == TradeKind.Invoice && document.State == TradeState.Posted && document.PostingDate <= at &&
                line.StockSalesFactor != 0 && !db.AccountingAudit.Any(a =>
                    a.CompanyId == company && a.EntityId == line.Id && a.Action == LegacyNonStockAction)
            select new { line.Id, line.StockItemId, line.StockSalesFactor, line.Quantity,
                InvoiceId = document.Id, PostingDate = document.PostingDate!.Value }).ToListAsync(ct);
        var issues = operations.Where(x => x.Kind == StockKind.Issue).ToLookup(x => x.TradeLineId);
        var cancellations = await (from line in db.TradeLines.AsNoTracking()
            join credit in db.TradeDocuments.AsNoTracking() on line.DocumentId equals credit.Id
            where line.CompanyId == company && credit.CompanyId == company && line.CancelUnissuedStock &&
                credit.State == TradeState.Posted && credit.Kind == TradeKind.CustomerCredit && credit.PostingDate <= at
            group line by line.OriginalLineId!.Value into lines
            select new { Sale = lines.Key, Quantity = lines.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Sale, x => x.Quantity, ct);
        var relief = movements.ToLookup(x => x.OperationId);
        var cogs = await db.InventoryAccounts.AsNoTracking().Where(x => x.CompanyId == company && x.Role == InventoryRole.CostOfSales)
            .Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(ct);
        var journalCosts = await (from line in db.JournalLines join journal in db.Journals on line.JournalId equals journal.Id
            where line.CompanyId == company && journal.CompanyId == company && line.AccountId == cogs &&
                journal.State == JournalState.Posted && journal.SourceModule == "Inventory.Issue" && journal.PostingDate <= at
            group line by new { journal.Id, journal.SourceDocumentId, journal.PostingDate } into rows
            select new { rows.Key.Id, rows.Key.SourceDocumentId, rows.Key.PostingDate, Cost = rows.Sum(x => x.Debit - x.Credit) })
            .ToDictionaryAsync(x => x.Id, ct);
        var returns = operations.Where(x => x.Kind == StockKind.CustomerReturn).ToLookup(x => x.RelatedOperationId);
        var later = await db.StockOperations.AsNoTracking().Where(x => x.CompanyId == company &&
            x.Kind == StockKind.Issue && x.State == StockState.Posted && x.Date > at)
            .Select(x => x.TradeLineId).Distinct().ToListAsync(ct);
        var laterLines = later.ToHashSet();
        var legacyItemIds = sales.Where(x => x.StockSalesFactor == null)
            .SelectMany(x => issues[x.Id]).Select(x => x.ItemId).Distinct().ToArray();
        var legacyFactors = await db.InventoryItems.AsNoTracking().Where(x => x.CompanyId == company && legacyItemIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.SalesFactor, ct);
        return sales.Select(sale =>
        {
            var candidates = issues[sale.Id].Where(x => x.TradeDocumentId == sale.InvoiceId).ToArray();
            var legacyItems = candidates.Select(x => x.ItemId).Distinct().ToArray();
            Guid? item = sale.StockItemId ?? (legacyItems.Length == 1 ? legacyItems[0] : null);
            var factor = sale.StockSalesFactor ?? (item is { } id ? legacyFactors[id] : 0m);
            var posted = candidates.Where(x => x.ItemId == item && x.Date >= sale.PostingDate).ToArray();
            decimal Cost(StockOperation op) => relief[op.Id].Sum(m => m.Allowance - m.Gross);
            return new FulfillmentBalance(sale.InvoiceId, sale.Id, item, sale.PostingDate, sale.Quantity * factor,
                posted.Sum(x => StockPlanning.Input(x).Quantity),
                posted.SelectMany(x => returns[x.Id]).Sum(x => StockPlanning.Input(x).Quantity),
                posted.Select(x => x.Id).Order().ToArray(), laterLines.Contains(sale.Id))
            {
                Cancelled = cancellations.GetValueOrDefault(sale.Id) * factor,
                CostOfSales = posted.Sum(Cost),
                CostEvidenceAgrees = posted.All(op =>
                    relief[op.Id].Sum(m => -m.Quantity) == StockPlanning.Input(op).Quantity &&
                    (op.JournalId is { } journalId && journalCosts.TryGetValue(journalId, out var evidence)
                        ? evidence.SourceDocumentId == op.Id && evidence.PostingDate == op.Date && evidence.Cost == Cost(op)
                        : Cost(op) == 0))
            };
        }).OrderBy(x => x.InvoiceLineId).ToArray();
    }

    public async Task<object> Report(Guid actor, Guid org, Guid company, DateOnly at, DateOnly? from, CancellationToken ct)
    {
        if (at.Year < 1900 || (from.HasValue && (from.Value.Year < 1900 || from > at)))
            throw InventoryRegister.Bad("Provide an ordered inventory report date range from 1900.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var (movements, operations) = await Load(company, at, ct);
        var receipts = Receipts(operations, at);
        var fulfillment = await Fulfillment(company, at, operations, movements, ct);
        var controls = await Reconcile(company, at, movements, receipts, ct);
        var discrepancies = await OpenDiscrepancies(company, at, ct);
        var policy = await db.InventoryPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company, ct);
        var issueIds = operations.Where(x => x.Kind == StockKind.Issue).Select(x => x.Id).ToHashSet();
        var balances = movements.GroupBy(x => (x.ItemId, x.WarehouseId)).Select(rows =>
        {
            var balance = Balance(rows.Key.ItemId, rows.Key.WarehouseId, rows);
            return new { Balance = balance, FifoLayers = policy?.Method == ValuationMethod.Fifo ? Lots(rows) : [],
                AverageUnitCost = balance.OnHand == 0 ? 0 : Round(balance.Gross / balance.OnHand, 8),
                LastIssue = rows.Where(x => x.Quantity < 0 && issueIds.Contains(x.OperationId))
                    .Select(x => (DateOnly?)x.Date).Max() };
        }).ToArray();
        var orders = await db.ProcurementDocuments.AsNoTracking().Include(x => x.Lines).Where(x => x.CompanyId == company &&
            x.IsOrder && x.Date <= at && x.State == ProcurementState.Confirmed).OrderBy(x => x.Date).ThenBy(x => x.Id).ToListAsync(ct);
        var activity = operations.Where(x => !from.HasValue || x.Date >= from.Value).ToArray();
        var movementActivity = movements.Where(x => !from.HasValue || x.Date >= from.Value).ToArray();
        var mappings = await db.InventoryAccounts.AsNoTracking().Where(x => x.CompanyId == company)
            .ToDictionaryAsync(x => x.Role, x => x.AccountId, ct);
        decimal Effects(InventoryRole role) => !mappings.TryGetValue(role, out var id) ? 0 :
            activity.SelectMany(x => StockPlanning.Plan(x).Lines).Where(x => x.AccountId == id).Sum(x => x.Debit - x.Credit);
        var stockCogs = Effects(InventoryRole.CostOfSales);
        var fromDate = from ?? DateOnly.MinValue;
        var cogs = mappings.TryGetValue(InventoryRole.CostOfSales, out var cogsAccount) ?
            await (from line in db.JournalLines join journal in db.Journals on line.JournalId equals journal.Id
                where line.CompanyId == company && journal.CompanyId == company && line.AccountId == cogsAccount &&
                    journal.State == JournalState.Posted && journal.PostingDate <= at &&
                    journal.PostingDate >= fromDate &&
                    journal.SourceModule != FinancialMath.YearEndSource && journal.SourceModule != "OpeningBalance"
                select line.Debit - line.Credit).SumAsync(ct) : 0;
        var opening = from.HasValue ? movements.Where(x => x.Date < from.Value).Sum(x => x.Gross - x.Allowance) : 0;
        var closing = movements.Sum(x => x.Gross - x.Allowance);
        var average = (opening + closing) / 2;
        var operationsByOrderLine = operations.ToLookup(x => x.OrderLineId);
        var result = new
        {
            At = at, From = from, Balances = balances, Receipts = receipts, Fulfillment = fulfillment, Reconciliation = controls,
            OpenDiscrepancies = discrepancies.Select(x => new { x.Id, x.Kind, x.OrderLineId, x.ReceiptId, x.Date, x.Evidence }).ToArray(),
            Agrees = controls.All(x => x.Difference == 0), Movements = movementActivity, Operations = activity,
            CostOfSales = cogs, StockCostOfSales = stockCogs, CostOfSalesDifference = cogs - stockCogs,
            OpeningCarryingValue = opening, ClosingCarryingValue = closing,
            Turnover = average > 0 ? Round(cogs / average, 6) : (decimal?)null,
            PriceVariance = Effects(InventoryRole.PriceVariance), WriteDown = Effects(InventoryRole.WriteDown),
            Commitments = orders.Select(o => new { o.Id, o.SupplierId, o.Date, Lines = o.Lines.Select(l =>
            {
                var receiptsForLine = operationsByOrderLine[l.Id].Where(x => x.Kind == StockKind.Receipt).ToArray();
                var corrections = operationsByOrderLine[l.Id].Where(x => x.TradeDocumentId == null &&
                    x.Kind is StockKind.ReceiptCorrection or StockKind.SupplierReturn).ToArray();
                var received = receiptsForLine.Sum(x => StockPlanning.Input(x).Quantity) - corrections.Sum(x => x.SourceQuantity);
                var remaining = l.BaseQuantity - received;
                return new { l.Id, l.ItemId, Ordered = l.BaseQuantity, Received = received, Remaining = remaining,
                    RemainingNet = l.NetAmount - receiptsForLine.Sum(x => x.Accrual) + corrections.Sum(x => x.Accrual) };
            }).ToArray() }).ToArray()
        };
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<InventoryCloseEvidence> Check(Guid company, AccountingPeriod period, CancellationToken ct)
    {
        var policy = await db.InventoryPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company, ct);
        if (policy is null) return new([], "");
        var (movements, operations) = await Load(company, period.EndDate, ct);
        var receipts = Receipts(operations, period.EndDate);
        var controls = await Reconcile(company, period.EndDate, movements, receipts, ct);
        var fulfillment = await Fulfillment(company, period.EndDate, operations, movements, ct);
        var discrepancies = await OpenDiscrepancies(company, period.EndDate, ct);
        var findings = controls.Where(x => x.Difference != 0).Select(x =>
            new CloseFinding("inventory-gl", FindingSeverity.Blocking,
                FormattableString.Invariant($"{x.Role} differs from stock evidence by {x.Difference}."), policy.Id)).ToList();
        foreach (var receipt in receipts.Where(x => x.Grni != 0))
            findings.Add(new("inventory-grni", FindingSeverity.Review, "Unmatched goods receipt remains on the GRNI schedule.", receipt.ReceiptId));
        foreach (var discrepancy in discrepancies)
            findings.Add(new("inventory-discrepancy", FindingSeverity.Review, $"Open {discrepancy.Kind} discrepancy needs review.", discrepancy.Id));
        foreach (var line in fulfillment.Where(x => x.InvoicePostingDate >= period.StartDate || !x.ItemId.HasValue || x.Remaining != 0))
        {
            if (!line.ItemId.HasValue)
                findings.Add(new("inventory-unclassified-sale", FindingSeverity.Blocking,
                    "Historical sales line lacks a retained stock classification at the cutoff.", line.InvoiceLineId));
            else if (line.Remaining != 0)
                findings.Add(new("inventory-fulfillment-cutoff", FindingSeverity.Blocking,
                    FormattableString.Invariant($"Posted issues differ from required base quantity by {line.Remaining} at the cutoff."), line.InvoiceLineId));
            else if (!line.CostEvidenceAgrees)
                findings.Add(new("inventory-fulfillment-cost", FindingSeverity.Blocking,
                    "Issue quantities or carrying costs do not reconcile to posted COGS evidence.", line.InvoiceLineId));
        }
        var pending = await db.StockOperations.AsNoTracking().Where(x => x.CompanyId == company && x.Date <= period.EndDate &&
            (x.State == StockState.Draft || x.State == StockState.Approved)).OrderBy(x => x.Id).ToListAsync(ct);
        foreach (var row in pending)
        {
            var blocking = row.State == StockState.Approved &&
                row.Kind is StockKind.Receipt or StockKind.MatchBill or StockKind.Issue or StockKind.Count or StockKind.WriteDown;
            findings.Add(new("inventory-pending", blocking ? FindingSeverity.Blocking : FindingSeverity.Review,
                $"{row.Kind} remains unposted at the inventory cutoff.", row.Id));
        }
        var cogs = await db.InventoryAccounts.Where(x => x.CompanyId == company && x.Role == InventoryRole.CostOfSales)
            .Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(ct);
        var mapped = await db.ReportDefinitions.AsNoTracking().Where(x => x.CompanyId == company)
            .OrderByDescending(x => x.Revision).Select(x => x.Mappings.Where(m => m.AccountId == cogs).Select(m => (StatementGroup?)m.Group)
                .FirstOrDefault()).FirstOrDefaultAsync(ct);
        if (mapped.HasValue && mapped != StatementGroup.CostOfSales)
            findings.Add(new("inventory-cogs-mapping", FindingSeverity.Blocking, "Published COGS account classification must be Cost of Sales.", cogs));
        return new(findings, TradeRules.Hash(new { policy.Id, Controls = controls, Receipts = receipts, Fulfillment = fulfillment,
            Discrepancies = discrepancies.Select(x => x.Id),
            Posted = operations.Select(x => new { x.Id, x.Version, x.Sequence }),
            Pending = pending.Select(x => new { x.Id, x.Version, x.State }) }));
    }

    private Task<List<ProcurementDiscrepancy>> OpenDiscrepancies(Guid company, DateOnly at, CancellationToken ct) =>
        db.ProcurementDiscrepancies.AsNoTracking().Where(x => x.CompanyId == company && x.Date <= at &&
            (x.ResolutionDate == null || x.ResolutionDate > at)).OrderBy(x => x.Id).ToListAsync(ct);
}
