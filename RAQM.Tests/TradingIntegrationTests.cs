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
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Trading;
using Xunit;

namespace RAQM.Tests;
// These cases must execute on PostgreSQL. They are never substituted with EF InMemory or silently skipped.
[Collection("PostgreSQL"), Trait("Category", "Integration")]
public sealed partial class TradingIntegrationTests(PostgreSqlFixture fixture)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed record Setup(HttpClient Client, Guid Actor, Guid Org, Company Company, Dictionary<string, AccountResponse> Accounts, FiscalYearResponse Year)
    {
        public string Root => $"/api/v1/organizations/{Org}/companies/{Company.Id}/trading";
        public string Core => $"/api/v1/organizations/{Org}/companies/{Company.Id}/accounting";
    }

    private static async Task<T> Send<T>(HttpClient c, HttpMethod method, string path, object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };
        using var response = await c.SendAsync(request);
        Assert.True(response.StatusCode == status, $"{method} {path}: {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task Denied(HttpClient c, string path, object body, HttpStatusCode status)
    {
        using var response = await c.PostAsJsonAsync(path, body);
        Assert.True(response.StatusCode == status, $"{path}: {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<Setup> Arrange()
    {
        var client = fixture.Factory.CreateClient();
        var tokens = await Send<TokenResponse>(client, HttpMethod.Post, "/api/v1/auth/onboard", new OnboardRequest($"{Guid.NewGuid():N}@trading.example", "Trading_Test-Password1!", "Trading Org", ApiFactory.OnboardingKey));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        var org = me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid();
        var company = await Send<Company>(client, HttpMethod.Post, $"/api/v1/organizations/{org}/companies", new CompanyRequest("Trading company", Guid.NewGuid().ToString("N"), "USD", 1, 1), HttpStatusCode.Created);
        var core = $"/api/v1/organizations/{org}/companies/{company.Id}/accounting";
        var accounts = (await Send<List<AccountResponse>>(client, HttpMethod.Post, core + "/accounts/initialize", new InitializeAccountsRequest())).ToDictionary(x => x.Code);
        foreach (var(code, name, kind, parent)in new[]
        {
            ("1200", "Receivables", AccountClass.Asset, "1000"),
            ("1300", "Recoverable tax", AccountClass.Asset, "1000"),
            ("1400", "Supplier advances", AccountClass.Asset, "1000"),
            ("2200", "Tax payable", AccountClass.Liability, "2000"),
            ("2300", "Customer advances", AccountClass.Liability, "2000")
        }

        )
        {
            accounts[code] = await Send<AccountResponse>(client, HttpMethod.Post, core + "/accounts", new AccountRequest(code, name, null, kind, true, accounts[parent].Id), HttpStatusCode.Created);
        }

        var year = await Send<FiscalYearResponse>(client, HttpMethod.Post, core + "/fiscal-years", new FiscalYearRequest(Today.Year, "Trading year"), HttpStatusCode.Created);
        var s = new Setup(client, me.GetProperty("userId").GetGuid(), org, company, accounts, year);
        await Send<List<ControlAccountResponse>>(client, HttpMethod.Put, s.Root + "/configuration", new ControlAccountRequest(accounts["1200"].Id, accounts["2100"].Id, accounts["4100"].Id, accounts["5100"].Id, accounts["2200"].Id, accounts["1300"].Id, accounts["2300"].Id, accounts["1400"].Id, accounts["3100"].Id, [accounts["1100"].Id], false, "Initial setup"));
        return s;
    }

    private static Task<PartyResponse> Party(Setup s, PartyKind side, decimal? limit = null) => Send<PartyResponse>(s.Client, HttpMethod.Post, s.Root + "/parties", new PartyRequest(side, Guid.NewGuid().ToString("N"), "Counterparty", null, null, "Billing address", "TAX-ID", 30, limit, s.Accounts[side == PartyKind.Customer ? "1200" : "2100"].Id, s.Accounts[side == PartyKind.Customer ? "4100" : "5100"].Id), HttpStatusCode.Created);
    private static Task<TradeDocumentResponse> Draft(Setup s, PartyResponse p, decimal amount = 1000, Guid? tax = null) => Send<TradeDocumentResponse>(s.Client, HttpMethod.Post, s.Root + "/documents", new TradeDocumentRequest(p.Id, p.Kind == PartyKind.Customer ? TradeKind.Invoice : TradeKind.Bill, Today, Today.AddDays(30), "Document", Guid.NewGuid().ToString("N"), [new("Service", 1, amount, TaxRateId: tax)]), HttpStatusCode.Created);
    private static Task<TradeDocumentResponse> Approve(Setup s, TradeDocumentResponse d) => Send<TradeDocumentResponse>(s.Client, HttpMethod.Post, $"{s.Root}/documents/{d.Id}/approve", new DocumentTransitionRequest(d.Version, "Reviewed"));
    private static Task<TradeDocumentResponse> Post(Setup s, TradeDocumentResponse d, string key = "post", IReadOnlyList<ApplyFundingRequest>? funding = null) => Send<TradeDocumentResponse>(s.Client, HttpMethod.Post, $"{s.Root}/documents/{d.Id}/post", new PostTradeDocumentRequest(d.Version, Today, key, ApplyFunding: funding));
    private static Task<PaymentResponse> Pay(Setup s, PartyResponse p, decimal amount, string key = "payment", IReadOnlyList<PaymentAllocationRequest>? allocations = null, bool advance = false) => Send<PaymentResponse>(s.Client, HttpMethod.Post, s.Root + "/payments", new PaymentRequest(p.Id, s.Accounts["1100"].Id, amount, Today, "Bank reference", key, advance, Allocations: allocations));
    private static Task<AllocationResponse> Allocate(Setup s, Guid payment, Guid document, decimal amount, string key) => Send<AllocationResponse>(s.Client, HttpMethod.Post, s.Root + "/allocations", new AllocationRequest(FundingKind.Payment, payment, document, amount, Today, key));
    private static Task<TradingReconciliation?> Reconcile(Setup s) => s.Client.GetFromJsonAsync<TradingReconciliation>($"{s.Root}/reconciliation?asOf={Today:yyyy-MM-dd}");
    private async Task<List<JournalLine>> Lines(Guid journal)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().JournalLines.AsNoTracking().Where(x => x.JournalId == journal).ToListAsync();
    }

    private async Task<int> Count(Setup s)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RaqmDbContext>().Journals.CountAsync(x => x.CompanyId == s.Company.Id && x.State == JournalState.Posted);
    }

    [Theory]
    [InlineData(PartyKind.Customer)]
    [InlineData(PartyKind.Supplier)]
    public async Task InvoiceBillPartialFullSettlementStatementsAgingAndLedgerAgree(PartyKind side)
    {
        var s = await Arrange();
        using var client = s.Client;
        var party = await Party(s, side);
        var d = await Post(s, await Approve(s, await Draft(s, party)));
        var lines = await Lines(d.JournalId!.Value);
        var control = lines.Single(x => x.AccountId == party.ControlAccountId);
        Assert.Equal(side == PartyKind.Customer ? 1000 : 0, control.Debit);
        Assert.Equal(side == PartyKind.Supplier ? 1000 : 0, control.Credit);
        var offset = lines.Single(x => x.AccountId == party.DefaultAccountId);
        Assert.Equal(side == PartyKind.Supplier ? 1000 : 0, offset.Debit);
        Assert.Equal(side == PartyKind.Customer ? 1000 : 0, offset.Credit);
        var p = await Pay(s, party, 600, allocations: [new(d.Id, 600)]);
        Assert.Equal(600, p.ControlAmount);
        Assert.Equal(0, p.AdvanceAmount);
        Assert.Equal(0, p.Unapplied);
        var partial = (await client.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{d.Id}"))!;
        Assert.Equal(400, partial.Outstanding);
        Assert.Equal(SettlementStatus.Partial, partial.SettlementStatus);
        var statement = (await client.GetFromJsonAsync<PartyStatement>($"{s.Root}/parties/{party.Id}/statement?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))!;
        Assert.Equal(400, statement.ClosingControlBalance);
        Assert.Equal(400, Assert.Single(statement.Aging).Outstanding);
        var aging = (await client.GetFromJsonAsync<AgingResponse>($"{s.Root}/aging?asOf={Today:yyyy-MM-dd}"))!;
        Assert.Equal(400, Assert.Single(aging.Parties).Total);
        await Pay(s, party, 400, "final", [new(d.Id, 400)]);
        var settled = (await client.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{d.Id}"))!;
        Assert.Equal(0, settled.Outstanding);
        Assert.Equal(SettlementStatus.Settled, settled.SettlementStatus);
        var recon = (await Reconcile(s))!;
        Assert.True(recon.Agrees);
        Assert.All(recon.Controls, x => Assert.Equal(0, x.Difference));
        Assert.Equal(1000, recon.Payments);
        Assert.Equal(1000, recon.NetAllocations);
        Assert.Equal(0, recon.PaymentConservationDifference);
    }

    [Theory]
    [InlineData(PartyKind.Customer)]
    [InlineData(PartyKind.Supplier)]
    public async Task OverpaymentIsAdvanceAndApplicationAndReversalReconcile(PartyKind side)
    {
        var s = await Arrange();
        using var client = s.Client;
        var party = await Party(s, side);
        var first = await Post(s, await Approve(s, await Draft(s, party)));
        var payment = await Pay(s, party, 1200, allocations: [new(first.Id, 1000)]);
        Assert.Equal(1000, payment.ControlAmount);
        Assert.Equal(200, payment.AdvanceAmount);
        Assert.Equal(200, payment.Unapplied);
        var second = await Post(s, await Approve(s, await Draft(s, party, 200)));
        var allocation = await Allocate(s, payment.Id, second.Id, 200, "advance-use");
        Assert.Equal(200, allocation.AdvanceAmount);
        Assert.NotNull(allocation.JournalId);
        var advanceLines = await Lines(allocation.JournalId!.Value);
        Assert.Equal(200, advanceLines.Sum(x => x.Debit));
        Assert.Equal(200, advanceLines.Sum(x => x.Credit));
        Assert.True((await Reconcile(s))!.Agrees);
        await Send<PaymentResponse>(client, HttpMethod.Post, $"{s.Root}/payments/{payment.Id}/reverse", new ReversePaymentRequest(Today, "reverse", "Bank correction"));
        Assert.True((await Reconcile(s))!.Agrees);
        Assert.Equal(1200, (await Reconcile(s))!.Controls.Single(x => x.Name == (side == PartyKind.Customer ? "Receivable" : "Payable")).Subledger);
        Assert.Equal(6, await Count(s)); // two documents, payment, advance transfer, two exact reversals
    }

    [Theory]
    [InlineData(PartyKind.Customer)]
    [InlineData(PartyKind.Supplier)]
    public async Task TaxCreditRefundAndOriginalReferencesStayTraceable(PartyKind side)
    {
        var s = await Arrange();
        using var client = s.Client;
        var party = await Party(s, side);
        var code = await Send<TaxCodeResponse>(client, HttpMethod.Post, s.Root + "/tax-codes", new TaxCodeRequest("VAT", "Configurable tax", s.Accounts["2200"].Id, s.Accounts["1300"].Id), HttpStatusCode.Created);
        var rate = await Send<TaxRateResponse>(client, HttpMethod.Post, $"{s.Root}/tax-codes/{code.Id}/rates", new TaxRateRequest(Today, Today.AddDays(365), 15), HttpStatusCode.Created);
        var d = await Post(s, await Approve(s, await Draft(s, party, 1000, rate.Id)));
        Assert.Equal(1000, d.NetTotal);
        Assert.Equal(150, d.TaxTotal);
        Assert.Equal(1150, d.Total);
        var taxLine = (await Lines(d.JournalId!.Value)).Single(x => x.AccountId == s.Accounts[side == PartyKind.Customer ? "2200" : "1300"].Id);
        Assert.Equal(side == PartyKind.Supplier ? 150 : 0, taxLine.Debit);
        Assert.Equal(side == PartyKind.Customer ? 150 : 0, taxLine.Credit);
        var p = await Pay(s, party, 1150, allocations: [new(d.Id, 1150)]);
        var credit = await Send<TradeDocumentResponse>(client, HttpMethod.Post, s.Root + "/documents", new TradeDocumentRequest(party.Id, side == PartyKind.Customer ? TradeKind.CustomerCredit : TradeKind.SupplierCredit, Today, null, "Credit correction", null, [new("Credit", .2m, 0, OriginalLineId: d.Lines[0].Id)], d.Id), HttpStatusCode.Created);
        credit = await Post(s, await Approve(s, credit));
        Assert.Equal(230, credit.Total);
        Assert.Equal(30, credit.TaxTotal);
        var refund = await Send<PaymentResponse>(client, HttpMethod.Post, s.Root + "/refunds", new RefundRequest(FundingKind.CreditNote, credit.Id, s.Accounts["1100"].Id, 230, Today, "refund", "Refund approved"));
        Assert.Equal(230, refund.ControlAmount);
        Assert.Equal(0, refund.AdvanceAmount);
        Assert.True((await Reconcile(s))!.Agrees);
        await Denied(client, s.Root + "/documents", new TradeDocumentRequest(party.Id, credit.Kind, Today, null, "Excess", null, [new("Credit", 1, 0, OriginalLineId: d.Lines[0].Id)], d.Id), HttpStatusCode.Conflict);
        await Denied(client, $"{s.Core}/journals/{d.JournalId}/reverse", new ReverseJournalRequest(Today, "unsafe", "Must use credit"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ConcurrentInvoicePostingReplaysOneJournalAndCreditAutoAllocates()
    {
        var s = await Arrange();
        using var client = s.Client;
        var party = await Party(s, PartyKind.Customer);
        var approved = await Approve(s, await Draft(s, party));
        var posts = await Task.WhenAll(Post(s, approved, "same-invoice"), Post(s, approved, "same-invoice"));
        var invoice = posts[0];
        Assert.Equal(invoice.JournalId, posts[1].JournalId);
        Assert.Equal(1, await Count(s));
        await Denied(client, $"{s.Root}/documents/{invoice.Id}/post",
            new PostTradeDocumentRequest(approved.Version, Today, "different"), HttpStatusCode.Conflict);
        var credit = await Send<TradeDocumentResponse>(client, HttpMethod.Post, s.Root + "/documents",
            new TradeDocumentRequest(party.Id, TradeKind.CustomerCredit, Today, null, "Credit", null,
                [new("Partial credit", .25m, 0, OriginalLineId: invoice.Lines[0].Id)], invoice.Id), HttpStatusCode.Created);
        credit = await Post(s, await Approve(s, credit));
        Assert.Equal(250, credit.Total);
        Assert.Equal(0, credit.Outstanding);
        Assert.Equal(750, (await client.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{invoice.Id}"))!.Outstanding);
        Assert.Equal(2, await Count(s));
        Assert.True((await Reconcile(s))!.Agrees);
    }

    [Fact]
    public async Task DraftEditRecalculatesTaxAndSupplierReferencesAreUniquePerSupplier()
    {
        var s = await Arrange();
        using var client = s.Client;
        var party = await Party(s, PartyKind.Supplier);
        var code = await Send<TaxCodeResponse>(client, HttpMethod.Post, s.Root + "/tax-codes",
            new TaxCodeRequest("VAT", "Configurable tax", s.Accounts["2200"].Id, s.Accounts["1300"].Id), HttpStatusCode.Created);
        var rate = await Send<TaxRateResponse>(client, HttpMethod.Post, $"{s.Root}/tax-codes/{code.Id}/rates",
            new TaxRateRequest(Today, Today.AddDays(30), 15), HttpStatusCode.Created);
        await Denied(client, $"{s.Root}/tax-codes/{code.Id}/rates", new TaxRateRequest(Today.AddDays(30), Today.AddDays(60), 10), HttpStatusCode.Conflict);
        var request = new TradeDocumentRequest(party.Id, TradeKind.Bill, Today.AddDays(-1), null, "Bill", "Invoice-1", [new("Line", 1, 100)]);
        var draft = await Send<TradeDocumentResponse>(client, HttpMethod.Post, s.Root + "/documents", request, HttpStatusCode.Created);
        await Denied(client, s.Root + "/documents", request with { ExternalReference = " invoice-1 " }, HttpStatusCode.Conflict);
        var edited = await Send<TradeDocumentResponse>(client, HttpMethod.Put, $"{s.Root}/documents/{draft.Id}",
            new UpdateTradeDocumentRequest(request with { DocumentDate = Today, Lines = [new("Inclusive", 1, 115, TaxRateId: rate.Id, TaxInclusive: true)] }, draft.Version));
        Assert.Equal(100, edited.NetTotal);
        Assert.Equal(15, edited.TaxTotal);
        Assert.NotEqual(draft.Lines[0].Id, edited.Lines[0].Id);
        await Post(s, await Approve(s, edited));
        await Denied(client, s.Root + "/payments", new PaymentRequest(party.Id, s.Accounts["1100"].Id, .001m, Today, "Invalid scale", "invalid"), HttpStatusCode.BadRequest);
        Assert.Equal(1, await Count(s));
    }

    [Fact]
    public async Task OnePaymentCanSettleMultipleDocumentsAndCannotBeOverallocated()
    {
        var s = await Arrange();
        using var client = s.Client;
        var p = await Party(s, PartyKind.Customer);
        var a = await Post(s, await Approve(s, await Draft(s, p, 300)));
        var b = await Post(s, await Approve(s, await Draft(s, p, 700)));
        var payment = await Pay(s, p, 1000, allocations: [new(a.Id, 300), new(b.Id, 700)]);
        Assert.Equal(1000, payment.Allocated);
        Assert.Equal(0, payment.Unapplied);
        await Denied(client, s.Root + "/allocations", new AllocationRequest(FundingKind.Payment, payment.Id, a.Id, 1, Today, "excess"), HttpStatusCode.Conflict);
        Assert.True((await Reconcile(s))!.Agrees);
    }

    [Fact]
    public async Task ConcurrentIdempotentPaymentsAndAllocationsHaveSingleEffects()
    {
        var s = await Arrange();
        using var client = s.Client;
        var p = await Party(s, PartyKind.Customer);
        var d = await Post(s, await Approve(s, await Draft(s, p)));
        var replies = await Task.WhenAll(Pay(s, p, 600, "same"), Pay(s, p, 600, "same"));
        Assert.Equal(replies[0].Id, replies[1].Id);
        var attempts = await Task.WhenAll(client.PostAsJsonAsync(s.Root + "/allocations", new AllocationRequest(FundingKind.Payment, replies[0].Id, d.Id, 600, Today, "a")), client.PostAsJsonAsync(s.Root + "/allocations", new AllocationRequest(FundingKind.Payment, replies[0].Id, d.Id, 600, Today, "b")));
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in attempts)
            response.Dispose();
        Assert.Equal(2, await Count(s));
        Assert.True((await Reconcile(s))!.Agrees);
        await Denied(client, s.Root + "/payments", new PaymentRequest(p.Id, s.Accounts["1100"].Id, 601, Today, "Bank reference", "same"), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ConcurrentDifferentPaymentsSerializeControlExposureAndAdvanceSplit()
    {
        var s = await Arrange();
        using var client = s.Client;
        var p = await Party(s, PartyKind.Customer);
        await Post(s, await Approve(s, await Draft(s, p)));
        var payments = await Task.WhenAll(Pay(s, p, 800, "first"), Pay(s, p, 800, "second"));
        Assert.Equal(1000, payments.Sum(x => x.ControlAmount));
        Assert.Equal(600, payments.Sum(x => x.AdvanceAmount));
        Assert.True((await Reconcile(s))!.Agrees);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceFailureAfterPostingAndSwallowedJoinedFailureRollBackEverything(bool swallow)
    {
        var s = await Arrange();
        using var client = s.Client;
        var p = await Party(s, PartyKind.Customer);
        var approved = await Approve(s, await Draft(s, p));
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var tx = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
            var service = scope.ServiceProvider.GetRequiredService<ITradeDocumentService>();
            await Assert.ThrowsAnyAsync<Exception>(() => tx.Execute<int>(s.Actor, s.Org, s.Company.Id, async (_, ct) =>
            {
                await service.Post(s.Actor, s.Org, s.Company.Id, approved.Id, new(approved.Version, Today, "fault"), ct);
                if (swallow)
                {
                    try
                    {
                        await tx.Execute<int>(s.Actor, s.Org, s.Company.Id, (_, _) => throw new InvalidOperationException("Injected joined failure"), ct);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    return 1;
                }

                throw new InvalidOperationException("Injected source failure after engine flush");
            }, default));
        }

        Assert.Equal(0, await Count(s));
        var d = (await client.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{approved.Id}"))!;
        Assert.Equal(TradeState.Approved, d.FinancialState);
        Assert.Null(d.JournalId);
        await Post(s, d);
        Assert.Equal(1, await Count(s));
    }

    [Fact]
    public async Task FailedJournalAndCreditLimitPostingLeaveSourceApprovedAndNoJournals()
    {
        var s = await Arrange();
        using var client = s.Client;
        var p = await Party(s, PartyKind.Customer, 100);
        var d = await Approve(s, await Draft(s, p));
        await Denied(client, $"{s.Root}/documents/{d.Id}/post", new PostTradeDocumentRequest(d.Version, Today, "limit"), HttpStatusCode.Conflict);
        Assert.Equal(0, await Count(s));
        var period = s.Year.Periods.Single(x => x.StartDate <= Today && x.EndDate >= Today);
        // This test isolates posting into a closed period; the close API separately rejects future-period closure.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            var fixturePeriod = await db.Periods.SingleAsync(x => x.Id == period.Id);
            fixturePeriod.State = PeriodState.Closed;
            await db.SaveChangesAsync();
        }
        await Denied(client, $"{s.Root}/documents/{d.Id}/post", new PostTradeDocumentRequest(d.Version, Today, "closed"), HttpStatusCode.Conflict);
        Assert.Equal(0, await Count(s));
        Assert.Equal(TradeState.Approved, (await client.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{d.Id}"))!.FinancialState);
    }

    [Fact]
    public async Task ZeroCreditLimitAllowsFullyPrepaidAtomicInvoice()
    {
        var s = await Arrange();
        using var client = s.Client;
        var p = await Party(s, PartyKind.Customer, 0);
        var payment = await Pay(s, p, 1000, advance: true);
        Assert.Equal(1000, payment.AdvanceAmount);
        var d = await Post(s, await Approve(s, await Draft(s, p)), funding: [new(FundingKind.Payment, payment.Id, 1000)]);
        Assert.Equal(0, d.Outstanding);
        Assert.True((await Reconcile(s))!.Agrees);
    }

    [Fact]
    public async Task WrongCompanyPartyAllocationManualControlAndReaderWritesAreDenied()
    {
        var a = await Arrange();
        var b = await Arrange();
        using var ac = a.Client;
        using var bc = b.Client;
        var pa = await Party(a, PartyKind.Customer);
        var pb = await Party(b, PartyKind.Customer);
        var da = await Post(a, await Approve(a, await Draft(a, pa)));
        var pay = await Pay(a, pa, 1000);
        await Denied(ac, a.Root + "/documents", new TradeDocumentRequest(pb.Id, TradeKind.Invoice, Today, null, "Foreign", null, [new("Line", 1, 10)]), HttpStatusCode.NotFound);
        var other = await Party(a, PartyKind.Customer);
        var foreignPartyDoc = await Post(a, await Approve(a, await Draft(a, other)));
        await Denied(ac, a.Root + "/allocations", new AllocationRequest(FundingKind.Payment, pay.Id, foreignPartyDoc.Id, 1, Today, "foreign"), HttpStatusCode.BadRequest);
        var manual = await Send<JournalResponse>(ac, HttpMethod.Post, a.Core + "/journals", new JournalRequest(Today, Today, "Bypass", "bypass", [new(pa.ControlAccountId, 10, 0, null), new(a.Accounts["4100"].Id, 0, 10, null)]), HttpStatusCode.Created);
        await Denied(ac, $"{a.Core}/journals/{manual.Id}/post", new PostJournalRequest("protected", manual.Version), HttpStatusCode.Forbidden);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.OrganizationMembers.Add(new() { OrganizationId = a.Org, TenantId = a.Company.TenantId, UserId = b.Actor, Role = OrganizationRole.Member });
            db.CompanyMembers.Add(new() { CompanyId = a.Company.Id, OrganizationId = a.Org, TenantId = a.Company.TenantId, UserId = b.Actor, Role = CompanyRole.Reader });
            await db.SaveChangesAsync();
        }

        await Denied(bc, a.Root + "/payments", new PaymentRequest(pa.Id, a.Accounts["1100"].Id, 1, Today, "Reader", "reader"), HttpStatusCode.Forbidden);
        await Denied(bc, a.Root + "/refunds", new RefundRequest(FundingKind.Payment, pay.Id, a.Accounts["1100"].Id, 1, Today, "reader-refund", "Unauthorized"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DatabaseRejectsPostedSourceMutationAndSplitSourceCommit()
    {
        var s = await Arrange();
        using var client = s.Client;
        var p = await Party(s, PartyKind.Customer);
        var posted = await Post(s, await Approve(s, await Draft(s, p)));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("UPDATE trading.\"Documents\" SET \"Total\"=\"Total\"+1 WHERE \"Id\"=@id", connection))
        {
            command.Parameters.AddWithValue("id", posted.Id);
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }

        using var scope = fixture.Factory.Services.CreateScope();
        var tx = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
        var engine = scope.ServiceProvider.GetRequiredService<IPostingEngine>();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var approved = await Approve(s, await Draft(s, p, 100));
        await Assert.ThrowsAsync<PostgresException>(() => tx.Execute<int>(s.Actor, s.Org, s.Company.Id, async (_, ct) =>
        {
            var source = await db.TradeDocuments.Include(x => x.Lines).SingleAsync(x => x.Id == approved.Id, ct);
            await engine.PostAsync(s.Actor, new(new(s.Company.TenantId, s.Org, s.Company.Id), "AR.Invoice", source.Id, "split", Today, "USD", RAQM.Api.Helpers.Trading.TradeRules.DocumentPosting(source), null, Today, "Split commit fault"), ct);
            return 1; // Deliberately omit source finalization. Deferred DB contract must reject COMMIT.
        }, default));
        Assert.Equal(1, await Count(s));
    }
}
