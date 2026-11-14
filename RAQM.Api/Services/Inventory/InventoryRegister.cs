using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Inventory;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.Inventory;

public sealed class InventoryRegister(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions)
{
    internal static T Scoped<T>(Company company, T row) where T : FinancialEvidence
    {
        row.CompanyId = company.Id;
        row.OrganizationId = company.OrganizationId;
        row.TenantId = company.TenantId;
        return row;
    }

    internal static ApiException Bad(string message) => new(400, message, "inventory_input");
    internal static ApiException Conflict(string message) => new(409, message, "inventory_state");
    internal static ApiException Missing(string message) => new(404, message, "inventory_missing");
    internal static string Code(string value) => AccountingRules.Text(value, 40, "Inventory code").ToUpperInvariant();

    public Task<InventoryPolicy> Configure(Guid actor, Guid org, Guid company, InventoryPolicyRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            if (await db.InventoryPolicies.AnyAsync(x => x.CompanyId == company, token))
                throw Conflict("Inventory policy and account mappings are already established.");
            var roles = Enum.GetValues<InventoryRole>();
            if (!Enum.IsDefined(input.Method) || input.Accounts is null || input.Accounts.Count != roles.Length ||
                roles.Any(r => !input.Accounts.ContainsKey(r)) || input.Accounts.Values.Distinct().Count() != roles.Length)
                throw Bad("Provide a valuation method and a distinct account for each role.");
            var units = CurrencyPolicy.Units(scope.BaseCurrency);
            CurrencyPolicy.Validate(input.InvoicePriceTolerance, units);
            CurrencyPolicy.Validate(input.ApprovalLimit, units);
            if (input.ApprovalLimit <= 0) throw Bad("Approval limit must be positive.");
            var ids = input.Accounts.Values.ToArray();
            if (await db.AssetControls.AnyAsync(x => x.CompanyId == company && ids.Contains(x.AccountId), token) ||
                await db.TradingAccounts.AnyAsync(x => x.CompanyId == company && ids.Contains(x.AccountId), token) ||
                await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == company && ids.Contains(x.LedgerAccountId), token))
                throw Conflict("Inventory accounts cannot reuse asset, trading or treasury control mappings.");
            var accounts = await db.Accounts.AsNoTracking().Where(x => x.CompanyId == company && ids.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, token);
            foreach (var role in roles)
            {
                var classification = role switch
                {
                    InventoryRole.Inventory or InventoryRole.InvoiceClearing or InventoryRole.Allowance => AccountClass.Asset,
                    InventoryRole.Grni => AccountClass.Liability,
                    InventoryRole.OpeningEquity => AccountClass.Equity,
                    InventoryRole.Gain => AccountClass.Revenue,
                    _ => AccountClass.Expense
                };
                if (!accounts.TryGetValue(input.Accounts[role], out var account) || account.Classification != classification ||
                    !account.IsActive || !account.IsPosting || account.IsContra != (role == InventoryRole.Allowance))
                    throw Bad($"Account for {role} must be an active eligible posting account in this company.");
                if ((int)role <= 3 && await (from line in db.JournalLines join journal in db.Journals on line.JournalId equals journal.Id
                    where line.CompanyId == company && journal.CompanyId == company && line.AccountId == account.Id && journal.State == JournalState.Posted &&
                        (role != InventoryRole.Inventory || journal.SourceModule != "OpeningBalance" || line.Debit <= 0)
                    select line.Id).AnyAsync(token))
                    throw Conflict("Stock controls require dedicated accounts. Only eligible opening inventory debits may predate setup.");
                db.InventoryAccounts.Add(Scoped(scope, new InventoryAccount { Role = role, AccountId = account.Id }));
            }
            var result = Scoped(scope, new InventoryPolicy { Method = input.Method, InvoicePriceTolerance = input.InvoicePriceTolerance,
                ApprovalLimit = input.ApprovalLimit });
            db.InventoryPolicies.Add(result);
            access.Audit(scope, actor, result.Id, "InventoryPolicyEstablished", result.Method.ToString());
            return result;
        }, ct);

    public Task<InventoryCategory> AddCategory(Guid actor, Guid org, Guid company, InventoryCategoryRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var code = Code(input.Code);
            if (await db.InventoryCategories.AnyAsync(x => x.CompanyId == company && x.Code == code, token))
                throw Conflict("Category code already exists.");
            var row = Scoped(scope, new InventoryCategory { Code = code, Name = AccountingRules.Text(input.Name, 160, "Category name") });
            db.InventoryCategories.Add(row);
            access.Audit(scope, actor, row.Id, "InventoryCategoryCreated", code);
            return row;
        }, ct);

    public Task<InventoryItem> AddItem(Guid actor, Guid org, Guid company, InventoryItemRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var code = Code(input.Code);
            if (await db.InventoryItems.AnyAsync(x => x.CompanyId == company && x.Code == code, token))
                throw Conflict("Item code already exists.");
            if (!Enum.IsDefined(input.Kind) || !await db.InventoryCategories.AnyAsync(x => x.CompanyId == company && x.Id == input.CategoryId, token))
                throw Bad("Select a valid item kind and company category.");
            if (input.SupplierId is { } supplier && !await db.TradingParties.AnyAsync(x => x.CompanyId == company &&
                x.Id == supplier && x.Kind == PartyKind.Supplier && x.IsActive, token))
                throw Bad("Default supplier must be active in this company.");
            if (input.TaxCodeId is { } tax && !await db.TradingTaxCodes.AnyAsync(x => x.CompanyId == company && x.Id == tax && x.IsActive, token))
                throw Bad("Tax code must be active in this company.");
            AccountingRules.OptionalText(input.Description, 1000);
            foreach (var price in new[] { input.DefaultPurchasePrice, input.DefaultSalesPrice })
                if (price.HasValue) CurrencyPolicy.Validate(price.Value, CurrencyPolicy.Units(scope.BaseCurrency));
            var row = Scoped(scope, new InventoryItem { Code = code, Name = AccountingRules.Text(input.Name, 160, "Item name"),
                CategoryId = input.CategoryId, Kind = input.Kind, SupplierId = input.SupplierId, TaxCodeId = input.TaxCodeId,
                Description = input.Description?.Trim(), DefaultPurchasePrice = input.DefaultPurchasePrice, DefaultSalesPrice = input.DefaultSalesPrice,
                BaseUnit = Code(input.BaseUnit), PurchaseUnit = Code(input.PurchaseUnit), SalesUnit = Code(input.SalesUnit),
                PurchaseFactor = StockValuation.Quantity(input.PurchaseFactor), SalesFactor = StockValuation.Quantity(input.SalesFactor) });
            if ((row.BaseUnit == row.PurchaseUnit && row.PurchaseFactor != 1) || (row.BaseUnit == row.SalesUnit && row.SalesFactor != 1) ||
                (row.PurchaseUnit == row.SalesUnit && row.PurchaseFactor != row.SalesFactor))
                throw Bad("An identical unit code must have the same conversion factor.");
            db.InventoryItems.Add(row);
            access.Audit(scope, actor, row.Id, "InventoryItemCreated", code);
            return row;
        }, ct);

    public Task<Warehouse> AddWarehouse(Guid actor, Guid org, Guid company, WarehouseRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var code = Code(input.Code);
            if (await db.Warehouses.AnyAsync(x => x.CompanyId == company && x.Code == code, token))
                throw Conflict("Warehouse code already exists.");
            if (input.ManagerId is { } manager && !await db.CompanyMembers.AnyAsync(x => x.CompanyId == company &&
                x.TenantId == scope.TenantId && x.UserId == manager && x.Role == CompanyRole.Manager, token))
                throw Bad("Warehouse manager must have company financial write permission.");
            var row = Scoped(scope, new Warehouse { Code = code, Name = AccountingRules.Text(input.Name, 160, "Warehouse name"),
                Location = AccountingRules.Text(input.Location, 250, "Location"), ManagerId = input.ManagerId });
            db.Warehouses.Add(row);
            access.Audit(scope, actor, row.Id, "InventoryWarehouseCreated", code);
            return row;
        }, ct);

    public async Task<object> Get(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return new
        {
            Policy = await db.InventoryPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company, ct),
            Accounts = await db.InventoryAccounts.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Role).ToListAsync(ct),
            Categories = await db.InventoryCategories.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct),
            Warehouses = await db.Warehouses.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct)
        };
    }

    public async Task<PageResult<InventoryItem>> Items(Guid actor, Guid org, Guid company, int page, int size,
        string? search, ItemKind? kind, bool? active, Guid? category, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        AccountingRules.OptionalText(search, 100);
        if (kind.HasValue && !Enum.IsDefined(kind.Value)) throw Bad("Unknown item type.");
        await access.Resolve(actor, org, company, false, false, ct);
        var query = db.InventoryItems.AsNoTracking().Where(x => x.CompanyId == company);
        if (kind.HasValue) query = query.Where(x => x.Kind == kind);
        if (active.HasValue) query = query.Where(x => x.IsActive == active);
        if (category.HasValue) query = query.Where(x => x.CategoryId == category);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => EF.Functions.ILike(x.Code, "%" + search + "%") || EF.Functions.ILike(x.Name, "%" + search + "%"));
        return new(await query.OrderBy(x => x.Code).Skip((page - 1) * size).Take(size).ToListAsync(ct),
            await query.CountAsync(ct), page, size);
    }

    public Task<bool> SetStatus(Guid actor, Guid org, Guid company, Guid id, bool warehouse, InventoryStatusRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var reason = AccountingRules.Text(input.Reason, 500, "Status reason");
            var item = warehouse ? null : await db.InventoryItems.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token);
            var location = !warehouse ? null : await db.Warehouses.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token);
            var status = item?.IsActive ?? location?.IsActive ?? throw Missing("Inventory master record was not found.");
            if (status == input.IsActive) return status;
            if (!input.IsActive)
            {
                var rows = db.StockMovements.Where(x => x.CompanyId == company && (warehouse ? x.WarehouseId == id : x.ItemId == id));
                var stock = await rows.SumAsync(x => x.Quantity, token);
                var pending = await db.StockOperations.AnyAsync(x => x.CompanyId == company && (warehouse ? x.WarehouseId == id : x.ItemId == id) &&
                    (x.State == StockState.Draft || x.State == StockState.Approved), token);
                if (stock != 0 || pending)
                    throw Conflict("Clear stock and pending inventory operations before deactivation.");
            }
            if (item is not null) item.IsActive = input.IsActive;
            if (location is not null) location.IsActive = input.IsActive;
            access.Audit(scope, actor, id, input.IsActive ? "InventoryMasterActivated" : "InventoryMasterDeactivated", reason);
            return input.IsActive;
        }, ct);

    public async Task<IReadOnlyList<AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.AccountingAudit.AsNoTracking().Where(x => x.CompanyId == company && x.EntityId == id)
            .OrderBy(x => x.At).ThenBy(x => x.Id).Select(x => new AuditResponse(x.Id, x.EntityId, x.Action, x.ActorId, x.At, x.Reason)).ToListAsync(ct);
    }

    internal Task<InventoryPolicy?> Policy(Guid company, CancellationToken ct) =>
        db.InventoryPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company, ct);
}
