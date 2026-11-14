using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Inventory;

namespace RAQM.Api.Services.Inventory;

public sealed class InventoryPostingPolicy(RaqmDbContext db)
{
    public async Task Validate(Guid actor, Guid company, PostingRequest request, CancellationToken ct)
    {
        var staged = db.StockOperations.AsNoTracking().Where(x => x.CompanyId == company && x.State == StockState.Approved &&
            x.PostedBy == actor && x.Date == request.AccountingDate && x.PlanJson != null);
        if (request.SourceModule.StartsWith("Inventory.", StringComparison.Ordinal))
        {
            var source = await staged.SingleOrDefaultAsync(x => x.Id == request.SourceDocumentId, ct);
            if (source is null || request.SourceModule != StockWorkflow.Source(source.Kind) ||
                request.IdempotencyKey != StockWorkflow.PostingKey(source.Id) || !StockPlanning.Plan(source).Lines.SequenceEqual(request.Lines))
                throw Denied("Inventory journal requires the exact approved stock operation plan.");
            return;
        }
        if (request.ReversesJournalId is { } original &&
            await (from operation in db.StockOperations
                join line in db.JournalLines on operation.SourceJournalLineId equals (Guid?)line.Id into sources
                from line in sources.DefaultIfEmpty()
                where operation.CompanyId == company && operation.State == StockState.Posted &&
                    (operation.JournalId == original || (line.CompanyId == company && line.JournalId == original))
                select operation.Id).AnyAsync(ct))
            throw Denied("The journal is linked to posted stock evidence. Use a supported inventory correction.");
        if (request.SourceModule == "AR.Credit")
            await ValidateSalesCredit(company, request.SourceDocumentId, staged, ct);

        var ids = request.Lines.Select(x => x.AccountId).Distinct().ToArray();
        var controls = await db.InventoryAccounts.AsNoTracking().Where(x => x.CompanyId == company && ids.Contains(x.AccountId) &&
            (x.Role == InventoryRole.Inventory || x.Role == InventoryRole.Grni || x.Role == InventoryRole.InvoiceClearing ||
                x.Role == InventoryRole.Allowance)).Select(x => x.Role).ToListAsync(ct);
        if (controls.Count == 0) return;
        if (request.SourceModule == "OpeningBalance" && controls.All(x => x == InventoryRole.Inventory)) return;
        if (controls.All(x => x == InventoryRole.InvoiceClearing) &&
            (request.SourceModule is "AP.Bill" or "AP.Credit"))
        {
            var kind = request.SourceModule == "AP.Bill" ? StockKind.MatchBill : StockKind.SupplierReturn;
            if (await staged.AnyAsync(x => x.Kind == kind && x.TradeDocumentId == request.SourceDocumentId, ct)) return;
        }
        throw Denied("Inventory control accounts require a supported and approved inventory source.");
    }

    private async Task ValidateSalesCredit(Guid company, Guid document, IQueryable<StockOperation> staged, CancellationToken ct)
    {
        var credits = await (from credit in db.TradeLines
            join basis in db.TradeLines on credit.OriginalLineId equals (Guid?)basis.Id
            where credit.CompanyId == company && basis.CompanyId == company && credit.DocumentId == document &&
                (credit.CancelUnissuedStock || basis.StockItemId != null || db.StockOperations.Any(issue => issue.CompanyId == company &&
                    issue.TradeLineId == basis.Id && issue.Kind == StockKind.Issue && issue.State == StockState.Posted))
            select new { Credit = credit, Basis = basis }).AsNoTracking().ToListAsync(ct);
        foreach (var row in credits)
        {
            if (!row.Credit.CancelUnissuedStock)
            {
                if (!await staged.AnyAsync(x => x.Kind == StockKind.CustomerReturn && x.TradeDocumentId == document &&
                    x.TradeLineId == row.Credit.Id, ct))
                    throw Denied("A stocked invoice credit requires an approved return against a posted issue.");
                continue;
            }

            var credit = await db.TradeDocuments.AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == document, ct);
            if (row.Basis.StockItemId is null || credit.ApprovedBy is null || credit.ApprovedBy == credit.CreatedBy)
                throw Denied("An independently approved credit is required to cancel unissued stock.");
            var factor = row.Basis.StockSalesFactor!.Value;
            StockValuation.Quantity(row.Credit.Quantity * factor);
            var issued = (await db.StockOperations.AsNoTracking().Where(x => x.CompanyId == company &&
                x.TradeLineId == row.Basis.Id && x.Kind == StockKind.Issue && x.State == StockState.Posted)
                .ToListAsync(ct)).Sum(x => StockPlanning.Input(x).Quantity);
            var cancelled = await StockPlanning.CancelledSales(db, company, row.Basis.Id).SumAsync(x => x.Quantity, ct);
            if (issued + (cancelled + row.Credit.Quantity) * factor > row.Basis.Quantity * factor)
                throw new ApiException(409, "Cancellation exceeds the unissued sales quantity.", "stock_cancellation_quantity");
            if (await staged.AnyAsync(x => x.Kind == StockKind.CustomerReturn && x.TradeLineId == row.Credit.Id, ct))
                throw Denied("An unissued cancellation cannot be posted as a stock return.");
        }
    }

    private static ApiException Denied(string message) => new(403, message, "inventory_posting_source");
}
