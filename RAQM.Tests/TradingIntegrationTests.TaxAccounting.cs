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
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.TaxAccounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.RegulatoryDocuments;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Services.TaxAccounting;
using RAQM.Api.Services.TaxReporting;
using Xunit;

namespace RAQM.Tests;

public sealed partial class TradingIntegrationTests
{
    [Fact]
    public async Task ConfiguredSaleRetainsTaxRulePostsExactOutputAndFreezesReturnAndInvoice()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var reg = await TaxRegistration(s, reviewer);
        await TaxControl(s, reg, TaxControlRole.Output, s.Accounts["2200"].Id);
        var rate = await TaxRate(s, 10m);
        var root = TaxRoot(s, reg);
        var rule = await Send<TaxRule>(owner, HttpMethod.Post, root + "/rules",
            new TaxRuleRequest("DEFAULT", "SERVICE", null, PartyKind.Customer, 1, TaxTreatment.Standard, rate.Id, Today, Today, "Approved policy"));
        await Denied(owner, $"{root}/rules/{rule.Id}/approve", new { }, HttpStatusCode.Conflict);
        await Send<TaxRule>(reviewer, HttpMethod.Post, $"{root}/rules/{rule.Id}/approve", new { });
        var party = await Party(s, PartyKind.Customer);
        var quote = new TaxQuoteRequest(party.Id, Today, "DEFAULT", [new("Service", 1, 1100m, 0, true, "SERVICE")]);
        var preview = await Send<TaxQuote>(owner, HttpMethod.Post, root + "/preview", quote);
        Assert.Equal((1000m, 100m, 1100m), (preview.Net, preview.Tax, preview.Total));
        var source = await Send<TaxCommercialResponse>(owner, HttpMethod.Post, root + "/documents",
            new TaxCommercialRequest(quote, Today, "Taxed invoice", null));
        var posted = await Post(s, await Approve(s, source.Document));
        var ledger = await Lines(posted.JournalId!.Value);
        Assert.Equal(1100m, ledger.Single(x => x.AccountId == party.ControlAccountId).Debit);
        Assert.Equal(1000m, ledger.Single(x => x.AccountId == party.DefaultAccountId).Credit);
        Assert.Equal(100m, ledger.Single(x => x.AccountId == s.Accounts["2200"].Id).Credit);
        var report = await owner.GetFromJsonAsync<TaxReportSnapshot>($"{root}/report?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(report!.Agrees);
        Assert.Equal(100m, report.OutputTax);
        Assert.Equal(rule.Id, Assert.Single(report.Entries).RuleId);
        var period = await Send<TaxPeriod>(owner, HttpMethod.Post, root + "/periods", new TaxPeriodRequest(Today, Today, "Tax cutoff"));
        var history = $"{root}/periods/{period.Id}/returns";
        var work = await Send<TaxReturnVersion>(owner, HttpMethod.Post, history, new TaxReturnRequest(null, "Prepared"));
        await Send<TaxReturnVersion>(reviewer, HttpMethod.Post, $"{history}/{work.Id}/approve", new { });
        var revised = await Send<TaxReturnVersion>(owner, HttpMethod.Post, history, new TaxReturnRequest(work.Id, "Amendment review"));
        Assert.Equal(work.Id, revised.SupersedesId);
        Assert.Equal(2, revised.Version);
        var invoice = await Send<RegulatoryDocument>(owner, HttpMethod.Post, TaxBase(s) + $"/structured-invoices/source/{posted.Id}", new { });
        Assert.Equal(100m, JsonDocument.Parse(invoice.ContentJson).RootElement.GetProperty("Tax").GetDecimal());
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""UPDATE tax."Returns" SET "SnapshotJson"='{}' WHERE "Id"=@id""", connection);
        command.Parameters.AddWithValue("id", work.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task ConfiguredSupplierCreditRetainsHistoricalInputTaxAndConservesRecovery()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var reg = await TaxRegistration(s, reviewer);
        await TaxControl(s, reg, TaxControlRole.Input, s.Accounts["1300"].Id);
        var rate = await TaxRate(s, 10m);
        var root = TaxRoot(s, reg);
        var rule = await Send<TaxRule>(owner, HttpMethod.Post, root + "/rules",
            new TaxRuleRequest("PURCHASE", null, null, PartyKind.Supplier, 1, TaxTreatment.Standard, rate.Id, Today, Today, "Recovery permitted"));
        await Send<TaxRule>(reviewer, HttpMethod.Post, $"{root}/rules/{rule.Id}/approve", new { });
        var supplier = await Party(s, PartyKind.Supplier);
        var quote = new TaxQuoteRequest(supplier.Id, Today, "PURCHASE", [new("Service", 1, 1000, 0, false, "SERVICES")]);
        var created = await Send<TaxCommercialResponse>(owner, HttpMethod.Post, root + "/documents",
            new TaxCommercialRequest(quote, Today, "Taxed supplier bill", "BILL-01"));
        var bill = await Post(s, await Approve(s, created.Document));
        Assert.Equal(100m, (await Lines(bill.JournalId!.Value)).Single(x => x.AccountId == s.Accounts["1300"].Id).Debit);
        var credit = await Send<TradeDocumentResponse>(owner, HttpMethod.Post, s.Root + "/documents",
            new TradeDocumentRequest(supplier.Id, TradeKind.SupplierCredit, Today, Today, "Supplier credit", null,
                [new("Half credit", 0.5m, 0, OriginalLineId: bill.Lines[0].Id)], bill.Id), HttpStatusCode.Created);
        credit = await Post(s, await Approve(s, credit));
        Assert.Equal(50m, (await Lines(credit.JournalId!.Value)).Single(x => x.AccountId == s.Accounts["1300"].Id).Credit);
        var report = await owner.GetFromJsonAsync<TaxReportSnapshot>($"{root}/report?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(report!.Agrees);
        Assert.Equal(50m, report.RecoverableInput);
        Assert.All(report.Entries, x => Assert.Equal(rule.Id, x.RuleId));
    }

    [Fact]
    public async Task ReverseChargePostsRecoverableAndNonrecoverableTaxOnceAndCorrectsExactly()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var reg = await TaxRegistration(s, reviewer);
        await TaxControl(s, reg, TaxControlRole.Output, s.Accounts["2200"].Id);
        await TaxControl(s, reg, TaxControlRole.Input, s.Accounts["1300"].Id);
        var expense = await TaxAccount(s, "5600", AccountClass.Expense);
        await TaxControl(s, reg, TaxControlRole.Nonrecoverable, expense.Id);
        var supplier = await Party(s, PartyKind.Supplier);
        var bill = await Post(s, await Approve(s, await Draft(s, supplier)));
        var input = new TaxOperationRequest(TaxOperationKind.ReverseCharge, "RC-1", Today, "Reviewed cross-border supply", BillId: bill.Id,
            Rate: 10, RecoveryPercent: 60, PolicyEvidence: "Documented eligibility");
        var assessed = await TaxPost(s, reviewer, reg, input);
        var journal = await Lines(assessed.JournalId!.Value);
        Assert.Equal(100m, journal.Single(x => x.AccountId == s.Accounts["2200"].Id).Credit);
        Assert.Equal(60m, journal.Single(x => x.AccountId == s.Accounts["1300"].Id).Debit);
        Assert.Equal(40m, journal.Single(x => x.AccountId == expense.Id).Debit);
        await Denied(owner, TaxRoot(s, reg) + "/operations", input with { Reference = "RC-2" }, HttpStatusCode.Conflict);
        await TaxPost(s, reviewer, reg, new TaxOperationRequest(TaxOperationKind.Correction, "REV", Today, "Reversed tax basis", BasisId: assessed.Id));
        var report = await owner.GetFromJsonAsync<TaxReportSnapshot>($"{TaxRoot(s, reg)}/report?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(report!.Agrees);
        Assert.Equal(0m, report.OutputTax);
        Assert.Equal(0m, report.RecoverableInput);
        Assert.Equal(0m, report.NonrecoverableTax);
    }

    [Fact]
    public async Task WithholdingSettlesGrossApAndRollsBackWhenTheBillCannotFundTheApprovedAmount()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var reg = await TaxRegistration(s, reviewer);
        var bankLedger = await TaxAccount(s, "1500", AccountClass.Asset);
        var payable = await TaxAccount(s, "2500", AccountClass.Liability);
        await TaxControl(s, reg, TaxControlRole.Clearing, s.Accounts["1100"].Id);
        await TaxControl(s, reg, TaxControlRole.Withholding, payable.Id);
        var bankRoot = TaxBase(s).Replace("/tax-accounting", "/banking", StringComparison.Ordinal);
        var profile = await Send<BankProfile>(owner, HttpMethod.Post, bankRoot + "/banks", new BankProfileRequest("TXBANK", "Tax bank", "ABCDUS33"));
        var bank = await Send<TreasuryAccountResponse>(owner, HttpMethod.Post, bankRoot + "/accounts",
            new TreasuryAccountRequest(TreasuryAccountKind.Bank, "TXACCOUNT", "Bank ledger", bankLedger.Id, profile.Id, "123456789"));
        var supplier = await Party(s, PartyKind.Supplier);
        var bill = await Post(s, await Approve(s, await Draft(s, supplier)));
        var input = new TaxOperationRequest(TaxOperationKind.Withhold, "WHT-1", Today, "Approved supplier payment", BillId: bill.Id,
            BankAccountId: bank.Id, Amount: 600, Rate: 5, PolicyEvidence: "Eligible service contract");
        var partial = await TaxPost(s, reviewer, reg, input);
        var taxLines = await Lines(partial.JournalId!.Value);
        Assert.Equal(570m, taxLines.Single(x => x.AccountId == bankLedger.Id).Credit);
        Assert.Equal(30m, taxLines.Single(x => x.AccountId == payable.Id).Credit);
        var after = await owner.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{bill.Id}");
        Assert.Equal(400m, after!.Outstanding);
        var report = await owner.GetFromJsonAsync<TaxReportSnapshot>($"{TaxRoot(s, reg)}/report?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(report!.Agrees);
        Assert.Equal(0m, report.Reconciliation.Single(x => x.Role == TaxControlRole.Clearing).Closing);
        var route = TaxRoot(s, reg) + "/operations";
        var next = await Send<TaxOperation>(owner, HttpMethod.Post, route, input with { Reference = "WHT-2", Amount = 400 });
        await Send<TaxOperation>(reviewer, HttpMethod.Post, $"{route}/{next.Id}/approve", new { });
        var credit = await Send<TradeDocumentResponse>(owner, HttpMethod.Post, s.Root + "/documents",
            new TradeDocumentRequest(supplier.Id, TradeKind.SupplierCredit, Today, Today, "Partial supplier credit", null,
                [new("Partial credit", 0.001m, 0, OriginalLineId: bill.Lines[0].Id)], bill.Id), HttpStatusCode.Created);
        await Post(s, await Approve(s, credit));
        var beforeCount = await Count(s);
        await Denied(owner, $"{route}/{next.Id}/post", new { }, HttpStatusCode.Conflict);
        Assert.Equal(beforeCount, await Count(s));
        var operations = await owner.GetFromJsonAsync<TaxOperation[]>(route);
        Assert.Equal(TaxEvidenceState.Approved, operations!.Single(x => x.Id == next.Id).State);
    }

    [Fact]
    public async Task R03_002_ZeroRoundedInstallmentRetainsBasisAndLaterCumulativeWithholding()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var reg = await TaxRegistration(s, reviewer);
        var bankLedger = await TaxAccount(s, "1500", AccountClass.Asset);
        var payable = await TaxAccount(s, "2500", AccountClass.Liability);
        await TaxControl(s, reg, TaxControlRole.Clearing, s.Accounts["1100"].Id);
        await TaxControl(s, reg, TaxControlRole.Withholding, payable.Id);
        var bankRoot = TaxBase(s).Replace("/tax-accounting", "/banking", StringComparison.Ordinal);
        var profile = await Send<BankProfile>(owner, HttpMethod.Post, bankRoot + "/banks",
            new BankProfileRequest("TXBANK", "Tax bank", "ABCDUS33"));
        var bank = await Send<TreasuryAccountResponse>(owner, HttpMethod.Post, bankRoot + "/accounts",
            new TreasuryAccountRequest(TreasuryAccountKind.Bank, "TXACCOUNT", "Bank ledger", bankLedger.Id, profile.Id, "123456789"));
        var bill = await Post(s, await Approve(s, await Draft(s, await Party(s, PartyKind.Supplier), 0.15m)));
        var input = new TaxOperationRequest(TaxOperationKind.Withhold, "ZERO-FIRST", Today, "Installment",
            BillId: bill.Id, BankAccountId: bank.Id, Amount: 0.05m, Rate: 10, PolicyEvidence: "Eligible service contract");
        var route = TaxRoot(s, reg) + "/operations";
        var first = await TaxPost(s, reviewer, reg, input);
        Assert.Equal(0m, first.Amount);
        Assert.NotNull(first.PaymentId);
        Assert.Equal(first.Id, (await Send<TaxOperation>(owner, HttpMethod.Post, route, input)).Id);
        Assert.Equal(first.JournalId, (await Send<TaxOperation>(owner, HttpMethod.Post, $"{route}/{first.Id}/post", new { })).JournalId);
        var firstLines = await Lines(first.JournalId!.Value);
        Assert.Equal(2, firstLines.Count);
        Assert.Equal(0.05m, firstLines.Single(x => x.AccountId == bankLedger.Id).Credit);
        Assert.DoesNotContain(firstLines, x => x.AccountId == payable.Id);
        var firstPayment = await owner.GetFromJsonAsync<PaymentResponse>($"{s.Root}/payments/{first.PaymentId}");
        var firstApLines = await Lines(firstPayment!.JournalId);
        Assert.Equal(0.05m, firstApLines.Single(x => x.AccountId == s.Accounts["2100"].Id).Debit);
        Assert.Equal(0.05m, firstApLines.Single(x => x.AccountId == s.Accounts["1100"].Id).Credit);
        var period = await Send<TaxPeriod>(owner, HttpMethod.Post, TaxRoot(s, reg) + "/periods",
            new TaxPeriodRequest(Today, Today, "Withholding cutoff"));
        var returns = $"{TaxRoot(s, reg)}/periods/{period.Id}/returns";
        var initialReturn = await Send<TaxReturnVersion>(owner, HttpMethod.Post, returns, new TaxReturnRequest(null, "Zero installment basis"));
        Assert.Contains(JsonSerializer.Deserialize<TaxReportSnapshot>(initialReturn.SnapshotJson)!.Entries,
            x => x.SourceId == first.Id && x.TaxableBase == 0.05m);
        await Send<TaxReturnVersion>(reviewer, HttpMethod.Post, $"{returns}/{initialReturn.Id}/approve", new { });
        var later = await TaxPost(s, reviewer, reg, input with { Reference = "SECOND", Amount = 0.10m });
        Assert.Equal(0.02m, (await Lines(later.JournalId!.Value)).Single(x => x.AccountId == payable.Id).Credit);
        Assert.Equal(0.08m, (await Lines(later.JournalId.Value)).Single(x => x.AccountId == bankLedger.Id).Credit);
        var secondPayment = await owner.GetFromJsonAsync<PaymentResponse>($"{s.Root}/payments/{later.PaymentId}");
        Assert.Equal(0.10m, (await Lines(secondPayment!.JournalId)).Single(x => x.AccountId == s.Accounts["2100"].Id).Debit);
        var retained = await owner.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{bill.Id}");
        Assert.Equal(0m, retained!.Outstanding);
        Assert.Equal(0.02m, Math.Round(0.15m * 0.10m, 2, MidpointRounding.ToEven));
        var report = await owner.GetFromJsonAsync<TaxReportSnapshot>($"{TaxRoot(s, reg)}/report?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(report!.Agrees);
        Assert.Equal(0.02m, report.Withheld);
        Assert.Equal(0m, report.Reconciliation.Single(x => x.Role == TaxControlRole.Clearing).Closing);
        Assert.Equal(-0.02m, report.Reconciliation.Single(x => x.Role == TaxControlRole.Withholding).Closing);
        var amended = await Send<TaxReturnVersion>(owner, HttpMethod.Post, returns,
            new TaxReturnRequest(initialReturn.Id, "Later positive withholding"));
        Assert.Equal(0.02m, JsonSerializer.Deserialize<TaxReportSnapshot>(amended.SnapshotJson)!.Withheld);
        await Send<TaxReturnVersion>(reviewer, HttpMethod.Post, $"{returns}/{amended.Id}/approve", new { });
        var firstCertificate = await owner.GetFromJsonAsync<JsonElement>($"{TaxRoot(s, reg)}/withholding/{first.Id}/certificate");
        var secondCertificate = await owner.GetFromJsonAsync<JsonElement>($"{TaxRoot(s, reg)}/withholding/{later.Id}/certificate");
        Assert.Equal(0m, firstCertificate.GetProperty("withheld").GetDecimal());
        Assert.Equal(0.02m, secondCertificate.GetProperty("withheld").GetDecimal());
        await TaxPost(s, reviewer, reg, new(TaxOperationKind.Correction, "UNDO-SECOND", Today, "Correction", BasisId: later.Id));
        await TaxPost(s, reviewer, reg, new(TaxOperationKind.Correction, "UNDO-FIRST", Today, "Correction", BasisId: first.Id));
        Assert.Equal(0.15m, (await owner.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{bill.Id}"))!.Outstanding);
        var reversed = await owner.GetFromJsonAsync<TaxReportSnapshot>($"{TaxRoot(s, reg)}/report?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(reversed!.Agrees);
        Assert.Equal(0m, reversed.Withheld);
        var retainedReturns = await owner.GetFromJsonAsync<TaxReturnVersion[]>(returns);
        Assert.Equal(initialReturn.SourceHash, retainedReturns!.Single(x => x.Id == initialReturn.Id).SourceHash);
        Assert.Equal(amended.SourceHash, retainedReturns!.Single(x => x.Id == amended.Id).SourceHash);
        Assert.Equal(0m, JsonSerializer.Deserialize<TaxReportSnapshot>(retainedReturns!.Single(x => x.Id == initialReturn.Id).SnapshotJson)!.Withheld);
        Assert.Equal(0.02m, JsonSerializer.Deserialize<TaxReportSnapshot>(retainedReturns!.Single(x => x.Id == amended.Id).SnapshotJson)!.Withheld);
        var correctedCertificate = await owner.GetFromJsonAsync<JsonElement>($"{TaxRoot(s, reg)}/withholding/{first.Id}/certificate");
        Assert.NotEqual(JsonValueKind.Null, correctedCertificate.GetProperty("correctedBy").ValueKind);
    }

    [Fact]
    public async Task OrdinaryApPaymentNeedsExplicitIneligibilityAttestationBeforeLaterWithholding()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var reg = await TaxRegistration(s, reviewer);
        var bankLedger = await TaxAccount(s, "1500", AccountClass.Asset);
        var payable = await TaxAccount(s, "2500", AccountClass.Liability);
        await TaxControl(s, reg, TaxControlRole.Clearing, s.Accounts["1100"].Id);
        await TaxControl(s, reg, TaxControlRole.Withholding, payable.Id);
        var bankRoot = TaxBase(s).Replace("/tax-accounting", "/banking", StringComparison.Ordinal);
        var profile = await Send<BankProfile>(owner, HttpMethod.Post, bankRoot + "/banks",
            new BankProfileRequest("TXBANK", "Tax bank", "ABCDUS33"));
        var bank = await Send<TreasuryAccountResponse>(owner, HttpMethod.Post, bankRoot + "/accounts",
            new TreasuryAccountRequest(TreasuryAccountKind.Bank, "TXACCOUNT", "Bank ledger", bankLedger.Id, profile.Id, "123456789"));
        var supplier = await Party(s, PartyKind.Supplier);
        var bill = await Post(s, await Approve(s, await Draft(s, supplier, 0.15m)));
        await Send<PaymentResponse>(owner, HttpMethod.Post, s.Root + "/payments",
            new PaymentRequest(supplier.Id, bankLedger.Id, 0.05m, Today, "Ordinary settlement", "ordinary",
                Allocations: [new(bill.Id, 0.05m)], BankAccountId: bank.Id));
        var input = new TaxOperationRequest(TaxOperationKind.Withhold, "MIXED", Today, "Eligible remainder",
            BillId: bill.Id, BankAccountId: bank.Id, Amount: 0.10m, Rate: 10,
            PolicyEvidence: "Owner attests the earlier ordinary payment was ineligible");
        var route = TaxRoot(s, reg) + "/operations";
        await Denied(owner, route, input, HttpStatusCode.Conflict);
        var other = await Arrange();
        using (other.Client)
        using (var crossCompany = await other.Client.PostAsJsonAsync(route,
            input with { Reference = "FOREIGN", PriorOrdinarySettlementsIneligible = true }))
            Assert.True(crossCompany.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        var attested = await Send<TaxOperation>(owner, HttpMethod.Post, route,
            input with { PriorOrdinarySettlementsIneligible = true });
        await Denied(owner, $"{route}/{attested.Id}/approve", new { }, HttpStatusCode.Conflict);
        await Send<TaxOperation>(reviewer, HttpMethod.Post, $"{route}/{attested.Id}/approve", new { });
        var posted = await Send<TaxOperation>(owner, HttpMethod.Post, $"{route}/{attested.Id}/post", new { });
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.True(await db.AccountingAudit.AsNoTracking().AnyAsync(x => x.CompanyId == s.Company.Id &&
                x.EntityId == posted.Id && x.Action == "TaxPriorSettlementIneligible"));
        }
        Assert.Equal(0.01m, posted.Amount);
        Assert.Equal(0.01m, (await Lines(posted.JournalId!.Value)).Single(x => x.AccountId == payable.Id).Credit);
        Assert.Equal(0m, (await owner.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{bill.Id}"))!.Outstanding);
        var report = await owner.GetFromJsonAsync<TaxReportSnapshot>($"{TaxRoot(s, reg)}/report?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(report!.Agrees);
        Assert.Equal(0.01m, report.Withheld);
        var competingBill = await Post(s, await Approve(s, await Draft(s, supplier, 0.15m)));
        var competing = await Send<TaxOperation>(owner, HttpMethod.Post, route,
            input with { Reference = "APPROVAL-RACE", BillId = competingBill.Id,
                PriorOrdinarySettlementsIneligible = false });
        async Task<HttpStatusCode> ApproveTax()
        {
            using var response = await reviewer.PostAsJsonAsync($"{route}/{competing.Id}/approve", new { });
            return response.StatusCode;
        }
        async Task<HttpStatusCode> PayOrdinary()
        {
            using var response = await owner.PostAsJsonAsync(s.Root + "/payments",
                new PaymentRequest(supplier.Id, bankLedger.Id, 0.05m, Today, "Ordinary settlement", "ordinary-race",
                    Allocations: [new(competingBill.Id, 0.05m)], BankAccountId: bank.Id));
            return response.StatusCode;
        }
        var race = await Task.WhenAll(ApproveTax(), PayOrdinary());
        Assert.Equal(HttpStatusCode.OK, race[1]);
        Assert.Contains(race[0], new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        await Denied(owner, $"{route}/{competing.Id}/post", new { }, HttpStatusCode.Conflict);
        var pinned = await Send<TaxOperation>(owner, HttpMethod.Post, route,
            input with { Reference = "PINNED-ORDINARY", BillId = competingBill.Id, Amount = 0.05m,
                PriorOrdinarySettlementsIneligible = true });
        await Send<TaxOperation>(reviewer, HttpMethod.Post, $"{route}/{pinned.Id}/approve", new { });
        await Send<PaymentResponse>(owner, HttpMethod.Post, s.Root + "/payments",
            new PaymentRequest(supplier.Id, bankLedger.Id, 0.01m, Today, "Later ordinary settlement", "ordinary-later",
                Allocations: [new(competingBill.Id, 0.01m)], BankAccountId: bank.Id));
        await Denied(owner, $"{route}/{pinned.Id}/post", new { }, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ZeroFirstAndIntermediateWithholdingIncrementsRetainTheirBasis()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var reg = await TaxRegistration(s, reviewer);
        var bankLedger = await TaxAccount(s, "1500", AccountClass.Asset);
        var payable = await TaxAccount(s, "2500", AccountClass.Liability);
        await TaxControl(s, reg, TaxControlRole.Clearing, s.Accounts["1100"].Id);
        await TaxControl(s, reg, TaxControlRole.Withholding, payable.Id);
        var bankRoot = TaxBase(s).Replace("/tax-accounting", "/banking", StringComparison.Ordinal);
        var profile = await Send<BankProfile>(owner, HttpMethod.Post, bankRoot + "/banks",
            new BankProfileRequest("TXBANK", "Tax bank", "ABCDUS33"));
        var bank = await Send<TreasuryAccountResponse>(owner, HttpMethod.Post, bankRoot + "/accounts",
            new TreasuryAccountRequest(TreasuryAccountKind.Bank, "TXACCOUNT", "Bank ledger", bankLedger.Id, profile.Id, "123456789"));
        var bill = await Post(s, await Approve(s, await Draft(s, await Party(s, PartyKind.Supplier), 0.20m)));
        decimal[] expected = [0m, 0m, 0.01m, 0m];
        for (var i = 0; i < expected.Length; i++)
        {
            var posted = await TaxPost(s, reviewer, reg, new(TaxOperationKind.Withhold, $"PART-{i}", Today,
                "Eligible installment", BillId: bill.Id, BankAccountId: bank.Id, Amount: 0.05m, Rate: 5,
                PolicyEvidence: "Eligible service contract"));
            Assert.Equal(expected[i], posted.Amount);
        }
        Assert.Equal(0m, (await owner.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{bill.Id}"))!.Outstanding);
        var report = await owner.GetFromJsonAsync<TaxReportSnapshot>($"{TaxRoot(s, reg)}/report?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(report!.Agrees);
        Assert.Equal(0.01m, report.Withheld);
    }

    [Fact]
    public async Task ConcurrentEligibleInstallmentsReprepareTheLoserWithoutDuplicateLiability()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var reg = await TaxRegistration(s, reviewer);
        var bankLedger = await TaxAccount(s, "1500", AccountClass.Asset);
        var payable = await TaxAccount(s, "2500", AccountClass.Liability);
        await TaxControl(s, reg, TaxControlRole.Clearing, s.Accounts["1100"].Id);
        await TaxControl(s, reg, TaxControlRole.Withholding, payable.Id);
        var bankRoot = TaxBase(s).Replace("/tax-accounting", "/banking", StringComparison.Ordinal);
        var profile = await Send<BankProfile>(owner, HttpMethod.Post, bankRoot + "/banks",
            new BankProfileRequest("TXBANK", "Tax bank", "ABCDUS33"));
        var bank = await Send<TreasuryAccountResponse>(owner, HttpMethod.Post, bankRoot + "/accounts",
            new TreasuryAccountRequest(TreasuryAccountKind.Bank, "TXACCOUNT", "Bank ledger", bankLedger.Id, profile.Id, "123456789"));
        var bill = await Post(s, await Approve(s, await Draft(s, await Party(s, PartyKind.Supplier), 0.15m)));
        var route = TaxRoot(s, reg) + "/operations";
        var input = new TaxOperationRequest(TaxOperationKind.Withhold, "RACE-FIRST", Today, "Eligible installment",
            BillId: bill.Id, BankAccountId: bank.Id, Amount: 0.05m, Rate: 10, PolicyEvidence: "Eligible service contract");
        var first = await Send<TaxOperation>(owner, HttpMethod.Post, route, input);
        var second = await Send<TaxOperation>(owner, HttpMethod.Post, route,
            input with { Reference = "RACE-SECOND", Amount = 0.10m });
        await Send<TaxOperation>(reviewer, HttpMethod.Post, $"{route}/{first.Id}/approve", new { });
        await Send<TaxOperation>(reviewer, HttpMethod.Post, $"{route}/{second.Id}/approve", new { });
        async Task<HttpStatusCode> PostOne(Guid id)
        {
            using var response = await owner.PostAsJsonAsync($"{route}/{id}/post", new { });
            return response.StatusCode;
        }
        var outcomes = await Task.WhenAll(PostOne(first.Id), PostOne(second.Id));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, outcomes.Order());
        var operations = await owner.GetFromJsonAsync<TaxOperation[]>(route);
        var winner = Assert.Single(operations!, x => x.State == TaxEvidenceState.Posted);
        var loser = operations!.Single(x => x.Id != winner.Id);
        Assert.Equal(TaxEvidenceState.Approved, loser.State);
        await Send<TaxOperation>(owner, HttpMethod.Post, $"{route}/{loser.Id}/cancel", new { });
        var remainder = winner.Id == first.Id ? 0.10m : 0.05m;
        await TaxPost(s, reviewer, reg, input with { Reference = "RACE-RETRY", Amount = remainder });
        Assert.Equal(0m, (await owner.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{bill.Id}"))!.Outstanding);
        var report = await owner.GetFromJsonAsync<TaxReportSnapshot>($"{TaxRoot(s, reg)}/report?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(report!.Agrees);
        Assert.Equal(0.02m, report.Withheld);
    }

    [Fact]
    public async Task WithholdingCorrectionRacingLaterInstallmentHasOneConsistentWinner()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var reg = await TaxRegistration(s, reviewer);
        var bankLedger = await TaxAccount(s, "1500", AccountClass.Asset);
        var payable = await TaxAccount(s, "2500", AccountClass.Liability);
        await TaxControl(s, reg, TaxControlRole.Clearing, s.Accounts["1100"].Id);
        await TaxControl(s, reg, TaxControlRole.Withholding, payable.Id);
        var bankRoot = TaxBase(s).Replace("/tax-accounting", "/banking", StringComparison.Ordinal);
        var profile = await Send<BankProfile>(owner, HttpMethod.Post, bankRoot + "/banks",
            new BankProfileRequest("TXBANK", "Tax bank", "ABCDUS33"));
        var bank = await Send<TreasuryAccountResponse>(owner, HttpMethod.Post, bankRoot + "/accounts",
            new TreasuryAccountRequest(TreasuryAccountKind.Bank, "TXACCOUNT", "Bank ledger", bankLedger.Id, profile.Id, "123456789"));
        var bill = await Post(s, await Approve(s, await Draft(s, await Party(s, PartyKind.Supplier), 0.15m)));
        var route = TaxRoot(s, reg) + "/operations";
        var first = await TaxPost(s, reviewer, reg, new(TaxOperationKind.Withhold, "RACE-BASIS", Today,
            "Eligible installment", BillId: bill.Id, BankAccountId: bank.Id, Amount: 0.05m, Rate: 10,
            PolicyEvidence: "Eligible service contract"));
        var correction = await Send<TaxOperation>(owner, HttpMethod.Post, route,
            new TaxOperationRequest(TaxOperationKind.Correction, "RACE-CORRECTION", Today, "Correction", BasisId: first.Id));
        var later = await Send<TaxOperation>(owner, HttpMethod.Post, route,
            new TaxOperationRequest(TaxOperationKind.Withhold, "RACE-LATER", Today, "Eligible installment",
                BillId: bill.Id, BankAccountId: bank.Id, Amount: 0.10m, Rate: 10,
                PolicyEvidence: "Eligible service contract"));
        await Send<TaxOperation>(reviewer, HttpMethod.Post, $"{route}/{correction.Id}/approve", new { });
        await Send<TaxOperation>(reviewer, HttpMethod.Post, $"{route}/{later.Id}/approve", new { });
        async Task<HttpStatusCode> PostOne(Guid id)
        {
            using var response = await owner.PostAsJsonAsync($"{route}/{id}/post", new { });
            return response.StatusCode;
        }
        var outcomes = await Task.WhenAll(PostOne(correction.Id), PostOne(later.Id));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, outcomes.Order());
        var operations = await owner.GetFromJsonAsync<TaxOperation[]>(route);
        var corrected = operations!.Single(x => x.Id == correction.Id).State == TaxEvidenceState.Posted;
        var nextPosted = operations!.Single(x => x.Id == later.Id).State == TaxEvidenceState.Posted;
        Assert.NotEqual(corrected, nextPosted);
        var outstanding = (await owner.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{bill.Id}"))!.Outstanding;
        Assert.Equal(corrected ? 0.15m : 0m, outstanding);
        var report = await owner.GetFromJsonAsync<TaxReportSnapshot>($"{TaxRoot(s, reg)}/report?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(report!.Agrees);
        Assert.Equal(corrected ? 0m : 0.02m, report.Withheld);
    }

    [Fact]
    public async Task DeferredTaxCommitFailureRollsBackSupplierPaymentAndPreservesOriginalException()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var reg = await TaxRegistration(s, reviewer);
        var bankLedger = await TaxAccount(s, "1500", AccountClass.Asset);
        var payable = await TaxAccount(s, "2500", AccountClass.Liability);
        await TaxControl(s, reg, TaxControlRole.Clearing, s.Accounts["1100"].Id);
        await TaxControl(s, reg, TaxControlRole.Withholding, payable.Id);
        var bankRoot = TaxBase(s).Replace("/tax-accounting", "/banking", StringComparison.Ordinal);
        var profile = await Send<BankProfile>(owner, HttpMethod.Post, bankRoot + "/banks",
            new BankProfileRequest("TXBANK", "Tax bank", "ABCDUS33"));
        var bank = await Send<TreasuryAccountResponse>(owner, HttpMethod.Post, bankRoot + "/accounts",
            new TreasuryAccountRequest(TreasuryAccountKind.Bank, "TXACCOUNT", "Bank ledger", bankLedger.Id, profile.Id, "123456789"));
        var bill = await Post(s, await Approve(s, await Draft(s, await Party(s, PartyKind.Supplier), 0.15m)));
        var route = TaxRoot(s, reg) + "/operations";
        var draft = await Send<TaxOperation>(owner, HttpMethod.Post, route,
            new TaxOperationRequest(TaxOperationKind.Withhold, "FAIL-AT-COMMIT", Today, "Eligible installment",
                BillId: bill.Id, BankAccountId: bank.Id, Amount: 0.05m, Rate: 10,
                PolicyEvidence: "Eligible service contract"));
        await Send<TaxOperation>(reviewer, HttpMethod.Post, $"{route}/{draft.Id}/approve", new { });
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var install = new NpgsqlCommand("""
            CREATE FUNCTION tax.wp06_forced_commit_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected deferred tax failure' USING ERRCODE='23514'; END $$;
            CREATE CONSTRAINT TRIGGER z_wp06_forced_commit_failure AFTER UPDATE ON tax."Operations"
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW."State"=3)
            EXECUTE FUNCTION tax.wp06_forced_commit_failure();
            """, connection))
            await install.ExecuteNonQueryAsync();
        try
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<TaxOperationService>();
            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                service.Change(s.Actor, s.Org, s.Company.Id, reg, draft.Id, "post", default));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            Assert.Contains("injected deferred tax failure", failure.MessageText);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("""
                DROP TRIGGER z_wp06_forced_commit_failure ON tax."Operations";
                DROP FUNCTION tax.wp06_forced_commit_failure();
                """, connection);
            await cleanup.ExecuteNonQueryAsync();
        }
        var operations = await owner.GetFromJsonAsync<TaxOperation[]>(route);
        var retained = Assert.Single(operations!, x => x.Id == draft.Id);
        Assert.Equal(TaxEvidenceState.Approved, retained.State);
        Assert.Null(retained.PaymentId);
        Assert.Null(retained.JournalId);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            Assert.False(await db.Payments.AsNoTracking().AnyAsync(x => x.CompanyId == s.Company.Id &&
                x.RequestKey == $"tax-ap-{draft.Id:N}"));
            Assert.False(await db.Allocations.AsNoTracking().AnyAsync(x => x.CompanyId == s.Company.Id &&
                x.TargetDocumentId == bill.Id));
            Assert.False(await db.Journals.AsNoTracking().AnyAsync(x => x.CompanyId == s.Company.Id &&
                x.SourceModule == "Tax.Operation" && x.SourceDocumentId == draft.Id));
        }
        Assert.Equal(0.15m, (await owner.GetFromJsonAsync<TradeDocumentResponse>($"{s.Root}/documents/{bill.Id}"))!.Outstanding);
        var report = await owner.GetFromJsonAsync<TaxReportSnapshot>($"{TaxRoot(s, reg)}/report?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.True(report!.Agrees);
        Assert.Equal(0m, report.Withheld);
    }

    [Fact]
    public async Task CurrentProvisionUsesExternalBasisAndGuardsControlAndDuplicatePosting()
    {
        var s = await Arrange();
        using var owner = s.Client;
        using var reviewer = await TaxReviewer(s);
        var reg = await TaxRegistration(s, reviewer);
        var expense = await TaxAccount(s, "5800", AccountClass.Expense);
        var payable = await TaxAccount(s, "2800", AccountClass.Liability);
        await TaxControl(s, reg, TaxControlRole.CurrentExpense, expense.Id);
        await TaxControl(s, reg, TaxControlRole.CurrentPayable, payable.Id);
        var root = TaxRoot(s, reg);
        var period = await Send<TaxPeriod>(owner, HttpMethod.Post, root + "/periods", new TaxPeriodRequest(Today, Today, "Income tax estimate"));
        var input = new TaxOperationRequest(TaxOperationKind.CurrentProvision, "CURRENT-1", Today, "Approved estimate", PeriodId: period.Id,
            Base: 900, Rate: 25, AccountingAdjustments: 100, PolicyEvidence: "External computation reference");
        var provision = await TaxPost(s, reviewer, reg, input);
        var ledger = await Lines(provision.JournalId!.Value);
        Assert.Equal(250m, ledger.Single(x => x.AccountId == expense.Id).Debit);
        Assert.Equal(250m, ledger.Single(x => x.AccountId == payable.Id).Credit);
        await Denied(owner, root + "/operations", input with { Reference = "CURRENT-DUP" }, HttpStatusCode.Conflict);
        var manual = await Send<JournalResponse>(owner, HttpMethod.Post, s.Core + "/journals", new JournalRequest(Today, Today, "Unauthorized",
            null, [new(expense.Id, 1, 0, null), new(payable.Id, 0, 1, null)]), HttpStatusCode.Created);
        await Denied(owner, $"{s.Core}/journals/{manual.Id}/post", new PostJournalRequest("tax-direct", manual.Version), HttpStatusCode.Conflict);
    }

    private async Task<HttpClient> TaxReviewer(Setup s)
    {
        var reviewer = fixture.Factory.CreateClient();
        var token = await Send<TokenResponse>(reviewer, HttpMethod.Post, "/api/v1/auth/onboard", new OnboardRequest(
            $"{Guid.NewGuid():N}@tax.example", "Taxes_Test-Password1!", "Tax reviewer org", ApiFactory.OnboardingKey));
        reviewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var me = await reviewer.GetFromJsonAsync<JsonElement>("/api/v1/me");
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        db.OrganizationMembers.Add(new() { TenantId = s.Company.TenantId, OrganizationId = s.Org, UserId = me.GetProperty("userId").GetGuid(),
            Role = OrganizationRole.Owner });
        await db.SaveChangesAsync();
        return reviewer;
    }

    private static string TaxBase(Setup s) => $"/api/v1/organizations/{s.Org}/companies/{s.Company.Id}/tax-accounting";
    private static string TaxRoot(Setup s, Guid reg) => $"{TaxBase(s)}/registrations/{reg}";

    private static async Task<Guid> TaxRegistration(Setup s, HttpClient reviewer)
    {
        var row = await Send<TaxRegistration>(s.Client, HttpMethod.Post, TaxBase(s) + "/registrations",
            new TaxRegistrationRequest("VAT", "US", null, "Illustrative authority", "REG-1", "US", "USD", Today, "Internal test policy"));
        await Send<TaxRegistration>(reviewer, HttpMethod.Post, TaxRoot(s, row.Id) + "/approve", new { });
        return row.Id;
    }

    private static Task<TaxControl> TaxControl(Setup s, Guid reg, TaxControlRole role, Guid account) =>
        Send<TaxControl>(s.Client, HttpMethod.Post, TaxRoot(s, reg) + "/controls", new TaxControlRequest(role, account, "Tax control"));
    private static Task<AccountResponse> TaxAccount(Setup s, string code, AccountClass classification) =>
        Send<AccountResponse>(s.Client, HttpMethod.Post, s.Core + "/accounts", new AccountRequest(code, code, null, classification, true), HttpStatusCode.Created);
    private static async Task<TaxRateResponse> TaxRate(Setup s, decimal rate)
    {
        var code = await Send<TaxCodeResponse>(s.Client, HttpMethod.Post, s.Root + "/tax-codes",
            new TaxCodeRequest("TAX", "Illustrative tax", s.Accounts["2200"].Id, s.Accounts["1300"].Id), HttpStatusCode.Created);
        return await Send<TaxRateResponse>(s.Client, HttpMethod.Post, s.Root + $"/tax-codes/{code.Id}/rates",
            new TaxRateRequest(Today, Today, rate), HttpStatusCode.Created);
    }

    private static async Task<TaxOperation> TaxPost(Setup s, HttpClient reviewer, Guid reg, TaxOperationRequest input)
    {
        var route = TaxRoot(s, reg) + "/operations";
        var draft = await Send<TaxOperation>(s.Client, HttpMethod.Post, route, input);
        await Send<TaxOperation>(reviewer, HttpMethod.Post, $"{route}/{draft.Id}/approve", new { });
        return await Send<TaxOperation>(s.Client, HttpMethod.Post, $"{route}/{draft.Id}/post", new { });
    }
}
