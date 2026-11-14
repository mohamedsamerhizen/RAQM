using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Forecasting;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Budgeting;
using RAQM.Api.Helpers.Forecasting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.FinancialReporting;
using RAQM.Api.Services.Inventory;
using RAQM.Api.Services.Trading;

namespace RAQM.Api.Services.Forecasting;

public sealed class CashForecastSource(RaqmDbContext db, FinancialLedger ledger, TimeProvider clock)
{
    private async Task<Guid[]> CashAccounts(Guid company, CancellationToken ct)
    {
        var treasury = await db.TreasuryAccounts.AsNoTracking().Where(x => x.CompanyId == company).Select(x => x.LedgerAccountId).ToListAsync(ct);
        var trading = await db.TradingAccounts.AsNoTracking().Where(x => x.CompanyId == company && x.Role == AccountRole.Cash)
            .Select(x => x.AccountId).ToListAsync(ct);
        return treasury.Concat(trading).Distinct().ToArray();
    }

    public async Task<decimal> OpeningCash(Guid company, DateOnly at, CancellationToken ct)
    {
        var accounts = await CashAccounts(company, ct);
        return accounts.Length == 0 ? 0 : await ledger.Lines(company, at, accounts: accounts).SumAsync(x => x.Debit - x.Credit, ct);
    }

    public async Task<(decimal Opening, CashEvent[] Events, string[] Warnings)> Build(Guid company, ForecastRequest input,
        DateOnly end, string currency, CancellationToken ct)
    {
        var cashAccounts = await CashAccounts(company, ct);
        var opening = cashAccounts.Length == 0 ? 0 : await ledger.Lines(company, input.AsOf, accounts: cashAccounts).SumAsync(x => x.Debit - x.Credit, ct);
        var snapshot = await TradingSnapshot.Load(db, company, ct);
        var events = new List<CashEvent>();
        var warnings = new List<string>();
        if (await db.FxEffects.AnyAsync(x => x.CompanyId == company && x.Date <= input.AsOf, ct))
            warnings.Add("Foreign currency positions and foreign bank balances are reported separately in FX exposure. Forecast events cover functional cash and legacy obligations.");
        if (cashAccounts.Length == 0) warnings.Add("No mapped cash accounts. The opening cash projection is zero.");
        foreach (var party in snapshot.Documents.Where(x => x.PostingDate <= input.AsOf && !TradeRules.IsCredit(x.Kind)).GroupBy(x => x.PartyId))
        {
            var funding = Math.Max(0, snapshot.UnallocatedControl(party.Key, input.AsOf) + snapshot.UnappliedCredits(party.Key, input.AsOf));
            if (funding > 0) warnings.Add($"Unapplied control funding for {party.Key} is assigned to the earliest obligations for forecasting.");
            foreach (var document in party.OrderBy(x => x.ScheduledPaymentDate ?? x.DueDate).ThenBy(x => x.Id))
            {
                var outstanding = Math.Max(0, snapshot.Outstanding(document, input.AsOf));
                var offset = Math.Min(funding, outstanding);
                funding -= offset;
                outstanding -= offset;
                if (outstanding == 0) continue;
                var isReceivable = TradeRules.Side(document.Kind) == PartyKind.Customer;
                var due = document.ScheduledPaymentDate ?? document.DueDate;
                var date = ForecastMath.ExpectedDate(due, input.AsOf, isReceivable ? input.CustomerDelayDays : input.SupplierDelayDays);
                events.Add(new(isReceivable ? "AR" : "AP", document.Id, document.Number ?? document.Description,
                    date, isReceivable ? outstanding : -outstanding, document.ScheduledPaymentDate is null || due <= input.AsOf));
            }
        }
        if (input.AsOf == DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            await AddPurchasing(company, input, CurrencyPolicy.Units(currency), events, ct);
        else warnings.Add("Historical cash snapshots exclude unposted procurement because Phase 06 PO status has no historical effective timestamp.");
        if (input.Cash is { } assumptions)
        {
            if (assumptions.Count > 100 || assumptions.Any(x => x is null)) throw BudgetMath.Bad("At most 100 cash assumptions are supported.");
            foreach (var row in assumptions)
            {
                CurrencyPolicy.Validate(Math.Abs(row.Amount), CurrencyPolicy.Units(currency));
                if (row.Amount == 0 || row.Occurrences is < 1 or > 12 || row.FirstDate <= input.AsOf || row.FirstDate > end)
                    throw BudgetMath.Bad("Cash schedules require a nonzero amount, a date inside the horizon and up to 12 occurrences.");
                var evidence = AccountingRules.Text(row.Evidence, 250, "Cash assumption evidence");
                for (var i = 0; i < row.Occurrences; i++)
                {
                    var date = row.FirstDate.AddMonths(i);
                    if (date <= end) events.Add(new("Assumption", null, evidence, date, row.Amount, true));
                }
            }
        }
        return (opening, events.OrderBy(x => x.Date).ThenBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.SourceId).ToArray(), warnings.ToArray());
    }

    private async Task AddPurchasing(Guid company, ForecastRequest input, int units, List<CashEvent> events, CancellationToken ct)
    {
        var orders = await db.ProcurementDocuments.AsNoTracking().Include(x => x.Lines).Where(x => x.CompanyId == company && x.IsOrder &&
            x.State == ProcurementState.Confirmed && x.Date <= input.AsOf).ToListAsync(ct);
        if (orders.Count == 0) return;
        var operations = await db.StockOperations.AsNoTracking().Where(x => x.CompanyId == company && x.State == StockState.Posted &&
            x.Date <= input.AsOf).ToListAsync(ct);
        var receiptBalances = InventoryReporting.Receipts(operations, input.AsOf).ToLookup(x => x.OrderLineId);
        var activity = operations.ToLookup(x => x.OrderLineId);
        var suppliers = await db.TradingParties.AsNoTracking().Where(x => x.CompanyId == company && x.Kind == PartyKind.Supplier)
            .ToDictionaryAsync(x => x.Id, x => x.PaymentTermsDays, ct);
        var stocked = (await db.InventoryItems.AsNoTracking().Where(x => x.CompanyId == company && x.Kind == ItemKind.Stocked)
            .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        foreach (var order in orders.OrderBy(x => x.Id))
        {
            var terms = order.SupplierId is { } id ? suppliers.GetValueOrDefault(id) : 0;
            var delivery = order.ExpectedDelivery ?? input.AsOf.AddDays(1);
            var delay = Math.Min(365, terms + input.SupplierDelayDays);
            foreach (var line in order.Lines.Where(x => stocked.Contains(x.ItemId)).OrderBy(x => x.Id))
            {
                var corrections = activity[line.Id].Where(x => x.TradeDocumentId == null && x.Kind is StockKind.ReceiptCorrection or StockKind.SupplierReturn)
                    .Sum(x => x.Accrual);
                var remaining = Math.Max(0, line.NetAmount - activity[line.Id].Where(x => x.Kind == StockKind.Receipt).Sum(x => x.Accrual) + corrections);
                decimal WithTax(decimal net) => line.NetAmount <= 0 ? net : net + BudgetMath.Round(line.TaxAmount * (net / line.NetAmount), units);
                if (remaining > 0) events.Add(new("PO", line.Id, order.Number, ForecastMath.ExpectedDate(delivery, input.AsOf, delay), -WithTax(remaining), true));
                foreach (var receipt in receiptBalances[line.Id].Where(x => x.Grni > 0))
                {
                    var invoiceDue = receipt.Date.AddDays(terms);
                    events.Add(new("GRNI", receipt.ReceiptId, order.Number, ForecastMath.ExpectedDate(invoiceDue, input.AsOf, input.SupplierDelayDays),
                        -WithTax(receipt.Grni), true));
                }
            }
        }
    }
}
