using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.ForeignExchange;
using Xunit;

namespace RAQM.Tests;

[Collection("PostgreSQL"), Trait("Category", "Integration")]
public sealed partial class MultiCurrencyIntegrationTests(PostgreSqlFixture fixture)
{
    private sealed record Actor(HttpClient Client, Guid Id, Guid Org) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
    private sealed record Setup(Actor Owner, Actor Maker, Company Company, FiscalYearResponse Year, Dictionary<string, Guid> Chart,
        Dictionary<FxAccountRole, Guid> Accounts, FxCurrency Currency, PartyResponse Customer, PartyResponse Supplier, Guid TaxOutput,
        Guid TaxInput) : IDisposable
    {
        public string Root => $"/api/v1/organizations/{Owner.Org}/companies/{Company.Id}";
        public string Fx => Root + "/foreign-exchange";
        public string Core => Root + "/accounting";
        public DateOnly Date => Year.StartDate;
        public void Dispose() { Owner.Dispose(); Maker.Dispose(); }
    }

    private static async Task<T> Send<T>(HttpClient client, string path, object body, HttpStatusCode status = HttpStatusCode.OK, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == status, $"{path}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Actor> Onboard()
    {
        var client = fixture.Factory.CreateClient();
        var token = await Send<TokenResponse>(client, "/api/v1/auth/onboard", new OnboardRequest($"{Guid.NewGuid():N}@fx.example",
            "Currency_Test-Password1!", "Currency Org", ApiFactory.OnboardingKey));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        return new(client, me.GetProperty("userId").GetGuid(), me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid());
    }

    private async Task<Setup> Arrange(bool required = false, int fallback = 0)
    {
        var owner = await Onboard();
        var maker = await Onboard();
        var company = await Send<Company>(owner.Client, $"/api/v1/organizations/{owner.Org}/companies",
            new CompanyRequest("Foreign Company", Guid.NewGuid().ToString("N"), "USD", 1, 1), HttpStatusCode.Created);
        using (var scope = fixture.Factory.Services.CreateScope())
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
        async Task<Guid> Add(string code, AccountClass type) => (await Send<AccountResponse>(owner.Client, root + "/accounting/accounts",
            new AccountRequest(code, code, null, type, true, chart[$"{(int)type + 1}000"]), HttpStatusCode.Created)).Id;
        var ar = await Add("TRADE-AR", AccountClass.Asset);
        var taxInput = await Add("TRADE-INPUT", AccountClass.Asset);
        var supplierAdvance = await Add("TRADE-SA", AccountClass.Asset);
        var taxOutput = await Add("TRADE-OUTPUT", AccountClass.Liability);
        var customerAdvance = await Add("TRADE-CA", AccountClass.Liability);
        await Send<List<ControlAccountResponse>>(owner.Client, root + "/trading/configuration", new ControlAccountRequest(ar, chart["2100"],
            chart["4100"], chart["5100"], taxOutput, taxInput, customerAdvance, supplierAdvance, chart["3100"], [chart["1100"]],
            false, "Trading controls"), method: HttpMethod.Put);
        var customer = await Send<PartyResponse>(owner.Client, root + "/trading/parties", new PartyRequest(PartyKind.Customer,
            "CUS", "Customer", null, null, null, null, 30, null, ar, chart["4100"]), HttpStatusCode.Created);
        var supplier = await Send<PartyResponse>(owner.Client, root + "/trading/parties", new PartyRequest(PartyKind.Supplier,
            "SUP", "Supplier", null, null, null, null, 30, null, chart["2100"], chart["5100"]), HttpStatusCode.Created);
        var accounts = new Dictionary<FxAccountRole, Guid>();
        foreach (var role in Enum.GetValues<FxAccountRole>())
        {
            var classification = role switch
            {
                FxAccountRole.Receivable or FxAccountRole.SupplierAdvance or FxAccountRole.TaxInput => AccountClass.Asset,
                FxAccountRole.Payable or FxAccountRole.CustomerAdvance or FxAccountRole.TaxOutput => AccountClass.Liability,
                FxAccountRole.RealizedGain or FxAccountRole.UnrealizedGain => AccountClass.Revenue,
                _ => AccountClass.Expense
            };
            accounts[role] = await Add($"FX-{(int)role}", classification);
        }
        chart["EUR-BANK"] = await Add("EUR-BANK", AccountClass.Asset);
        var year = await Send<FiscalYearResponse>(owner.Client, root + "/accounting/fiscal-years", new FiscalYearRequest(DateTime.UtcNow.Year - 1, "FX year"),
            HttpStatusCode.Created);
        await Send<FxConfiguration>(owner.Client, root + "/foreign-exchange/settings", new FxConfigurationRequest(fallback, required, accounts));
        var currency = await Send<FxCurrency>(owner.Client, root + "/foreign-exchange/currencies", new EnableCurrencyRequest("EUR"));
        return new(owner, maker, company, year, chart, accounts, currency, customer, supplier, taxOutput, taxInput);
    }

    private static async Task<FxRate> Rate(Setup s, DateOnly date, decimal value, FxRateType type = FxRateType.Spot, Guid? replaces = null)
    {
        var draft = await Send<FxRate>(s.Maker.Client, s.Fx + "/rates", new FxRateRequest(s.Currency.Id, type, date, value,
            "Controlled quote", "Verified external quote", replaces));
        return await Send<FxRate>(s.Owner.Client, $"{s.Fx}/rates/{draft.Id}/approve", new FxAction(draft.Version, "Verified"));
    }
    private static Task<FxOperation> Prepare(Setup s, FxRequest input) => Send<FxOperation>(s.Maker.Client, s.Fx + "/operations", input);
    private static Task<FxOperation> Change(Setup s, FxOperation row, string action) =>
        Send<FxOperation>(s.Owner.Client, $"{s.Fx}/operations/{row.Id}/{action}", new FxAction(row.Version, "Reviewed", true));
    private static async Task<FxOperation> Execute(Setup s, FxRequest input) => await Change(s, await Change(s, await Prepare(s, input), "approve"), "post");
    private static FxRequest Invoice(Setup s, bool receivable, DateOnly date, decimal amount, string key = "invoice", Guid? tax = null) =>
        new(key, receivable ? FxKind.Invoice : FxKind.Bill, date, "Commercial evidence", s.Currency.Id,
            receivable ? s.Customer.Id : s.Supplier.Id, Reference: key, Lines: [new("Ordinary services", 1, amount, TaxRateId: tax)]);
    private static FxRequest Settlement(Setup s, FxOperation invoice, decimal amount, DateOnly date, string key, Guid? bank = null) =>
        new(key, FxKind.Settlement, date, "Settlement advice", PositionId: invoice.PositionId, BankPositionId: bank,
            FunctionalCashAccountId: bank.HasValue ? null : s.Chart["1100"], ForeignAmount: amount);
    private static FxPlan Plan(FxOperation operation) => JsonSerializer.Deserialize<FxPlan>(operation.PlanJson!)!;
    private static async Task<JsonElement> Report(Setup s, DateOnly at) =>
        await s.Owner.Client.GetFromJsonAsync<JsonElement>($"{s.Fx}/report?at={at:yyyy-MM-dd}");
    private static JsonElement Position(JsonElement report, Guid id) =>
        Assert.Single(report.GetProperty("positions").EnumerateArray(), x => x.GetProperty("positionId").GetGuid() == id);
    private async Task<List<JournalLine>> Journal(FxOperation row)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().JournalLines.AsNoTracking().Where(x => x.JournalId == row.JournalId).ToListAsync();
    }

    [Fact]
    public async Task RateApprovalIsIndependentSupersessionRetainsTheHistoricalConversionAndMutationIsDenied()
    {
        using var s = await Arrange(fallback: 1);
        using var ownCurrency = await s.Owner.Client.PostAsJsonAsync(s.Fx + "/currencies", new EnableCurrencyRequest("USD"));
        Assert.Equal(HttpStatusCode.BadRequest, ownCurrency.StatusCode);
        using var missing = await s.Maker.Client.PostAsJsonAsync(s.Fx + "/operations", Invoice(s, true, s.Date, 100));
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        var proposed = await Send<FxRate>(s.Maker.Client, s.Fx + "/rates", new FxRateRequest(s.Currency.Id, FxRateType.Spot, s.Date, 1.10m, "Quote", "Evidence"));
        using var unauthorized = await s.Maker.Client.PostAsJsonAsync($"{s.Fx}/rates/{proposed.Id}/approve", new FxAction(proposed.Version, "Unauthorized"));
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        await Send<FxRate>(s.Owner.Client, $"{s.Fx}/rates/{proposed.Id}/approve", new FxAction(proposed.Version, "Approved"));
        var invoice = await Execute(s, Invoice(s, true, s.Date, 100));
        var replacement = await Rate(s, s.Date, 1.12m, replaces: proposed.Id);
        Assert.Equal(2, replacement.Revision);
        var fallback = await Execute(s, Invoice(s, true, s.Date.AddDays(1), 100, "fallback"));
        Assert.Equal(replacement.Id, Plan(fallback).AppliedRateId);
        Assert.Equal(proposed.Id, Plan(invoice).AppliedRateId);
        Assert.Equal(110m, (await Journal(invoice)).Single(x => x.AccountId == s.Accounts[FxAccountRole.Receivable]).Debit);
        using var stale = await s.Maker.Client.PostAsJsonAsync(s.Fx + "/operations", Invoice(s, true, s.Date.AddDays(2), 100, "stale"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""UPDATE foreign_exchange."FxRates" SET "ExchangeRate"=2 WHERE "Id"=@id""", connection);
        command.Parameters.AddWithValue("id", proposed.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task ReceivablePartiallySettlesRevaluesTwiceAndClosesWithoutDoubleRecognition()
    {
        using var s = await Arrange();
        await Rate(s, s.Date, 1.10m);
        var invoice = await Execute(s, Invoice(s, true, s.Date, 100));
        var firstDate = s.Date.AddDays(3);
        await Rate(s, firstDate, 1.20m);
        var first = await Execute(s, Settlement(s, invoice, 60, firstDate, "partial"));
        Assert.Equal(6m, Plan(first).Realized);
        Assert.Equal(66m, (await Journal(first)).Single(x => x.AccountId == s.Accounts[FxAccountRole.Receivable]).Credit);
        var cutOne = s.Year.Periods[0].EndDate;
        var cutTwo = s.Year.Periods[1].EndDate;
        await Rate(s, cutOne, 1.15m, FxRateType.Closing);
        await Rate(s, cutTwo, 1.25m, FxRateType.Closing);
        var revalOne = await Execute(s, new("close-one", FxKind.Revaluation, cutOne, "Closing evidence"));
        var revalTwo = await Execute(s, new("close-two", FxKind.Revaluation, cutTwo, "Closing evidence"));
        Assert.Equal(2m, Plan(revalOne).Unrealized);
        Assert.Equal(4m, Plan(revalTwo).Unrealized);
        var finalDate = cutTwo.AddDays(1);
        await Rate(s, finalDate, 1.30m);
        var final = await Execute(s, Settlement(s, invoice, 40, finalDate, "final"));
        Assert.Equal(2m, Plan(final).Realized);
        Assert.Equal(50m, (await Journal(final)).Single(x => x.AccountId == s.Accounts[FxAccountRole.Receivable]).Credit);
        Assert.Equal(final.JournalId, (await Change(s, final, "post")).JournalId);
        var historical = Position(await Report(s, cutOne), invoice.PositionId!.Value);
        Assert.Equal((40m, 46m), (historical.GetProperty("foreignAmount").GetDecimal(), historical.GetProperty("baseAmount").GetDecimal()));
        var latest = await Report(s, finalDate);
        Assert.True(latest.GetProperty("agrees").GetBoolean());
        Assert.Equal(0m, Position(latest, invoice.PositionId.Value).GetProperty("baseAmount").GetDecimal());
        Assert.Equal(14m, latest.GetProperty("companyExchangeIncome").EnumerateArray().Sum(x => x.GetProperty("signedIncome").GetDecimal()));
        var trial = await s.Owner.Client.GetFromJsonAsync<TrialBalanceResponse>($"{s.Core}/trial-balance?from={s.Date:yyyy-MM-dd}&to={finalDate:yyyy-MM-dd}");
        Assert.Equal("USD", trial!.Currency);
        Assert.Equal(trial.ClosingDebit, trial.ClosingCredit);
    }

    [Fact]
    public async Task SupplierBillRetainsTaxAndUsesRevaluedPayableInTheLossCalculation()
    {
        using var s = await Arrange();
        var path = s.Root + "/trading/tax-codes";
        var code = await Send<TaxCodeResponse>(s.Owner.Client, path, new TaxCodeRequest("VAT", "Tax", s.TaxOutput, s.TaxInput), HttpStatusCode.Created);
        var tax = await Send<TaxRateResponse>(s.Owner.Client, $"{path}/{code.Id}/rates", new TaxRateRequest(s.Date, s.Year.EndDate, 10), HttpStatusCode.Created);
        await Rate(s, s.Date, 1.10m);
        var bill = await Execute(s, Invoice(s, false, s.Date, 100, "bill", tax.Id));
        var lines = await Journal(bill);
        Assert.Equal(121m, lines.Single(x => x.AccountId == s.Accounts[FxAccountRole.Payable]).Credit);
        Assert.Equal(11m, lines.Single(x => x.AccountId == s.Accounts[FxAccountRole.TaxInput]).Debit);
        var cutoff = s.Year.Periods[0].EndDate;
        await Rate(s, cutoff, 1.20m, FxRateType.Closing);
        Assert.Equal(-11m, Plan(await Execute(s, new("revalue", FxKind.Revaluation, cutoff, "Closing evidence"))).Unrealized);
        var date = cutoff.AddDays(1);
        await Rate(s, date, 1.30m);
        var paid = await Execute(s, Settlement(s, bill, 55, date, "paid"));
        Assert.Equal(-5.50m, Plan(paid).Realized);
        Assert.Equal(66m, (await Journal(paid)).Single(x => x.AccountId == s.Accounts[FxAccountRole.Payable]).Debit);
        var report = await Report(s, date);
        Assert.True(report.GetProperty("agrees").GetBoolean());
        Assert.Equal(55m, Position(report, bill.PositionId!.Value).GetProperty("foreignAmount").GetDecimal());
    }

    [Fact]
    public async Task ConcurrentSettlementsHaveOneWinnerAndInjectedFailureRollsBackJournalAndEffects()
    {
        using var s = await Arrange();
        await Rate(s, s.Date, 1.10m);
        var invoice = await Execute(s, Invoice(s, true, s.Date, 100));
        var one = await Change(s, await Prepare(s, Settlement(s, invoice, 70, s.Date, "one")), "approve");
        var two = await Change(s, await Prepare(s, Settlement(s, invoice, 70, s.Date, "two")), "approve");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var tx = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
            var workflow = scope.ServiceProvider.GetRequiredService<FxWorkflow>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => tx.Execute<int>(s.Owner.Id, s.Owner.Org, s.Company.Id, async (_, ct) =>
            {
                await workflow.Change(s.Owner.Id, s.Owner.Org, s.Company.Id, one.Id, "post", new(one.Version, "Reviewed", true), ct);
                throw new InvalidOperationException("Injected fault after GL and FX writes");
            }, default));
        }
        Assert.Equal(100m, Position(await Report(s, s.Date), invoice.PositionId!.Value).GetProperty("foreignAmount").GetDecimal());
        async Task<HttpStatusCode> Attempt(FxOperation op)
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Fx}/operations/{op.Id}/post", new FxAction(op.Version, "Reviewed", true));
            return response.StatusCode;
        }
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, (await Task.WhenAll(Attempt(one), Attempt(two))).Order());
        var report = await Report(s, s.Date);
        Assert.True(report.GetProperty("agrees").GetBoolean());
        Assert.Equal(30m, Position(report, invoice.PositionId.Value).GetProperty("foreignAmount").GetDecimal());
    }
}
