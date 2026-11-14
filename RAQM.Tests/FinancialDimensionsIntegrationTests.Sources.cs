using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Budgeting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Trading;
using Xunit;

namespace RAQM.Tests;

public sealed partial class FinancialDimensionsIntegrationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AdoptedPurchaseMustMatchHardDimensionAtActivation(bool matching, bool policyFirst)
    {
        using var s = await Arrange();
        var (_, supplier) = await TradeSetup(s);
        var definition = await Definition(s, DimensionKind.CostCenter);
        var source = await Value(s, definition, "SOURCE");
        var other = await Value(s, definition, "OTHER");
        var accounts = new Dictionary<InventoryRole, Guid>();
        foreach (var role in Enum.GetValues<InventoryRole>())
        {
            var type = role switch
            {
                InventoryRole.Grni => AccountClass.Liability, InventoryRole.OpeningEquity => AccountClass.Equity,
                InventoryRole.Gain => AccountClass.Revenue, InventoryRole.Inventory or InventoryRole.Allowance or
                InventoryRole.InvoiceClearing => AccountClass.Asset, _ => AccountClass.Expense
            };
            accounts[role] = await Account(s, $"INV-{(int)role}", type, role == InventoryRole.Allowance);
        }
        var inventory = s.Root + "/inventory";
        await Send<InventoryPolicy>(s.Owner.Client, inventory + "/policy", new InventoryPolicyRequest(ValuationMethod.Fifo, 0, 1000, accounts));
        var category = await Send<InventoryCategory>(s.Owner.Client, inventory + "/categories", new InventoryCategoryRequest("PARTS", "Parts"));
        var item = await Send<InventoryItem>(s.Owner.Client, inventory + "/items", new InventoryItemRequest(category.Id,
            "PART", "Part", ItemKind.Stocked, "EA", "EA", "EA", 1, 1, supplier.Id, null));
        var order = await Send<ProcurementDocument>(s.Maker.Client, inventory + "/purchasing", new ProcurementRequest(
            "old-dimension", true, null, supplier.Id, s.Date, "Purchase", [new(item.Id, 1, 80, 0, null)]));
        var lineId = Assert.Single(order.Lines).Id;
        await Attach(s, DimensionSource.Procurement, lineId, order.Version, [new(1, [new(definition.Id, source.Id)])]);
        order = await Send<ProcurementDocument>(s.Owner.Client, $"{inventory}/purchasing/{order.Id}/approve",
            new InventoryAction(order.Version, "Approved"));
        await Send<ProcurementDocument>(s.Owner.Client, $"{inventory}/purchasing/{order.Id}/confirm",
            new InventoryAction(order.Version, "Confirmed"));
        var account = accounts[InventoryRole.Inventory];
        var policy = new PolicyRequest(account, BudgetKind.Inventory, ControlMode.Warning, s.Date, 80, "Adopt historical order");
        if (policyFirst)
            await Send<BudgetPolicy>(s.Owner.Client, s.Root + "/budgets/controls", policy);
        var budgets = s.Root + "/budgets";
        var master = await Send<FinancialBudget>(s.Maker.Client, budgets, new BudgetRequest(s.Year.Id,
            BudgetKind.Inventory, "DIM", "Inventory", "Scope", 2000));
        var version = await Send<BudgetVersion>(s.Maker.Client, $"{budgets}/{master.Id}/versions",
            new BudgetVersionRequest("dimension", "Envelope", [new(account, 1200, AllocationMethod.Equal)]));
        var first = version.Allocations.Single(x => x.PeriodId == s.Year.Periods[0].Id);
        await Send<DimensionBudgetSlice>(s.Maker.Client, $"{s.Dimensions}/budget-versions/{version.Id}/slices",
            new BudgetSliceRequest(first.Id, matching ? source.Id : other.Id, 100, true));
        var action = $"{budgets}/versions/{version.Id}";
        version = await Send<BudgetVersion>(s.Maker.Client, action + "/submit", new BudgetAction(version.Version, "Submitted", s.Owner.Id));
        version = await Send<BudgetVersion>(s.Owner.Client, action + "/review", new BudgetAction(version.Version, "Reviewed"));
        version = await Send<BudgetVersion>(s.Owner.Client, action + "/approve", new BudgetAction(version.Version, "Approved"));
        using var activation = await s.Owner.Client.PostAsJsonAsync(action + "/activate",
            new BudgetAction(version.Version, "Activate", EffectiveDate: s.Date));
        Assert.Equal(matching || !policyFirst ? HttpStatusCode.OK : HttpStatusCode.Conflict, activation.StatusCode);
        if (matching)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.Equal(80m, await db.BudgetCommitments.Where(x => x.CompanyId == s.Company.Id &&
                x.ProcurementLineId == lineId).SumAsync(x => x.Amount));
            var next = await Send<ProcurementDocument>(s.Maker.Client, inventory + "/purchasing", new ProcurementRequest(
                "next-dimension", true, null, supplier.Id, s.Date, "New purchase", [new(item.Id, 1, 30, 0, null)]));
            await Attach(s, DimensionSource.Procurement, Assert.Single(next.Lines).Id, next.Version,
                [new(1, [new(definition.Id, source.Id)])]);
            using var denied = await s.Owner.Client.PostAsJsonAsync($"{inventory}/purchasing/{next.Id}/approve",
                new InventoryAction(next.Version, "Exceeds scope"));
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            Assert.Equal(80m, await db.BudgetCommitments.Where(x => x.CompanyId == s.Company.Id &&
                x.AccountId == account).SumAsync(x => x.Amount));
        }
        else if (policyFirst)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.Equal(BudgetState.Approved, (await db.BudgetVersions.SingleAsync(x => x.Id == version.Id)).State);
        }
        else
        {
            using var denied = await s.Owner.Client.PostAsJsonAsync(s.Root + "/budgets/controls", policy);
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.False(await db.BudgetPolicies.AnyAsync(x => x.CompanyId == s.Company.Id && x.AccountId == account));
            Assert.False(await db.BudgetCommitments.AnyAsync(x => x.CompanyId == s.Company.Id));
        }
    }
    private static async Task<Guid> Account(Setup s, string code, AccountClass type, bool contra = false) =>
        (await Send<AccountResponse>(s.Owner.Client, s.Core + "/accounts", new AccountRequest(code, code, null, type, true,
            s.Chart[$"{(int)type + 1}000"], contra), HttpStatusCode.Created)).Id;

    private static async Task<(PartyResponse Customer, PartyResponse Supplier)> TradeSetup(Setup s)
    {
        var ar = await Account(s, "TRADE-AR", AccountClass.Asset);
        var taxInput = await Account(s, "TRADE-INPUT", AccountClass.Asset);
        var advanceSupplier = await Account(s, "TRADE-SA", AccountClass.Asset);
        var taxOutput = await Account(s, "TRADE-OUTPUT", AccountClass.Liability);
        var advanceCustomer = await Account(s, "TRADE-CA", AccountClass.Liability);
        var trade = s.Root + "/trading";
        await Send<List<ControlAccountResponse>>(s.Owner.Client, trade + "/configuration", new ControlAccountRequest(ar, s.Chart["2100"],
            s.Chart["4100"], s.Chart["5100"], taxOutput, taxInput, advanceCustomer, advanceSupplier, s.Chart["3100"], [s.Chart["1100"]],
            false, "Controls"), method: HttpMethod.Put);
        var customer = await Send<PartyResponse>(s.Owner.Client, trade + "/parties", new PartyRequest(PartyKind.Customer, "CUS", "Customer", null,
            null, null, null, 30, null, ar, s.Chart["4100"]), HttpStatusCode.Created);
        var supplier = await Send<PartyResponse>(s.Owner.Client, trade + "/parties", new PartyRequest(PartyKind.Supplier, "SUP", "Supplier", null,
            null, null, null, 30, null, s.Chart["2100"], s.Chart["5100"]), HttpStatusCode.Created);
        return (customer, supplier);
    }

    private static async Task<DimensionProject> OpenProject(Setup s, DimensionDefinition definition, Guid? customer = null)
    {
        var project = await Send<DimensionProject>(s.Maker.Client, s.Dimensions + "/projects", new ProjectRequest(
            new ValueRequest(definition.Id, "P-1", "Project", "Operating", s.Date, ManagerId: s.Maker.Id),
            customer, s.Date, s.Year.EndDate, "Financial project", "Approved plan"));
        var route = $"{s.Dimensions}/projects/{project.Id}/state";
        await Send<DimensionProjectEvent>(s.Owner.Client, route, new ProjectAction(1, ProjectState.Approved, s.Date, "Approved"));
        await Send<DimensionProjectEvent>(s.Owner.Client, route, new ProjectAction(2, ProjectState.Active, s.Date, "Active"));
        return project;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommercialSourceTagsDistinctLinesAndLeavesTheControlLineUnassigned(bool sale)
    {
        using var s = await Arrange();
        var (customer, supplier) = await TradeSetup(s);
        var definition = await Definition(s, DimensionKind.Department);
        var a = await Value(s, definition, "A");
        var b = await Value(s, definition, "B");
        var path = s.Root + "/trading/documents";
        var trade = await Send<TradeDocumentResponse>(s.Owner.Client, path, new TradeDocumentRequest(sale ? customer.Id : supplier.Id,
            sale ? TradeKind.Invoice : TradeKind.Bill, s.Date, null, "Commercial", "doc", [new("First", 1, 100), new("Second", 1, 50)]),
            HttpStatusCode.Created);
        trade = await Send<TradeDocumentResponse>(s.Owner.Client, $"{path}/{trade.Id}/approve", new DocumentTransitionRequest(trade.Version, "Approved"));
        await Attach(s, DimensionSource.Trading, trade.Id, trade.Version, [new(1, [new(definition.Id, a.Id)]), new(2, [new(definition.Id, b.Id)])]);
        await Send<TradeDocumentResponse>(s.Owner.Client, $"{path}/{trade.Id}/post", new PostTradeDocumentRequest(trade.Version, s.Date, "doc", true, "Approved"));
        var report = await Report(s, definition.Id, extra: "&nonNominal=true");
        Assert.Equal(100m, Group(report, a.Id).GetProperty(sale ? "revenue" : "expense").GetDecimal());
        Assert.Equal(50m, Group(report, b.Id).GetProperty(sale ? "revenue" : "expense").GetDecimal());
        var unassigned = Assert.Single(report.GetProperty("groups").EnumerateArray(), x => x.GetProperty("valueId").ValueKind == JsonValueKind.Null);
        Assert.Equal(sale ? 150m : -150m, unassigned.GetProperty("other").GetDecimal());
    }

    [Fact]
    public async Task ProjectTracksPostedRevenueExpenseAndRetainsApprovedCompletionForecast()
    {
        using var s = await Arrange();
        var (customer, _) = await TradeSetup(s);
        var definition = await Definition(s, DimensionKind.Project);
        var project = await OpenProject(s, definition, customer.Id);
        var cost = await Journal(s, 40);
        await Attach(s, DimensionSource.Manual, cost.Id, cost.Version, [new(1, [new(definition.Id, project.ValueId)])]);
        await Post(s, cost, "cost");
        var path = s.Root + "/trading/documents";
        var sale = await Send<TradeDocumentResponse>(s.Owner.Client, path, new TradeDocumentRequest(customer.Id, TradeKind.Invoice, s.Date, null,
            "Project sale", "project", [new("Services", 1, 100)]), HttpStatusCode.Created);
        sale = await Send<TradeDocumentResponse>(s.Owner.Client, $"{path}/{sale.Id}/approve", new DocumentTransitionRequest(sale.Version, "Approved"));
        await Attach(s, DimensionSource.Trading, sale.Id, sale.Version, [new(1, [new(definition.Id, project.ValueId)])]);
        await Send<TradeDocumentResponse>(s.Owner.Client, $"{path}/{sale.Id}/post", new PostTradeDocumentRequest(sale.Version, s.Date, "sale", true, "Approved"));
        var root = $"{s.Dimensions}/projects/{project.Id}";
        await Send<DimensionProjectForecast>(s.Owner.Client, root + "/forecasts", new ProjectForecastRequest(s.Date, 20, 30, "Expected remaining work"));
        var report = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{root}/report?at={s.Date:yyyy-MM-dd}");
        Assert.Equal(60m, report.GetProperty("financials").GetProperty("contribution").GetDecimal());
        Assert.Equal(70m, Assert.Single(report.GetProperty("forecasts").EnumerateArray()).GetProperty("estimatedMargin").GetDecimal());
        await Send<DimensionProjectEvent>(s.Owner.Client, root + "/state", new ProjectAction(3, ProjectState.Completed, s.Date.AddDays(1), "Completed"));
        await Send<DimensionProjectEvent>(s.Owner.Client, root + "/state", new ProjectAction(4, ProjectState.Closed, s.Date.AddDays(1), "Closed"));
        var late = await Journal(s, 10, s.Date.AddDays(1));
        await Attach(s, DimensionSource.Manual, late.Id, late.Version, [new(1, [new(definition.Id, project.ValueId)])]);
        using var denied = await s.Owner.Client.PostAsJsonAsync($"{s.Core}/journals/{late.Id}/post", new PostJournalRequest("late", late.Version, true, "Test"));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
    }

    [Fact]
    public async Task ProcuredProjectMaterialRetainsAttributionAndOpenOrdersPreventProjectClosure()
    {
        using var s = await Arrange();
        var (_, supplier) = await TradeSetup(s);
        var definition = await Definition(s, DimensionKind.Project);
        var project = await OpenProject(s, definition);
        var accounts = new Dictionary<InventoryRole, Guid>();
        foreach (var role in Enum.GetValues<InventoryRole>())
        {
            var type = role switch
            {
                InventoryRole.Grni => AccountClass.Liability, InventoryRole.OpeningEquity => AccountClass.Equity, InventoryRole.Gain => AccountClass.Revenue,
                InventoryRole.Inventory or InventoryRole.Allowance or InventoryRole.InvoiceClearing => AccountClass.Asset, _ => AccountClass.Expense
            };
            accounts[role] = await Account(s, $"INV-{(int)role}", type, role == InventoryRole.Allowance);
        }
        var inventory = s.Root + "/inventory";
        await Send<InventoryPolicy>(s.Owner.Client, inventory + "/policy", new InventoryPolicyRequest(ValuationMethod.Fifo, 0, 1000, accounts));
        var category = await Send<InventoryCategory>(s.Owner.Client, inventory + "/categories", new InventoryCategoryRequest("PARTS", "Parts"));
        var item = await Send<InventoryItem>(s.Owner.Client, inventory + "/items", new InventoryItemRequest(category.Id, "PART", "Part", ItemKind.Stocked,
            "EA", "EA", "EA", 1, 1, supplier.Id, null));
        var warehouse = await Send<Warehouse>(s.Owner.Client, inventory + "/warehouses", new WarehouseRequest("MAIN", "Main", "HQ", s.Maker.Id));
        var order = await Send<ProcurementDocument>(s.Maker.Client, inventory + "/purchasing", new ProcurementRequest("po", true, null, supplier.Id,
            s.Date, "Project PO", [new(item.Id, 10, 10, 0, null)]));
        var line = Assert.Single(order.Lines).Id;
        await Attach(s, DimensionSource.Procurement, line, order.Version, [new(1, [new(definition.Id, project.ValueId)])]);
        async Task<ProcurementDocument> Change(ProcurementDocument x, string action) => await Send<ProcurementDocument>(s.Owner.Client,
            $"{inventory}/purchasing/{x.Id}/{action}", new InventoryAction(x.Version, "Approved"));
        await Change(await Change(order, "approve"), "confirm");
        var receipt = await Send<StockOperation>(s.Maker.Client, inventory + "/operations", new StockRequest("receipt", StockKind.Receipt,
            s.Date, item.Id, warehouse.Id, 4, "Receipt", OrderLineId: line));
        async Task<StockOperation> Stock(StockOperation x, string action) => await Send<StockOperation>(s.Owner.Client,
            $"{inventory}/operations/{x.Id}/{action}", new InventoryAction(x.Version, "Approved", true));
        await Stock(await Stock(receipt, "approve"), "post");
        var analytical = await Report(s, definition.Id, extra: "&nonNominal=true");
        Assert.Equal(40m, Group(analytical, project.ValueId).GetProperty("other").GetDecimal());
        var root = $"{s.Dimensions}/projects/{project.Id}";
        var report = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{root}/report?at={s.Date:yyyy-MM-dd}");
        Assert.Equal(60m, report.GetProperty("financials").GetProperty("openPurchaseCommitments").GetDecimal());
        await Send<DimensionProjectEvent>(s.Owner.Client, root + "/state", new ProjectAction(3, ProjectState.Completed, s.Date.AddDays(1), "Completed"));
        using var denied = await s.Owner.Client.PostAsJsonAsync(root + "/state", new ProjectAction(4, ProjectState.Closed, s.Date.AddDays(1), "Close"));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
    }
}
