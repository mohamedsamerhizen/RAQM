using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Inventory;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.BudgetControl;
using RAQM.Api.Services.FinancialDimensions;
using static RAQM.Api.Services.Inventory.InventoryRegister;

namespace RAQM.Api.Services.Inventory;

public sealed class ProcurementService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    InventoryRegister register, TimeProvider clock, BudgetControlService budget,
    DimensionPosting? dimensions = null, DimensionBudgetService? scopedBudgets = null, GovernanceGate? governance = null)
{
    public Task<ProcurementDocument> Prepare(Guid actor, Guid org, Guid company, ProcurementRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var key = AccountingRules.Text(input.Key, 64, "Procurement key");
            var evidence = AccountingRules.Text(input.Evidence, 1000, "Procurement evidence");
            if (input.Date.Year < 1900 || input.Date > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) ||
                (input.ExpectedDelivery.HasValue && input.ExpectedDelivery < input.Date) ||
                input.Lines is null || input.Lines.Count is < 1 or > 100 || input.Lines.Any(x => x is null) ||
                input.Lines.Select(x => x.ItemId).Distinct().Count() != input.Lines.Count ||
                input.IsOrder != input.SupplierId.HasValue || (!input.IsOrder && (input.RequisitionId.HasValue || input.ReplacesDocumentId.HasValue)))
                throw Bad("Provide valid procurement dates, party and distinct item lines.");
            AccountingRules.OptionalText(input.Department, 160);
            foreach (var line in input.Lines)
                TradeRules.Calculate(line.Quantity, line.UnitPrice, line.DiscountPercent, 0, false, scope.BaseCurrency);
            var hash = TradeRules.Hash(input with { Key = key, Evidence = evidence, Department = input.Department?.Trim() });
            var documents = db.ProcurementDocuments.Where(x => x.CompanyId == company).Include(x => x.Lines);
            var prior = await documents.SingleOrDefaultAsync(x => x.RequestKey == key, token);
            if (prior is not null)
                return prior.RequestHash == hash ? prior : throw Conflict("The procurement key belongs to another request.");
            if (input.SupplierId is { } supplier && !await db.TradingParties.AnyAsync(x => x.CompanyId == company &&
                x.Id == supplier && x.IsActive && x.Kind == PartyKind.Supplier, token))
                throw Bad("Supplier must be active in this company.");
            ProcurementDocument? requisition = null;
            if (input.RequisitionId is { } requisitionId)
            {
                requisition = await documents.SingleOrDefaultAsync(x => x.Id == requisitionId && !x.IsOrder &&
                    x.State == ProcurementState.Approved && x.Date <= input.Date, token)
                    ?? throw Bad("Select an approved company requisition dated on or before the order.");
                if (await documents.AnyAsync(x => x.RequisitionId == requisitionId &&
                    x.State != ProcurementState.Cancelled && x.State != ProcurementState.Rejected, token))
                    throw Conflict("This requisition already has an active order.");
            }
            if (input.ReplacesDocumentId is { } replaced)
            {
                if (!await documents.AnyAsync(x => x.Id == replaced && x.IsOrder && x.State == ProcurementState.Cancelled &&
                    x.RequisitionId == input.RequisitionId && x.Date <= input.Date, token))
                    throw Bad("An amendment must replace a cancelled pre-receipt order with the same requisition.");
                if (await documents.AnyAsync(x => x.ReplacesDocumentId == replaced && x.State != ProcurementState.Cancelled &&
                    x.State != ProcurementState.Rejected, token))
                    throw Conflict("This order already has an active replacement.");
            }
            var sequence = await documents.LongCountAsync(x => x.IsOrder == input.IsOrder, token) + 1;
            var document = Scoped(scope, new ProcurementDocument { IsOrder = input.IsOrder, SupplierId = input.SupplierId,
                RequisitionId = input.RequisitionId, ReplacesDocumentId = input.ReplacesDocumentId, Date = input.Date,
                ExpectedDelivery = input.ExpectedDelivery, Department = input.Department?.Trim(), Number = $"{(input.IsOrder ? "PO" : "PR")}-{sequence:D8}",
                Evidence = evidence, CreatedBy = actor, RequestKey = key, RequestHash = hash });
            var units = CurrencyPolicy.Units(scope.BaseCurrency);
            foreach (var line in input.Lines)
            {
                var item = await db.InventoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == line.ItemId && x.IsActive, token)
                    ?? throw Bad("Item must be active in this company.");
                var quantity = StockValuation.Quantity(line.Quantity);
                var baseQuantity = StockValuation.Quantity(quantity * item.PurchaseFactor);
                if (requisition is not null && !requisition.Lines.Any(x => x.ItemId == item.Id && x.BaseQuantity >= baseQuantity))
                    throw Bad("Order quantities must fall within the approved requisition.");
                decimal percent = 0;
                if (line.TaxRateId is { } taxRate)
                {
                    var rate = await db.TradingTaxRates.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == taxRate &&
                        x.EffectiveFrom <= input.Date && x.EffectiveTo >= input.Date && (item.TaxCodeId == null || x.TaxCodeId == item.TaxCodeId), token)
                        ?? throw Bad("Tax rate must match the item and order date.");
                    percent = rate.RatePercent;
                }
                var calculated = TradeRules.Calculate(quantity, line.UnitPrice, line.DiscountPercent, percent, false, scope.BaseCurrency);
                document.Lines.Add(Scoped(scope, new ProcurementLine { DocumentId = document.Id, ItemId = item.Id,
                    Quantity = quantity, BaseQuantity = baseQuantity, UnitPrice = line.UnitPrice, DiscountPercent = line.DiscountPercent,
                    NetAmount = calculated.Net, TaxAmount = calculated.Tax,
                    TaxRateId = line.TaxRateId }));
            }
            CurrencyPolicy.Validate(document.Lines.Sum(x => x.NetAmount + x.TaxAmount), units);
            db.ProcurementDocuments.Add(document);
            access.Audit(scope, actor, document.Id, document.ReplacesDocumentId.HasValue ? "ProcurementAmendmentPrepared" : "ProcurementPrepared", evidence);
            return document;
        }, ct);

    public Task<ProcurementDocument> Change(Guid actor, Guid org, Guid company, Guid id, string action, InventoryAction input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var document = await db.ProcurementDocuments.Include(x => x.Lines)
                .SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing("Procurement document was not found.");
            if (document.Version != input.Version) throw Conflict("The procurement document changed.");
            var reason = AccountingRules.Text(input.Reason, 500, "Action reason");
            switch (action)
            {
                case "approve" when document.State == ProcurementState.Draft:
                    if (document.CreatedBy == actor) throw Conflict("A different actor must approve procurement.");
                    var policy = await register.Policy(company, token) ?? throw Conflict("Configure inventory policy first.");
                    if (document.Lines.Sum(x => x.NetAmount + x.TaxAmount) > policy.ApprovalLimit)
                        throw Conflict("The document exceeds the configured approval limit.");
                    if (governance is not null) await governance.Enforce(scope, actor, FinancialDuty.ProcurementApproval, id, token);
                    if (dimensions is not null) await dimensions.CheckPurchase(company, document, Today(), token);
                    await budget.Procurement(scope, actor, document, action, token);
                    if (scopedBudgets is not null) await scopedBudgets.CheckProcurement(company, document, token);
                    document.ApprovedBy = actor;
                    document.State = ProcurementState.Approved;
                    break;
                case "confirm" when document.IsOrder && document.State == ProcurementState.Approved:
                    document.State = ProcurementState.Confirmed;
                    break;
                case "reject" when document.State == ProcurementState.Draft:
                    document.State = ProcurementState.Rejected;
                    break;
                case "cancel" when document.State is ProcurementState.Draft or ProcurementState.Approved or ProcurementState.Confirmed:
                    var lineIds = document.Lines.Select(x => x.Id).ToArray();
                    if (await db.StockOperations.AnyAsync(x => x.CompanyId == company && x.OrderLineId.HasValue &&
                        lineIds.Contains(x.OrderLineId.Value) && x.State == StockState.Posted, token) ||
                        await db.ProcurementDocuments.AnyAsync(x => x.CompanyId == company && x.RequisitionId == id &&
                            x.State != ProcurementState.Cancelled && x.State != ProcurementState.Rejected, token))
                        throw Conflict("This document has posted receipts or an active linked order.");
                    await budget.Procurement(scope, actor, document, action, token);
                    document.State = ProcurementState.Cancelled;
                    break;
                default:
                    throw Conflict("Invalid procurement transition.");
            }
            document.Version = Guid.NewGuid();
            access.Audit(scope, actor, document.Id, $"Procurement:{action}", reason);
            return document;

            DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        }, ct);

    public async Task<PageResult<ProcurementDocument>> List(Guid actor, Guid org, Guid company, int page, int size,
        string? search, ProcurementState? state, bool? order, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        AccountingRules.OptionalText(search, 100);
        if (state.HasValue && !Enum.IsDefined(state.Value)) throw Bad("Unknown procurement state.");
        await access.Resolve(actor, org, company, false, false, ct);
        var query = db.ProcurementDocuments.AsNoTracking().Where(x => x.CompanyId == company);
        if (state.HasValue) query = query.Where(x => x.State == state);
        if (order.HasValue) query = query.Where(x => x.IsOrder == order);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => EF.Functions.ILike(x.Number, "%" + search + "%") ||
            EF.Functions.ILike(x.Evidence, "%" + search + "%"));
        return new(await query.Include(x => x.Lines).OrderByDescending(x => x.Date).ThenBy(x => x.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct), await query.CountAsync(ct), page, size);
    }

    public Task<ProcurementDiscrepancy> RecordDiscrepancy(Guid actor, Guid org, Guid company, DiscrepancyRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            if (!Enum.IsDefined(input.Kind) || input.Date.Year < 1900 || input.Date > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
                throw Bad("Use a valid discrepancy type and date.");
            if (!await (from line in db.ProcurementLines join order in db.ProcurementDocuments on line.DocumentId equals order.Id
                where line.CompanyId == company && order.CompanyId == company && line.Id == input.OrderLineId && order.IsOrder &&
                    order.State == ProcurementState.Confirmed && order.Date <= input.Date select line.Id).AnyAsync(token))
                throw Bad("Discrepancy requires a confirmed company PO line.");
            if (input.ReceiptId.HasValue && !await db.StockOperations.AnyAsync(x => x.CompanyId == company &&
                x.Id == input.ReceiptId && x.Kind == StockKind.Receipt && x.State == StockState.Posted && x.OrderLineId == input.OrderLineId &&
                x.Date <= input.Date, token))
                throw Bad("Discrepancy receipt must belong to the same PO line.");
            var row = Scoped(scope, new ProcurementDiscrepancy { Kind = input.Kind, OrderLineId = input.OrderLineId,
                ReceiptId = input.ReceiptId, Date = input.Date, Evidence = AccountingRules.Text(input.Evidence, 1000, "Discrepancy evidence"),
                CreatedBy = actor });
            db.ProcurementDiscrepancies.Add(row);
            access.Audit(scope, actor, row.Id, "ProcurementDiscrepancyRecorded", input.Kind.ToString());
            return row;
        }, ct);

    public Task<ProcurementDiscrepancy> ResolveDiscrepancy(Guid actor, Guid org, Guid company, Guid id, DiscrepancyResolution input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var row = await db.ProcurementDiscrepancies.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token)
                ?? throw Missing("Procurement discrepancy was not found.");
            var reason = AccountingRules.Text(input.Reason, 500, "Resolution reason");
            if (row.ResolvedBy.HasValue)
                return row.Resolution == reason ? row : throw Conflict("This discrepancy has retained resolution evidence.");
            row.Resolution = reason;
            row.ResolvedBy = actor;
            row.ResolutionDate = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            access.Audit(scope, actor, row.Id, "ProcurementDiscrepancyResolved", reason);
            return row;
        }, ct);

    public async Task<PageResult<ProcurementDiscrepancy>> Discrepancies(Guid actor, Guid org, Guid company, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        await access.Resolve(actor, org, company, false, false, ct);
        var query = db.ProcurementDiscrepancies.AsNoTracking().Where(x => x.CompanyId == company);
        return new(await query.OrderByDescending(x => x.Date).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct),
            await query.CountAsync(ct), page, size);
    }
}
