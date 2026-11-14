using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Inventory;
using Xunit;

namespace RAQM.Tests;

[Collection("PostgreSQL"), Trait("Category", "Integration")]
public sealed class InventoryIntegrationTests(PostgreSqlFixture fixture)
{
    private sealed record Actor(HttpClient Client, Guid Id, Guid Org) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed record Setup(Actor Owner, Actor Maker, Company Company, InventoryItem Item, Warehouse Warehouse,
        DateOnly Date, Dictionary<InventoryRole, Guid> Accounts, Guid Supplier, Guid Customer, Guid Cash) : IDisposable
    {
        public string Root => $"/api/v1/organizations/{Owner.Org}/companies/{Company.Id}";
        public string Inventory => Root + "/inventory";
        public string Trade => Root + "/trading";
        public void Dispose() { Owner.Dispose(); Maker.Dispose(); }
    }

    private static async Task<T> Send<T>(HttpClient client, string path, object body, HttpStatusCode status = HttpStatusCode.OK, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == status, $"{path}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Actor> Onboard(ApiFactory? factory = null)
    {
        var client = (factory ?? fixture.Factory).CreateClient();
        var tokens = await Send<TokenResponse>(client, "/api/v1/auth/onboard",
            new OnboardRequest($"{Guid.NewGuid():N}@inventory.example", "Inventory_Test-Password1!", "Inventory Org", ApiFactory.OnboardingKey));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        return new(client, me.GetProperty("userId").GetGuid(), me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid());
    }

    private async Task<Setup> Arrange(ValuationMethod method, ApiFactory? factory = null)
    {
        var owner = await Onboard(factory);
        var maker = await Onboard(factory);
        var company = await Send<Company>(owner.Client, $"/api/v1/organizations/{owner.Org}/companies",
            new CompanyRequest("Inventory Company", Guid.NewGuid().ToString("N"), "USD", 1, 1), HttpStatusCode.Created);
        using (var scope = (factory ?? fixture.Factory).Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.OrganizationMembers.Add(new() { TenantId = company.TenantId, OrganizationId = owner.Org, UserId = maker.Id, Role = OrganizationRole.Member });
            db.CompanyMembers.Add(new() { TenantId = company.TenantId, OrganizationId = owner.Org, CompanyId = company.Id,
                UserId = maker.Id, Role = CompanyRole.Manager });
            await db.SaveChangesAsync();
        }
        var root = $"/api/v1/organizations/{owner.Org}/companies/{company.Id}";
        var chart = (await Send<List<AccountResponse>>(owner.Client, root + "/accounting/accounts/initialize", new InitializeAccountsRequest()))
            .ToDictionary(x => x.Code, x => x.Id);
        Task<AccountResponse> Account(string code, AccountClass type, bool contra = false) =>
            Send<AccountResponse>(owner.Client, root + "/accounting/accounts",
                new AccountRequest(code, code, null, type, true, chart[$"{(int)type + 1}000"], IsContra: contra), HttpStatusCode.Created);
        var accounts = new Dictionary<InventoryRole, Guid>();
        foreach (var (role, type) in new[]
        {
            (InventoryRole.Inventory, AccountClass.Asset), (InventoryRole.Grni, AccountClass.Liability),
            (InventoryRole.InvoiceClearing, AccountClass.Asset), (InventoryRole.Allowance, AccountClass.Asset),
            (InventoryRole.CostOfSales, AccountClass.Expense), (InventoryRole.Gain, AccountClass.Revenue),
            (InventoryRole.Loss, AccountClass.Expense), (InventoryRole.WriteDown, AccountClass.Expense),
            (InventoryRole.PriceVariance, AccountClass.Expense), (InventoryRole.OpeningEquity, AccountClass.Equity)
        })
            accounts[role] = (await Account($"INV-{(int)role}", type, role == InventoryRole.Allowance)).Id;
        var ar = (await Account("TRADE-AR", AccountClass.Asset)).Id;
        var taxIn = (await Account("TRADE-INPUT", AccountClass.Asset)).Id;
        var supplierAdvance = (await Account("TRADE-SA", AccountClass.Asset)).Id;
        var taxOut = (await Account("TRADE-OUTPUT", AccountClass.Liability)).Id;
        var customerAdvance = (await Account("TRADE-CA", AccountClass.Liability)).Id;
        await Send<JsonElement>(owner.Client, root + "/trading/configuration", new ControlAccountRequest(ar, chart["2100"], chart["4100"],
            chart["5100"], taxOut, taxIn, customerAdvance, supplierAdvance, chart["3100"], [chart["1100"]], false, "Test mappings"),
            method: HttpMethod.Put);
        var supplier = await Send<PartyResponse>(owner.Client, root + "/trading/parties",
            new PartyRequest(PartyKind.Supplier, "SUP", "Supplier", null, null, null, null, 30, null, chart["2100"], chart["5100"]),
            HttpStatusCode.Created);
        var customer = await Send<PartyResponse>(owner.Client, root + "/trading/parties",
            new PartyRequest(PartyKind.Customer, "CUS", "Customer", null, null, null, null, 30, null, ar, chart["4100"]), HttpStatusCode.Created);
        var year = await Send<FiscalYearResponse>(owner.Client, root + "/accounting/fiscal-years",
            new FiscalYearRequest(DateTime.UtcNow.Year - 1, "Test fiscal year"), HttpStatusCode.Created);
        await Send<InventoryPolicy>(owner.Client, root + "/inventory/policy", new InventoryPolicyRequest(method, 5, 100000, accounts));
        var category = await Send<InventoryCategory>(owner.Client, root + "/inventory/categories", new InventoryCategoryRequest("PARTS", "Parts"));
        var item = await Send<InventoryItem>(owner.Client, root + "/inventory/items",
            new InventoryItemRequest(category.Id, "WIDGET", "Widget", ItemKind.Stocked, "EA", "BOX", "EA", 2, 1, supplier.Id, null));
        var warehouse = await Send<Warehouse>(owner.Client, root + "/inventory/warehouses", new WarehouseRequest("MAIN", "Main", "HQ", maker.Id));
        return new(owner, maker, company, item, warehouse, year.StartDate, accounts, supplier.Id, customer.Id, chart["1100"]);
    }

    private static Task<StockOperation> Prepare(Setup s, StockRequest request) =>
        Send<StockOperation>(s.Maker.Client, s.Inventory + "/operations", request);
    private static Task<StockOperation> Change(Setup s, StockOperation row, string action) =>
        Send<StockOperation>(s.Owner.Client, $"{s.Inventory}/operations/{row.Id}/{action}", new InventoryAction(row.Version, "Reviewed stock evidence", true));
    private static async Task<StockOperation> Post(Setup s, StockRequest request) =>
        await Change(s, await Change(s, await Prepare(s, request), "approve"), "post");
    private static StockRequest Request(Setup s, StockKind kind, decimal quantity, decimal amount = 0) =>
        new(Guid.NewGuid().ToString("N"), kind, s.Date, s.Item.Id, s.Warehouse.Id, quantity, "Source documentation", Amount: amount);
    private static async Task<TradeDocumentResponse> Trade(Setup s, Guid party, TradeKind kind, TradeLineRequest line, Guid? original = null)
    {
        var doc = await Send<TradeDocumentResponse>(s.Owner.Client, s.Trade + "/documents",
            new TradeDocumentRequest(party, kind, s.Date, null, "Test trade document", Guid.NewGuid().ToString("N"), [line], original), HttpStatusCode.Created);
        return await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{doc.Id}/approve", new DocumentTransitionRequest(doc.Version, "Approved"));
    }

    private static async Task<ProcurementDocument> Order(Setup s)
    {
        var input = new ProcurementRequest("req-1", false, null, null, s.Date, "Stock needed", [new(s.Item.Id, 5, 20, 0, null)]);
        var req = await Send<ProcurementDocument>(s.Maker.Client, s.Inventory + "/purchasing", input);
        req = await Send<ProcurementDocument>(s.Owner.Client, $"{s.Inventory}/purchasing/{req.Id}/approve", new InventoryAction(req.Version, "Approved"));
        var order = await Send<ProcurementDocument>(s.Maker.Client, s.Inventory + "/purchasing",
            input with { Key = "order-1", IsOrder = true, RequisitionId = req.Id, SupplierId = s.Supplier });
        order = await Send<ProcurementDocument>(s.Owner.Client, $"{s.Inventory}/purchasing/{order.Id}/approve", new InventoryAction(order.Version, "Approved"));
        return await Send<ProcurementDocument>(s.Owner.Client, $"{s.Inventory}/purchasing/{order.Id}/confirm", new InventoryAction(order.Version, "Confirmed"));
    }

    [Fact]
    public async Task R02_002_CloseRequiresPostedStockIssueAndExactCost()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        var opening = await Post(s, Request(s, StockKind.Opening, 2, 60));
        var invoice = await Trade(s, s.Customer, TradeKind.Invoice, new("WIDGET", 2, 50, StockItemId: s.Item.Id));
        invoice = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{invoice.Id}/post",
            new PostTradeDocumentRequest(invoice.Version, s.Date, "stock-sale-without-issue", true, "Authorized sale"));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var period = await db.Periods.AsNoTracking().SingleAsync(x => x.CompanyId == s.Company.Id && x.StartDate == s.Date);
        var closeRoot = $"{s.Root}/financial-close";
        var view = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Owner.Client,
            $"{closeRoot}/periods/{period.Id}/sessions", new { });
        var route = $"{closeRoot}/sessions/{view.Session.Id}";
        view = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Owner.Client,
            $"{route}/tasks/{Assert.Single(view.Session.Tasks).Id}/complete",
            new RAQM.Api.DTOs.FinancialClose.CloseAction(view.Session.Version, "Reviewed"));
        var action = new RAQM.Api.DTOs.FinancialClose.CloseAction(view.Session.Version, "Reviewed");
        using var withoutIssue = await s.Owner.Client.PostAsJsonAsync($"{route}/state/approve", action);
        Assert.Equal(HttpStatusCode.Conflict, withoutIssue.StatusCode);
        Assert.NotNull(opening.JournalId);
        Assert.NotNull(invoice.JournalId);
        Assert.Equal(100m, invoice.Total);
        Assert.False(await db.StockOperations.AnyAsync(x => x.CompanyId == s.Company.Id && x.TradeDocumentId == invoice.Id));
        var cogs = s.Accounts[InventoryRole.CostOfSales];
        Assert.Equal(0m, await db.JournalLines.Where(x => x.CompanyId == s.Company.Id && x.AccountId == cogs &&
            db.Journals.Any(j => j.Id == x.JournalId && j.State == JournalState.Posted && j.PostingDate <= period.EndDate))
            .SumAsync(x => x.Debit - x.Credit));
        var issue = Request(s, StockKind.Issue, 1) with { TradeDocumentId = invoice.Id, TradeLineId = Assert.Single(invoice.Lines).Id };
        var cancelled = await Prepare(s, issue with { Key = "cancelled-issue" });
        await Change(s, cancelled, "cancel");
        await Post(s, issue);
        using var partiallyFulfilled = await s.Owner.Client.PostAsJsonAsync($"{route}/state/approve", action);
        Assert.Equal(HttpStatusCode.Conflict, partiallyFulfilled.StatusCode);
        var candidates = await Task.WhenAll(new[] { "final-1", "final-2" }.Select(async key =>
            await Change(s, await Prepare(s, issue with { Key = key }), "approve")));
        var responses = await Task.WhenAll(candidates.Select(x => s.Owner.Client.PostAsJsonAsync(
            $"{s.Inventory}/operations/{x.Id}/post", new InventoryAction(x.Version, "Issue remaining quantity", true))));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            var rejected = candidates[Array.FindIndex(responses, x => x.StatusCode == HttpStatusCode.Conflict)];
            await Change(s, rejected, "cancel");
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
        var result = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={s.Date:yyyy-MM-dd}");
        Assert.Equal(60m, result.GetProperty("costOfSales").GetDecimal());
        Assert.Equal(0m, result.GetProperty("closingCarryingValue").GetDecimal());
        Assert.Equal(0m, Assert.Single(result.GetProperty("fulfillment").EnumerateArray()).GetProperty("remaining").GetDecimal());
        view = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Owner.Client, $"{route}/state/approve", action);
        view = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Owner.Client, $"{route}/state/close",
            new RAQM.Api.DTOs.FinancialClose.CloseAction(view.Session.Version, "Close"));
        Assert.Equal(PeriodState.Closed, view.AccountingState);
    }

    [Fact]
    public async Task LaterIssueDoesNotSatisfyEarlierCloseAndForeignItemIsRejected()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        using var other = await Arrange(ValuationMethod.Fifo);
        using var foreignItem = await s.Owner.Client.PostAsJsonAsync(s.Trade + "/documents",
            new TradeDocumentRequest(s.Customer, TradeKind.Invoice, s.Date, null, "Invalid item", null,
                [new("Stock", 1, 100, StockItemId: other.Item.Id)]));
        Assert.Equal(HttpStatusCode.BadRequest, foreignItem.StatusCode);
        await Post(s, Request(s, StockKind.Opening, 1, 60));
        var invoice = await Trade(s, s.Customer, TradeKind.Invoice, new("Stock", 1, 100, StockItemId: s.Item.Id));
        invoice = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{invoice.Id}/post",
            new PostTradeDocumentRequest(invoice.Version, s.Date, "later-issue", true, "Approved"));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var period = await db.Periods.SingleAsync(x => x.CompanyId == s.Company.Id && x.StartDate == s.Date);
        var later = period.EndDate.AddDays(1);
        await Post(s, Request(s, StockKind.Issue, 1) with
            { Date = later, TradeDocumentId = invoice.Id, TradeLineId = Assert.Single(invoice.Lines).Id });
        var report = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={period.EndDate:yyyy-MM-dd}");
        var evidence = Assert.Single(report.GetProperty("fulfillment").EnumerateArray());
        Assert.True(evidence.GetProperty("laterIssue").GetBoolean());
        Assert.Equal(1m, evidence.GetProperty("remaining").GetDecimal());
        Assert.Equal(0m, evidence.GetProperty("costOfSales").GetDecimal());
        var laterReport = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={later:yyyy-MM-dd}");
        Assert.Equal(60m, Assert.Single(laterReport.GetProperty("fulfillment").EnumerateArray()).GetProperty("costOfSales").GetDecimal());
        using var legacyClose = await s.Owner.Client.PutAsJsonAsync($"{s.Root}/accounting/periods/{period.Id}/state",
            new PeriodStateRequest(PeriodState.Closed, "Close", period.Version));
        Assert.Equal(HttpStatusCode.Conflict, legacyClose.StatusCode);
    }

    [Fact]
    public async Task ReceiptAndBillMatchClearGrniAtomicallyAndRejectDirectClearingPost()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        var order = await Order(s);
        var receiptInput = Request(s, StockKind.Receipt, 2) with { OrderLineId = Assert.Single(order.Lines).Id };
        var receipt = await Post(s, receiptInput);
        Assert.Equal(20m, receipt.Accrual);
        Assert.Equal(receipt.Id, (await Prepare(s, receiptInput)).Id);
        var secondWarehouse = await Send<Warehouse>(s.Owner.Client, s.Inventory + "/warehouses",
            new WarehouseRequest("SECOND", "Second warehouse", "HQ", s.Maker.Id));
        var second = await Post(s, receiptInput with { Key = "receipt-two", Quantity = 4, WarehouseId = secondWarehouse.Id });
        using var over = await s.Maker.Client.PostAsJsonAsync(s.Inventory + "/operations", receiptInput with { Key = "over", Quantity = 5 });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        var discrepancy = await Send<ProcurementDiscrepancy>(s.Maker.Client, s.Inventory + "/purchasing/discrepancies",
            new DiscrepancyRequest(DiscrepancyKind.ReceiptQuantity, receiptInput.OrderLineId!.Value, second.Id, s.Date, "Delivery exceeds PO quantity"));
        using var unauthorized = await s.Maker.Client.PostAsJsonAsync($"{s.Inventory}/purchasing/discrepancies/{discrepancy.Id}/resolve",
            new DiscrepancyResolution("Unverified"));
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        var resolved = await Send<ProcurementDiscrepancy>(s.Owner.Client, $"{s.Inventory}/purchasing/discrepancies/{discrepancy.Id}/resolve",
            new DiscrepancyResolution("Supplier acknowledged the quantity difference"));
        Assert.Equal(s.Owner.Id, resolved.ResolvedBy);
        var bill = await Trade(s, s.Supplier, TradeKind.Bill, new("Stock", 2, 21, AccountId: s.Accounts[InventoryRole.InvoiceClearing]));
        using var direct = await s.Owner.Client.PostAsJsonAsync($"{s.Trade}/documents/{bill.Id}/post", new PostTradeDocumentRequest(bill.Version, s.Date, "direct", true, "Approved"));
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);
        var matchInput = Request(s, StockKind.MatchBill, 4, 42) with
        { ReceiptQuantities = [new(receipt.Id, 1), new(second.Id, 3)], TradeDocumentId = bill.Id, TradeLineId = Assert.Single(bill.Lines).Id };
        var unsegmented = await Send<TradeDocumentResponse>(s.Owner.Client, s.Trade + "/documents",
            new TradeDocumentRequest(s.Supplier, TradeKind.Bill, s.Date, null, "Two stock claims", "MULTI",
                [new("First", 2, 21, AccountId: s.Accounts[InventoryRole.InvoiceClearing]),
                 new("Second", 1, 21, AccountId: s.Accounts[InventoryRole.InvoiceClearing])]), HttpStatusCode.Created);
        unsegmented = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{unsegmented.Id}/approve",
            new DocumentTransitionRequest(unsegmented.Version, "Approved bill"));
        using var needsSplit = await s.Maker.Client.PostAsJsonAsync(s.Inventory + "/operations",
            matchInput with { Key = "needs-split", TradeDocumentId = unsegmented.Id, TradeLineId = unsegmented.Lines[0].Id });
        Assert.Equal(HttpStatusCode.BadRequest, needsSplit.StatusCode);
        Assert.Contains("Split stocked bill lines", await needsSplit.Content.ReadAsStringAsync());
        var intact = await s.Owner.Client.GetFromJsonAsync<TradeDocumentResponse>($"{s.Trade}/documents/{unsegmented.Id}");
        Assert.Equal(TradeState.Approved, intact!.FinancialState);
        Assert.Null(intact.JournalId);
        var duplicateBill = await Trade(s, s.Supplier, TradeKind.Bill,
            new("Duplicate receipt claim", 2, 21, AccountId: s.Accounts[InventoryRole.InvoiceClearing]));
        var duplicateInput = matchInput with { Key = "duplicate-match", TradeDocumentId = duplicateBill.Id,
            TradeLineId = Assert.Single(duplicateBill.Lines).Id };
        var approved = await Change(s, await Prepare(s, matchInput), "approve");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var tx = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
            var workflow = scope.ServiceProvider.GetRequiredService<StockWorkflow>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => tx.Execute<int>(s.Owner.Id, s.Owner.Org, s.Company.Id, async (_, ct) =>
            {
                await workflow.Change(s.Owner.Id, s.Owner.Org, s.Company.Id, approved.Id, "post", new(approved.Version, "Approved", true), ct);
                throw new InvalidOperationException("Injected fault after AP and inventory writes");
            }, default));
        }
        var rolledBack = await s.Owner.Client.GetFromJsonAsync<TradeDocumentResponse>($"{s.Trade}/documents/{bill.Id}");
        Assert.Equal(TradeState.Approved, rolledBack!.FinancialState);
        var match = await Change(s, approved, "post");
        using var duplicate = await s.Maker.Client.PostAsJsonAsync(s.Inventory + "/operations", duplicateInput);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var rejectedBill = await s.Owner.Client.GetFromJsonAsync<TradeDocumentResponse>($"{s.Trade}/documents/{duplicateBill.Id}");
        Assert.Equal(TradeState.Approved, rejectedBill!.FinancialState);
        Assert.Null(rejectedBill.JournalId);
        Assert.Equal(40m, match.Accrual);
        Assert.Equal(match.JournalId, (await Change(s, approved, "post")).JournalId);
        var postedBill = await s.Owner.Client.GetFromJsonAsync<TradeDocumentResponse>($"{s.Trade}/documents/{bill.Id}");
        Assert.Equal(TradeState.Posted, postedBill!.FinancialState);
        await Send<PaymentResponse>(s.Owner.Client, s.Trade + "/payments", new PaymentRequest(s.Supplier, s.Cash, 42, s.Date,
            "Supplier settlement", "payment", AllowBackdated: true, Reason: "Approved", Allocations: [new(bill.Id, 42)]));
        var settled = await s.Owner.Client.GetFromJsonAsync<TradeDocumentResponse>($"{s.Trade}/documents/{bill.Id}");
        Assert.Equal(0m, settled!.Outstanding);
        var report = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={s.Date:yyyy-MM-dd}");
        Assert.True(report.GetProperty("agrees").GetBoolean());
        Assert.Equal(2m, report.GetProperty("priceVariance").GetDecimal());
        Assert.Equal(20m, report.GetProperty("receipts").EnumerateArray().Sum(x => x.GetProperty("grni").GetDecimal()));
    }

    [Fact]
    public async Task IssueReleasesAllowanceAndLinksExistingInvoiceWithoutRepostingRevenue()
    {
        using var s = await Arrange(ValuationMethod.MovingAverage);
        await Post(s, Request(s, StockKind.Opening, 10, 80));
        await Post(s, Request(s, StockKind.WriteDown, 0, 60));
        var invoice = await Trade(s, s.Customer, TradeKind.Invoice, new("Stock", 6, 30, StockItemId: s.Item.Id));
        invoice = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{invoice.Id}/post",
            new PostTradeDocumentRequest(invoice.Version, s.Date, "revenue", true, "Approved"));
        var issue = await Post(s, Request(s, StockKind.Issue, 4) with { TradeDocumentId = invoice.Id, TradeLineId = Assert.Single(invoice.Lines).Id });
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.Equal(1, await db.Journals.CountAsync(x => x.CompanyId == s.Company.Id && x.SourceModule == "AR.Invoice"));
        var line = await db.StockMovements.SingleAsync(x => x.OperationId == issue.Id);
        Assert.Equal((-4m, -32m, -8m), (line.Quantity, line.Gross, line.Allowance));
        var credit = await Trade(s, s.Customer, TradeKind.CustomerCredit, new("Returned stock", 1, 30,
            OriginalLineId: Assert.Single(invoice.Lines).Id), invoice.Id);
        var returned = await Post(s, Request(s, StockKind.CustomerReturn, 1) with { RelatedOperationId = issue.Id,
            TradeDocumentId = credit.Id, TradeLineId = Assert.Single(credit.Lines).Id });
        var restored = await db.StockMovements.SingleAsync(x => x.OperationId == returned.Id);
        Assert.Equal((1m, 8m, 2m), (restored.Quantity, restored.Gross, restored.Allowance));
        var report = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={s.Date:yyyy-MM-dd}");
        var fulfillment = Assert.Single(report.GetProperty("fulfillment").EnumerateArray());
        Assert.Equal(2m, fulfillment.GetProperty("remaining").GetDecimal());
        Assert.Equal(1m, fulfillment.GetProperty("returned").GetDecimal());
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var update = new NpgsqlCommand("""UPDATE inventory."StockMovements" SET "Quantity"=0 WHERE "Id"=@id""", connection);
        update.Parameters.AddWithValue("id", line.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task TransferRevalidatesApprovedPlanAndBlocksCrossCompanyWarehouse()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        using var other = await Arrange(ValuationMethod.Fifo);
        await Post(s, Request(s, StockKind.Opening, 10, 100));
        var warehouse = await Send<Warehouse>(s.Owner.Client, s.Inventory + "/warehouses", new WarehouseRequest("NORTH", "North", "Branch", s.Maker.Id));
        var transfer = Request(s, StockKind.Transfer, 6) with { DestinationId = warehouse.Id };
        using var outside = await s.Maker.Client.PostAsJsonAsync(s.Inventory + "/operations", transfer with { DestinationId = other.Warehouse.Id });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        var approved = await Change(s, await Prepare(s, transfer), "approve");
        await Post(s, Request(s, StockKind.Reserve, 5));
        using var conflict = await s.Owner.Client.PostAsJsonAsync($"{s.Inventory}/operations/{approved.Id}/post",
            new InventoryAction(approved.Version, "Approved", true));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var report = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={s.Date:yyyy-MM-dd}");
        Assert.True(report.GetProperty("agrees").GetBoolean());
        Assert.Equal(5m, report.GetProperty("balances")[0].GetProperty("balance").GetProperty("available").GetDecimal());
    }

    [Fact]
    public async Task SimultaneousTransfersConserveCostAndOnlyOneCanDepleteStock()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        await Post(s, Request(s, StockKind.Opening, 10, 100));
        var destination = await Send<Warehouse>(s.Owner.Client, s.Inventory + "/warehouses",
            new WarehouseRequest("DEST", "Destination", "Branch", s.Maker.Id));
        var request = Request(s, StockKind.Transfer, 7) with { DestinationId = destination.Id };
        var a = await Change(s, await Prepare(s, request), "approve");
        var b = await Change(s, await Prepare(s, request with { Key = "second-transfer" }), "approve");
        async Task<HttpStatusCode> Attempt(StockOperation operation)
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Inventory}/operations/{operation.Id}/post",
                new InventoryAction(operation.Version, "Approved", true));
            return response.StatusCode;
        }
        var results = await Task.WhenAll(Attempt(a), Attempt(b));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, results.Order().ToArray());
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var movements = await db.StockMovements.Where(x => x.CompanyId == s.Company.Id && (x.OperationId == a.Id || x.OperationId == b.Id)).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal(0, movements.Sum(x => x.Quantity));
        Assert.Equal(0, movements.Sum(x => x.Gross));
        Assert.False(await db.Journals.AnyAsync(x => x.CompanyId == s.Company.Id && x.SourceModule == "Inventory.Transfer"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LandedCostReclassifiesOnlyAvailableAndSoldProvenanceAndCannotBeClaimedTwice(bool manual)
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        var order = await Order(s);
        var receipt = await Post(s, Request(s, StockKind.Receipt, 10) with { OrderLineId = Assert.Single(order.Lines).Id });
        var invoice = await Trade(s, s.Customer, TradeKind.Invoice, new("Goods", 4, 30, StockItemId: s.Item.Id));
        invoice = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{invoice.Id}/post",
            new PostTradeDocumentRequest(invoice.Version, s.Date, "invoice", true, "Approved"));
        await Post(s, Request(s, StockKind.Issue, 4) with { TradeDocumentId = invoice.Id, TradeLineId = Assert.Single(invoice.Lines).Id });
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Guid journal;
        if (manual)
        {
            var expense = await db.Accounts.Where(x => x.CompanyId == s.Company.Id && x.Code == "5100").Select(x => x.Id).SingleAsync();
            var path = s.Root + "/accounting/journals";
            var draft = await Send<JournalResponse>(s.Owner.Client, path,
                new JournalRequest(s.Date, s.Date, "Freight", null, [new(expense, 25, 0, null), new(s.Cash, 0, 25, null)]),
                HttpStatusCode.Created);
            journal = (await Send<PostingReceipt>(s.Owner.Client, $"{path}/{draft.Id}/post",
                new PostJournalRequest("manual-freight", draft.Version, true, "Approved"))).JournalId;
        }
        else
        {
            var freight = await Trade(s, s.Supplier, TradeKind.Bill, new("Freight", 1, 25));
            freight = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{freight.Id}/post",
                new PostTradeDocumentRequest(freight.Version, s.Date, "freight", true, "Approved"));
            journal = freight.JournalId!.Value;
        }
        var source = await db.JournalLines.SingleAsync(x => x.CompanyId == s.Company.Id && x.JournalId == journal && x.Debit == 25);
        var request = Request(s, StockKind.LandedCost, 0, 25) with { LandedReceipts = [receipt.Id], SourceJournalLineId = source.Id };
        using var wrongAmount = await s.Maker.Client.PostAsJsonAsync(s.Inventory + "/operations", request with { Key = "wrong-freight", Amount = 24 });
        Assert.Equal(HttpStatusCode.BadRequest, wrongAmount.StatusCode);
        var landed = await Post(s, request);
        var plan = JsonSerializer.Deserialize<StockPlan>(landed.PlanJson!)!;
        var allocation = Assert.Single(plan.Landed!);
        Assert.Equal((15m, 10m), (allocation.Capitalized, allocation.Expensed));
        using var duplicate = await s.Maker.Client.PostAsJsonAsync(s.Inventory + "/operations", request with { Key = "duplicate-freight" });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        if (manual)
        {
            using var reversed = await s.Owner.Client.PostAsJsonAsync($"{s.Root}/accounting/journals/{journal}/reverse",
                new ReverseJournalRequest(s.Date, "Attempt to remove landed-cost basis", "freight-reversal", true));
            Assert.Equal(HttpStatusCode.Forbidden, reversed.StatusCode);
        }
        var report = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={s.Date:yyyy-MM-dd}");
        Assert.True(report.GetProperty("agrees").GetBoolean());
        Assert.Equal(75m, report.GetProperty("closingCarryingValue").GetDecimal());
        Assert.Equal(50m, report.GetProperty("costOfSales").GetDecimal());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CustomerReturnPreservesOriginalCogsAttribution(bool damaged, bool hasAllowance)
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        var root = s.Root + "/dimensions";
        var definition = await Send<DimensionDefinition>(s.Owner.Client, root + "/definitions",
            new DefinitionRequest(DimensionKind.CostCenter, "COST", "Cost center", "Fulfillment attribution"));
        var center = await Send<DimensionValue>(s.Owner.Client, root + "/values",
            new ValueRequest(definition.Id, "SALES", "Sales", "Operating", s.Date));
        var projectDefinition = await Send<DimensionDefinition>(s.Owner.Client, root + "/definitions",
            new DefinitionRequest(DimensionKind.Project, "PROJECT", "Project", "Return profitability"));
        var project = await Send<DimensionProject>(s.Maker.Client, root + "/projects", new ProjectRequest(
            new ValueRequest(projectDefinition.Id, "ORDER", "Customer order", "Contract", s.Date, ManagerId: s.Maker.Id),
            s.Customer, s.Date, s.Date.AddYears(1), "Customer order", "Signed order"));
        await Send<DimensionProjectEvent>(s.Owner.Client, $"{root}/projects/{project.Id}/state",
            new ProjectAction(1, ProjectState.Approved, s.Date, "Approved"));
        await Send<DimensionProjectEvent>(s.Owner.Client, $"{root}/projects/{project.Id}/state",
            new ProjectAction(2, ProjectState.Active, s.Date, "Active"));
        await Post(s, Request(s, StockKind.Opening, 1, 60));
        if (hasAllowance) await Post(s, Request(s, StockKind.WriteDown, 0, 40));
        var invoice = await Send<TradeDocumentResponse>(s.Owner.Client, s.Trade + "/documents",
            new TradeDocumentRequest(s.Customer, TradeKind.Invoice, s.Date, null, "Sale", null,
                [new("Stock", 1, 100, StockItemId: s.Item.Id)]), HttpStatusCode.Created);
        invoice = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{invoice.Id}/approve",
            new DocumentTransitionRequest(invoice.Version, "Approved"));
        await Send<DimensionBinding>(s.Owner.Client, $"{root}/sources/Trading/{invoice.Id}",
            new DimensionBindingRequest(invoice.Version, 0,
                [new(1, [new(definition.Id, center.Id), new(projectDefinition.Id, project.ValueId)])], "Sales attribution"));
        invoice = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{invoice.Id}/post",
            new PostTradeDocumentRequest(invoice.Version, s.Date, "sale-return", true, "Approved"));
        var issue = await Post(s, Request(s, StockKind.Issue, 1) with
            { TradeDocumentId = invoice.Id, TradeLineId = Assert.Single(invoice.Lines).Id });
        var credit = await Trade(s, s.Customer, TradeKind.CustomerCredit,
            new("Return", 1, 100, OriginalLineId: Assert.Single(invoice.Lines).Id), invoice.Id);
        var returned = await Post(s, Request(s, StockKind.CustomerReturn, 1) with
            { RelatedOperationId = issue.Id, TradeDocumentId = credit.Id, TradeLineId = Assert.Single(credit.Lines).Id, Damaged = damaged });
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var cogs = s.Accounts[InventoryRole.CostOfSales];
        var lines = await (from line in db.JournalLines join tag in db.PostedDimensions on line.Id equals tag.JournalLineId
            where line.CompanyId == s.Company.Id && line.AccountId == cogs && tag.ValueId == center.Id
            select line).ToListAsync();
        Assert.Equal(hasAllowance ? 40m : 60m, lines.Where(x => x.JournalId == issue.JournalId).Sum(x => x.Debit - x.Credit));
        Assert.Equal(hasAllowance ? 40m : 60m, Assert.Single(lines, x => x.JournalId == returned.JournalId).Credit);
        var projectLines = await (from line in db.JournalLines join tag in db.PostedDimensions on line.Id equals tag.JournalLineId
            where line.CompanyId == s.Company.Id && line.AccountId == cogs && tag.ValueId == project.ValueId
            select line).ToListAsync();
        Assert.Equal(hasAllowance ? 40m : 60m,
            Assert.Single(projectLines, x => x.JournalId == returned.JournalId).Credit);
        Assert.Equal(damaged ? 0m : 1m, await db.StockMovements.Where(x => x.OperationId == returned.Id).SumAsync(x => x.Quantity));
        if (damaged)
        {
            Assert.Equal(60m, await db.JournalLines.Where(x => x.JournalId == returned.JournalId &&
                x.AccountId == s.Accounts[InventoryRole.Loss]).SumAsync(x => x.Debit));
            Assert.True(await (from line in db.JournalLines join tag in db.PostedDimensions on line.Id equals tag.JournalLineId
                where line.JournalId == returned.JournalId && line.AccountId == s.Accounts[InventoryRole.Loss] &&
                    tag.ValueId == project.ValueId select line.Id).AnyAsync());
        }
    }

    [Fact]
    public async Task UnissuedCancellationAndCompetingIssueCannotBothPost()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        await Post(s, Request(s, StockKind.Opening, 1, 60));
        var invoice = await PostStockSale(s, 1);
        var pendingIssue = await Change(s, await Prepare(s, Request(s, StockKind.Issue, 1) with
            { TradeDocumentId = invoice.Id, TradeLineId = Assert.Single(invoice.Lines).Id }), "approve");
        var credit = await Cancellation(s, invoice, 1);
        var request = CreditPosting(s, credit);
        var attempts = await Task.WhenAll(
            s.Owner.Client.PostAsJsonAsync($"{s.Trade}/documents/{credit.Id}/post", request),
            s.Owner.Client.PostAsJsonAsync($"{s.Inventory}/operations/{pendingIssue.Id}/post",
                new InventoryAction(pendingIssue.Version, "Ship", true)));
        using var cancellation = attempts[0];
        using var issue = attempts[1];
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Conflict);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.Equal(cancellation.IsSuccessStatusCode, await db.Journals.AnyAsync(x => x.SourceDocumentId == credit.Id));
        Assert.Equal(issue.IsSuccessStatusCode, await db.StockMovements.AnyAsync(x => x.OperationId == pendingIssue.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StockedSaleCancellationPreservesFulfillmentCloseAndLedger(int issued)
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        await Post(s, Request(s, StockKind.Opening, 2, 60));
        var invoice = await PostStockSale(s, 2);
        var line = Assert.Single(invoice.Lines);
        if (issued > 0)
            await Post(s, Request(s, StockKind.Issue, issued) with { TradeDocumentId = invoice.Id, TradeLineId = line.Id });

        var credit = await Cancellation(s, invoice, Math.Max(1, 2 - issued));
        var request = CreditPosting(s, credit);
        var creditRoute = $"{s.Trade}/documents/{credit.Id}/post";
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        if (issued == 2)
        {
            using var rejected = await s.Owner.Client.PostAsJsonAsync(creditRoute, request);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.False(await db.Journals.AnyAsync(x => x.SourceDocumentId == credit.Id));
            return;
        }

        var competing = await Cancellation(s, invoice, 2 - issued);
        var requests = await Task.WhenAll(s.Owner.Client.PostAsJsonAsync(creditRoute, request),
            s.Owner.Client.PostAsJsonAsync($"{s.Trade}/documents/{competing.Id}/post", CreditPosting(s, competing)));
        using var first = requests[0];
        using var second = requests[1];
        var winner = Assert.Single(requests, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(requests, x => x.StatusCode == HttpStatusCode.Conflict);
        var posted = (await winner.Content.ReadFromJsonAsync<TradeDocumentResponse>())!;
        var replay = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{posted.Id}/post",
            posted.Id == credit.Id ? request : CreditPosting(s, competing));
        Assert.Equal(posted.JournalId, replay.JournalId);
        Assert.Equal(50m * (2 - issued), posted.Total);
        Assert.True(Assert.Single(posted.Lines).CancelUnissuedStock);
        var creditLines = await db.JournalLines.Where(x => x.JournalId == posted.JournalId).ToListAsync();
        var invoiceLines = await db.JournalLines.Where(x => x.JournalId == invoice.JournalId).ToListAsync();
        Assert.Equal(2, creditLines.Count);
        foreach (var original in invoiceLines)
        {
            var offset = Assert.Single(creditLines, x => x.AccountId == original.AccountId);
            Assert.Equal(original.Debit * (2 - issued) / 2m, offset.Credit);
            Assert.Equal(original.Credit * (2 - issued) / 2m, offset.Debit);
        }
        Assert.Equal(issued == 0 ? 1 : 2, await db.StockMovements.CountAsync(x => x.CompanyId == s.Company.Id));
        var inventory = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={s.Date:yyyy-MM-dd}");
        var fulfillment = Assert.Single(inventory.GetProperty("fulfillment").EnumerateArray());
        Assert.Equal(2 - issued, fulfillment.GetProperty("cancelled").GetDecimal());
        Assert.Equal(0, fulfillment.GetProperty("remaining").GetDecimal());
        Assert.Equal(30m * issued, inventory.GetProperty("costOfSales").GetDecimal());
        Assert.Equal(60m - 30m * issued, inventory.GetProperty("closingCarryingValue").GetDecimal());
        await ChangePeriod(s, PeriodState.Closed);
    }

    [Fact]
    public async Task UnissuedCancellationRequiresIndependentOwnerAndOpenPeriod()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        var invoice = await PostStockSale(s, 2);
        var input = new TradeDocumentRequest(s.Customer, TradeKind.CustomerCredit, s.Date, null, "Cancellation", null,
            [new("Cancelled", 1, 50, OriginalLineId: Assert.Single(invoice.Lines).Id, CancelUnissuedStock: true)], invoice.Id);
        var self = await Send<TradeDocumentResponse>(s.Owner.Client, s.Trade + "/documents", input, HttpStatusCode.Created);
        using var selfApproval = await s.Owner.Client.PostAsJsonAsync($"{s.Trade}/documents/{self.Id}/approve",
            new DocumentTransitionRequest(self.Version, "Self approval"));
        Assert.Equal(HttpStatusCode.Conflict, selfApproval.StatusCode);
        var credit = await Cancellation(s, invoice, 1);
        using var foreign = await Onboard();
        using var denied = await foreign.Client.PostAsJsonAsync($"{s.Trade}/documents/{credit.Id}/post", CreditPosting(s, credit));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        // Seed the closed-period prerequisite. The previous test verifies the real close gate.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var period = await db.Periods.SingleAsync(x => x.CompanyId == s.Company.Id && x.StartDate == s.Date);
        period.State = PeriodState.Closed;
        await db.SaveChangesAsync();
        using var closed = await s.Owner.Client.PostAsJsonAsync($"{s.Trade}/documents/{credit.Id}/post", CreditPosting(s, credit));
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        Assert.False(await db.Journals.AnyAsync(x => x.SourceDocumentId == credit.Id));
    }

    private async Task ChangePeriod(Setup s, PeriodState state)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var period = await db.Periods.SingleAsync(x => x.CompanyId == s.Company.Id && x.StartDate == s.Date);
        await Send<PeriodResponse>(s.Owner.Client, $"{s.Root}/accounting/periods/{period.Id}/state",
            new PeriodStateRequest(state, "Close", period.Version), method: HttpMethod.Put);
    }

    private static async Task<TradeDocumentResponse> PostStockSale(Setup s, int quantity)
    {
        var sale = await Trade(s, s.Customer, TradeKind.Invoice, new("Stock", quantity, 50, StockItemId: s.Item.Id));
        return await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{sale.Id}/post",
            new PostTradeDocumentRequest(sale.Version, s.Date, Guid.NewGuid().ToString("N"), true, "Posted sale"));
    }

    private static async Task<TradeDocumentResponse> Cancellation(Setup s, TradeDocumentResponse invoice, int quantity)
    {
        var draft = await Send<TradeDocumentResponse>(s.Maker.Client, s.Trade + "/documents",
            new TradeDocumentRequest(s.Customer, TradeKind.CustomerCredit, s.Date, null, "Cancellation", Guid.NewGuid().ToString("N"),
                [new("Cancelled", quantity, 50, OriginalLineId: Assert.Single(invoice.Lines).Id, CancelUnissuedStock: true)], invoice.Id),
            HttpStatusCode.Created);
        return await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{draft.Id}/approve",
            new DocumentTransitionRequest(draft.Version, "Approved cancellation"));
    }

    private static PostTradeDocumentRequest CreditPosting(Setup s, TradeDocumentResponse credit) =>
        new(credit.Version, s.Date, credit.Id.ToString("N"), true, "Apply cancellation");

    [Fact]
    public async Task FractionalCancellationRejectsHalfMicroUnitAndExactAlternativeCloses()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        var item = await Send<InventoryItem>(s.Owner.Client, s.Inventory + "/items",
            new InventoryItemRequest(s.Item.CategoryId, "FRAC", "Fraction", ItemKind.Stocked, "EA", "BOX", "PACK", 2, 1.5m, s.Supplier, null));
        await Post(s, Request(s, StockKind.Opening, 3, 60) with { ItemId = item.Id });
        var invoice = await Trade(s, s.Customer, TradeKind.Invoice, new("Fraction", 2, 50, StockItemId: item.Id));
        invoice = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{invoice.Id}/post",
            new PostTradeDocumentRequest(invoice.Version, s.Date, "fraction-sale", true, "Post"));
        var saleLine = Assert.Single(invoice.Lines);
        TradeDocumentRequest Credit(decimal quantity, string reference) => new(s.Customer, TradeKind.CustomerCredit, s.Date, null,
            "Cancellation", reference, [new("Fraction", quantity, 50, OriginalLineId: saleLine.Id, CancelUnissuedStock: true)], invoice.Id);
        // Both halfway products are rejected, regardless of which adjacent value has an even digit.
        foreach (var quantity in new[] { 0.333333m, 0.333335m })
        {
            using var rejected = await s.Maker.Client.PostAsJsonAsync(s.Trade + "/documents", Credit(quantity, "UNREPRESENTABLE"));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("stock_quantity", await rejected.Content.ReadAsStringAsync());
        }

        var draft = await Send<TradeDocumentResponse>(s.Maker.Client, s.Trade + "/documents", Credit(0.333334m, "EXACT"),
            HttpStatusCode.Created);
        var approved = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{draft.Id}/approve",
            new DocumentTransitionRequest(draft.Version, "Review"));
        var post = CreditPosting(s, approved);
        var credit = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{draft.Id}/post", post);
        var replay = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{draft.Id}/post", post);
        Assert.Equal(credit.JournalId, replay.JournalId);
        Assert.Equal(16.67m, credit.Total);

        var report = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={s.Date:yyyy-MM-dd}");
        var fulfillment = Assert.Single(report.GetProperty("fulfillment").EnumerateArray());
        Assert.Equal(0.500001m, fulfillment.GetProperty("cancelled").GetDecimal());
        Assert.Equal(2.499999m, fulfillment.GetProperty("remaining").GetDecimal());
        Assert.Equal(0m, report.GetProperty("costOfSales").GetDecimal());

        var issueRequest = Request(s, StockKind.Issue, 2.499999m) with
            { ItemId = item.Id, TradeDocumentId = invoice.Id, TradeLineId = saleLine.Id };
        var issue = await Post(s, issueRequest);
        Assert.Equal(issue.Id, (await Prepare(s, issueRequest)).Id);
        var repeatedIssue = await Send<StockOperation>(s.Owner.Client, $"{s.Inventory}/operations/{issue.Id}/post",
            new InventoryAction(issue.Version, "Retry", true));
        Assert.Equal(issue.JournalId, repeatedIssue.JournalId);
        report = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={s.Date:yyyy-MM-dd}");
        Assert.Equal(0m, Assert.Single(report.GetProperty("fulfillment").EnumerateArray()).GetProperty("remaining").GetDecimal());
        Assert.Equal(50m, report.GetProperty("costOfSales").GetDecimal());
        Assert.Equal(10m, report.GetProperty("closingCarryingValue").GetDecimal());

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var invoiceLines = await db.JournalLines.Where(x => x.JournalId == invoice.JournalId).ToListAsync();
        var creditLines = await db.JournalLines.Where(x => x.JournalId == credit.JournalId).ToListAsync();
        Assert.Equal(2, creditLines.Count);
        foreach (var original in invoiceLines)
        {
            var offset = Assert.Single(creditLines, x => x.AccountId == original.AccountId);
            Assert.Equal(original.Debit == 0 ? 0m : 16.67m, offset.Credit);
            Assert.Equal(original.Credit == 0 ? 0m : 16.67m, offset.Debit);
        }
        Assert.Equal(2, await db.StockMovements.CountAsync(x => x.CompanyId == s.Company.Id));
        Assert.Equal(1, await db.Journals.CountAsync(x => x.SourceDocumentId == credit.Id));
        Assert.Equal(1, await db.Journals.CountAsync(x => x.SourceDocumentId == issue.Id));
        await ChangePeriod(s, PeriodState.Closed);
    }

    [Fact]
    public async Task CancellationPrecisionRejectsDatabasePostingOfIncompatibleDraft()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        var item = await Send<InventoryItem>(s.Owner.Client, s.Inventory + "/items",
            new InventoryItemRequest(s.Item.CategoryId, "FRAC", "Fraction", ItemKind.Stocked, "EA", "BOX", "PACK", 2, 1.5m, s.Supplier, null));
        var invoice = await Trade(s, s.Customer, TradeKind.Invoice, new("Fraction", 2, 50, StockItemId: item.Id));
        invoice = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{invoice.Id}/post",
            new PostTradeDocumentRequest(invoice.Version, s.Date, "db-fraction-sale", true, "Post"));
        var draft = await Send<TradeDocumentResponse>(s.Maker.Client, s.Trade + "/documents",
            new TradeDocumentRequest(s.Customer, TradeKind.CustomerCredit, s.Date, null, "Cancellation", "DB-CREDIT",
                [new("Fraction", 0.333334m, 50, OriginalLineId: Assert.Single(invoice.Lines).Id, CancelUnissuedStock: true)], invoice.Id),
            HttpStatusCode.Created);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        // Both quantities have the same monetary price. Simulate a draft imported before migration 22.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE trading."DocumentLines" SET "Quantity"=0.333333 WHERE "Id"={Assert.Single(draft.Lines).Id}
            """);
        var approved = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{draft.Id}/approve",
            new DocumentTransitionRequest(draft.Version, "Review retained draft"));
        using var denied = await s.Owner.Client.PostAsJsonAsync($"{s.Trade}/documents/{draft.Id}/post", CreditPosting(s, approved));
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains("stock_quantity", await denied.Content.ReadAsStringAsync());
        Assert.False(await db.Journals.AnyAsync(x => x.SourceDocumentId == draft.Id));

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            CREATE TEMP TABLE precision_probe ("State" integer, "SourceModule" text, "CompanyId" uuid, "SourceDocumentId" uuid);
            CREATE CONSTRAINT TRIGGER precision_probe AFTER INSERT ON precision_probe DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION inventory.cancellation_precision_guard_v1();
            INSERT INTO precision_probe VALUES (1, 'AR.Credit', @company, @credit);
            """, connection, transaction);
        command.Parameters.AddWithValue("company", s.Company.Id);
        command.Parameters.AddWithValue("credit", draft.Id);
        await command.ExecuteNonQueryAsync();
        var failure = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Contains("six decimal places", failure.MessageText);
    }

    [Fact]
    public async Task DeferredStockCreditGuardRejectsMissingReturnEvidence()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        var invoice = await Trade(s, s.Customer, TradeKind.Invoice, new("Stock", 1, 100, StockItemId: s.Item.Id));
        invoice = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{invoice.Id}/post",
            new PostTradeDocumentRequest(invoice.Version, s.Date, "guard-invoice", true, "Approved"));
        var credit = await Trade(s, s.Customer, TradeKind.CustomerCredit,
            new("Credit", 1, 100, OriginalLineId: Assert.Single(invoice.Lines).Id), invoice.Id);
        using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Trade}/documents/{credit.Id}/post",
            new PostTradeDocumentRequest(credit.Version, s.Date, "guard-credit", true, "Without stock return"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var probe = new NpgsqlCommand("""
            CREATE TEMP TABLE credit_guard_probe (
                "State" integer, "SourceModule" text, "CompanyId" uuid, "SourceDocumentId" uuid,
                "PostingDate" date, "PostedBy" uuid
            );
            CREATE CONSTRAINT TRIGGER credit_guard_probe AFTER INSERT ON credit_guard_probe
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION inventory.stock_credit_guard_v1();
            INSERT INTO credit_guard_probe VALUES (1, 'AR.Credit', @company, @credit, @date, @actor);
            """, connection, transaction);
        probe.Parameters.AddWithValue("company", s.Company.Id);
        probe.Parameters.AddWithValue("credit", credit.Id);
        probe.Parameters.AddWithValue("date", s.Date);
        probe.Parameters.AddWithValue("actor", s.Owner.Id);
        await probe.ExecuteNonQueryAsync();
        var failure = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal("Stocked sales credit requires finalized return evidence", failure.MessageText);
        var retained = await s.Owner.Client.GetFromJsonAsync<TradeDocumentResponse>($"{s.Trade}/documents/{credit.Id}");
        Assert.Equal(TradeState.Approved, retained!.FinancialState);
        Assert.Null(retained.JournalId);
    }

    [Fact]
    public async Task PopulatedPreStockIntentUpgradePreservesHistoryAndRequiresAuditedOwnerClassification()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        await using var factory = new ApiFactory(postgres.GetConnectionString());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            await db.Database.MigrateAsync("20261114095147_Wp07OffboardingEvidence");
            // The current API maps the new flag, while this fixture seeds an older schema.
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE trading."DocumentLines" ADD COLUMN "CancelUnissuedStock" boolean NOT NULL DEFAULT false""");
        }
        using var s = await Arrange(ValuationMethod.Fifo, factory);
        await Post(s, Request(s, StockKind.Opening, 1, 60));
        var issuedSale = await Trade(s, s.Customer, TradeKind.Invoice, new("Historical stock", 1, 100, StockItemId: s.Item.Id));
        issuedSale = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{issuedSale.Id}/post",
            new PostTradeDocumentRequest(issuedSale.Version, s.Date, "legacy-issued", true, "Posted sale"));
        var issue = await Post(s, Request(s, StockKind.Issue, 1) with
            { TradeDocumentId = issuedSale.Id, TradeLineId = Assert.Single(issuedSale.Lines).Id });
        var serviceSale = await Trade(s, s.Customer, TradeKind.Invoice, new("Historical service", 1, 35));
        serviceSale = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{serviceSale.Id}/post",
            new PostTradeDocumentRequest(serviceSale.Version, s.Date, "legacy-service", true, "Posted service"));
        var stockWithoutIssue = await Trade(s, s.Customer, TradeKind.Invoice, new("Ambiguous sale", 1, 70, StockItemId: s.Item.Id));
        stockWithoutIssue = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{stockWithoutIssue.Id}/post",
            new PostTradeDocumentRequest(stockWithoutIssue.Version, s.Date, "legacy-ambiguous", true, "Posted sale"));
        var journalIds = new[] { issuedSale.JournalId!.Value, serviceSale.JournalId!.Value, stockWithoutIssue.JournalId!.Value,
            issue.JournalId!.Value };
        decimal before;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            before = await db.JournalLines.Where(x => journalIds.Contains(x.JournalId)).SumAsync(x => x.Debit);
            await db.Database.MigrateAsync("20261113110000_GovernedJournalCorrections");
            Assert.Equal(17, (await db.Database.GetAppliedMigrationsAsync()).Count());
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE trading."DocumentLines" DROP COLUMN "CancelUnissuedStock" """);
            await db.Database.MigrateAsync();
            Assert.Equal(22, (await db.Database.GetAppliedMigrationsAsync()).Count());
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.Equal(before, await db.JournalLines.Where(x => journalIds.Contains(x.JournalId)).SumAsync(x => x.Debit));
            Assert.All(await db.TradeLines.Where(x => x.DocumentId == issuedSale.Id || x.DocumentId == serviceSale.Id ||
                x.DocumentId == stockWithoutIssue.Id).ToListAsync(), x =>
                { Assert.Null(x.StockItemId); Assert.Null(x.StockSalesFactor); });
            Assert.Equal(issuedSale.Id, (await db.StockOperations.SingleAsync(x => x.Id == issue.Id)).TradeDocumentId);
            Assert.True(await db.StockMovements.AnyAsync(x => x.OperationId == issue.Id));
        }
        var issuedLine = Assert.Single(issuedSale.Lines).Id;
        var serviceLine = Assert.Single(serviceSale.Lines).Id;
        var ambiguousLine = Assert.Single(stockWithoutIssue.Lines).Id;
        var route = $"{s.Inventory}/legacy-sales-lines";
        using (var unauthorized = await s.Maker.Client.PostAsJsonAsync($"{route}/{serviceLine}/confirm-non-stock",
            new LegacySaleClassification("Service contract")))
            Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        using (var issueDenied = await s.Owner.Client.PostAsJsonAsync($"{route}/{issuedLine}/confirm-non-stock",
            new LegacySaleClassification("Incorrectly call stock a service")))
            Assert.Equal(HttpStatusCode.Conflict, issueDenied.StatusCode);
        await Send<Guid>(s.Owner.Client, $"{route}/{serviceLine}/confirm-non-stock",
            new LegacySaleClassification("Signed historical service contract"));
        await Send<Guid>(s.Owner.Client, $"{route}/{serviceLine}/confirm-non-stock",
            new LegacySaleClassification("Signed historical service contract"));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            var audit = Assert.Single(await db.AccountingAudit.Where(x => x.CompanyId == s.Company.Id &&
                x.EntityId == serviceLine && x.Action == "LegacyInvoiceLineConfirmedNonStock").ToListAsync());
            Assert.Equal(s.Owner.Id, audit.ActorId);
            Assert.Equal("Signed historical service contract", audit.Reason);
            Assert.Equal(before, await db.JournalLines.Where(x => journalIds.Contains(x.JournalId)).SumAsync(x => x.Debit));
            var period = await db.Periods.SingleAsync(x => x.CompanyId == s.Company.Id && x.StartDate == s.Date);
            var report = scope.ServiceProvider.GetRequiredService<InventoryReporting>();
            var check = await report.Check(s.Company.Id, period, default);
            Assert.Contains(check.Findings, x => x.Rule == "inventory-unclassified-sale" && x.SourceId == ambiguousLine);
            Assert.DoesNotContain(check.Findings, x => x.Rule == "inventory-unclassified-sale" && x.SourceId == serviceLine);
            Assert.DoesNotContain(check.Findings, x => x.Rule == "inventory-fulfillment-cutoff" && x.SourceId == issuedLine);
        }
    }

    [Fact]
    public async Task CompetingCustomerReturnsConsumeOneIssueOnce()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        await Post(s, Request(s, StockKind.Opening, 1, 60));
        var sale = await Trade(s, s.Customer, TradeKind.Invoice, new("Stock", 1, 100, StockItemId: s.Item.Id));
        sale = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{sale.Id}/post",
            new PostTradeDocumentRequest(sale.Version, s.Date, "race-sale", true, "Approved"));
        var issue = await Post(s, Request(s, StockKind.Issue, 1) with
            { TradeDocumentId = sale.Id, TradeLineId = Assert.Single(sale.Lines).Id });
        async Task<StockOperation> Candidate(string key)
        {
            var credit = await Trade(s, s.Customer, TradeKind.CustomerCredit,
                new("Return", 1, 100, OriginalLineId: Assert.Single(sale.Lines).Id), sale.Id);
            var draft = await Prepare(s, Request(s, StockKind.CustomerReturn, 1) with
            { Key = key, RelatedOperationId = issue.Id, TradeDocumentId = credit.Id, TradeLineId = Assert.Single(credit.Lines).Id });
            return await Change(s, draft, "approve");
        }
        var first = await Candidate("return-a");
        var second = await Candidate("return-b");
        async Task<HttpStatusCode> Attempt(StockOperation row)
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Inventory}/operations/{row.Id}/post",
                new InventoryAction(row.Version, "Return stock", true));
            return response.StatusCode;
        }
        var outcomes = await Task.WhenAll(Attempt(first), Attempt(second));
        Assert.Single(outcomes, x => x == HttpStatusCode.OK);
        Assert.Single(outcomes, x => x == HttpStatusCode.Conflict);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var posted = await db.StockOperations.Where(x => (x.Id == first.Id || x.Id == second.Id) &&
            x.State == StockState.Posted).ToListAsync();
        Assert.Single(posted);
        Assert.Equal(1m, await db.StockMovements.Where(x => x.OperationId == posted[0].Id).SumAsync(x => x.Quantity));
        Assert.Equal(60m, await db.JournalLines.Where(x => x.JournalId == posted[0].JournalId &&
            x.AccountId == s.Accounts[InventoryRole.CostOfSales]).SumAsync(x => x.Credit));
        Assert.Equal(1, await db.Journals.CountAsync(x => x.CompanyId == s.Company.Id && x.SourceModule == "AR.Credit"));
    }

    [Fact]
    public async Task CompetingLandedCostClaimsCannotDoubleCapitalizeOneExpense()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        var order = await Order(s);
        var receipt = await Post(s, Request(s, StockKind.Receipt, 10) with { OrderLineId = Assert.Single(order.Lines).Id });
        var freight = await Trade(s, s.Supplier, TradeKind.Bill, new("Freight", 1, 25));
        freight = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{freight.Id}/post",
            new PostTradeDocumentRequest(freight.Version, s.Date, "race-freight", true, "Approved"));
        Guid source;
        using (var scope = fixture.Factory.Services.CreateScope())
            source = (await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().JournalLines.SingleAsync(x =>
                x.JournalId == freight.JournalId && x.Debit == 25)).Id;
        var request = Request(s, StockKind.LandedCost, 0, 25) with
        { LandedReceipts = [receipt.Id], SourceJournalLineId = source };
        var first = await Change(s, await Prepare(s, request with { Key = "landed-a" }), "approve");
        var second = await Change(s, await Prepare(s, request with { Key = "landed-b" }), "approve");
        async Task<HttpStatusCode> Attempt(StockOperation row)
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Inventory}/operations/{row.Id}/post",
                new InventoryAction(row.Version, "Capitalize freight", true));
            return response.StatusCode;
        }
        var outcomes = await Task.WhenAll(Attempt(first), Attempt(second));
        Assert.Single(outcomes, x => x == HttpStatusCode.OK);
        Assert.Single(outcomes, x => x is HttpStatusCode.BadRequest or HttpStatusCode.Conflict);
        using var scope2 = fixture.Factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var posted = Assert.Single(await db.StockOperations.Where(x => x.SourceJournalLineId == source &&
            x.State == StockState.Posted).ToListAsync());
        Assert.Equal(25m, await db.JournalLines.Where(x => x.JournalId == posted.JournalId &&
            x.AccountId == s.Accounts[InventoryRole.Inventory]).SumAsync(x => x.Debit));
        Assert.Equal(25m, await db.StockMovements.Where(x => x.OperationId == posted.Id).SumAsync(x => x.Gross));
    }

    [Fact]
    public async Task CloseAndFinalFulfillmentRaceCannotCloseWithMissingCost()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        await Post(s, Request(s, StockKind.Opening, 1, 60));
        var sale = await Trade(s, s.Customer, TradeKind.Invoice, new("Stock", 1, 100, StockItemId: s.Item.Id));
        sale = await Send<TradeDocumentResponse>(s.Owner.Client, $"{s.Trade}/documents/{sale.Id}/post",
            new PostTradeDocumentRequest(sale.Version, s.Date, "close-race", true, "Approved"));
        var candidate = await Change(s, await Prepare(s, Request(s, StockKind.Issue, 1) with
            { TradeDocumentId = sale.Id, TradeLineId = Assert.Single(sale.Lines).Id }), "approve");
        Guid periodId;
        using (var scope = fixture.Factory.Services.CreateScope())
            periodId = (await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().Periods.SingleAsync(x =>
                x.CompanyId == s.Company.Id && x.StartDate == s.Date)).Id;
        var root = $"{s.Root}/financial-close";
        var view = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Owner.Client,
            $"{root}/periods/{periodId}/sessions", new { });
        var route = $"{root}/sessions/{view.Session.Id}";
        view = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Owner.Client,
            $"{route}/tasks/{Assert.Single(view.Session.Tasks).Id}/complete",
            new RAQM.Api.DTOs.FinancialClose.CloseAction(view.Session.Version, "Reviewed"));
        using (var blocked = await s.Owner.Client.PostAsJsonAsync($"{route}/state/approve",
            new RAQM.Api.DTOs.FinancialClose.CloseAction(view.Session.Version, "Before fulfillment")))
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var closeAction = new RAQM.Api.DTOs.FinancialClose.CloseAction(view.Session.Version, "Concurrently reviewed");
        var responses = await Task.WhenAll(
            s.Owner.Client.PostAsJsonAsync($"{route}/state/approve", closeAction),
            s.Owner.Client.PostAsJsonAsync($"{s.Inventory}/operations/{candidate.Id}/post",
                new InventoryAction(candidate.Version, "Fulfill sale", true)));
        using var close = responses[0];
        using var issue = responses[1];
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        Assert.Contains(close.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.Equal(60m, await db.JournalLines.Where(x => x.CompanyId == s.Company.Id &&
                x.AccountId == s.Accounts[InventoryRole.CostOfSales] && x.Debit > 0).SumAsync(x => x.Debit));
            Assert.Equal(1, await db.StockOperations.CountAsync(x => x.Id == candidate.Id && x.State == StockState.Posted));
        }
        if (close.StatusCode == HttpStatusCode.Conflict)
            view = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Owner.Client,
                $"{route}/state/approve", closeAction);
        else
            view = (await close.Content.ReadFromJsonAsync<RAQM.Api.DTOs.FinancialClose.CloseView>())!;
        view = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Owner.Client, $"{route}/state/close",
            new RAQM.Api.DTOs.FinancialClose.CloseAction(view.Session.Version, "Complete"));
        Assert.Equal(PeriodState.Closed, view.AccountingState);
    }

    [Fact]
    public async Task CountShortageAndNrvRecoveryReconcileAndReadinessDetectsPendingCount()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        await Post(s, Request(s, StockKind.Opening, 10, 100));
        await Post(s, Request(s, StockKind.WriteDown, 0, 80));
        var recovery = await Post(s, Request(s, StockKind.WriteDown, 0, 110));
        Assert.Equal(-20m, Assert.Single(JsonSerializer.Deserialize<StockPlan>(recovery.PlanJson!)!.Movements).Allowance);
        var draft = await Prepare(s, Request(s, StockKind.Count, 7));
        Assert.Equal(10m, draft.BookQuantity);
        var approved = await Change(s, draft, "approve");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            var period = await db.Periods.SingleAsync(x => x.CompanyId == s.Company.Id && x.StartDate == s.Date);
            var report = scope.ServiceProvider.GetRequiredService<InventoryReporting>();
            var check = await report.Check(s.Company.Id, period, default);
            Assert.Contains(check.Findings, x => x.Rule == "inventory-pending" && x.SourceId == approved.Id &&
                x.Severity == RAQM.Api.DTOs.FinancialClose.FindingSeverity.Blocking);
        }
        var count = await Change(s, approved, "post");
        var plan = JsonSerializer.Deserialize<StockPlan>(count.PlanJson!)!;
        Assert.Equal(30m, plan.Lines.Single(x => x.AccountId == s.Accounts[InventoryRole.Loss]).Debit);
        var final = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={s.Date:yyyy-MM-dd}");
        Assert.True(final.GetProperty("agrees").GetBoolean());
        Assert.Equal(70m, final.GetProperty("closingCarryingValue").GetDecimal());
    }

    [Fact]
    public async Task SupplierReturnBeforeInvoiceReleasesReceiptAccrualAndAllowsReplacementDelivery()
    {
        using var s = await Arrange(ValuationMethod.Fifo);
        var order = await Order(s);
        var request = Request(s, StockKind.Receipt, 10) with { OrderLineId = Assert.Single(order.Lines).Id };
        var receipt = await Post(s, request);
        var returned = await Post(s, Request(s, StockKind.SupplierReturn, 4) with { RelatedOperationId = receipt.Id });
        Assert.Equal(40m, returned.Accrual);
        var replacement = await Post(s, request with { Key = "replacement", Quantity = 4 });
        Assert.Equal(40m, replacement.Accrual);
        var report = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Inventory}/report?at={s.Date:yyyy-MM-dd}");
        Assert.True(report.GetProperty("agrees").GetBoolean());
        Assert.Equal(100m, report.GetProperty("receipts").EnumerateArray().Sum(x => x.GetProperty("grni").GetDecimal()));
    }
}
