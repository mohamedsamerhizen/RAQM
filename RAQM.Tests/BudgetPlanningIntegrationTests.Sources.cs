using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Budgeting;
using RAQM.Api.DTOs.Forecasting;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Entities.Forecasting;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.FinancialAnalytics;
using RAQM.Api.Services.Inventory;
using Xunit;

namespace RAQM.Tests;

public sealed partial class BudgetPlanningIntegrationTests
{
    private static async Task<Guid> AddAccount(Setup s, string code, AccountClass type, bool contra = false) =>
        (await Send<AccountResponse>(s.Owner.Client, s.Core + "/accounts", new AccountRequest(code, code, null, type,
            true, s.Chart[$"{(int)type + 1}000"], contra), HttpStatusCode.Created)).Id;

    private static async Task<(Guid Supplier, Guid Customer)> TradeSetup(Setup s)
    {
        var ar = await AddAccount(s, "TRADE-AR", AccountClass.Asset);
        var taxIn = await AddAccount(s, "TRADE-INPUT", AccountClass.Asset);
        var sa = await AddAccount(s, "TRADE-SA", AccountClass.Asset);
        var taxOut = await AddAccount(s, "TRADE-OUTPUT", AccountClass.Liability);
        var ca = await AddAccount(s, "TRADE-CA", AccountClass.Liability);
        await Send<List<ControlAccountResponse>>(s.Owner.Client, s.Root + "/trading/configuration",
            new ControlAccountRequest(ar, s.Chart["2100"], s.Chart["4100"], s.Chart["5100"], taxOut, taxIn, ca, sa, s.Chart["3100"],
                [s.Chart["1100"]], false, "Trading controls"), method: HttpMethod.Put);
        var supplier = await Send<PartyResponse>(s.Owner.Client, s.Root + "/trading/parties", new PartyRequest(PartyKind.Supplier,
            "SUP", "Supplier", null, null, null, null, 30, null, s.Chart["2100"], s.Chart["5100"]), HttpStatusCode.Created);
        var customer = await Send<PartyResponse>(s.Owner.Client, s.Root + "/trading/parties", new PartyRequest(PartyKind.Customer,
            "CUS", "Customer", null, null, null, null, 30, null, ar, s.Chart["4100"]), HttpStatusCode.Created);
        return (supplier.Id, customer.Id);
    }

    private static async Task<TradeDocumentResponse> Trade(Setup s, Guid party, TradeKind kind, decimal amount, string key,
        Guid? account = null, decimal quantity = 1, bool post = true)
    {
        var path = s.Root + "/trading/documents";
        var due = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        var row = await Send<TradeDocumentResponse>(s.Owner.Client, path,
            new TradeDocumentRequest(party, kind, s.Date, due, "Planning obligation", key, [new("Terms", quantity, amount, AccountId: account)]),
            HttpStatusCode.Created);
        row = await Send<TradeDocumentResponse>(s.Owner.Client, $"{path}/{row.Id}/approve", new DocumentTransitionRequest(row.Version, "Approved"));
        return post ? await Send<TradeDocumentResponse>(s.Owner.Client, $"{path}/{row.Id}/post",
            new PostTradeDocumentRequest(row.Version, s.Date, key, true, "Approved")) : row;
    }

    private static async Task<(Guid Supplier, Dictionary<InventoryRole, Guid> Roles, InventoryItem Item, Warehouse Warehouse)> InventorySetup(Setup s)
    {
        var (supplier, _) = await TradeSetup(s);
        var roles = new Dictionary<InventoryRole, Guid>();
        foreach (var role in Enum.GetValues<InventoryRole>())
        {
            var type = role switch
            {
                InventoryRole.Grni => AccountClass.Liability, InventoryRole.OpeningEquity => AccountClass.Equity,
                InventoryRole.Gain => AccountClass.Revenue, InventoryRole.Inventory or InventoryRole.Allowance or
                InventoryRole.InvoiceClearing => AccountClass.Asset, _ => AccountClass.Expense
            };
            roles[role] = await AddAccount(s, $"INV-{(int)role}", type, role == InventoryRole.Allowance);
        }
        var inventory = s.Root + "/inventory";
        await Send<InventoryPolicy>(s.Owner.Client, inventory + "/policy", new InventoryPolicyRequest(ValuationMethod.Fifo, 0, 1000, roles));
        var category = await Send<InventoryCategory>(s.Owner.Client, inventory + "/categories", new InventoryCategoryRequest("PARTS", "Parts"));
        var item = await Send<InventoryItem>(s.Owner.Client, inventory + "/items", new InventoryItemRequest(category.Id, "WIDGET",
            "Widget", ItemKind.Stocked, "EA", "EA", "EA", 1, 1, supplier, null));
        var warehouse = await Send<Warehouse>(s.Owner.Client, inventory + "/warehouses", new WarehouseRequest("MAIN", "Main", "HQ", s.Maker.Id));
        return (supplier, roles, item, warehouse);
    }

    [Fact]
    public async Task PopulatedBudgetEventsSurviveForwardGuardUpgradeWithoutRebooking()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        await using var factory = new ApiFactory(postgres.GetConnectionString());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            await db.Database.MigrateAsync("20261113120000_StockedSalesIntegrity");
            // Allow the current model to seed through the API at this older migration.
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE trading."DocumentLines" ADD COLUMN "CancelUnissuedStock" boolean NOT NULL DEFAULT false""");
        }
        using var s = await Arrange(factory);
        var (supplier, roles, item, warehouse) = await InventorySetup(s);
        var account = roles[InventoryRole.Inventory];
        var (budget, _) = await Publish(s, BudgetKind.Inventory, account, 120);
        await Hard(s, BudgetKind.Inventory, account);
        var inventory = s.Root + "/inventory";
        var draft = await Send<ProcurementDocument>(s.Maker.Client, inventory + "/purchasing", new ProcurementRequest(
            "retained-order", true, null, supplier, s.Date, "Retained purchase", [new(item.Id, 10, 12, 0, null)]));
        var approved = await Send<ProcurementDocument>(s.Owner.Client, $"{inventory}/purchasing/{draft.Id}/approve",
            new InventoryAction(draft.Version, "Approved"));
        await Send<ProcurementDocument>(s.Owner.Client, $"{inventory}/purchasing/{draft.Id}/confirm",
            new InventoryAction(approved.Version, "Confirmed"));
        var receipt = await Send<StockOperation>(s.Maker.Client, inventory + "/operations", new StockRequest("retained-receipt",
            StockKind.Receipt, s.Date, item.Id, warehouse.Id, 4, "Received", OrderLineId: Assert.Single(draft.Lines).Id));
        receipt = await Send<StockOperation>(s.Owner.Client, $"{inventory}/operations/{receipt.Id}/approve",
            new InventoryAction(receipt.Version, "Approved", true));
        receipt = await Send<StockOperation>(s.Owner.Client, $"{inventory}/operations/{receipt.Id}/post",
            new InventoryAction(receipt.Version, "Posted", true));
        Guid[] eventIds;
        decimal journalDebit;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            eventIds = await db.BudgetCommitments.Where(x => x.ProcurementLineId == Assert.Single(draft.Lines).Id)
                .OrderBy(x => x.EventKey).Select(x => x.Id).ToArrayAsync();
            journalDebit = await db.JournalLines.Where(x => x.JournalId == receipt.JournalId).SumAsync(x => x.Debit);
            Assert.Equal(18, (await db.Database.GetAppliedMigrationsAsync()).Count());
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE trading."DocumentLines" DROP COLUMN "CancelUnissuedStock" """);
            await db.Database.MigrateAsync();
            Assert.Equal(22, (await db.Database.GetAppliedMigrationsAsync()).Count());
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.Equal(eventIds, await db.BudgetCommitments.Where(x => x.ProcurementLineId == Assert.Single(draft.Lines).Id)
                .OrderBy(x => x.EventKey).Select(x => x.Id).ToArrayAsync());
            Assert.Equal(journalDebit, await db.JournalLines.Where(x => x.JournalId == receipt.JournalId).SumAsync(x => x.Debit));
        }
        var row = Assert.Single((await Report(s, budget.Id, account)).Accounts);
        Assert.Equal((72m, 48m, 0m), (row.Commitments, row.Actual, row.Available!.Value));
        await Send<BudgetPolicy>(s.Owner.Client, s.Budgets + "/controls", new PolicyRequest(account,
            BudgetKind.Inventory, ControlMode.Hard, s.Date.AddDays(1), 80, "Renew control"));
        Assert.Equal(72m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments);
    }

    [Fact]
    public async Task R02_004_HardBudgetAdoptsPreviouslyConfirmedPurchaseOrder()
    {
        using var s = await Arrange();
        var (supplier, _) = await TradeSetup(s);
        var roles = new Dictionary<InventoryRole, Guid>();
        foreach (var role in Enum.GetValues<InventoryRole>())
        {
            var type = role switch
            {
                InventoryRole.Grni => AccountClass.Liability,
                InventoryRole.OpeningEquity => AccountClass.Equity,
                InventoryRole.Gain => AccountClass.Revenue,
                InventoryRole.Inventory or InventoryRole.Allowance or InventoryRole.InvoiceClearing => AccountClass.Asset,
                _ => AccountClass.Expense
            };
            roles[role] = await AddAccount(s, $"INV-{(int)role}", type, role == InventoryRole.Allowance);
        }
        var root = s.Root + "/inventory";
        await Send<InventoryPolicy>(s.Owner.Client, root + "/policy", new InventoryPolicyRequest(ValuationMethod.Fifo, 0, 1000, roles));
        var category = await Send<InventoryCategory>(s.Owner.Client, root + "/categories", new InventoryCategoryRequest("PARTS", "Parts"));
        var item = await Send<InventoryItem>(s.Owner.Client, root + "/items", new InventoryItemRequest(category.Id, "WIDGET",
            "Widget", ItemKind.Stocked, "EA", "EA", "EA", 1, 1, supplier, null));
        var path = root + "/purchasing";
        Task<ProcurementDocument> Change(ProcurementDocument row, string action) => Send<ProcurementDocument>(
            s.Owner.Client, $"{path}/{row.Id}/{action}", new InventoryAction(row.Version, "Reviewed"));
        async Task<ProcurementDocument> Order(string key, decimal amount)
        {
            var input = new ProcurementRequest(key, true, RequisitionId: null, SupplierId: supplier, s.Date,
                "Stock order", [new(item.Id, 1, amount, 0, null)]);
            var draft = await Send<ProcurementDocument>(s.Maker.Client, path, input);
            return await Change(await Change(draft, "approve"), "confirm");
        }

        var old = await Order("legacy", 80);
        var account = roles[InventoryRole.Inventory];
        var (budget, _) = await Publish(s, BudgetKind.Inventory, account, 100);
        await Hard(s, BudgetKind.Inventory, account);
        var afterActivation = Assert.Single((await Report(s, budget.Id, account)).Accounts);
        Assert.Equal((80m, 0m, 20m), (afterActivation.Commitments, afterActivation.Actual, afterActivation.Available!.Value));
        var fresh = await Send<ProcurementDocument>(s.Maker.Client, path, new ProcurementRequest("after-control", true,
            RequisitionId: null, SupplierId: supplier, s.Date, "Stock order", [new(item.Id, 1, 30, 0, null)]));
        using var denied = await s.Owner.Client.PostAsJsonAsync($"{path}/{fresh.Id}/approve",
            new InventoryAction(fresh.Version, "Reviewed"));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var row = Assert.Single((await Report(s, budget.Id, account)).Accounts);
        Assert.Equal(80m, row.Commitments);
        Assert.Equal(110m, old.Lines.Sum(x => x.NetAmount) + fresh.Lines.Sum(x => x.NetAmount));
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            var lineId = Assert.Single(old.Lines).Id;
            Assert.Single(await db.BudgetCommitments.Where(x => x.CompanyId == s.Company.Id && x.ProcurementLineId == lineId &&
                x.EventKey == $"reserve:{lineId:N}").ToListAsync());
            Assert.Single(await db.AccountingAudit.Where(x => x.CompanyId == s.Company.Id && x.EntityId == lineId &&
                x.Action == "BudgetCommitmentAdopted").ToListAsync());
        }
    }

    [Fact]
    public async Task PrePolicyPartialReceiptConvertsAdoptedCommitmentWithoutBillDuplication()
    {
        using var s = await Arrange();
        var (supplier, roles, item, warehouse) = await InventorySetup(s);
        var inventory = s.Root + "/inventory";
        var input = new ProcurementRequest("legacy-partial", true, null, supplier, s.Date, "Stock order",
            [new(item.Id, 10, 12, 0, null)]);
        var order = await Send<ProcurementDocument>(s.Maker.Client, inventory + "/purchasing", input);
        Task<ProcurementDocument> Purchase(ProcurementDocument row, string action) => Send<ProcurementDocument>(s.Owner.Client,
            $"{inventory}/purchasing/{row.Id}/{action}", new InventoryAction(row.Version, "Reviewed"));
        order = await Purchase(await Purchase(order, "approve"), "confirm");
        var receipt = await Send<StockOperation>(s.Maker.Client, inventory + "/operations", new StockRequest("legacy-receipt",
            StockKind.Receipt, s.Date, item.Id, warehouse.Id, 4, "Receipt", OrderLineId: Assert.Single(order.Lines).Id));
        Task<StockOperation> Stock(StockOperation row, string action) => Send<StockOperation>(s.Owner.Client,
            $"{inventory}/operations/{row.Id}/{action}", new InventoryAction(row.Version, "Reviewed", true));
        receipt = await Stock(await Stock(receipt, "approve"), "post");
        var account = roles[InventoryRole.Inventory];
        var (budget, _) = await Publish(s, BudgetKind.Inventory, account, 120);
        await Hard(s, BudgetKind.Inventory, account);
        var adopted = Assert.Single((await Report(s, budget.Id, account)).Accounts);
        Assert.Equal((72m, 48m, 0m), (adopted.Commitments, adopted.Actual, adopted.Available!.Value));
        await Send<BudgetPolicy>(s.Owner.Client, s.Budgets + "/controls",
            new PolicyRequest(account, BudgetKind.Inventory, ControlMode.Hard, s.Date.AddDays(1), 80, "Renew hard control"));
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            var events = await db.BudgetCommitments.Where(x => x.CompanyId == s.Company.Id &&
                x.ProcurementLineId == Assert.Single(order.Lines).Id).ToListAsync();
            Assert.Equal(2, events.Count);
            Assert.Equal(120m, events.Single(x => x.EventKey.StartsWith("reserve:")).Amount);
            Assert.Equal(-48m, events.Single(x => x.StockOperationId == receipt.Id).Amount);
        }
        var bill = await Trade(s, supplier, TradeKind.Bill, 12, "adopted-bill", roles[InventoryRole.InvoiceClearing], 4, false);
        var match = await Send<StockOperation>(s.Maker.Client, inventory + "/operations", new StockRequest("adopted-match",
            StockKind.MatchBill, s.Date, item.Id, warehouse.Id, 4, "Supplier invoice", RelatedOperationId: receipt.Id,
            TradeDocumentId: bill.Id, TradeLineId: Assert.Single(bill.Lines).Id, Amount: 48));
        await Stock(await Stock(match, "approve"), "post");
        var afterBill = Assert.Single((await Report(s, budget.Id, account)).Accounts);
        Assert.Equal((72m, 48m, 0m), (afterBill.Commitments, afterBill.Actual, afterBill.Available!.Value));
        var finalReceipt = await Send<StockOperation>(s.Maker.Client, inventory + "/operations", new StockRequest("final-receipt",
            StockKind.Receipt, s.Date, item.Id, warehouse.Id, 6, "Final receipt", OrderLineId: Assert.Single(order.Lines).Id));
        await Stock(await Stock(finalReceipt, "approve"), "post");
        var converted = Assert.Single((await Report(s, budget.Id, account)).Accounts);
        Assert.Equal((0m, 120m, 0m), (converted.Commitments, converted.Actual, converted.Available!.Value));
    }

    [Fact]
    public async Task OverexposedHardActivationRollsBackAndCancelledOrderIsNotAdopted()
    {
        using var s = await Arrange();
        var (supplier, roles, item, _) = await InventorySetup(s);
        var path = s.Root + "/inventory/purchasing";
        async Task<ProcurementDocument> Order(string key, decimal amount, Guid? replaced = null)
        {
            var draft = await Send<ProcurementDocument>(s.Maker.Client, path, new ProcurementRequest(key, true, null,
                supplier, s.Date, "Stock order", [new(item.Id, 1, amount, 0, null)], ReplacesDocumentId: replaced));
            var approved = await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{draft.Id}/approve",
                new InventoryAction(draft.Version, "Reviewed"));
            return await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{draft.Id}/confirm",
                new InventoryAction(approved.Version, "Confirmed"));
        }
        var cancelled = await Order("old-order", 40);
        cancelled = await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{cancelled.Id}/cancel",
            new InventoryAction(cancelled.Version, "Replaced"));
        var replacement = await Order("replacement", 80, cancelled.Id);
        var account = roles[InventoryRole.Inventory];
        var (budget, _) = await Publish(s, BudgetKind.Inventory, account, 50);
        using (var denied = await s.Owner.Client.PostAsJsonAsync(s.Budgets + "/controls",
            new PolicyRequest(account, BudgetKind.Inventory, ControlMode.Hard, s.Date, 80, "Insufficient authority")))
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.False(await db.BudgetPolicies.AnyAsync(x => x.CompanyId == s.Company.Id && x.AccountId == account));
            Assert.False(await db.BudgetCommitments.AnyAsync(x => x.CompanyId == s.Company.Id));
        }
        await Send<BudgetPolicy>(s.Owner.Client, s.Budgets + "/controls",
            new PolicyRequest(account, BudgetKind.Inventory, ControlMode.Warning, s.Date, 80, "Retain excess as warning"));
        var row = Assert.Single((await Report(s, budget.Id, account)).Accounts);
        Assert.Equal((50m, 80m, -30m), (row.Budget, row.Commitments, row.Available!.Value));
        using var scope2 = fixture.Factory.Services.CreateScope();
        var retained = scope2.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.False(await retained.BudgetCommitments.AnyAsync(x => x.ProcurementLineId == Assert.Single(cancelled.Lines).Id));
        Assert.True(await retained.BudgetCommitments.AnyAsync(x => x.ProcurementLineId == Assert.Single(replacement.Lines).Id));
    }

    [Fact]
    public async Task HardActivationAndNewOrderApprovalRaceCannotMissExposure()
    {
        using var s = await Arrange();
        var (supplier, roles, item, _) = await InventorySetup(s);
        var path = s.Root + "/inventory/purchasing";
        async Task<ProcurementDocument> Draft(string key, decimal amount) => await Send<ProcurementDocument>(s.Maker.Client,
            path, new ProcurementRequest(key, true, null, supplier, s.Date, "Stock order", [new(item.Id, 1, amount, 0, null)]));
        var old = await Draft("race-old", 80);
        old = await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{old.Id}/approve", new InventoryAction(old.Version, "Approved"));
        await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{old.Id}/confirm", new InventoryAction(old.Version, "Confirmed"));
        var fresh = await Draft("race-new", 30);
        var account = roles[InventoryRole.Inventory];
        var (budget, _) = await Publish(s, BudgetKind.Inventory, account, 100);
        var responses = await Task.WhenAll(
            s.Owner.Client.PostAsJsonAsync(s.Budgets + "/controls",
                new PolicyRequest(account, BudgetKind.Inventory, ControlMode.Hard, s.Date, 80, "Concurrent activation")),
            s.Owner.Client.PostAsJsonAsync($"{path}/{fresh.Id}/approve", new InventoryAction(fresh.Version, "Concurrent order")));
        using var activation = responses[0];
        using var approval = responses[1];
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        if (activation.StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal(80m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments);
            Assert.Equal(ProcurementState.Draft, (await db.ProcurementDocuments.SingleAsync(x => x.Id == fresh.Id)).State);
        }
        else
        {
            Assert.False(await db.BudgetPolicies.AnyAsync(x => x.CompanyId == s.Company.Id && x.AccountId == account));
            Assert.Equal(ProcurementState.Approved, (await db.ProcurementDocuments.SingleAsync(x => x.Id == fresh.Id)).State);
            Assert.False(await db.BudgetCommitments.AnyAsync(x => x.CompanyId == s.Company.Id));
        }
    }

    [Fact]
    public async Task TwoOrderApprovalsAndCancellationConserveFinalHardHeadroom()
    {
        using var s = await Arrange();
        var (supplier, roles, item, _) = await InventorySetup(s);
        var path = s.Root + "/inventory/purchasing";
        async Task<ProcurementDocument> Draft(string key, decimal amount) => await Send<ProcurementDocument>(s.Maker.Client,
            path, new ProcurementRequest(key, true, null, supplier, s.Date, "Stock order", [new(item.Id, 1, amount, 0, null)]));
        Task<ProcurementDocument> Approve(ProcurementDocument row) => Send<ProcurementDocument>(s.Owner.Client,
            $"{path}/{row.Id}/approve", new InventoryAction(row.Version, "Approved"));
        var old = await Approve(await Draft("headroom-old", 80));
        await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{old.Id}/confirm", new InventoryAction(old.Version, "Confirmed"));
        var account = roles[InventoryRole.Inventory];
        var (budget, _) = await Publish(s, BudgetKind.Inventory, account, 100);
        await Hard(s, BudgetKind.Inventory, account);
        var candidates = new[] { await Draft("headroom-a", 20), await Draft("headroom-b", 20) };
        var approvals = await Task.WhenAll(candidates.Select(x => s.Owner.Client.PostAsJsonAsync($"{path}/{x.Id}/approve",
            new InventoryAction(x.Version, "Use final authority"))));
        try
        {
            Assert.Single(approvals, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(approvals, x => x.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var response in approvals) response.Dispose(); }
        Assert.Equal((100m, 0m), (Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments,
            Assert.Single((await Report(s, budget.Id, account)).Accounts).Available!.Value));
        var winner = candidates[Array.FindIndex(approvals, x => x.StatusCode == HttpStatusCode.OK)];
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var current = await db.ProcurementDocuments.SingleAsync(x => x.Id == winner.Id);
        await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{winner.Id}/cancel", new InventoryAction(current.Version, "Cancel"));
        var after = Assert.Single((await Report(s, budget.Id, account)).Accounts);
        Assert.Equal((80m, 20m), (after.Commitments, after.Available!.Value));
    }

    [Fact]
    public async Task ReceiptPostingAndHistoricalAdoptionRaceConservesOneTwenty()
    {
        using var s = await Arrange();
        var (supplier, roles, item, warehouse) = await InventorySetup(s);
        var inventory = s.Root + "/inventory";
        var draft = await Send<ProcurementDocument>(s.Maker.Client, inventory + "/purchasing", new ProcurementRequest(
            "receipt-race-po", true, null, supplier, s.Date, "Stock order", [new(item.Id, 10, 12, 0, null)]));
        var approved = await Send<ProcurementDocument>(s.Owner.Client, $"{inventory}/purchasing/{draft.Id}/approve",
            new InventoryAction(draft.Version, "Approved"));
        await Send<ProcurementDocument>(s.Owner.Client, $"{inventory}/purchasing/{draft.Id}/confirm",
            new InventoryAction(approved.Version, "Confirmed"));
        var receipt = await Send<StockOperation>(s.Maker.Client, inventory + "/operations", new StockRequest("receipt-race",
            StockKind.Receipt, s.Date, item.Id, warehouse.Id, 4, "Received", OrderLineId: Assert.Single(draft.Lines).Id));
        receipt = await Send<StockOperation>(s.Owner.Client, $"{inventory}/operations/{receipt.Id}/approve",
            new InventoryAction(receipt.Version, "Approved", true));
        var account = roles[InventoryRole.Inventory];
        var (budget, _) = await Publish(s, BudgetKind.Inventory, account, 120);
        var responses = await Task.WhenAll(
            s.Owner.Client.PostAsJsonAsync(s.Budgets + "/controls",
                new PolicyRequest(account, BudgetKind.Inventory, ControlMode.Hard, s.Date, 80, "Receipt race")),
            s.Owner.Client.PostAsJsonAsync($"{inventory}/operations/{receipt.Id}/post",
                new InventoryAction(receipt.Version, "Post receipt", true)));
        try { Assert.All(responses, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode)); }
        finally { foreach (var response in responses) response.Dispose(); }
        var row = Assert.Single((await Report(s, budget.Id, account)).Accounts);
        Assert.Equal((72m, 48m, 0m), (row.Commitments, row.Actual, row.Available!.Value));
    }

    [Fact]
    public async Task AdoptedRequisitionRestoresWhenOldOrderIsCancelledAndReplaced()
    {
        using var s = await Arrange();
        var (supplier, roles, item, _) = await InventorySetup(s);
        var path = s.Root + "/inventory/purchasing";
        var input = new ProcurementRequest("old-req", false, null, null, s.Date, "Need stock",
            [new(item.Id, 1, 80, 0, null)]);
        Task<ProcurementDocument> Change(ProcurementDocument row, string action) => Send<ProcurementDocument>(s.Owner.Client,
            $"{path}/{row.Id}/{action}", new InventoryAction(row.Version, "Reviewed"));
        var req = await Change(await Send<ProcurementDocument>(s.Maker.Client, path, input), "approve");
        var old = await Send<ProcurementDocument>(s.Maker.Client, path, input with
        { Key = "old-po", IsOrder = true, RequisitionId = req.Id, SupplierId = supplier });
        old = await Change(await Change(old, "approve"), "confirm");
        var account = roles[InventoryRole.Inventory];
        var (budget, _) = await Publish(s, BudgetKind.Inventory, account, 100);
        await Hard(s, BudgetKind.Inventory, account);
        Assert.Equal(80m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments);
        old = await Change(old, "cancel");
        Assert.Equal(80m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments);
        var replacement = await Send<ProcurementDocument>(s.Maker.Client, path, input with
        { Key = "replacement-po", IsOrder = true, RequisitionId = req.Id, SupplierId = supplier, ReplacesDocumentId = old.Id });
        replacement = await Change(await Change(replacement, "approve"), "confirm");
        Assert.Equal(80m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.Equal(0m, await db.BudgetCommitments.Where(x => x.ProcurementLineId == Assert.Single(req.Lines).Id)
            .SumAsync(x => x.Amount));
        Assert.Equal(0m, await db.BudgetCommitments.Where(x => x.ProcurementLineId == Assert.Single(old.Lines).Id)
            .SumAsync(x => x.Amount));
        Assert.Equal(80m, await db.BudgetCommitments.Where(x => x.ProcurementLineId == Assert.Single(replacement.Lines).Id)
            .SumAsync(x => x.Amount));
    }

    [Fact]
    public async Task WaiverConsumptionAndJournalRollbackRemainAtomic()
    {
        using var s = await Arrange();
        var account = s.Chart["5100"];
        var (budget, _) = await Publish(s, BudgetKind.Operating, account, 100);
        await Hard(s, BudgetKind.Operating, account);
        var draft = await Journal(s, s.Date, account, s.Chart["1100"], 120);
        const string key = "waiver-rollback";
        var grant = await Send<BudgetOverride>(s.Owner.Client, s.Budgets + "/overrides", new OverrideRequest(account,
            $"journal:{draft.SourceDocumentId:N}:{TradeRules.Hash(key)}", 20,
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2), "Approved exception"));
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var tx = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
            var journals = scope.ServiceProvider.GetRequiredService<IJournalService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => tx.Execute<int>(s.Owner.Id, s.Owner.Org, s.Company.Id,
                async (_, ct) =>
                {
                    await journals.Post(s.Owner.Id, s.Owner.Org, s.Company.Id, draft.Id,
                        new PostJournalRequest(key, draft.Version, true, "Approved exception"), ct);
                    throw new InvalidOperationException("Injected rollback after waiver and posting");
                }, default));
        }
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.Null((await db.BudgetOverrides.SingleAsync(x => x.Id == grant.Id)).UsedAt);
            Assert.False(await db.Journals.AnyAsync(x => x.Id == draft.Id && x.State == JournalState.Posted));
            Assert.Equal(0m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Actual);
        }
        await Post(s, draft, key);
        using var finalScope = fixture.Factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.NotNull((await finalDb.BudgetOverrides.SingleAsync(x => x.Id == grant.Id)).UsedAt);
        Assert.Equal(120m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Actual);
    }

    [Fact]
    public async Task HistoricalOrdersRetainTheirDistinctFiscalPeriodBuckets()
    {
        using var s = await Arrange();
        var (supplier, roles, item, _) = await InventorySetup(s);
        var path = s.Root + "/inventory/purchasing";
        async Task<ProcurementDocument> Order(string key, DateOnly date, decimal amount)
        {
            var draft = await Send<ProcurementDocument>(s.Maker.Client, path, new ProcurementRequest(key, true, null,
                supplier, date, "Historical order", [new(item.Id, 1, amount, 0, null)]));
            var approved = await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{draft.Id}/approve",
                new InventoryAction(draft.Version, "Approved"));
            return await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{draft.Id}/confirm",
                new InventoryAction(approved.Version, "Confirmed"));
        }
        var first = await Order("period-a", s.Year.Periods[0].StartDate, 30);
        var second = await Order("period-b", s.Year.Periods[1].StartDate, 40);
        var account = roles[InventoryRole.Inventory];
        await Publish(s, BudgetKind.Inventory, account, 100);
        await Hard(s, BudgetKind.Inventory, account);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var events = await db.BudgetCommitments.Where(x => x.CompanyId == s.Company.Id && x.AccountId == account).ToListAsync();
        Assert.Equal((s.Year.Periods[0].Id, 30m),
            (Assert.Single(events, x => x.ProcurementLineId == Assert.Single(first.Lines).Id).PeriodId,
             Assert.Single(events, x => x.ProcurementLineId == Assert.Single(first.Lines).Id).Amount));
        Assert.Equal((s.Year.Periods[1].Id, 40m),
            (Assert.Single(events, x => x.ProcurementLineId == Assert.Single(second.Lines).Id).PeriodId,
             Assert.Single(events, x => x.ProcurementLineId == Assert.Single(second.Lines).Id).Amount));
        Assert.Equal(70m, events.Sum(x => x.Amount));
    }

    [Fact]
    public async Task OrderCancellationRacingNewConsumptionKeepsOnlyOpenObligations()
    {
        using var s = await Arrange();
        var (supplier, roles, item, _) = await InventorySetup(s);
        var path = s.Root + "/inventory/purchasing";
        Task<ProcurementDocument> Draft(string key, decimal amount) => Send<ProcurementDocument>(s.Maker.Client, path,
            new ProcurementRequest(key, true, null, supplier, s.Date, "Stock order", [new(item.Id, 1, amount, 0, null)]));
        var old = await Draft("cancel-race-old", 80);
        old = await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{old.Id}/approve", new InventoryAction(old.Version, "Approved"));
        old = await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{old.Id}/confirm", new InventoryAction(old.Version, "Confirmed"));
        var account = roles[InventoryRole.Inventory];
        var (budget, _) = await Publish(s, BudgetKind.Inventory, account, 100);
        await Hard(s, BudgetKind.Inventory, account);
        var fresh = await Draft("cancel-race-new", 30);
        var responses = await Task.WhenAll(
            s.Owner.Client.PostAsJsonAsync($"{path}/{old.Id}/cancel", new InventoryAction(old.Version, "Cancelled")),
            s.Owner.Client.PostAsJsonAsync($"{path}/{fresh.Id}/approve", new InventoryAction(fresh.Version, "Approved")));
        using var cancellation = responses[0];
        using var approval = responses[1];
        Assert.Equal(HttpStatusCode.OK, cancellation.StatusCode);
        Assert.Contains(approval.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        var row = Assert.Single((await Report(s, budget.Id, account)).Accounts);
        Assert.Equal(approval.StatusCode == HttpStatusCode.OK ? 30m : 0m, row.Commitments);
        Assert.Equal(100m - row.Commitments, row.Available!.Value);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.Equal(0m, await db.BudgetCommitments.Where(x => x.ProcurementLineId == Assert.Single(old.Lines).Id)
            .SumAsync(x => x.Amount));
    }

    [Fact]
    public async Task OpenPriorYearOrderConsumesCurrentYearAuthorityWhenHardPolicyBegins()
    {
        using var s = await Arrange();
        var (supplier, roles, item, _) = await InventorySetup(s);
        var earlier = await Send<FiscalYearResponse>(s.Owner.Client, s.Core + "/fiscal-years",
            new FiscalYearRequest(s.Date.Year - 1, "Earlier fiscal year"), HttpStatusCode.Created);
        var path = s.Root + "/inventory/purchasing";
        var draft = await Send<ProcurementDocument>(s.Maker.Client, path, new ProcurementRequest("prior-year-order", true,
            null, supplier, earlier.StartDate, "Open purchase", [new(item.Id, 1, 80, 0, null)]));
        var approved = await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{draft.Id}/approve",
            new InventoryAction(draft.Version, "Approved"));
        await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{draft.Id}/confirm",
            new InventoryAction(approved.Version, "Confirmed"));
        var account = roles[InventoryRole.Inventory];
        var (budget, _) = await Publish(s, BudgetKind.Inventory, account, 100);
        await Hard(s, BudgetKind.Inventory, account);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var adopted = Assert.Single(await db.BudgetCommitments.Where(x => x.ProcurementLineId == Assert.Single(draft.Lines).Id).ToListAsync());
        Assert.Equal(s.Year.Periods[0].Id, adopted.PeriodId);
        Assert.Equal(80m, adopted.Amount);
        var fresh = await Send<ProcurementDocument>(s.Maker.Client, path, new ProcurementRequest("new-year-order", true,
            null, supplier, s.Date, "New purchase", [new(item.Id, 1, 30, 0, null)]));
        using var denied = await s.Owner.Client.PostAsJsonAsync($"{path}/{fresh.Id}/approve",
            new InventoryAction(fresh.Version, "Exceeds current authority"));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal(80m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments);
    }

    [Fact]
    public async Task ExistingPriorYearCommitmentRollsIntoReplacementHardPolicy()
    {
        using var s = await Arrange();
        var (supplier, roles, item, _) = await InventorySetup(s);
        var earlier = await Send<FiscalYearResponse>(s.Owner.Client, s.Core + "/fiscal-years",
            new FiscalYearRequest(s.Date.Year - 1, "Earlier fiscal year"), HttpStatusCode.Created);
        var account = roles[InventoryRole.Inventory];
        var oldBudget = await Master(s, BudgetKind.Inventory, earlier.Id);
        var oldVersion = await Approve(s, await Draft(s, oldBudget, account, 100));
        await Action(s, oldVersion, "activate", date: earlier.StartDate);
        await Send<BudgetPolicy>(s.Owner.Client, s.Budgets + "/controls", new PolicyRequest(account,
            BudgetKind.Inventory, ControlMode.Hard, earlier.StartDate, 80, "Prior-year control"));
        var path = s.Root + "/inventory/purchasing";
        var draft = await Send<ProcurementDocument>(s.Maker.Client, path, new ProcurementRequest("old-controlled", true,
            null, supplier, earlier.StartDate, "Open purchase", [new(item.Id, 1, 80, 0, null)]));
        var approved = await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{draft.Id}/approve",
            new InventoryAction(draft.Version, "Approved"));
        var order = await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{draft.Id}/confirm",
            new InventoryAction(approved.Version, "Confirmed"));
        var budget = await Send<FinancialBudget>(s.Maker.Client, s.Budgets, new BudgetRequest(s.Year.Id,
            BudgetKind.Inventory, "CURRENT-INVENTORY", "Current inventory", "Annual authority", 1000));
        var currentVersion = await Approve(s, await Draft(s, budget, account, 100));
        await Action(s, currentVersion, "activate", date: s.Date);
        await Hard(s, BudgetKind.Inventory, account);
        var line = Assert.Single(order.Lines).Id;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            var events = await db.BudgetCommitments.Where(x => x.ProcurementLineId == line).ToListAsync();
            Assert.Equal(0m, events.Where(x => earlier.Periods.Any(p => p.Id == x.PeriodId)).Sum(x => x.Amount));
            Assert.Equal(80m, events.Where(x => s.Year.Periods.Any(p => p.Id == x.PeriodId)).Sum(x => x.Amount));
            Assert.Single(events, x => x.EventKey.StartsWith("rollin:"));
        }
        Assert.Equal(80m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var forged = new NpgsqlCommand("""
                INSERT INTO planning."BudgetCommitmentEvents" ("Id","TenantId","OrganizationId","CompanyId","AccountId",
                    "PeriodId","ProcurementLineId","AssetOperationId","StockOperationId","Kind","Date","Amount","EventKey","ActorId")
                SELECT gen_random_uuid(),"TenantId","OrganizationId","CompanyId","AccountId",@period,
                    "ProcurementLineId",NULL,NULL,"Kind",@date,10,@key,"ActorId"
                FROM planning."BudgetCommitmentEvents" WHERE "CompanyId"=@company AND "EventKey"=@reserve
                """, connection, transaction);
            forged.Parameters.AddWithValue("period", s.Year.Periods[1].Id);
            forged.Parameters.AddWithValue("date", DateOnly.FromDateTime(DateTime.UtcNow));
            forged.Parameters.AddWithValue("key", $"rollin:{line:N}:{s.Year.Periods[1].Id:N}");
            forged.Parameters.AddWithValue("company", s.Company.Id);
            forged.Parameters.AddWithValue("reserve", $"reserve:{line:N}");
            Assert.Equal(1, await forged.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation,
                (await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync())).SqlState);
        }
        var cancelled = await Send<ProcurementDocument>(s.Owner.Client, $"{path}/{order.Id}/cancel",
            new InventoryAction(order.Version, "Cancelled"));
        Assert.Equal(ProcurementState.Cancelled, cancelled.State);
        Assert.Equal(0m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments);
    }

    [Fact]
    public async Task PriorYearRequisitionAndOrderRollForwardThroughCancellation()
    {
        using var s = await Arrange();
        var (supplier, roles, item, _) = await InventorySetup(s);
        var earlier = await Send<FiscalYearResponse>(s.Owner.Client, s.Core + "/fiscal-years",
            new FiscalYearRequest(s.Date.Year - 1, "Earlier fiscal year"), HttpStatusCode.Created);
        var account = roles[InventoryRole.Inventory];
        var oldBudget = await Master(s, BudgetKind.Inventory, earlier.Id);
        await Action(s, await Approve(s, await Draft(s, oldBudget, account, 100)), "activate", date: earlier.StartDate);
        await Send<BudgetPolicy>(s.Owner.Client, s.Budgets + "/controls", new PolicyRequest(account,
            BudgetKind.Inventory, ControlMode.Hard, earlier.StartDate, 80, "Prior-year control"));
        var path = s.Root + "/inventory/purchasing";
        var input = new ProcurementRequest("old-req-year", false, null, null, earlier.StartDate, "Need stock",
            [new(item.Id, 1, 80, 0, null)]);
        Task<ProcurementDocument> Change(ProcurementDocument row, string action) => Send<ProcurementDocument>(s.Owner.Client,
            $"{path}/{row.Id}/{action}", new InventoryAction(row.Version, "Reviewed"));
        var req = await Change(await Send<ProcurementDocument>(s.Maker.Client, path, input), "approve");
        var order = await Send<ProcurementDocument>(s.Maker.Client, path, input with
        { Key = "old-po-year", IsOrder = true, RequisitionId = req.Id, SupplierId = supplier });
        order = await Change(await Change(order, "approve"), "confirm");
        var budget = await Send<FinancialBudget>(s.Maker.Client, s.Budgets, new BudgetRequest(s.Year.Id,
            BudgetKind.Inventory, "CURR-REQ", "Current inventory", "Authority", 1000));
        await Action(s, await Approve(s, await Draft(s, budget, account, 100)), "activate", date: s.Date);
        await Hard(s, BudgetKind.Inventory, account);
        Assert.Equal(80m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments);
        order = await Change(order, "cancel");
        Assert.Equal(80m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments);
        var replacement = await Send<ProcurementDocument>(s.Maker.Client, path, input with
        { Key = "replacement-year", Date = s.Date, IsOrder = true, RequisitionId = req.Id,
          SupplierId = supplier, ReplacesDocumentId = order.Id });
        await Change(await Change(replacement, "approve"), "confirm");
        Assert.Equal(80m, Assert.Single((await Report(s, budget.Id, account)).Accounts).Commitments);
    }

    [Fact]
    public async Task RequisitionConvertsToOrderAndReceiptRelievesCommitmentAtomicallyWithoutConsumingTwiceAtBilling()
    {
        using var s = await Arrange();
        var (supplier, _) = await TradeSetup(s);
        var roles = new Dictionary<InventoryRole, Guid>();
        foreach (var role in Enum.GetValues<InventoryRole>())
        {
            var type = role switch
            {
                InventoryRole.Grni => AccountClass.Liability, InventoryRole.OpeningEquity => AccountClass.Equity,
                InventoryRole.Gain => AccountClass.Revenue, InventoryRole.Inventory or InventoryRole.Allowance or
                InventoryRole.InvoiceClearing => AccountClass.Asset, _ => AccountClass.Expense
            };
            roles[role] = await AddAccount(s, $"INV-{(int)role}", type, role == InventoryRole.Allowance);
        }
        var inventory = s.Root + "/inventory";
        await Send<InventoryPolicy>(s.Owner.Client, inventory + "/policy", new InventoryPolicyRequest(ValuationMethod.Fifo, 0, 1000, roles));
        var category = await Send<InventoryCategory>(s.Owner.Client, inventory + "/categories", new InventoryCategoryRequest("PARTS", "Parts"));
        var item = await Send<InventoryItem>(s.Owner.Client, inventory + "/items", new InventoryItemRequest(category.Id, "WIDGET",
            "Widget", ItemKind.Stocked, "EA", "EA", "EA", 1, 1, supplier, null));
        var warehouse = await Send<Warehouse>(s.Owner.Client, inventory + "/warehouses", new WarehouseRequest("MAIN", "Main", "HQ", s.Maker.Id));
        var account = roles[InventoryRole.Inventory];
        var (budget, _) = await Publish(s, BudgetKind.Inventory, account, 150);
        await Hard(s, BudgetKind.Inventory, account);
        var input = new ProcurementRequest("req", false, null, null, s.Date, "Needed", [new(item.Id, 10, 10, 0, null)]);
        var req = await Send<ProcurementDocument>(s.Maker.Client, inventory + "/purchasing", input);
        Task<ProcurementDocument> Change(ProcurementDocument row, string action) => Send<ProcurementDocument>(s.Owner.Client,
            $"{inventory}/purchasing/{row.Id}/{action}", new InventoryAction(row.Version, "Reviewed"));
        req = await Change(req, "approve");
        async Task<BudgetVariance> Totals() => Assert.Single((await Report(s, budget.Id, account)).Accounts);
        Assert.Equal(100, (await Totals()).Commitments);
        var po = await Send<ProcurementDocument>(s.Maker.Client, inventory + "/purchasing", input with
        {
            Key = "po", IsOrder = true, SupplierId = supplier, RequisitionId = req.Id, Lines = [new(item.Id, 10, 12, 0, null)]
        });
        po = await Change(po, "approve");
        Assert.Equal(120, (await Totals()).Commitments);
        po = await Change(po, "cancel");
        Assert.Equal(100, (await Totals()).Commitments);
        var replacement = await Send<ProcurementDocument>(s.Maker.Client, inventory + "/purchasing", input with
        {
            Key = "po-2", IsOrder = true, SupplierId = supplier, RequisitionId = req.Id, ReplacesDocumentId = po.Id,
            Lines = [new(item.Id, 10, 12, 0, null)]
        });
        replacement = await Change(await Change(replacement, "approve"), "confirm");
        var receipt = await Send<StockOperation>(s.Maker.Client, inventory + "/operations", new StockRequest("receipt", StockKind.Receipt,
            s.Date, item.Id, warehouse.Id, 4, "Receipt", OrderLineId: Assert.Single(replacement.Lines).Id));
        Task<StockOperation> StockChange(StockOperation row, string action) => Send<StockOperation>(s.Owner.Client,
            $"{inventory}/operations/{row.Id}/{action}", new InventoryAction(row.Version, "Reviewed", true));
        receipt = await StockChange(receipt, "approve");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var tx = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
            var workflow = scope.ServiceProvider.GetRequiredService<StockWorkflow>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => tx.Execute<int>(s.Owner.Id, s.Owner.Org, s.Company.Id, async (_, ct) =>
            {
                await workflow.Change(s.Owner.Id, s.Owner.Org, s.Company.Id, receipt.Id, "post", new(receipt.Version, "Approved", true), ct);
                throw new InvalidOperationException("Injected fault after staged relief and GL");
            }, default));
        }
        Assert.Equal((120m, 0m), ((await Totals()).Commitments, (await Totals()).Actual));
        receipt = await StockChange(receipt, "post");
        var afterReceipt = await Totals();
        Assert.Equal((72m, 48m, 30m), (afterReceipt.Commitments, afterReceipt.Actual, afterReceipt.Available!.Value));
        var bill = await Trade(s, supplier, TradeKind.Bill, 12, "inventory-bill", roles[InventoryRole.InvoiceClearing], 4, false);
        var match = await Send<StockOperation>(s.Maker.Client, inventory + "/operations", new StockRequest("match", StockKind.MatchBill,
            s.Date, item.Id, warehouse.Id, 4, "Approved supplier invoice", RelatedOperationId: receipt.Id,
            TradeDocumentId: bill.Id, TradeLineId: Assert.Single(bill.Lines).Id, Amount: 48));
        await StockChange(await StockChange(match, "approve"), "post");
        var afterBill = await Totals();
        Assert.Equal((72m, 48m), (afterBill.Commitments, afterBill.Actual));
    }

    [Fact]
    public async Task CapitalBudgetConvertsApprovedAcquisitionToCapitalCost()
    {
        using var s = await Arrange();
        var assetCost = await AddAccount(s, "PPE-COST", AccountClass.Asset);
        var accumulated = await AddAccount(s, "PPE-DEP", AccountClass.Asset, true);
        var impairment = await AddAccount(s, "PPE-IMP", AccountClass.Asset, true);
        var loss = await AddAccount(s, "PPE-LOSS", AccountClass.Expense);
        var impairmentExpense = await AddAccount(s, "PPE-IMPEXP", AccountClass.Expense);
        var gain = await AddAccount(s, "PPE-GAIN", AccountClass.Revenue);
        var payable = await AddAccount(s, "PPE-OFFSET", AccountClass.Liability);
        var assets = s.Root + "/fixed-assets";
        var category = await Send<AssetCategory>(s.Owner.Client, assets + "/categories", new CategoryRequest("PPE", "Equipment", true, false, 0,
            assetCost, accumulated, s.Chart["5100"], impairmentExpense, impairment, gain, loss));
        var (budget, _) = await Publish(s, BudgetKind.Capital, assetCost, 200);
        await Hard(s, BudgetKind.Capital, assetCost);
        var asset = await Send<FixedAsset>(s.Maker.Client, assets + "/register", new RegisterAssetRequest(category.Id,
            "MACHINE", "Machine", "Production equipment", s.Date, null, "Purchase contract", new("HQ", null, null, null, null),
            [new("Motor", 120, 0, 12)]));
        var operation = await Send<AssetOperation>(s.Maker.Client, assets + "/operations", new AssetOperationRequest(AssetOperationKind.Acquisition,
            s.Date, "acquire", "Acquisition", "Signed basis", asset.Id, Funding: CapitalFunding.Direct, Amount: 120, OffsetAccountId: payable));
        Task<AssetOperation> Change(AssetOperation row, string action) => Send<AssetOperation>(s.Owner.Client,
            $"{assets}/operations/{row.Id}/{action}", new AssetAction(row.Version, "Reviewed", true));
        operation = await Change(operation, "approve");
        var committed = Assert.Single((await Report(s, budget.Id, assetCost)).Accounts);
        Assert.Equal((120m, 0m), (committed.Commitments, committed.Actual));
        await Change(operation, "post");
        var used = Assert.Single((await Report(s, budget.Id, assetCost)).Accounts);
        Assert.Equal((0m, 120m, 80m), (used.Commitments, used.Actual, used.Available!.Value));
    }

    [Fact]
    public async Task CashSnapshotRetainsOpenArAndApButExcludesPaidAmounts()
    {
        using var s = await Arrange();
        var (supplier, customer) = await TradeSetup(s);
        var ar = await Trade(s, customer, TradeKind.Invoice, 100, "ar");
        await Trade(s, supplier, TradeKind.Bill, 70, "ap");
        await Send<PaymentResponse>(s.Owner.Client, s.Root + "/trading/payments", new PaymentRequest(customer, s.Chart["1100"], 40,
            s.Date, "Customer payment", "paid", AllowBackdated: true, Reason: "Approved", Allocations: [new(ar.Id, 40)]));
        await Send<FiscalYearResponse>(s.Owner.Client, s.Core + "/fiscal-years", new FiscalYearRequest(s.Date.Year + 1, "Next"), HttpStatusCode.Created);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var plan = await Send<ForecastVersion>(s.Maker.Client, s.Forecasts, new ForecastRequest("cash", "CASH", "Liquidity", today, 3,
            [new(s.Chart["4100"], ForecastMethod.Manual, 0, [100, 100, 100])]));
        var snapshot = JsonSerializer.Deserialize<ForecastSnapshot>(plan.SnapshotJson)!;
        Assert.Equal(40m, snapshot.OpeningCash);
        Assert.Equal(60m, Assert.Single(snapshot.Cash, x => x.Kind == "AR").Amount);
        Assert.Equal(-70m, Assert.Single(snapshot.Cash, x => x.Kind == "AP").Amount);
        Assert.Equal(30m, snapshot.CashBuckets[^1].Closing);
    }

    [Fact]
    public async Task ForecastScenarioRetainsApprovedBaselineAndPreventsDatabaseSnapshotEdits()
    {
        using var s = await Arrange();
        await Send<FiscalYearResponse>(s.Owner.Client, s.Core + "/fiscal-years", new FiscalYearRequest(s.Date.Year + 1, "Next"), HttpStatusCode.Created);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var baseline = await Send<ForecastVersion>(s.Maker.Client, s.Forecasts, new ForecastRequest("base", "BASE", "Baseline", today, 3,
            [new(s.Chart["4100"], ForecastMethod.Manual, 0, [100, 100, 100]),
             new(s.Chart["5100"], ForecastMethod.Manual, 0, [70, 70, 70])]));
        baseline = await Send<ForecastVersion>(s.Owner.Client, $"{s.Forecasts}/{baseline.Id}/approve", new ForecastAction(baseline.Version, "Approved"));
        var scenario = await Send<ForecastVersion>(s.Maker.Client, s.Forecasts + "/scenarios",
            new ScenarioRequest("down", "DOWN", "Stress", baseline.Id, -20, 5, 0, 0, "Explicit assumptions"));
        var compare = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Forecasts}/{scenario.Id}/comparison?cutoff={today:yyyy-MM-dd}");
        Assert.Equal(19.5m, compare.GetProperty("operatingResult").GetDecimal());
        Assert.Equal(90m, compare.GetProperty("comparativeOperatingResult").GetDecimal());
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""UPDATE planning."ForecastVersions" SET "SnapshotJson"='{}'::jsonb WHERE "Id"=@id""", connection);
        command.Parameters.AddWithValue("id", baseline.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }
}
