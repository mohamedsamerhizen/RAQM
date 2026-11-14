using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Inventory;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.BudgetControl;
using static RAQM.Api.Services.Inventory.InventoryRegister;

namespace RAQM.Api.Services.Inventory;

public sealed class StockWorkflow(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions, StockPlanning planning,
    IPostingEngine posting, ITradeDocumentService trading, TimeProvider clock, BudgetControlService budget, GovernanceGate? governance = null)
{
    public Task<StockOperation> Prepare(Guid actor, Guid org, Guid company, StockRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            input = input with { Key = AccountingRules.Text(input.Key, 64, "Operation key"),
                Evidence = AccountingRules.Text(input.Evidence, 1000, "Operation evidence") };
            ValidateShape(input);
            StockValuation.Quantity(input.Quantity, input.Kind is StockKind.Count or StockKind.WriteDown or StockKind.LandedCost);
            CurrencyPolicy.Validate(input.Amount, CurrencyPolicy.Units(scope.BaseCurrency));
            var hash = TradeRules.Hash(input);
            var prior = await db.StockOperations.SingleOrDefaultAsync(x => x.CompanyId == company && x.RequestKey == input.Key, token);
            if (prior is not null)
                return prior.RequestHash == hash ? prior : throw Conflict("Operation key was used for a different request.");
            await CheckWarehouseAccess(actor, scope, input.WarehouseId, token);
            if (input.DestinationId is { } destination) await CheckWarehouseAccess(actor, scope, destination, token);
            if (input.Kind == StockKind.MatchBill && input.ReceiptQuantities is { } parts)
            {
                var receipts = parts.Select(x => x.ReceiptId).ToArray();
                var warehouses = await db.StockOperations.Where(x => x.CompanyId == company && receipts.Contains(x.Id))
                    .Select(x => x.WarehouseId).Distinct().ToListAsync(token);
                foreach (var warehouse in warehouses.Where(x => x != input.WarehouseId))
                    await CheckWarehouseAccess(actor, scope, warehouse, token);
            }
            var operation = Scoped(scope, new StockOperation { Kind = input.Kind, Date = input.Date, ItemId = input.ItemId,
                WarehouseId = input.WarehouseId, RelatedOperationId = input.RelatedOperationId ?? input.ReceiptQuantities?.First().ReceiptId ??
                    input.LandedReceipts?.First(), OrderLineId = input.OrderLineId,
                TradeDocumentId = input.TradeDocumentId, TradeLineId = input.TradeLineId, SourceJournalLineId = input.SourceJournalLineId,
                RequestKey = input.Key, RequestHash = hash, PayloadJson = JsonSerializer.Serialize(input), CreatedBy = actor });
            if (input.Kind is StockKind.ReceiptCorrection or StockKind.SupplierReturn)
                operation.OrderLineId = await db.StockOperations.Where(x => x.CompanyId == company && x.Id == input.RelatedOperationId)
                    .Select(x => x.OrderLineId).SingleOrDefaultAsync(token);
            if (input.Kind == StockKind.Count)
                operation.BookQuantity = await db.StockMovements.Where(x => x.CompanyId == company && x.ItemId == input.ItemId &&
                    x.WarehouseId == input.WarehouseId).SumAsync(x => x.Quantity, token);
            await planning.Build(scope, operation, input, token);
            db.StockOperations.Add(operation);
            access.Audit(scope, actor, operation.Id, "StockOperationPrepared", input.Evidence);
            return operation;
        }, ct);

    public Task<StockOperation> Change(Guid actor, Guid org, Guid company, Guid id, string action, InventoryAction input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var operation = await Find(company, id, token);
            if (action == "post" && operation.State == StockState.Posted) return operation;
            if (operation.Version != input.Version) throw Conflict("The stock operation changed.");
            var reason = AccountingRules.Text(input.Reason, 500, "Action reason");
            var request = StockPlanning.Input(operation);
            if (action == "approve" && operation.State == StockState.Draft)
            {
                if (operation.CreatedBy == actor) throw Conflict("A different actor must approve inventory evidence.");
                var plan = await planning.Build(scope, operation, request, token);
                operation.PlanJson = JsonSerializer.Serialize(plan);
                operation.ApprovedBy = actor;
                operation.State = StockState.Approved;
            }
            else if (action == "cancel" && operation.State is StockState.Draft or StockState.Approved)
                operation.State = StockState.Cancelled;
            else if (action == "post" && operation.State == StockState.Approved)
            {
                if (operation.Date < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) && !input.AllowBackdated)
                    throw Bad("Backdating requires explicit authorization.");
                var plan = await planning.Build(scope, operation, request, token);
                // Deserialize and serialize the stored jsonb plan to normalize PostgreSQL's object key ordering.
                if (TradeRules.Hash(plan) != TradeRules.Hash(StockPlanning.Plan(operation)))
                    throw Conflict("Approved inventory effects changed. Cancel and prepare a new operation.");
                if (governance is not null && operation.Kind is StockKind.Count or StockKind.WriteDown)
                    await governance.Enforce(scope, actor, FinancialDuty.StockAdjustment, id, token);
                await Post(scope, actor, operation, plan, input.AllowBackdated, reason, token);
            }
            else throw Conflict("Invalid stock operation transition.");
            operation.Version = Guid.NewGuid();
            access.Audit(scope, actor, operation.Id, $"StockOperation:{action}", reason);
            return operation;
        }, ct);

    private async Task Post(Company scope, Guid actor, StockOperation operation, StockPlan plan, bool backdated, string reason, CancellationToken ct)
    {
        operation.PostedBy = actor;
        await db.SaveChangesAsync(ct);
        await budget.Stock(scope, actor, operation, plan.Accrual, ct);
        if (PostsTrade(operation))
        {
            var documentId = operation.TradeDocumentId!.Value;
            var version = await db.TradeDocuments.AsNoTracking().Where(x => x.CompanyId == scope.Id && x.Id == documentId)
                .Select(x => x.Version).SingleAsync(ct);
            await trading.Post(actor, scope.OrganizationId, scope.Id, documentId,
                new(version, operation.Date, PostingKey(operation.Id), backdated, reason), ct);
        }
        if (plan.Lines.Count > 0)
        {
            var receipt = await posting.PostAsync(actor, new(new(scope.TenantId, scope.OrganizationId, scope.Id),
                Source(operation.Kind), operation.Id, PostingKey(operation.Id), operation.Date, scope.BaseCurrency, plan.Lines, null,
                operation.Date, $"Inventory {operation.Kind}", operation.RequestKey, backdated, reason), ct);
            operation.JournalId = receipt.JournalId;
        }
        var sequence = (await db.StockOperations.Where(x => x.CompanyId == scope.Id).MaxAsync(x => x.Sequence, ct) ?? 0) + 1;
        for (var i = 0; i < plan.Movements.Count; i++)
        {
            var movement = plan.Movements[i];
            db.StockMovements.Add(Scoped(scope, new StockMovement { OperationId = operation.Id, ItemId = operation.ItemId,
                WarehouseId = movement.WarehouseId, Date = operation.Date, Sequence = sequence, Number = i + 1,
                Quantity = movement.Quantity, Gross = movement.Gross, Allowance = movement.Allowance, Reserved = movement.Reserved,
                LotsJson = JsonSerializer.Serialize(movement.Lots) }));
        }
        await db.SaveChangesAsync(ct);
        operation.Sequence = sequence;
        operation.Accrual = plan.Accrual;
        operation.SourceQuantity = plan.SourceQuantity;
        operation.State = StockState.Posted;
    }

    public async Task<StockPlan> Preview(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var operation = await Find(company, id, ct);
        var plan = operation.State == StockState.Posted ? StockPlanning.Plan(operation) :
            await planning.Build(scope, operation, StockPlanning.Input(operation), ct);
        await tx.CommitAsync(ct);
        return plan;
    }

    public async Task<PageResult<StockOperation>> List(Guid actor, Guid org, Guid company, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        await access.Resolve(actor, org, company, false, false, ct);
        var query = db.StockOperations.AsNoTracking().Where(x => x.CompanyId == company);
        return new(await query.OrderByDescending(x => x.Date).ThenByDescending(x => x.Sequence).ThenBy(x => x.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct), await query.CountAsync(ct), page, size);
    }

    private async Task CheckWarehouseAccess(Guid actor, Company scope, Guid id, CancellationToken ct)
    {
        var warehouse = await planning.Warehouse(scope.Id, id, ct);
        if (warehouse.ManagerId.HasValue && warehouse.ManagerId != actor)
            await access.Resolve(actor, scope.OrganizationId, scope.Id, true, true, ct);
    }

    private async Task<StockOperation> Find(Guid company, Guid id, CancellationToken ct) =>
        await db.StockOperations.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
        ?? throw Missing("Stock operation was not found.");

    internal static string PostingKey(Guid id) => $"inventory:{id:N}";
    internal static string Source(StockKind kind) => $"Inventory.{kind}";
    internal static bool PostsTrade(StockOperation op) =>
        op.Kind is StockKind.MatchBill or StockKind.CustomerReturn || (op.Kind == StockKind.SupplierReturn && op.TradeDocumentId.HasValue);

    private static void ValidateShape(StockRequest input)
    {
        if (!Enum.IsDefined(input.Kind)) throw Bad("Unknown stock operation.");
        var related = input.Kind is StockKind.MatchBill or StockKind.Release or StockKind.ReceiptCorrection or
            StockKind.CustomerReturn or StockKind.SupplierReturn or StockKind.LandedCost;
        var requiresTrade = input.Kind is StockKind.Issue or StockKind.MatchBill or StockKind.CustomerReturn;
        if ((related && !input.RelatedOperationId.HasValue && input.ReceiptQuantities is null && input.LandedReceipts is null) ||
            (!related && input.Kind != StockKind.Issue && input.RelatedOperationId.HasValue) ||
            (input.OrderLineId.HasValue != (input.Kind == StockKind.Receipt)) ||
            (input.DestinationId.HasValue != (input.Kind == StockKind.Transfer)) ||
            (input.TradeDocumentId.HasValue != input.TradeLineId.HasValue) ||
            (requiresTrade && !input.TradeDocumentId.HasValue) ||
            (!requiresTrade && input.Kind != StockKind.SupplierReturn && input.TradeDocumentId.HasValue) ||
            (input.SourceJournalLineId.HasValue && input.Kind is not (StockKind.Opening or StockKind.LandedCost)) ||
            (input.Kind == StockKind.LandedCost && !input.SourceJournalLineId.HasValue) ||
            (input.ReceiptQuantities is { } receipts && (input.Kind != StockKind.MatchBill || input.RelatedOperationId.HasValue ||
                receipts.Count is < 1 or > 100 || receipts.Any(x => x is null) ||
                receipts.Select(x => x.ReceiptId).Distinct().Count() != receipts.Count)) ||
            (input.LandedReceipts is { } landed && (input.Kind != StockKind.LandedCost || input.RelatedOperationId.HasValue ||
                landed.Count is < 1 or > 100 || landed.Distinct().Count() != landed.Count)) ||
            (!Enum.IsDefined(input.LandedBasis) || (input.LandedBasis != LandedCostBasis.Quantity && input.Kind != StockKind.LandedCost)) ||
            (input.Damaged && input.Kind != StockKind.CustomerReturn))
            throw Bad("Source fields must match the selected stock operation.");
        if (input.ReceiptQuantities is { } quantities && quantities.Sum(x => StockValuation.Quantity(x.Quantity)) != input.Quantity)
            throw Bad("Receipt allocations must sum to the requested matched quantity.");
    }
}
