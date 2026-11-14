using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Inventory;
using static RAQM.Api.Services.Inventory.InventoryRegister;
using static RAQM.Api.Helpers.Inventory.StockValuation;

namespace RAQM.Api.Services.Inventory;

public sealed partial class StockPlanning(RaqmDbContext db, InventoryRegister register, TimeProvider clock)
{
    public async Task<StockPlan> Build(Company scope, StockOperation operation, StockRequest input, CancellationToken ct)
    {
        var company = scope.Id;
        var units = CurrencyPolicy.Units(scope.BaseCurrency);
        var policy = await register.Policy(company, ct) ?? throw Conflict("Configure inventory policy first.");
        if (input.Date.Year < 1900 || input.Date > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) ||
            !await db.Periods.AnyAsync(x => x.CompanyId == company && x.State == PeriodState.Open &&
                x.StartDate <= input.Date && x.EndDate >= input.Date, ct))
            throw Conflict("Stock operations require a valid date in an open accounting period.");
        if (await (from close in db.YearEndRecords join year in db.FiscalYears on close.FiscalYearId equals year.Id
            where close.CompanyId == company && close.Finalized && year.EndDate >= input.Date select close.Id).AnyAsync(ct))
            throw Conflict("The stock date is covered by a finalized fiscal year.");
        Quantity(input.Quantity, input.Kind is StockKind.Count or StockKind.WriteDown or StockKind.LandedCost);
        CurrencyPolicy.Validate(input.Amount, units);
        var item = await db.InventoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
            x.Id == input.ItemId && x.IsActive && x.Kind == ItemKind.Stocked, ct)
            ?? throw Bad("A stocked, active item in this company is required.");
        await Warehouse(company, input.WarehouseId, ct);
        var posted = db.StockOperations.AsNoTracking().Where(x => x.CompanyId == company && x.State == StockState.Posted);
        if (await posted.AnyAsync(x => x.Date > input.Date, ct))
            throw Conflict("Stock dates cannot precede the latest posted company inventory operation.");
        var accounts = await db.InventoryAccounts.AsNoTracking().Where(x => x.CompanyId == company).ToDictionaryAsync(x => x.Role, x => x.AccountId, ct);
        var rows = await Rows(company, item.Id, input.WarehouseId, ct);
        var balance = Balance(item.Id, input.WarehouseId, rows);
        var lots = Lots(rows);
        var movements = new List<MovementPlan>();
        var lines = new List<JournalDraftLine>();
        decimal accrual = 0, sourceQuantity = 0;
        IReadOnlyList<ReceiptClaim>? claims = null;
        IReadOnlyList<LandedAllocation>? landedAllocations = null;

        void Line(InventoryRole role, decimal debit) => ExternalLine(accounts[role], debit);
        void ExternalLine(Guid account, decimal debit)
        {
            if (debit != 0) lines.Add(new(account, Math.Max(debit, 0), Math.Max(-debit, 0), $"Inventory {input.Kind}"));
        }
        void Incoming(decimal quantity, decimal gross, decimal allowance = 0, Guid? origin = null) =>
            movements.Add(new(input.WarehouseId, quantity, gross, allowance, 0,
                [new(LotId(operation.Id, 0), origin ?? operation.Id, quantity, gross)]));
        MovementPlan TakeStock(decimal quantity, Guid? origin = null) => Take(balance, lots, quantity, policy.Method, units, origin);
        void Consume(MovementPlan movement, InventoryRole expense)
        {
            movements.Add(movement);
            Line(InventoryRole.Inventory, movement.Gross);
            Line(expense, -movement.Gross);
            Line(InventoryRole.Allowance, -movement.Allowance);
            Line(expense, movement.Allowance);
        }
        switch (input.Kind)
        {
            case StockKind.Receipt:
                accrual = await Receipt(input, posted, company, units, ct);
                Incoming(input.Quantity, accrual);
                Line(InventoryRole.Inventory, accrual);
                Line(InventoryRole.Grni, -accrual);
                break;
            case StockKind.MatchBill:
                claims = await Match(input, item, policy, posted, accounts[InventoryRole.InvoiceClearing], units, ct);
                accrual = claims.Sum(x => x.Accrual);
                sourceQuantity = input.Quantity;
                Line(InventoryRole.Grni, accrual);
                Line(InventoryRole.InvoiceClearing, -input.Amount);
                Line(InventoryRole.PriceVariance, input.Amount - accrual);
                break;
            case StockKind.Issue:
                await CheckIssue(input, item, posted, ct);
                if (input.RelatedOperationId.HasValue)
                {
                    await CheckReservation(input, posted, ct);
                    balance = balance with { Reserved = balance.Reserved - input.Quantity };
                }
                var issue = TakeStock(input.Quantity);
                Consume(issue with { Reserved = input.RelatedOperationId.HasValue ? -input.Quantity : 0 }, InventoryRole.CostOfSales);
                break;
            case StockKind.Transfer:
                if (input.DestinationId is not { } destination || destination == input.WarehouseId)
                    throw Bad("A different company destination warehouse is required.");
                await Warehouse(company, destination, ct);
                var transfer = TakeStock(input.Quantity);
                movements.Add(transfer);
                var transferShares = policy.Method == ValuationMethod.Fifo
                    ? transfer.Lots.Select(x => -x.Cost).ToArray()
                    : Allocate(-transfer.Gross, transfer.Lots.Select(x => -x.Quantity).ToArray(), units);
                movements.Add(new(destination, input.Quantity, -transfer.Gross, -transfer.Allowance, 0, transfer.Lots
                    .Select((x, i) => new LotChange(LotId(operation.Id, i), x.Origin, -x.Quantity, transferShares[i])).ToArray()));
                break;
            case StockKind.Reserve:
                if (input.Quantity > balance.Available) throw Conflict("Reservation exceeds available stock.");
                movements.Add(new(input.WarehouseId, 0, 0, 0, input.Quantity, []));
                break;
            case StockKind.Release:
                await CheckReservation(input, posted, ct);
                movements.Add(new(input.WarehouseId, 0, 0, 0, -input.Quantity, []));
                break;
            case StockKind.Count:
                if (operation.BookQuantity != balance.OnHand) throw Conflict("Book quantity changed. Capture a new physical count.");
                var difference = input.Quantity - balance.OnHand;
                if (difference < 0) Consume(TakeStock(-difference), InventoryRole.Loss);
                else if (difference > 0)
                {
                    if (input.Amount <= 0) throw Bad("Provide supported cost for the count surplus.");
                    Incoming(difference, input.Amount);
                    Line(InventoryRole.Inventory, input.Amount);
                    Line(InventoryRole.Gain, -input.Amount);
                }
                break;
            case StockKind.Opening:
                if (rows.Count != 0 || input.Amount <= 0) throw Conflict("Opening stock requires a new warehouse/item balance and positive cost.");
                var firstFiscalDate = await db.FiscalYears.Where(x => x.CompanyId == company).MinAsync(x => (DateOnly?)x.StartDate, ct);
                if (input.Date != firstFiscalDate) throw Bad("Opening stock must use the first fiscal year's start date.");
                Incoming(input.Quantity, input.Amount);
                if (input.SourceJournalLineId is { } opening)
                    await CheckOpening(company, opening, accounts[InventoryRole.Inventory], input, posted, ct);
                else
                {
                    await CheckNewOpening(company, accounts[InventoryRole.Inventory], input, posted, ct);
                    Line(InventoryRole.Inventory, input.Amount);
                    Line(InventoryRole.OpeningEquity, -input.Amount);
                }
                break;
            case StockKind.WriteDown:
                if (balance.OnHand == 0 || input.Quantity != 0) throw Bad("Supply total warehouse/item NRV as the amount, with quantity zero.");
                var allowance = Math.Max(0, balance.Gross - input.Amount) - balance.Allowance;
                if (allowance == 0) throw Conflict("NRV does not change the allowance.");
                movements.Add(new(input.WarehouseId, 0, 0, allowance, 0, []));
                Line(InventoryRole.Allowance, -allowance);
                Line(InventoryRole.WriteDown, allowance);
                break;
            case StockKind.ReceiptCorrection:
            case StockKind.SupplierReturn:
                var receipt = await Related(input, posted, StockKind.Receipt, ct);
                var available = await ReceiptUnmatched(posted, receipt, units, ct);
                var supplierReturn = TakeStock(input.Quantity, receipt.Id);
                if (input.Kind == StockKind.SupplierReturn && input.TradeDocumentId.HasValue)
                {
                    var credit = await SupplierCredit(input, item, posted, receipt, accounts[InventoryRole.InvoiceClearing], units, ct);
                    Line(InventoryRole.InvoiceClearing, credit);
                    Line(InventoryRole.PriceVariance, -supplierReturn.Gross - credit);
                }
                else
                {
                    if (input.Quantity > available.Quantity) throw Conflict("Only unbilled receipt quantity can reverse GRNI.");
                    accrual = Share(available.Accrual, input.Quantity, available.Quantity, units);
                    sourceQuantity = input.Quantity;
                    Line(InventoryRole.Grni, accrual);
                    Line(InventoryRole.PriceVariance, -supplierReturn.Gross - accrual);
                }
                movements.Add(supplierReturn);
                Line(InventoryRole.Inventory, supplierReturn.Gross);
                Line(InventoryRole.Allowance, -supplierReturn.Allowance);
                Line(InventoryRole.WriteDown, supplierReturn.Allowance);
                break;
            case StockKind.CustomerReturn:
                var original = await Related(input, posted, StockKind.Issue, ct);
                var (gross, release) = await CustomerReturn(input, item, posted, original, units, ct);
                if (input.Damaged)
                {
                    Line(InventoryRole.Loss, gross - release);
                    Line(InventoryRole.CostOfSales, release - gross);
                }
                else
                {
                    var source = await db.StockMovements.AsNoTracking().SingleAsync(x => x.CompanyId == company && x.OperationId == original.Id, ct);
                    var used = System.Text.Json.JsonSerializer.Deserialize<LotChange[]>(source.LotsJson)!;
                    var originShares = used.GroupBy(x => x.Origin).ToArray();
                    var quantities = Allocate(input.Quantity, originShares.Select(x => -x.Sum(y => y.Quantity)).ToArray(), 6);
                    var costs = Allocate(gross, originShares.Select(x => -x.Sum(y => y.Quantity)).ToArray(), units);
                    // A return retains provenance when the issue drew from a single receipt.
                    if (quantities.Any(x => x == 0) || originShares.Length != 1)
                        Incoming(input.Quantity, gross, release);
                    else
                        movements.Add(new(input.WarehouseId, input.Quantity, gross, release, 0, originShares.Select((x, i) =>
                            new LotChange(LotId(operation.Id, i), x.Key, quantities[i], costs[i])).ToArray()));
                    Line(InventoryRole.Inventory, gross);
                    Line(InventoryRole.CostOfSales, -gross + release);
                    Line(InventoryRole.Allowance, -release);
                }
                accrual = gross;
                sourceQuantity = release;
                break;
            case StockKind.LandedCost:
                var landed = await Landed(input, posted, company, units, ct);
                movements.AddRange(landed.Movements);
                landedAllocations = landed.Allocations;
                var capitalized = landed.Movements.Sum(x => x.Gross);
                Line(InventoryRole.Inventory, capitalized);
                Line(InventoryRole.CostOfSales, input.Amount - capitalized);
                ExternalLine(landed.Account, -input.Amount);
                break;
            default:
                throw Bad("Unsupported inventory operation.");
        }
        if (lines.Count > 0) AccountingRules.Lines(lines, scope.BaseCurrency, true);
        return new(movements, lines, accrual, sourceQuantity, claims, landedAllocations);
    }

    internal Task<List<StockMovement>> Rows(Guid company, Guid item, Guid warehouse, CancellationToken ct) =>
        db.StockMovements.AsNoTracking().Where(x => x.CompanyId == company && x.ItemId == item && x.WarehouseId == warehouse)
            .OrderBy(x => x.Sequence).ThenBy(x => x.Number).ToListAsync(ct);

    internal async Task<Warehouse> Warehouse(Guid company, Guid id, CancellationToken ct) =>
        await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id && x.IsActive, ct)
        ?? throw Bad("Select an active warehouse in this company.");
}
