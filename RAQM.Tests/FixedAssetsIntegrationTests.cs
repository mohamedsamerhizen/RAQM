using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Entities.YearEnd;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.FixedAssets;
using Xunit;

namespace RAQM.Tests;

[Collection("PostgreSQL"), Trait("Category", "Integration")]
public sealed class FixedAssetsIntegrationTests(PostgreSqlFixture fixture)
{
    private sealed record Setup(HttpClient Client, Guid Actor, Guid Org, Company Company, FiscalYearResponse Year,
        Dictionary<string, AccountResponse> Accounts, AssetCategory Category) : IDisposable
    {
        public string Root => $"/api/v1/organizations/{Org}/companies/{Company.Id}";
        public string Core => Root + "/accounting";
        public string Assets => Root + "/fixed-assets";
        public DateOnly Start => Year.StartDate;
        public PeriodResponse Period(int number) => Year.Periods.Single(x => x.Number == number);
        public void Dispose() => Client.Dispose();
    }

    private static async Task<T> Send<T>(HttpClient client, string path, object body, HttpMethod? method = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == expected, $"{path}: expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task Denied(HttpClient client, string path, object body, HttpStatusCode expected)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        Assert.True(response.StatusCode == expected, $"{path}: expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<Setup> Arrange(bool depreciable = true, decimal threshold = 0)
    {
        var client = fixture.Factory.CreateClient();
        var tokens = await Send<TokenResponse>(client, "/api/v1/auth/onboard",
            new OnboardRequest($"{Guid.NewGuid():N}@assets.example", "Asset_Test-Password1!", "Asset Org", ApiFactory.OnboardingKey));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        var org = me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid();
        var company = await Send<Company>(client, $"/api/v1/organizations/{org}/companies",
            new CompanyRequest("Fixed Asset Company", Guid.NewGuid().ToString("N"), "USD", 4, 1), expected: HttpStatusCode.Created);
        var root = $"/api/v1/organizations/{org}/companies/{company.Id}";
        var core = root + "/accounting";
        var accounts = (await Send<List<AccountResponse>>(client, core + "/accounts/initialize", new InitializeAccountsRequest())).ToDictionary(x => x.Code);
        foreach (var (code, name, type, parent, contra) in new[]
        {
            ("1200","Asset cost",AccountClass.Asset,"1000",false), ("1210","Accumulated depreciation",AccountClass.Asset,"1000",true),
            ("1220","Accumulated impairment",AccountClass.Asset,"1000",true), ("1230","Asset clearing",AccountClass.Asset,"1000",false),
            ("1300","Receivable",AccountClass.Asset,"1000",false), ("1400","Tax recoverable",AccountClass.Asset,"1000",false),
            ("1500","Supplier advances",AccountClass.Asset,"1000",false), ("2200","Acquisition liability",AccountClass.Liability,"2000",false),
            ("2250","Disposal clearing",AccountClass.Liability,"2000",false), ("2300","Tax payable",AccountClass.Liability,"2000",false),
            ("2400","Customer advances",AccountClass.Liability,"2000",false), ("3200","Retained earnings",AccountClass.Equity,"3000",false),
            ("4200","Gain on disposal",AccountClass.Revenue,"4000",false), ("5110","Impairment",AccountClass.Expense,"5000",false),
            ("5120","Loss on disposal",AccountClass.Expense,"5000",false), ("5130","Repairs",AccountClass.Expense,"5000",false)
        })
            accounts[code] = await Send<AccountResponse>(client, core + "/accounts", new AccountRequest(code, name, null, type,
                true, accounts[parent].Id, IsContra: contra), expected: HttpStatusCode.Created);
        var year = await Send<FiscalYearResponse>(client, core + "/fiscal-years",
            new FiscalYearRequest(DateTime.UtcNow.Year - 2, "Asset fiscal year"), expected: HttpStatusCode.Created);
        var category = await Send<AssetCategory>(client, root + "/fixed-assets/categories", Category(accounts, depreciable, threshold));
        return new(client, me.GetProperty("userId").GetGuid(), org, company, year, accounts, category);
    }

    private static CategoryRequest Category(Dictionary<string, AccountResponse> a, bool depreciable, decimal threshold) =>
        new("PPE", "Property", depreciable, true, threshold, a["1200"].Id, a["1210"].Id, a["5100"].Id, a["5110"].Id,
            a["1220"].Id, a["4200"].Id, a["5120"].Id, a["1230"].Id);
    private static Task<FixedAsset> Register(Setup s, string code, IReadOnlyList<ComponentRequest> components, Guid? supplier = null,
        DateOnly? acquisition = null) => Send<FixedAsset>(s.Client, s.Assets + "/register",
        new RegisterAssetRequest(s.Category.Id, code, code, "Equipment acquisition", acquisition ?? s.Start, supplier,
            "Verified procurement", new("HQ", "North", "Operations", "CC1", s.Actor), components));
    private static Task<AssetOperation> Prepare(Setup s, AssetOperationRequest request) => Send<AssetOperation>(s.Client, s.Assets + "/operations", request);
    private static Task<AssetOperation> Change(Setup s, AssetOperation operation, string action) =>
        Send<AssetOperation>(s.Client, $"{s.Assets}/operations/{operation.Id}/{action}", new AssetAction(operation.Version, "Owner approved asset evidence", true));
    private static async Task<AssetOperation> Execute(Setup s, AssetOperationRequest request) =>
        await Change(s, await Change(s, await Prepare(s, request), "approve"), "post");
    private static AssetOperationRequest Acquisition(Setup s, FixedAsset asset, decimal amount) =>
        new(AssetOperationKind.Acquisition, s.Start, $"acq-{asset.Code}", "Capitalize", "Signed purchase evidence",
            asset.Id, Funding: CapitalFunding.Direct, Amount: amount, OffsetAccountId: s.Accounts["2200"].Id);
    private static Task<AssetOperation> Service(Setup s, FixedAsset asset, DateOnly date) =>
        Execute(s, new(AssetOperationKind.AvailableForUse, date, $"use-{asset.Code}", "Ready", "Commissioning checklist", asset.Id));
    private static AssetOperationRequest Run(Setup s, int month) =>
        new(AssetOperationKind.Depreciation, s.Period(month).EndDate, $"dep-{month}", "Fiscal run", "Reviewed asset register", PeriodId: s.Period(month).Id);
    private static async Task<AssetPosition> Position(Setup s, string code, DateOnly at) =>
        Assert.Single((await s.Client.GetFromJsonAsync<PageResult<AssetPosition>>($"{s.Assets}/register?at={at:yyyy-MM-dd}&search={code}"))!.Items);
    private static async Task<AssetPeriodReport> Report(Setup s, int month) =>
        (await s.Client.GetFromJsonAsync<AssetPeriodReport>($"{s.Assets}/report?period={s.Period(month).Id}"))!;
    private static Task<TreasuryAccountResponse> Cash(Setup s) => Send<TreasuryAccountResponse>(s.Client, s.Root + "/banking/accounts",
        new TreasuryAccountRequest(TreasuryAccountKind.Cash, "CASH", "Cash", s.Accounts["1100"].Id));

    private async Task<List<JournalLine>> Lines(Guid journal)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().JournalLines.AsNoTracking().Where(x => x.JournalId == journal).ToListAsync();
    }
    private async Task<int> Posted(Setup s)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().Journals.CountAsync(x => x.CompanyId == s.Company.Id && x.State == JournalState.Posted);
    }

    [Fact]
    public async Task CategoryScopeAndPostingAccountsAreValidatedAndManagerCannotApprove()
    {
        using var s = await Arrange();
        using var foreign = await Arrange();
        await Denied(s.Client, s.Assets + "/categories", Category(s.Accounts, true, 0) with
        { Code = "INVALID", CostId = foreign.Accounts["1200"].Id, AccumulatedDepreciationId = s.Accounts["1100"].Id,
            AccumulatedImpairmentId = s.Accounts["1400"].Id, ClearingId = null }, HttpStatusCode.BadRequest);
        await Denied(s.Client, s.Assets + "/categories", Category(s.Accounts, true, 0) with
        { Code = "BAD-SIDE", CostId = s.Accounts["1300"].Id, AccumulatedDepreciationId = s.Accounts["1100"].Id,
            AccumulatedImpairmentId = s.Accounts["1400"].Id, ClearingId = null }, HttpStatusCode.BadRequest);
        var asset = await Register(s, "ASSET-1", [new("Machine", 1200, 0, 12)]);
        await Denied(foreign.Client, s.Assets + "/operations", Acquisition(s, asset, 1200), HttpStatusCode.NotFound);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.OrganizationMembers.Add(new() { TenantId = s.Company.TenantId, OrganizationId = s.Org, UserId = foreign.Actor, Role = OrganizationRole.Member });
            db.CompanyMembers.Add(new() { TenantId = s.Company.TenantId, OrganizationId = s.Org, CompanyId = s.Company.Id,
                UserId = foreign.Actor, Role = CompanyRole.Manager });
            await db.SaveChangesAsync();
        }
        var draft = await Send<AssetOperation>(foreign.Client, s.Assets + "/operations", Acquisition(s, asset, 1200));
        await Denied(foreign.Client, $"{s.Assets}/operations/{draft.Id}/approve", new AssetAction(draft.Version, "Unauthorized", true), HttpStatusCode.Forbidden);
        var posted = await Change(s, await Change(s, draft, "approve"), "post");
        Assert.NotNull(posted.JournalId);
        var audit = (await s.Client.GetFromJsonAsync<List<AuditResponse>>($"{s.Assets}/audit/{asset.Id}"))!;
        Assert.Contains(audit, x => x.Action == "FixedAsset:Acquisition" && x.ActorId == s.Actor);
    }

    [Fact]
    public async Task PartialComponentRunIsAtomicUnderFaultAndConcurrentRetries()
    {
        using var s = await Arrange();
        var asset = await Register(s, "COMPONENTS", [new("Motor", 24000, 0, 48), new("Control", 1200, 0, 12)]);
        var acquisition = await Execute(s, Acquisition(s, asset, 25200));
        Assert.Equal(25200, (await Lines(acquisition.JournalId!.Value)).Single(x => x.AccountId == s.Accounts["1200"].Id).Debit);
        Assert.Equal(acquisition.Id, (await Prepare(s, Acquisition(s, asset, 25200))).Id);
        await Denied(s.Client, s.Assets + "/operations", Acquisition(s, asset, 25200) with { Key = "different" }, HttpStatusCode.Conflict);
        Assert.Equal(AssetStatus.Capitalized, (await Position(s, asset.Code, s.Start)).Status);
        await Service(s, asset, s.Start.AddDays(15));
        var draft = await Prepare(s, Run(s, 1));
        var preview = (await s.Client.GetFromJsonAsync<AssetPlan>($"{s.Assets}/operations/{draft.Id}/preview"))!;
        Assert.Equal(300, preview.Movements.Sum(x => x.DepreciationCharge));
        var approved = await Change(s, draft, "approve");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var tx = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
            var workflow = scope.ServiceProvider.GetRequiredService<IAssetWorkflow>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => tx.Execute<int>(s.Actor, s.Org, s.Company.Id, async (_, ct) =>
            {
                await workflow.Change(s.Actor, s.Org, s.Company.Id, approved.Id, "post", new(approved.Version, "Approved", true), ct);
                throw new InvalidOperationException("Injected fault after asset and GL updates");
            }, default));
        }
        Assert.Equal(1, await Posted(s));
        Assert.Equal(0, (await Position(s, asset.Code, s.Period(1).EndDate)).Depreciation);
        var results = await Task.WhenAll(Change(s, approved, "post"), Change(s, approved, "post"));
        Assert.NotNull(results[0].JournalId);
        Assert.Equal(results[0].JournalId, results[1].JournalId);
        Assert.Equal(2, await Posted(s));
        var lines = await Lines(results[0].JournalId!.Value);
        Assert.Equal(300, lines.Single(x => x.AccountId == s.Accounts["5100"].Id).Debit);
        Assert.Equal(300, lines.Single(x => x.AccountId == s.Accounts["1210"].Id).Credit);
        Assert.Equal(24900, (await Position(s, asset.Code, s.Period(1).EndDate)).NetBookValue);
        Assert.All((await Report(s, 1)).Reconciliation, x => Assert.Equal(0, x.Difference));
    }

    [Theory]
    [InlineData(CapitalFunding.ExistingCost)]
    [InlineData(CapitalFunding.ClearBill)]
    public async Task PostedBillNetCostIsClaimedOnceWithoutRepeatingPayableOrRecoverableTax(CapitalFunding funding)
    {
        using var s = await Arrange();
        var a = s.Accounts;
        var trading = s.Root + "/trading";
        await Send<List<ControlAccountResponse>>(s.Client, trading + "/configuration", new ControlAccountRequest(a["1300"].Id, a["2100"].Id,
            a["4100"].Id, a["5100"].Id, a["2300"].Id, a["1400"].Id, a["2400"].Id, a["1500"].Id, a["3100"].Id,
            [a["1100"].Id], false, "Trading controls"), HttpMethod.Put);
        var supplier = await Send<PartyResponse>(s.Client, trading + "/parties", new PartyRequest(PartyKind.Supplier, "SUP", "Supplier", null,
            null, null, null, 30, null, a["2100"].Id, a["5100"].Id), expected: HttpStatusCode.Created);
        var tax = await Send<TaxCodeResponse>(s.Client, trading + "/tax-codes", new TaxCodeRequest("VAT", "Input tax", a["2300"].Id, a["1400"].Id),
            expected: HttpStatusCode.Created);
        var rate = await Send<TaxRateResponse>(s.Client, $"{trading}/tax-codes/{tax.Id}/rates", new TaxRateRequest(s.Start, s.Year.EndDate, 10),
            expected: HttpStatusCode.Created);
        var target = a[funding == CapitalFunding.ExistingCost ? "1200" : "1230"].Id;
        var bill = await Send<TradeDocumentResponse>(s.Client, trading + "/documents", new TradeDocumentRequest(supplier.Id, TradeKind.Bill,
            s.Start, null, "Asset bill", "SUP-1", [new("Equipment", 1, 1000, 10, target, rate.Id),
                new("Maintenance", 1, 50)]), expected: HttpStatusCode.Created);
        bill = await Send<TradeDocumentResponse>(s.Client, $"{trading}/documents/{bill.Id}/approve", new DocumentTransitionRequest(bill.Version, "Approved bill"));
        bill = await Send<TradeDocumentResponse>(s.Client, $"{trading}/documents/{bill.Id}/post",
            new PostTradeDocumentRequest(bill.Version, s.Start, "post-bill", true, "Approved historical bill"));
        var billLines = await Lines(bill.JournalId!.Value);
        Assert.Equal(1040, bill.Total);
        Assert.Equal(90, billLines.Single(x => x.AccountId == a["1400"].Id).Debit);
        Assert.Equal(1040, billLines.Single(x => x.AccountId == a["2100"].Id).Credit);
        var source = billLines.Single(x => x.AccountId == target).Id;
        var asset = await Register(s, "BILL-ASSET", [new("Unit", 900, 0, 12)], supplier.Id);
        var request = Acquisition(s, asset, 900) with { Funding = funding, OffsetAccountId = null, SourceLines = [source] };
        var recognized = await Execute(s, request);
        if (funding == CapitalFunding.ExistingCost)
        {
            Assert.Null(recognized.JournalId);
            Assert.Equal(1, await Posted(s));
        }
        else
        {
            var move = await Lines(recognized.JournalId!.Value);
            Assert.Equal(900, move.Single(x => x.AccountId == a["1200"].Id).Debit);
            Assert.Equal(900, move.Single(x => x.AccountId == a["1230"].Id).Credit);
            Assert.Equal(2, await Posted(s));
        }
        var duplicate = await Register(s, "DUP-ASSET", [new("Duplicate", 900, 0, 12)], supplier.Id);
        await Denied(s.Client, s.Assets + "/operations", request with { AssetId = duplicate.Id, Key = "reuse-bill" }, HttpStatusCode.Conflict);
        async Task<TradeDocumentResponse> ApproveCredit(TradeLineResponse line)
        {
            var draft = await Send<TradeDocumentResponse>(s.Client, trading + "/documents", new TradeDocumentRequest(supplier.Id,
                TradeKind.SupplierCredit, s.Start, null, "Supplier credit", null, [new("Credit", 1, 0, OriginalLineId: line.Id)], bill.Id),
                expected: HttpStatusCode.Created);
            return await Send<TradeDocumentResponse>(s.Client, $"{trading}/documents/{draft.Id}/approve",
                new DocumentTransitionRequest(draft.Version, "Approved"));
        }
        var unclaimed = await ApproveCredit(bill.Lines.Single(x => x.Description == "Maintenance"));
        var postedCredit = await Send<TradeDocumentResponse>(s.Client, $"{trading}/documents/{unclaimed.Id}/post",
            new PostTradeDocumentRequest(unclaimed.Version, s.Start, "maintenance-credit", true, "Unclaimed cost"));
        Assert.Equal(50m, (await Lines(postedCredit.JournalId!.Value)).Single(x => x.AccountId == a["2100"].Id).Debit);
        var claimed = await ApproveCredit(bill.Lines.Single(x => x.Description == "Equipment"));
        await Denied(s.Client, $"{trading}/documents/{claimed.Id}/post",
            new PostTradeDocumentRequest(claimed.Version, s.Start, "capitalized-credit", true, "Claimed cost"), HttpStatusCode.Conflict);
        Assert.All((await Report(s, 1)).Reconciliation, x => Assert.Equal(0, x.Difference));
    }

    [Fact]
    public async Task AssetCapitalizationAndSupplierCreditRaceHasOneFinancialWinner()
    {
        using var s = await Arrange();
        var a = s.Accounts;
        var trading = s.Root + "/trading";
        await Send<List<ControlAccountResponse>>(s.Client, trading + "/configuration", new ControlAccountRequest(a["1300"].Id,
            a["2100"].Id, a["4100"].Id, a["5100"].Id, a["2300"].Id, a["1400"].Id, a["2400"].Id,
            a["1500"].Id, a["3100"].Id, [a["1100"].Id], false, "Trading controls"), HttpMethod.Put);
        var supplier = await Send<PartyResponse>(s.Client, trading + "/parties", new PartyRequest(PartyKind.Supplier,
            "RACE-SUP", "Supplier", null, null, null, null, 30, null, a["2100"].Id, a["5100"].Id),
            expected: HttpStatusCode.Created);
        var bill = await Send<TradeDocumentResponse>(s.Client, trading + "/documents", new TradeDocumentRequest(
            supplier.Id, TradeKind.Bill, s.Start, null, "Asset purchase", "RACE-BILL",
            [new("Machine", 1, 900, AccountId: a["1200"].Id)]), expected: HttpStatusCode.Created);
        bill = await Send<TradeDocumentResponse>(s.Client, $"{trading}/documents/{bill.Id}/approve",
            new DocumentTransitionRequest(bill.Version, "Approved"));
        bill = await Send<TradeDocumentResponse>(s.Client, $"{trading}/documents/{bill.Id}/post",
            new PostTradeDocumentRequest(bill.Version, s.Start, "asset-race-bill", true, "Approved"));
        var source = (await Lines(bill.JournalId!.Value)).Single(x => x.AccountId == a["1200"].Id).Id;
        var asset = await Register(s, "RACE-ASSET", [new("Machine", 900, 0, 12)], supplier.Id);
        var acquisition = await Change(s, await Prepare(s, Acquisition(s, asset, 900) with
            { Funding = CapitalFunding.ExistingCost, OffsetAccountId = null, SourceLines = [source] }), "approve");
        var credit = await Send<TradeDocumentResponse>(s.Client, trading + "/documents", new TradeDocumentRequest(
            supplier.Id, TradeKind.SupplierCredit, s.Start, null, "Refund machine", null,
            [new("Machine credit", 1, 0, OriginalLineId: Assert.Single(bill.Lines).Id)], bill.Id),
            expected: HttpStatusCode.Created);
        credit = await Send<TradeDocumentResponse>(s.Client, $"{trading}/documents/{credit.Id}/approve",
            new DocumentTransitionRequest(credit.Version, "Approved"));
        async Task<HttpStatusCode> Capitalize()
        {
            using var response = await s.Client.PostAsJsonAsync($"{s.Assets}/operations/{acquisition.Id}/post",
                new AssetAction(acquisition.Version, "Capitalize", true));
            return response.StatusCode;
        }
        async Task<HttpStatusCode> Credit()
        {
            using var response = await s.Client.PostAsJsonAsync($"{trading}/documents/{credit.Id}/post",
                new PostTradeDocumentRequest(credit.Version, s.Start, "asset-race-credit", true, "Refund"));
            return response.StatusCode;
        }
        var outcomes = await Task.WhenAll(Capitalize(), Credit());
        Assert.Single(outcomes, x => x == HttpStatusCode.OK);
        Assert.Single(outcomes, x => x == HttpStatusCode.Conflict);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var claimed = await db.AssetOperations.AnyAsync(x => x.Id == acquisition.Id && x.State == AssetOperationState.Posted);
        var credited = await db.TradeDocuments.AnyAsync(x => x.Id == credit.Id && x.State == TradeState.Posted);
        Assert.NotEqual(claimed, credited);
        Assert.Equal(900m, (await Lines(bill.JournalId.Value)).Single(x => x.Id == source).Debit);
        Assert.Equal(1 + (credited ? 1 : 0), await Posted(s));
    }

    [Fact]
    public async Task OpeningBatchRequiresExactGlAndCreatesNoSecondOpeningJournal()
    {
        using var s = await Arrange();
        var a = s.Accounts;
        var batch = await Send<OpeningBatchResponse>(s.Client, s.Core + "/opening-balances",
            new OpeningBalanceRequest("opening", "Legacy property", s.Start,
            [new(a["1200"].Id, 10000, 0, null), new(a["1210"].Id, 0, 7000, null), new(a["1220"].Id, 0, 500, null), new(a["3100"].Id, 0, 2500, null)]),
            expected: HttpStatusCode.Created);
        var draft = (await s.Client.GetFromJsonAsync<JournalResponse>($"{s.Core}/journals/{batch.JournalId}"))!;
        await Send<PostingReceipt>(s.Client, $"{s.Core}/opening-balances/{batch.Id}/post",
            new PostJournalRequest("post-opening", draft.Version, true, "Historical conversion"));
        var first = await Register(s, "OPEN-A", [new("Equipment", 6000, 0, 10, 4000, 500)], acquisition: s.Start.AddYears(-1));
        var second = await Register(s, "OPEN-B", [new("Equipment", 4000, 0, 10, 3000)], acquisition: s.Start.AddYears(-1));
        var command = new AssetOperationRequest(AssetOperationKind.Opening, s.Start, "opening-assets", "Conversion", "Posted opening reconciliation",
            OpeningJournalId: batch.JournalId, OpeningAssets: [first.Id], HistoricalAvailableDate: s.Start.AddMonths(-6));
        await Denied(s.Client, s.Assets + "/operations", command, HttpStatusCode.Conflict);
        var opening = await Execute(s, command with { OpeningAssets = [first.Id, second.Id] });
        Assert.Null(opening.JournalId);
        Assert.Equal(1, await Posted(s));
        var report = await Report(s, 1);
        var total = Assert.Single(report.Categories);
        Assert.Equal(10000, total.Cost);
        Assert.Equal(7000, total.Depreciation);
        Assert.Equal(500, total.Impairment);
        Assert.Equal(2500, total.NetBookValue);
        Assert.All(report.Reconciliation, x => Assert.Equal(0, x.Difference));
    }

    [Fact]
    public async Task EstimateAndImprovementAreProspectiveAndTransfersAndRepairsDoNotChangeCost()
    {
        using var s = await Arrange(threshold: 100);
        await Cash(s);
        var asset = await Register(s, "IMPROVE", [new("Main unit", 1200, 0, 12)]);
        await Execute(s, Acquisition(s, asset, 1200));
        await Service(s, asset, s.Start);
        await Execute(s, Run(s, 1));
        var component = Assert.Single(asset.Components);
        var date = s.Period(2).StartDate;
        var estimate = await Execute(s, new(AssetOperationKind.Estimate, date, "estimate", "Updated usage", "Engineering report",
            asset.Id, component.Id, RemainingMonths: 7, Residual: 0));
        Assert.Null(estimate.JournalId);
        var improvement = await Execute(s, new(AssetOperationKind.Improvement, date, "improvement", "More capacity", "Commissioned upgrade",
            asset.Id, component.Id, Funding: CapitalFunding.Direct, Amount: 300, OffsetAccountId: s.Accounts["2200"].Id));
        Assert.Equal(300, (await Lines(improvement.JournalId!.Value)).Single(x => x.AccountId == s.Accounts["1200"].Id).Debit);
        await Denied(s.Client, s.Assets + "/operations", new AssetOperationRequest(AssetOperationKind.Improvement, date, "replacement", "Replacement",
            "Retirement required", asset.Id, component.Id, Funding: CapitalFunding.Direct, Amount: 300, OffsetAccountId: s.Accounts["2200"].Id,
            ReplacesExistingComponent: true), HttpStatusCode.BadRequest);
        var repair = await Execute(s, new(AssetOperationKind.Repair, date, "repair", "Maintenance", "Servicing receipt", asset.Id, Amount: 50,
            OffsetAccountId: s.Accounts["1100"].Id, RepairExpenseId: s.Accounts["5130"].Id));
        Assert.Equal(50, (await Lines(repair.JournalId!.Value)).Single(x => x.AccountId == s.Accounts["5130"].Id).Debit);
        var transfer = await Execute(s, new(AssetOperationKind.Transfer, date, "transfer", "Reassign", "Custody receipt", asset.Id,
            Assignment: new("Warehouse", "South", "Distribution", "CC2", s.Actor, PhysicalStatus.UnderMaintenance)));
        Assert.Null(transfer.JournalId);
        await Execute(s, Run(s, 2));
        var prior = await Position(s, asset.Code, s.Period(1).EndDate);
        Assert.Equal(1200, prior.Cost);
        Assert.Equal(100, prior.Depreciation);
        Assert.Equal("North", prior.Assignment.Branch);
        var actual = await Position(s, asset.Code, s.Period(2).EndDate);
        Assert.Equal(1500, actual.Cost);
        Assert.Equal(300, actual.Depreciation);
        Assert.Equal(1200, actual.NetBookValue);
        Assert.Equal("South", actual.Assignment.Branch);
        Assert.Equal(s.Actor, actual.Assignment.CustodianId);
        Assert.Equal(50, (await Report(s, 2)).Repairs);
    }

    [Fact]
    public async Task ImpairmentReducesFutureDepreciationAndRecoveryHonorsTheHypotheticalCeiling()
    {
        using var s = await Arrange();
        var asset = await Register(s, "IMPAIRED", [new("Main unit", 10000, 0, 5)]);
        var component = Assert.Single(asset.Components);
        await Execute(s, Acquisition(s, asset, 10000));
        await Service(s, asset, s.Start);
        await Execute(s, Run(s, 1));
        var impaired = await Execute(s, new(AssetOperationKind.Impairment, s.Period(2).StartDate, "impairment", "Damage",
            "Externally supported recoverable value 6000", asset.Id, component.Id, Amount: 6000));
        Assert.Equal(2000, (await Lines(impaired.JournalId!.Value)).Single(x => x.AccountId == s.Accounts["5110"].Id).Debit);
        await Execute(s, Run(s, 2));
        Assert.Equal(1500, (await Report(s, 2)).Depreciation);
        Assert.Equal(4500, (await Position(s, asset.Code, s.Period(2).EndDate)).NetBookValue);
        var recovery = await Execute(s, new(AssetOperationKind.Recovery, s.Period(3).StartDate, "recovery", "Improved evidence",
            "Externally supported recoverable value 9000", asset.Id, component.Id, Amount: 9000));
        Assert.Equal(1500, (await Lines(recovery.JournalId!.Value)).Single(x => x.AccountId == s.Accounts["1220"].Id).Debit);
        Assert.Equal(6000, (await Position(s, asset.Code, s.Period(3).StartDate)).NetBookValue);
        await Execute(s, Run(s, 3));
        Assert.Equal(2000, (await Report(s, 3)).Depreciation);
        Assert.All((await Report(s, 3)).Reconciliation, x => Assert.Equal(0, x.Difference));
    }

    [Theory]
    [InlineData(9000, true, 1000)]
    [InlineData(7000, false, -1000)]
    [InlineData(0, false, -8000)]
    public async Task DisposalClearsDepreciationAndRecognizesGainLossOrWriteOffExactlyOnce(int proceeds, bool existingReceipt, int result)
    {
        using var s = await Arrange();
        await Cash(s);
        var asset = await Register(s, "DISPOSE", [new("Equipment", 10000, 0, 5)]);
        await Execute(s, Acquisition(s, asset, 10000));
        await Service(s, asset, s.Start);
        await Execute(s, Run(s, 1));
        var date = s.Period(2).StartDate;
        Guid[] sources = [];
        if (existingReceipt)
        {
            var draft = await Send<JournalResponse>(s.Client, s.Core + "/journals", new JournalRequest(date, date, "Disposal receipt", "RECEIPT",
                [new(s.Accounts["1100"].Id, proceeds, 0, null), new(s.Accounts["2250"].Id, 0, proceeds, null)]), expected: HttpStatusCode.Created);
            var payment = await Send<PostingReceipt>(s.Client, $"{s.Core}/journals/{draft.Id}/post",
                new PostJournalRequest("received", draft.Version, true, "Evidence"));
            sources = [(await Lines(payment.JournalId)).Single(x => x.AccountId == s.Accounts["2250"].Id).Id];
        }
        var operation = new AssetOperationRequest(proceeds == 0 ? AssetOperationKind.WriteOff : AssetOperationKind.Sale, date, "dispose",
            "Retirement", "Signed sale or scrap evidence", asset.Id, Amount: proceeds,
            OffsetAccountId: proceeds == 0 || existingReceipt ? null : s.Accounts["1100"].Id, SourceLines: sources);
        var disposed = await Execute(s, operation);
        Assert.Equal(disposed.Id, (await Change(s, disposed, "post")).Id);
        await Denied(s.Client, s.Assets + "/operations", operation with { Key = "duplicate" }, HttpStatusCode.Conflict);
        var lines = await Lines(disposed.JournalId!.Value);
        Assert.Equal(10000, lines.Single(x => x.AccountId == s.Accounts["1200"].Id).Credit);
        Assert.Equal(2000, lines.Single(x => x.AccountId == s.Accounts["1210"].Id).Debit);
        if (existingReceipt)
        {
            Assert.DoesNotContain(lines, x => x.AccountId == s.Accounts["1100"].Id);
            Assert.Equal(9000, lines.Single(x => x.AccountId == s.Accounts["2250"].Id).Debit);
        }
        Assert.Equal(result, (await Report(s, 2)).DisposalGainLoss);
        var status = (await Position(s, asset.Code, date)).Status;
        Assert.Equal(proceeds == 0 ? AssetStatus.WrittenOff : AssetStatus.Disposed, status);
        Assert.Equal(0, (await Position(s, asset.Code, date)).NetBookValue);
        Assert.Null((await Execute(s, Run(s, 2))).JournalId);
        Assert.All((await Report(s, 2)).Reconciliation, x => Assert.Equal(0, x.Difference));
    }

    [Fact]
    public async Task MandatoryDepreciationBlocksCloseUntilRunAndClosedPeriodRejectsPosting()
    {
        using var s = await Arrange();
        var asset = await Register(s, "CLOSE", [new("Equipment", 1200, 0, 1)]);
        await Execute(s, Acquisition(s, asset, 1200));
        await Service(s, asset, s.Start);
        var first = s.Period(1);
        var close = s.Root + "/financial-close";
        var session = await Send<CloseView>(s.Client, $"{close}/periods/{first.Id}/sessions", new { });
        var ready = (await s.Client.GetFromJsonAsync<CloseReadiness>($"{close}/sessions/{session.Session.Id}/readiness"))!;
        Assert.Contains(ready.Findings, x => x.Rule == "fixed-asset-depreciation" && x.Severity == FindingSeverity.Blocking);
        await Execute(s, Run(s, 1));
        ready = (await s.Client.GetFromJsonAsync<CloseReadiness>($"{close}/sessions/{session.Session.Id}/readiness"))!;
        Assert.DoesNotContain(ready.Findings, x => x.Rule == "fixed-asset-depreciation");
        var second = s.Period(2);
        var approved = await Change(s, await Prepare(s, Run(s, 2)), "approve");
        await Send<PeriodResponse>(s.Client, $"{s.Core}/periods/{second.Id}/state",
            new PeriodStateRequest(PeriodState.Closed, "Closed by historical procedure", second.Version), HttpMethod.Put);
        await Denied(s.Client, $"{s.Assets}/operations/{approved.Id}/post", new AssetAction(approved.Version, "Closed", true), HttpStatusCode.Conflict);
        Assert.Equal(2, await Posted(s));
    }

    [Fact]
    public async Task NonDepreciableLandAndImpairmentFlowIntoYearEndThroughTheLedger()
    {
        using var s = await Arrange(depreciable: false);
        var a = s.Accounts;
        var land = await Register(s, "LAND", [new("Land", 10000, 0, 0)]);
        await Execute(s, Acquisition(s, land, 10000));
        await Service(s, land, s.Start);
        Assert.Null((await Execute(s, Run(s, 1))).JournalId);
        await Execute(s, new(AssetOperationKind.Impairment, s.Period(2).StartDate, "land-loss", "Recoverability declined",
            "Valuer report", land.Id, Assert.Single(land.Components).Id, Amount: 9600));
        var reports = s.Root + "/financial-reporting";
        await Send<ReportDefinition>(s.Client, reports + "/definitions", new DefinitionRequest(0, a["3200"].Id,
            [new(a["3200"].Id, StatementGroup.RetainedEarnings)], [], "Retained earnings approval"));
        foreach (var period in s.Year.Periods.OrderBy(x => x.Number).Take(11))
            await Send<PeriodResponse>(s.Client, $"{s.Core}/periods/{period.Id}/state",
                new PeriodStateRequest(PeriodState.Closed, "Reviewed", period.Version), HttpMethod.Put);
        var close = s.Root + "/financial-close";
        var session = await Send<CloseView>(s.Client, $"{close}/periods/{s.Period(12).Id}/sessions", new { });
        var task = Assert.Single(session.Session.Tasks);
        session = await Send<CloseView>(s.Client, $"{close}/sessions/{session.Session.Id}/tasks/{task.Id}/complete", new CloseAction(session.Session.Version, "Reviewed assets"));
        session = await Send<CloseView>(s.Client, $"{close}/sessions/{session.Session.Id}/state/approve", new CloseAction(session.Session.Version, "Accepted classifications"));
        var year = await Send<YearEndRecord>(s.Client, $"{close}/years/{s.Year.Id}/close",
            new YearEndRequest(session.Session.Id, session.Session.Version, "year", "Close fiscal year", true));
        Assert.Equal(-400, year.Profit);
        Assert.Equal(400, (await Lines(year.JournalId!.Value)).Single(x => x.AccountId == a["3200"].Id).Debit);
        var pnl = await Send<FinancialReport>(s.Client, reports + "/query", new ReportQuery(ReportKind.Income, FiscalYearId: s.Year.Id));
        Assert.Equal(-400, pnl.Rows.Single(x => x.Key == "period:net").Amount);
        Assert.Equal(9600, (await Position(s, land.Code, s.Year.EndDate)).NetBookValue);
    }

    [Fact]
    public async Task DatabaseRejectsChangingPostedComponentEvidence()
    {
        using var s = await Arrange();
        var asset = await Register(s, "RETAIN", [new("Equipment", 1200, 0, 12)]);
        await Execute(s, Acquisition(s, asset, 1200));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE fixed_assets.\"AssetMovements\" SET \"Cost\"=1 WHERE \"ComponentId\"=@id", connection);
        command.Parameters.AddWithValue("id", Assert.Single(asset.Components).Id);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }
}
