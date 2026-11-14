using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Services.MultiCurrency;
using Xunit;

namespace RAQM.Tests;

public sealed partial class MultiCurrencyIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonMonetaryAdvanceIsExcludedFromRevaluationAndAppliesAtHistoricalBasis(bool customer)
    {
        using var s = await Arrange();
        await Rate(s, s.Date, 1.10m);
        var advance = await Execute(s, new("advance", FxKind.Advance, s.Date, "Non-refundable consideration",
            s.Currency.Id, customer ? s.Customer.Id : s.Supplier.Id, FunctionalCashAccountId: s.Chart["1100"],
            ForeignAmount: 40, NonRefundableAdvance: true));
        var cutoff = s.Year.Periods[0].EndDate;
        await Rate(s, cutoff, 1.40m, FxRateType.Closing);
        using var nothing = await s.Maker.Client.PostAsJsonAsync(s.Fx + "/operations", new FxRequest("close", FxKind.Revaluation, cutoff, "Closing"));
        Assert.Equal(HttpStatusCode.Conflict, nothing.StatusCode);
        var date = cutoff.AddDays(1);
        await Rate(s, date, 1.30m);
        var document = await Execute(s, Invoice(s, customer, date, 100) with { AdvancePositionId = advance.PositionId, AppliedAdvance = 40 });
        var plan = Plan(document);
        Assert.Equal(44m, plan.AdvanceBasis);
        Assert.Equal(122m, plan.BaseAmount);
        var report = await Report(s, date);
        Assert.Equal(78m, Position(report, document.PositionId!.Value).GetProperty("baseAmount").GetDecimal());
        Assert.Equal(0m, Position(report, advance.PositionId!.Value).GetProperty("foreignAmount").GetDecimal());
        Assert.True(report.GetProperty("agrees").GetBoolean());
    }

    [Fact]
    public async Task FullCreditOfPaidInvoiceCreatesRefundAtTheHistoricBasis()
    {
        using var s = await Arrange();
        await Rate(s, s.Date, 1.10m);
        var invoice = await Execute(s, Invoice(s, true, s.Date, 100));
        var receiptDate = s.Date.AddDays(1);
        await Rate(s, receiptDate, 1.20m);
        Assert.Equal(10m, Plan(await Execute(s, Settlement(s, invoice, 100, receiptDate, "receipt"))).Realized);
        var credit = await Execute(s, new("credit", FxKind.Credit, receiptDate, "Full linked credit", OriginalId: invoice.Id));
        var creditLines = await Journal(credit);
        Assert.Equal(110m, creditLines.Single(x => x.AccountId == s.Chart["4100"]).Debit);
        var creditPosition = Position(await Report(s, receiptDate), credit.PositionId!.Value);
        Assert.Equal((100m, 110m), (creditPosition.GetProperty("foreignAmount").GetDecimal(), creditPosition.GetProperty("baseAmount").GetDecimal()));
        var refundDate = receiptDate.AddDays(1);
        await Rate(s, refundDate, 1.30m);
        var refund = await Execute(s, Settlement(s, credit, 100, refundDate, "refund"));
        Assert.Equal(-20m, Plan(refund).Realized);
        var report = await Report(s, refundDate);
        Assert.True(report.GetProperty("agrees").GetBoolean());
        Assert.Equal(-10m, report.GetProperty("companyExchangeIncome").EnumerateArray().Sum(x => x.GetProperty("signedIncome").GetDecimal()));
    }

    [Fact]
    public async Task ForeignBankExchangeRevaluesAndRelievesItsOwnCarryingCost()
    {
        using var s = await Arrange();
        var bank = await Send<FxPosition>(s.Owner.Client, s.Fx + "/banks", new FxBankRequest(s.Currency.Id, s.Chart["EUR-BANK"], "EUR-BANK"));
        await Rate(s, s.Date, 1.10m);
        var purchase = await Execute(s, new("buy", FxKind.BankPurchase, s.Date, "FX deal", BankPositionId: bank.Id,
            FunctionalCashAccountId: s.Chart["1100"], ForeignAmount: 100, BaseFee: 2, FeeAccountId: s.Chart["5100"]));
        Assert.Equal(112m, (await Journal(purchase)).Single(x => x.AccountId == s.Chart["1100"]).Credit);
        var statement = await Send<FxBankStatement>(s.Owner.Client, $"{s.Fx}/banks/{bank.Id}/statements",
            new FxBankStatementRequest(s.Date, 100, "Opening bank advice"));
        Assert.Equal(100m, statement.BookForeign);
        using var blocked = await s.Maker.Client.PostAsJsonAsync(s.Fx + "/operations", new FxRequest("cutoff", FxKind.BankSale, s.Date,
            "FX deal", BankPositionId: bank.Id, FunctionalCashAccountId: s.Chart["1100"], ForeignAmount: 10));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var cutoff = s.Year.Periods[0].EndDate;
        await Rate(s, cutoff, 1.20m, FxRateType.Closing);
        Assert.Equal(10m, Plan(await Execute(s, new("bank-revalue", FxKind.Revaluation, cutoff, "Closing"))).Unrealized);
        var date = cutoff.AddDays(1);
        await Rate(s, date, 1.30m);
        var sale = await Execute(s, new("sell", FxKind.BankSale, date, "FX deal", BankPositionId: bank.Id,
            FunctionalCashAccountId: s.Chart["1100"], ForeignAmount: 40));
        Assert.Equal(4m, Plan(sale).Realized);
        var report = await Report(s, date);
        var balance = Position(report, bank.Id);
        Assert.Equal((60m, 72m), (balance.GetProperty("foreignAmount").GetDecimal(), balance.GetProperty("baseAmount").GetDecimal()));
        Assert.True(report.GetProperty("agrees").GetBoolean());
        var cash = await Send<FinancialReport>(s.Owner.Client, s.Root + "/financial-reporting/query", new ReportQuery(ReportKind.CashMovement, s.Date, date));
        Assert.Equal(0m, cash.IntegrityDifference);
        Assert.Equal(14m, cash.Rows.Single(x => x.Key == "cash:fx-effect").Amount);
        Assert.Equal(2m, cash.Rows.Single(x => x.Key == "cash:out").Amount);
    }

    [Fact]
    public async Task RevaluationBlocksCloseUntilPostedAndAllowsOneCorrectionAndReplacement()
    {
        using var s = await Arrange(required: true);
        await Rate(s, s.Date, 1.10m);
        await Execute(s, Invoice(s, true, s.Date, 100));
        var period = s.Year.Periods[0];
        var path = $"{s.Core}/periods/{period.Id}/state";
        using var blocked = await s.Owner.Client.PutAsJsonAsync(path, new PeriodStateRequest(PeriodState.Closed, "Close", period.Version));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var originalRate = await Rate(s, period.EndDate, 1.20m, FxRateType.Closing);
        var input = new FxRequest("close", FxKind.Revaluation, period.EndDate, "Closing");
        var first = await Change(s, await Prepare(s, input), "approve");
        var other = await Change(s, await Prepare(s, input with { Key = "duplicate" }), "approve");
        async Task<(Guid Id, HttpStatusCode Status)> Attempt(FxOperation op)
        {
            using var result = await s.Owner.Client.PostAsJsonAsync($"{s.Fx}/operations/{op.Id}/post", new FxAction(op.Version, "Approved", true));
            return (op.Id, result.StatusCode);
        }
        var attempts = await Task.WhenAll(Attempt(first), Attempt(other));
        Assert.Single(attempts, x => x.Status == HttpStatusCode.Conflict);
        var id = Assert.Single(attempts, x => x.Status == HttpStatusCode.OK).Id;
        var correction = await Execute(s, new("correction", FxKind.RevaluationCorrection, period.EndDate, "Correct quote", OriginalId: id));
        Assert.Equal(-10m, Plan(correction).Unrealized);
        var revisedRate = await Rate(s, period.EndDate, 1.15m, FxRateType.Closing, originalRate.Id);
        var replacement = await Execute(s, input with { Key = "replace", OriginalId = id });
        Assert.Equal(5m, Plan(replacement).Unrealized);
        var closed = await Send<PeriodResponse>(s.Owner.Client, path, new PeriodStateRequest(PeriodState.Closed, "Verified", period.Version), method: HttpMethod.Put);
        Assert.Equal(PeriodState.Closed, closed.State);
        var afterClose = await Send<FxRate>(s.Maker.Client, s.Fx + "/rates", new FxRateRequest(s.Currency.Id,
            FxRateType.Closing, period.EndDate, 1.16m, "Late revision", "Verified external quote", revisedRate.Id));
        using var deniedRate = await s.Owner.Client.PostAsJsonAsync($"{s.Fx}/rates/{afterClose.Id}/approve",
            new FxAction(afterClose.Version, "Closed period"));
        Assert.Equal(HttpStatusCode.Conflict, deniedRate.StatusCode);
        using var closedRevalue = await s.Maker.Client.PostAsJsonAsync(s.Fx + "/operations", input with { Key = "closed" });
        Assert.Equal(HttpStatusCode.Conflict, closedRevalue.StatusCode);
    }

    [Fact]
    public async Task R02_005_LaterFxPreventsSupersessionOfDependentClosingRate()
    {
        using var s = await Arrange(required: true);
        await Rate(s, s.Date, 1.10m);
        var invoice = await Execute(s, Invoice(s, true, s.Date, 100));
        var period = s.Year.Periods[0];
        var closing = await Rate(s, period.EndDate, 1.20m, FxRateType.Closing);
        var original = await Execute(s, new("initial-revaluation", FxKind.Revaluation, period.EndDate, "Original rate"));
        Assert.Equal(10m, Plan(original).Unrealized);
        var later = period.EndDate.AddDays(1);
        await Rate(s, later, 1.25m);
        var settlement = await Execute(s, Settlement(s, invoice, 20, later, "later-settlement"));
        Assert.NotNull(settlement.JournalId);
        var draft = await Send<FxRate>(s.Maker.Client, s.Fx + "/rates", new FxRateRequest(s.Currency.Id,
            FxRateType.Closing, period.EndDate, 1.15m, "Revised quote", "Verified external quote", closing.Id));
        using var supersession = await s.Owner.Client.PostAsJsonAsync($"{s.Fx}/rates/{draft.Id}/approve",
            new FxAction(draft.Version, "Revised quote"));
        Assert.Equal(HttpStatusCode.Conflict, supersession.StatusCode);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.Equal(FxRateState.Approved, (await db.FxRates.AsNoTracking().SingleAsync(x => x.Id == closing.Id)).State);
        Assert.Equal(FxRateState.Draft, (await db.FxRates.AsNoTracking().SingleAsync(x => x.Id == draft.Id)).State);
        using var correction = await s.Maker.Client.PostAsJsonAsync(s.Fx + "/operations",
            new FxRequest("early-correction", FxKind.RevaluationCorrection, period.EndDate, "Correct earlier rate", OriginalId: original.Id));
        Assert.True(correction.StatusCode == HttpStatusCode.Conflict,
            $"Correction returned {(int)correction.StatusCode}: {await correction.Content.ReadAsStringAsync()}");
        Assert.NotNull(original.JournalId);
        var historical = await Report(s, period.EndDate);
        Assert.True(historical.GetProperty("agrees").GetBoolean());
        Assert.Equal(120m, Position(historical, invoice.PositionId!.Value).GetProperty("baseAmount").GetDecimal());
        var current = await Report(s, later);
        Assert.True(current.GetProperty("agrees").GetBoolean());
        Assert.Equal(96m, Position(current, invoice.PositionId.Value).GetProperty("baseAmount").GetDecimal());
        var findings = (await scope.ServiceProvider.GetRequiredService<FxReporting>().Check(s.Company.Id,
            await db.Periods.AsNoTracking().SingleAsync(x => x.Id == period.Id), default)).Findings;
        Assert.DoesNotContain(findings, x => x.Rule == "fx-revaluation");
    }

    [Fact]
    public async Task R02_006_RevaluationCapacityIsExplicitAt499_500And501Positions()
    {
        using var s = await Arrange(required: true);
        await Rate(s, s.Date, 1.10m);
        var cutoff = s.Year.Periods[0].EndDate;
        await Rate(s, cutoff, 1.20m, FxRateType.Closing);
        FxOperation? lastInvoice = null;
        for (var count = 1; count <= 501; count++)
        {
            lastInvoice = await Execute(s, Invoice(s, true, s.Date, 1, $"capacity-{count}"));
            if (count is 499 or 500)
            {
                var draft = await Prepare(s, new($"batch-{count}", FxKind.Revaluation, cutoff, "Capacity boundary"));
                Assert.Equal(FxState.Draft, draft.State);
            }
        }
        using var rejected = await s.Maker.Client.PostAsJsonAsync(s.Fx + "/operations",
            new FxRequest("batch-501", FxKind.Revaluation, cutoff, "Capacity boundary"));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("501 open monetary positions", await rejected.Content.ReadAsStringAsync());
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var period = await db.Periods.AsNoTracking().SingleAsync(x => x.Id == s.Year.Periods[0].Id);
        var findings = (await scope.ServiceProvider.GetRequiredService<FxReporting>().Check(s.Company.Id, period, default)).Findings;
        Assert.Contains(findings, x => x.Rule == "fx-revaluation-capacity" && x.Severity == FindingSeverity.Blocking);
        await Execute(s, Settlement(s, lastInvoice!, 1, s.Date, "capacity-relief"));
        var posted = await Execute(s, new("posted-batch-500", FxKind.Revaluation, cutoff, "Complete cutoff"));
        Assert.Equal(500, Plan(posted).Valuations!.Count);
        Assert.Equal(50m, Plan(posted).Unrealized);
        Assert.Equal(1000, (await Journal(posted)).Count);
        Assert.DoesNotContain((await scope.ServiceProvider.GetRequiredService<FxReporting>().Check(s.Company.Id, period, default)).Findings,
            x => x.Rule is "fx-revaluation-capacity" or "fx-revaluation");
    }

    [Fact]
    public async Task ClosingRateApprovalAndRevaluationPostHaveOneConsistentWinner()
    {
        using var s = await Arrange(required: true);
        await Rate(s, s.Date, 1.10m);
        var invoice = await Execute(s, Invoice(s, true, s.Date, 100));
        var cutoff = s.Year.Periods[0].EndDate;
        var firstRate = await Rate(s, cutoff, 1.20m, FxRateType.Closing);
        var batch = await Change(s, await Prepare(s, new("race-batch", FxKind.Revaluation, cutoff, "Closing")), "approve");
        var replacement = await Send<FxRate>(s.Maker.Client, s.Fx + "/rates", new FxRateRequest(s.Currency.Id,
            FxRateType.Closing, cutoff, 1.25m, "Revised quote", "Verified external quote", firstRate.Id));
        async Task<HttpStatusCode> ApproveRate()
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Fx}/rates/{replacement.Id}/approve",
                new FxAction(replacement.Version, "Verified"));
            return response.StatusCode;
        }
        async Task<HttpStatusCode> PostBatch()
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Fx}/operations/{batch.Id}/post",
                new FxAction(batch.Version, "Verified", true));
            return response.StatusCode;
        }
        var outcomes = await Task.WhenAll(ApproveRate(), PostBatch());
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, outcomes.Order());
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var rate = await db.FxRates.AsNoTracking().SingleAsync(x => x.Id == firstRate.Id);
        var operation = await db.FxOperations.AsNoTracking().SingleAsync(x => x.Id == batch.Id);
        Assert.Equal(rate.State == FxRateState.Approved, operation.State == FxState.Posted);
        Assert.Equal(operation.State == FxState.Posted ? 120m : 110m,
            Position(await Report(s, cutoff), invoice.PositionId!.Value).GetProperty("baseAmount").GetDecimal());
    }

    [Fact]
    public async Task RevaluationCorrectionAndPeriodCloseHaveOneConsistentWinner()
    {
        using var s = await Arrange(required: true);
        await Rate(s, s.Date, 1.10m);
        await Execute(s, Invoice(s, true, s.Date, 100));
        var period = s.Year.Periods[0];
        await Rate(s, period.EndDate, 1.20m, FxRateType.Closing);
        var batch = await Execute(s, new("close-race-batch", FxKind.Revaluation, period.EndDate, "Closing"));
        var correction = await Change(s, await Prepare(s, new("close-race-correction", FxKind.RevaluationCorrection,
            period.EndDate, "Correction", OriginalId: batch.Id)), "approve");
        async Task<HttpStatusCode> Close()
        {
            using var response = await s.Owner.Client.PutAsJsonAsync($"{s.Core}/periods/{period.Id}/state",
                new PeriodStateRequest(PeriodState.Closed, "Verified", period.Version));
            return response.StatusCode;
        }
        async Task<HttpStatusCode> Correct()
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Fx}/operations/{correction.Id}/post",
                new FxAction(correction.Version, "Verified", true));
            return response.StatusCode;
        }
        var outcomes = await Task.WhenAll(Close(), Correct());
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, outcomes.Order());
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var closed = (await db.Periods.AsNoTracking().SingleAsync(x => x.Id == period.Id)).State == PeriodState.Closed;
        var corrected = (await db.FxOperations.AsNoTracking().SingleAsync(x => x.Id == correction.Id)).State == FxState.Posted;
        Assert.NotEqual(closed, corrected);
    }

    [Fact]
    public async Task ClosingRateSupersessionLosesToConcurrentLaterSettlement()
    {
        using var s = await Arrange(required: true);
        await Rate(s, s.Date, 1.10m);
        var invoice = await Execute(s, Invoice(s, true, s.Date, 100));
        var cutoff = s.Year.Periods[0].EndDate;
        var closing = await Rate(s, cutoff, 1.20m, FxRateType.Closing);
        await Execute(s, new("settlement-race-batch", FxKind.Revaluation, cutoff, "Closing"));
        var later = cutoff.AddDays(1);
        await Rate(s, later, 1.25m);
        var settlement = await Change(s, await Prepare(s, Settlement(s, invoice, 20, later, "settlement-race")), "approve");
        var revision = await Send<FxRate>(s.Maker.Client, s.Fx + "/rates", new FxRateRequest(s.Currency.Id,
            FxRateType.Closing, cutoff, 1.15m, "Revised quote", "Verified external quote", closing.Id));
        async Task<HttpStatusCode> Supersede()
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Fx}/rates/{revision.Id}/approve",
                new FxAction(revision.Version, "Verified"));
            return response.StatusCode;
        }
        async Task<HttpStatusCode> Settle()
        {
            using var response = await s.Owner.Client.PostAsJsonAsync($"{s.Fx}/operations/{settlement.Id}/post",
                new FxAction(settlement.Version, "Verified", true));
            return response.StatusCode;
        }
        var outcomes = await Task.WhenAll(Supersede(), Settle());
        Assert.Equal(HttpStatusCode.Conflict, outcomes[0]);
        Assert.Equal(HttpStatusCode.OK, outcomes[1]);
        var report = await Report(s, later);
        Assert.True(report.GetProperty("agrees").GetBoolean());
        Assert.Equal(96m, Position(report, invoice.PositionId!.Value).GetProperty("baseAmount").GetDecimal());
    }

    [Fact]
    public async Task CrossCompanyRateAndPositionLinksFailAndManualControlsStayProtected()
    {
        using var s = await Arrange();
        using var other = await Arrange();
        using var denied = await s.Maker.Client.PostAsJsonAsync(s.Fx + "/rates", new FxRateRequest(other.Currency.Id,
            FxRateType.Spot, s.Date, 1.10m, "Quote", "Cross-company"));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var draft = await Send<JournalResponse>(s.Owner.Client, s.Core + "/journals", new JournalRequest(s.Date, s.Date, "Invalid control",
            null, [new(s.Accounts[FxAccountRole.Receivable], 100, 0, null), new(s.Chart["4100"], 0, 100, null)]), HttpStatusCode.Created);
        using var manual = await s.Owner.Client.PostAsJsonAsync($"{s.Core}/journals/{draft.Id}/post", new PostJournalRequest("manual", draft.Version, true, "Invalid"));
        Assert.Equal(HttpStatusCode.Forbidden, manual.StatusCode);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.False(await db.FxEffects.AnyAsync(x => x.CompanyId == s.Company.Id));
    }

    [Fact]
    public async Task ForeignBankPaymentSeparatelyRelievesPayableAndBankAndRejectsAnImplicitConversion()
    {
        using var s = await Arrange();
        var bank = await Send<FxPosition>(s.Owner.Client, s.Fx + "/banks", new FxBankRequest(s.Currency.Id, s.Chart["EUR-BANK"], "EUR-BANK"));
        await Rate(s, s.Date, 1.10m);
        await Execute(s, new("funding", FxKind.BankPurchase, s.Date, "Funding", BankPositionId: bank.Id,
            FunctionalCashAccountId: s.Chart["1100"], ForeignAmount: 100));
        var bill = await Execute(s, Invoice(s, false, s.Date, 100));
        var paymentDate = s.Date.AddDays(1);
        await Rate(s, paymentDate, 1.20m);
        var payment = Settlement(s, bill, 60, paymentDate, "payment", bank.Id);
        using var implicitConversion = await s.Maker.Client.PostAsJsonAsync(s.Fx + "/operations", payment with
        {
            Key = "invalid", BankPositionId = null, FunctionalCashAccountId = bank.AccountId
        });
        Assert.Equal(HttpStatusCode.BadRequest, implicitConversion.StatusCode);
        var posted = await Execute(s, payment);
        var lines = await Journal(posted);
        Assert.Equal(66m, lines.Single(x => x.AccountId == s.Accounts[FxAccountRole.Payable]).Debit);
        Assert.Equal(66m, lines.Single(x => x.AccountId == bank.AccountId).Credit);
        Assert.Equal(6m, lines.Single(x => x.AccountId == s.Accounts[FxAccountRole.RealizedLoss]).Debit);
        Assert.Equal(6m, lines.Single(x => x.AccountId == s.Accounts[FxAccountRole.RealizedGain]).Credit);
        Assert.True((await Report(s, paymentDate)).GetProperty("agrees").GetBoolean());
    }

    [Fact]
    public async Task BaseAndForeignInvoicesShareTheCustomerCreditLimit()
    {
        using var s = await Arrange();
        var customer = s.Customer;
        await Send<PartyResponse>(s.Owner.Client, $"{s.Root}/trading/parties/{customer.Id}",
            new UpdatePartyRequest(new PartyRequest(customer.Kind, customer.Code, customer.Name, null, null, null, null,
                customer.PaymentTermsDays, 150, customer.ControlAccountId, customer.DefaultAccountId), customer.Version), method: HttpMethod.Put);
        await Rate(s, s.Date, 1.10m);
        await Execute(s, Invoice(s, true, s.Date, 100));
        var path = s.Root + "/trading/documents";
        var row = await Send<TradeDocumentResponse>(s.Owner.Client, path, new TradeDocumentRequest(customer.Id, TradeKind.Invoice,
            s.Date, null, "Base invoice", "base", [new("Services", 1, 60)]), HttpStatusCode.Created);
        row = await Send<TradeDocumentResponse>(s.Owner.Client, $"{path}/{row.Id}/approve", new DocumentTransitionRequest(row.Version, "Approved"));
        using var denied = await s.Owner.Client.PostAsJsonAsync($"{path}/{row.Id}/post", new PostTradeDocumentRequest(row.Version, s.Date,
            "base", true, "Approved"));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var retained = await s.Owner.Client.GetFromJsonAsync<TradeDocumentResponse>($"{path}/{row.Id}");
        Assert.Equal(TradeState.Approved, retained!.FinancialState);
    }
}
