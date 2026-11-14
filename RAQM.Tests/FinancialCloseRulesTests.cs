using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using RAQM.Api.Data;
using RAQM.Api.Data.Migrations;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Accruals;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accruals;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Services.FinancialReporting;
using Xunit;

namespace RAQM.Tests;

public sealed class FinancialCloseRulesTests
{
    private static readonly DateOnly July = new(2025, 7, 1);
    private static readonly DateOnly End = new(2026, 6, 30);

    [Fact]
    public void YearEndCapacityCountsRetainedEarningsLineAtBoundary()
    {
        var balanced = Enumerable.Range(0, 1000).ToDictionary(_ => Guid.NewGuid(), i => i % 2 == 0 ? 1m : -1m);
        Assert.Equal(1000, FinancialMath.ClosingLineCount(balanced));
        Assert.Equal(1000, FinancialMath.ClosingLines(balanced, Guid.NewGuid()).Count);
        balanced[balanced.Keys.First()] = 2m;
        Assert.Equal(1001, FinancialMath.ClosingLineCount(balanced));
        Assert.Equal(1001, FinancialMath.ClosingLines(balanced, Guid.NewGuid()).Count);
    }

    private static LedgerAccount Account(AccountClass type, string code, bool posting = true, Guid? parent = null, bool contra = false) =>
        new() { Classification = type, Code = code, Name = code, IsPosting = posting, ParentId = parent, IsContra = contra };

    private static FinancialLine Line(Guid journal, LedgerAccount account, DateOnly date, decimal debit, decimal credit, string source = "ManualJournal", string? original = null) =>
        new(Guid.NewGuid(), journal, account.Id, date, 1, 1, source, null, "GL-1", debit, credit, null, original);

    private sealed class Example
    {
        public LedgerAccount Root { get; } = Account(AccountClass.Asset, "1000", false);
        public LedgerAccount Cash { get; }
        public LedgerAccount Contra { get; }
        public LedgerAccount Capital { get; } = Account(AccountClass.Equity, "3100");
        public LedgerAccount Retained { get; } = Account(AccountClass.Equity, "3200");
        public LedgerAccount Revenue { get; } = Account(AccountClass.Revenue, "4100");
        public LedgerAccount Expense { get; } = Account(AccountClass.Expense, "5100");
        public IReadOnlyList<LedgerAccount> Accounts { get; }
        public Dictionary<Guid, StatementGroup> Mapping { get; }
        public List<FinancialLine> Lines { get; } = [];

        public Example()
        {
            Cash = Account(AccountClass.Asset, "1100", parent: Root.Id);
            Contra = Account(AccountClass.Asset, "1150", parent: Root.Id, contra: true);
            Accounts = [Root, Cash, Contra, Capital, Retained, Revenue, Expense];
            Mapping = new()
            {
                [Cash.Id] = StatementGroup.CurrentAsset, [Contra.Id] = StatementGroup.NoncurrentAsset,
                [Capital.Id] = StatementGroup.Capital, [Retained.Id] = StatementGroup.RetainedEarnings,
                [Revenue.Id] = StatementGroup.OperatingRevenue, [Expense.Id] = StatementGroup.OperatingExpense
            };
            Entry(July, Cash, Capital, 1000);
            Entry(July.AddDays(10), Cash, Revenue, 700);
            Entry(July.AddMonths(1), Expense, Cash, 200);
            Entry(July.AddMonths(1), Expense, Contra, 50);
        }

        public void Entry(DateOnly date, LedgerAccount debit, LedgerAccount credit, decimal amount, string source = "ManualJournal", string? original = null)
        {
            var id = Guid.NewGuid();
            Lines.Add(Line(id, debit, date, amount, 0, source, original));
            Lines.Add(Line(id, credit, date, 0, amount, source, original));
        }

        public Calculation Calculate(ReportKind kind, DateOnly? start = null, DateOnly? end = null) =>
            FinancialMath.Build(kind, new(start ?? July, end ?? End), July, Accounts, Lines, Mapping,
                new HashSet<Guid> { Cash.Id }, new Dictionary<string, CashGroup>());
    }

    private static decimal Amount(Calculation report, string key) => report.Rows.Single(x => x.Key == key).Amount;

    [Fact]
    public void BalanceSheetRollsUpContraAssetAndUnclosedProfitExactlyOnce()
    {
        var e = new Example();
        var r = e.Calculate(ReportKind.BalanceSheet);
        Assert.Equal(1450, Amount(r, "assets"));
        Assert.Equal(1450, Amount(r, $"balance:rollup:{e.Root.Id}"));
        Assert.Equal(-50, Amount(r, $"balance:account:{e.Contra.Id}"));
        Assert.Equal(450, Amount(r, "current-profit"));
        Assert.Equal(1450, Amount(r, "equity"));
        Assert.Equal(0, r.Difference);
    }

    [Fact]
    public void IncomeShowsPeriodYtdAndMappedOperatingTotals()
    {
        var e = new Example();
        var r = e.Calculate(ReportKind.Income, July.AddMonths(1), July.AddMonths(2).AddDays(-1));
        Assert.Equal(-250, Amount(r, "period:net"));
        Assert.Equal(450, Amount(r, "ytd:net"));
        Assert.Equal(450, Amount(r, "ytd:operating"));
        Assert.DoesNotContain(r.Rows, x => x.Key == "ytd:gross");
        e.Mapping[e.Expense.Id] = StatementGroup.CostOfSales;
        Assert.Equal(450, Amount(e.Calculate(ReportKind.Income), "period:gross"));
    }

    [Fact]
    public void YearEndTransfersProfitAndLeavesHistoricalIncomeAndEquityMovementIntact()
    {
        var e = new Example();
        var close = FinancialMath.ClosingLines(new Dictionary<Guid, decimal> { [e.Revenue.Id] = -700, [e.Expense.Id] = 250 }, e.Retained.Id);
        Assert.Equal(700, close.Single(x => x.AccountId == e.Revenue.Id).Debit);
        Assert.Equal(250, close.Single(x => x.AccountId == e.Expense.Id).Credit);
        Assert.Equal(450, close.Single(x => x.AccountId == e.Retained.Id).Credit);
        var id = Guid.NewGuid();
        e.Lines.AddRange(close.Select(x => Line(id, e.Accounts.Single(a => a.Id == x.AccountId), End, x.Debit, x.Credit, FinancialMath.YearEndSource)));
        var balance = e.Calculate(ReportKind.BalanceSheet);
        Assert.Equal(0, Amount(balance, "current-profit"));
        Assert.Equal(450, Amount(balance, "retained"));
        Assert.Equal(1450, Amount(balance, "equity"));
        Assert.Equal(450, Amount(e.Calculate(ReportKind.Income), "period:net"));
        var equity = e.Calculate(ReportKind.Equity);
        Assert.Equal(450, Amount(equity, "equity:transfer"));
        Assert.Equal(1450, Amount(equity, "equity:closing"));
        Assert.Equal(0, equity.Difference);
    }

    [Fact]
    public void LossClosingDebitsRetainedEarningsAndDoesNotInventJournalForZeroBalances()
    {
        var revenue = Guid.NewGuid();
        var expense = Guid.NewGuid();
        var retained = Guid.NewGuid();
        var lines = FinancialMath.ClosingLines(new Dictionary<Guid, decimal> { [revenue] = -50, [expense] = 120 }, retained);
        Assert.Equal(70, lines.Single(x => x.AccountId == retained).Debit);
        Assert.Equal(120, lines.Sum(x => x.Debit));
        Assert.Equal(120, lines.Sum(x => x.Credit));
        Assert.Empty(FinancialMath.ClosingLines(new Dictionary<Guid, decimal> { [expense] = 0 }, retained));
    }

    [Fact]
    public void CashMovementExcludesTransfersAndOpeningConversionFromExternalMovement()
    {
        var e = new Example();
        var bank = Account(AccountClass.Asset, "1200");
        e.Entry(July.AddDays(1), bank, e.Cash, 400, "Treasury.Transfer");
        e.Entry(July.AddDays(2), e.Cash, bank, 400, "Reversal", "Treasury.Transfer");
        e.Entry(July, bank, e.Capital, 100, "OpeningBalance");
        var r = FinancialMath.Build(ReportKind.CashMovement, new(July, End), July, e.Accounts.Append(bank).ToArray(),
            e.Lines, e.Mapping, new HashSet<Guid> { e.Cash.Id, bank.Id }, new Dictionary<string, CashGroup> { ["ManualJournal"] = CashGroup.Operating });
        Assert.Equal(1500, Amount(r, "cash:net"));
        Assert.Equal(100, Amount(r, "cash:conversion"));
        Assert.Equal(0, Amount(r, "cash:transfer"));
        Assert.Equal(1600, Amount(r, "cash:closing"));
        Assert.Equal(0, r.Difference);
    }

    [Fact]
    public void UnbalancedReportIsExposedWithoutPlug()
    {
        var e = new Example();
        e.Lines.Add(Line(Guid.NewGuid(), e.Cash, July, 12, 0));
        var r = e.Calculate(ReportKind.BalanceSheet);
        Assert.Equal(12, r.Difference);
        Assert.Equal(1462, Amount(r, "assets"));
        Assert.Equal(1450, Amount(r, "equity"));
    }

    [Theory]
    [InlineData("USD", 100.00, 3, 33.33, 33.34)]
    [InlineData("JPY", 10, 3, 3, 4)]
    [InlineData("IQD", 1, 3, 0.333, 0.334)]
    [InlineData("USD", 1200, 12, 100, 100)]
    public void RecognitionConservesTotalAndPutsResidualInFinalOccurrence(string currency, decimal total, int count, decimal first, decimal last)
    {
        var result = RecognitionRules.Allocate(total, count, currency);
        Assert.Equal(first, result[0]);
        Assert.Equal(last, result[^1]);
        Assert.Equal(total, result.Sum());
    }

    [Fact]
    public void RecognitionRejectsSubMinorUnitsAndImpossibleAllocations()
    {
        Assert.Throws<ApiException>(() => RecognitionRules.Allocate(0.005m, 1, "USD"));
        Assert.Throws<ApiException>(() => RecognitionRules.Allocate(0.01m, 2, "USD"));
    }

    [Theory]
    [InlineData(RecognitionKind.AccruedExpense, AccountClass.Expense, AccountClass.Liability)]
    [InlineData(RecognitionKind.AccruedRevenue, AccountClass.Asset, AccountClass.Revenue)]
    [InlineData(RecognitionKind.PrepaidExpense, AccountClass.Expense, AccountClass.Asset)]
    [InlineData(RecognitionKind.DeferredRevenue, AccountClass.Liability, AccountClass.Revenue)]
    public void SchedulePostsExactApprovedDebitAndCredit(RecognitionKind kind, AccountClass debit, AccountClass credit)
    {
        var schedule = new RecognitionSchedule { Kind = kind, DebitAccountId = Guid.NewGuid(), CreditAccountId = Guid.NewGuid(), Name = "Policy" };
        var result = RecognitionRules.Lines(schedule, new() { Amount = 120 });
        Assert.Equal((debit, credit), RecognitionRules.Classes(kind));
        Assert.Collection(result, d => Assert.Equal((schedule.DebitAccountId!.Value, 120m, 0m), (d.AccountId, d.Debit, d.Credit)),
            c => Assert.Equal((schedule.CreditAccountId!.Value, 0m, 120m), (c.AccountId, c.Debit, c.Credit)));
    }

    [Fact]
    public void AllExportFormatsUseProvidedAmountsAndProtectSpreadsheetText()
    {
        var report = new FinancialReport(ReportKind.Income, Guid.NewGuid(), "=BAD()", "USD", 3, Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, 42, new(July, End), null, [new("period:net", "Net (profit)", 321.25m, 310, 3.63m)], [], 0, null);
        var csv = Encoding.UTF8.GetString(FinancialExportWriter.Csv(report));
        Assert.Contains("'=BAD()", csv);
        Assert.Contains("\"period:net\",\"Net (profit)\",321.25,310,3.63", csv);
        var pdf = Encoding.ASCII.GetString(FinancialExportWriter.Pdf(report));
        Assert.StartsWith("%PDF-1.4", pdf);
        Assert.Contains("Net \\(profit\\): 321.25", pdf);
        var last = pdf.Split("startxref\n", StringSplitOptions.None)[^1].Split('\n')[0];
        Assert.StartsWith("xref", pdf[int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)..]);
        using var workbook = new ZipArchive(new MemoryStream(FinancialExportWriter.Xlsx(report)), ZipArchiveMode.Read);
        using var sheet = new StreamReader(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = System.Xml.Linq.XDocument.Parse(sheet.ReadToEnd());
        var ns = xml.Root!.Name.Namespace;
        var values = xml.Descendants(ns + "v").Select(x => x.Value).ToArray();
        Assert.Equal(new[] { "321.25", "310", "3.63" }, values);
        Assert.Empty(xml.Descendants(ns + "f"));
    }

    [Fact]
    public void PdfRejectsUnsupportedTextWhileCsvAndXlsxPreserveIt()
    {
        const string company = "شركة";
        var report = new FinancialReport(ReportKind.Income, Guid.NewGuid(), company, "USD", 3, Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, 42, new(July, End), null, [], [], 0, null);
        var error = Assert.Throws<RAQM.Api.Helpers.ApiException>(() => FinancialExportWriter.Pdf(report));
        Assert.Equal("report_pdf_text", error.Code);
        Assert.Contains(company, Encoding.UTF8.GetString(FinancialExportWriter.Csv(report)));
        using var workbook = new ZipArchive(new MemoryStream(FinancialExportWriter.Xlsx(report)));
        using var sheet = new StreamReader(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        Assert.Contains(company, sheet.ReadToEnd());
    }

    [Fact]
    public void Phase04MigrationAddsOnlyFinancialCloseTablesAndLedgerQueryTranslates()
    {
        var operations = new FinancialCloseV1().UpOperations;
        Assert.Equal(8, operations.OfType<CreateTableOperation>().Count());
        Assert.All(operations.OfType<CreateTableOperation>(), t => Assert.Equal("financial_close", t.Schema));
        Assert.DoesNotContain(operations, x => x is DropPrimaryKeyOperation or DropTableOperation or DropColumnOperation or AlterColumnOperation or AddColumnOperation);
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        var sql = new FinancialLedger(db).Lines(Guid.NewGuid(), End, July, [Guid.NewGuid()], true).ToQueryString();
        Assert.Contains("FinancialClose.YearEnd", sql);
        Assert.Contains("PostingDate", sql);
        Assert.Contains("ANY", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllPhase03OpenApiContractsAndFinancialEndpointAuthorizationArePreserved()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test");
        using var client = factory.CreateClient();
        var before = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Contracts", "phase03-openapi-before.json")))!;
        var after = (await client.GetFromJsonAsync<JsonObject>("/openapi/v1.json"))!;
        foreach (var path in before["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, after["paths"]![path.Key]), $"Route contract changed: {path.Key}");
        foreach (var schema in before["components"]!["schemas"]!.AsObject())
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, after["components"]!["schemas"]![schema.Key]), $"Schema changed: {schema.Key}");
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText!.Contains("/financial-reporting") || x.RoutePattern.RawText.Contains("/financial-close") ||
                x.RoutePattern.RawText.Contains("/recognition")).ToArray();
        Assert.Equal(24, routes.Length);
        Assert.All(routes, route =>
        {
            Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(route.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Contains("{companyId:guid}", route.RoutePattern.RawText);
        });
    }
}
