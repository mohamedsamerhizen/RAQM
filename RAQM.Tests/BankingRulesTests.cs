using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using RAQM.Api.Data;
using RAQM.Api.Data.Migrations;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.DTOs.Treasury;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.CashManagement;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Banking;
using RAQM.Api.Helpers.CashManagement;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.Treasury;
using Xunit;

namespace RAQM.Tests;

public sealed class BankingRulesTests
{
    private static readonly DateOnly Day = new(2026, 4, 1);
    private const string Header = "booking_date,value_date,amount,reference,description\n";
    private static StatementRequest Statement(string lines, decimal closing = 110) => new(Day, Day.AddDays(1), 100, closing, "April", Header + lines, "import");

    [Fact]
    public void CsvRetainsExternalValuesAndUsesSignedMovements()
    {
        var result = StatementCsv.Parse(Statement("2026-04-01,2026-04-02,20, X ,\"Deposit, branch\"\n2026-04-02,,-10,F1,Fee"), "USD");
        Assert.Empty(result.Errors);
        Assert.Equal(110, result.CalculatedClosing);
        Assert.Equal(20, result.Rows[0].Amount);
        Assert.Equal(-10, result.Rows[1].Amount);
        Assert.Equal(" X ", JsonNode.Parse(result.Rows[0].OriginalFields)!["reference"]!.GetValue<string>());
        Assert.Equal("Deposit, branch", result.Rows[0].Description);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void MappedDirectionalCsvRetainsStableIdAndQuotedMultilineDescription()
    {
        var request = new StatementRequest(Day, Day, 100, 80, "BANK1", "book;val;type;ref;memo;uid\n2026-04-01;20;DEBIT;R1;\"First\nsecond\";ID42",
            "one", new("book", null, "val", "ref", "memo", "uid", "type"), ";");
        var result = StatementCsv.Parse(request, "USD");
        Assert.Empty(result.Errors);
        var line = Assert.Single(result.Rows);
        Assert.Equal(-20, line.Amount);
        Assert.Equal("ID42", line.StableId);
        Assert.Contains("second", line.Description);
        Assert.Equal(80, result.CalculatedClosing);
    }

    [Theory]
    [InlineData("2026-04-05,,10,R,M")]
    [InlineData("04/01/2026,,10,R,M")]
    [InlineData("2026-04-01,wrong,10,R,M")]
    [InlineData("2026-04-01,,10.001,R,M")]
    [InlineData("2026-04-01,,NaN,R,M")]
    [InlineData("2026-04-01,,0,R,M")]
    [InlineData("2026-04-01,,10,R")]
    [InlineData("2026-04-01,,10,R,\"Missing closing quote")]
    public void InvalidCsvRowsAreReported(string row)
    {
        var result = StatementCsv.Parse(Statement(row), "USD");
        Assert.Contains(result.Errors, x => x.Row == 2);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void StatementMustBalanceAndFingerprintChangesWithEvidence()
    {
        var original = Statement("2026-04-01,,10,REF,Entry");
        var parsed = StatementCsv.Parse(original, "USD");
        Assert.Empty(parsed.Errors);
        Assert.Equal(parsed.Digest, StatementCsv.Parse(original with { IdempotencyKey = "different" }, "USD").Digest);
        Assert.NotEqual(parsed.Digest, StatementCsv.Parse(original with { Csv = original.Csv.Replace("REF", "ALT") }, "USD").Digest);
        Assert.Contains(StatementCsv.Parse(original with { Closing = 111 }, "USD").Errors, x => x.Row == 0);
    }

    [Fact]
    public void StableDuplicateIsRejectedAndAmbiguousDuplicateIsFlagged()
    {
        var input = "booking_date,value_date,amount,reference,description,id\n2026-04-01,,10,R,M,S1\n2026-04-01,,10,R,M,S1";
        var request = Statement("", 120) with { Csv = input, Columns = new(StableId: "id") };
        Assert.Contains(StatementCsv.Parse(request, "USD").Errors, x => x.Row == 3);
        var ambiguous = StatementCsv.Parse(request with { Columns = null }, "USD");
        Assert.Empty(ambiguous.Errors);
        Assert.Contains(ambiguous.Warnings, x => x.Row == 3);
    }

    [Fact]
    public void ExactMatchingAllowsAggregationAndRejectsPartialAndNettedMoves()
    {
        ReconciliationRules.Match(1000, [1000]);
        ReconciliationRules.Match(-1000, [-600, -400]);
        Assert.Throws<ApiException>(() => ReconciliationRules.Match(1000, [999.99m]));
        Assert.Throws<ApiException>(() => ReconciliationRules.Match(1000, [1100, -100]));
        Assert.Throws<ApiException>(() => ReconciliationRules.Match(1000, []));
    }

    [Fact]
    public void CutoffExplainsDepositsAndOutstandingWithdrawalsAndExposesDifferences()
    {
        Assert.Equal((150m, 30m, 0m), ReconciliationRules.Totals(1000, 1120, [100, 50, -30]));
        Assert.Equal((150m, 30m, -1m), ReconciliationRules.Totals(1000, 1121, [100, 50, -30]));
        var book = new[] { new BookMovement(Guid.NewGuid(), Guid.NewGuid(), Day.AddDays(-31), -30, "ManualJournal", "A", null, null) };
        var bucket = Assert.Single(ReconciliationRules.Age(Day, [], book));
        Assert.Equal("31–60", bucket.Bucket);
        Assert.Equal(30, bucket.Withdrawals);
    }

    [Fact]
    public void SuggestionsRequireUniqueSignedAmountReferenceAndDate()
    {
        var line = new BankStatementLine { Amount = 100, Reference = "ABC", BookingDate = Day };
        var book = new BookMovement(Guid.NewGuid(), Guid.NewGuid(), Day.AddDays(1), 100, "Settlement.Payment", " abc ", null, null);
        Assert.Equal(book.LineId, Assert.Single(ReconciliationRules.Suggest([line], [book])).JournalLineId);
        Assert.Empty(ReconciliationRules.Suggest([line], [book, book with { LineId = Guid.NewGuid() }]));
        Assert.Empty(ReconciliationRules.Suggest([line], [book with { Date = Day.AddDays(4) }]));
        Assert.Empty(ReconciliationRules.Suggest([line], [book with { Amount = -100 }]));
    }

    [Fact]
    public void TransferUsesOnlyDestinationDebitAndSourceCredit()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        var lines = TreasuryPosting.Lines(new() { Kind = TreasuryMovementKind.Transfer, CreditLedgerId = from, DebitLedgerId = to, Amount = 5000 });
        Assert.Collection(lines, d => Assert.Equal((to, 5000m, 0m), (d.AccountId, d.Debit, d.Credit)),
            c => Assert.Equal((from, 0m, 5000m), (c.AccountId, c.Debit, c.Credit)));
    }

    [Fact]
    public void HistoricalPhase02PaymentFingerprintRemainsByteCompatible()
    {
        var r = new PaymentRequest(Guid.NewGuid(), Guid.NewGuid(), 125, Day, "historical", "key");
        var historical = new { r.PartyId, r.CashAccountId, r.Amount, r.PostingDate, r.Reference, r.IdempotencyKey, r.TreatAsAdvance, r.AllowBackdated, r.Reason, r.Allocations };
        Assert.Equal(TradeRules.Hash(historical), TradeRules.Hash(r));
        Assert.NotEqual(TradeRules.Hash(r), TradeRules.Hash(r with { BankAccountId = Guid.NewGuid() }));
    }

    [Fact]
    public void MigrationKeepsIdentityKeysAndAddsNullablePaymentAttribution()
    {
        var operations = new BankingTreasuryV1().UpOperations;
        Assert.DoesNotContain(operations, x => x is DropPrimaryKeyOperation or DropTableOperation or DropColumnOperation or AlterColumnOperation);
        var column = Assert.Single(operations.OfType<AddColumnOperation>());
        Assert.Equal(("trading", "Payments", "BankAccountId", true), (column.Schema, column.Table, column.Name, column.IsNullable));
        Assert.Equal(9, operations.OfType<CreateTableOperation>().Count());
    }

    [Fact]
    public void BookQueriesTranslateForPostgreSqlWithAndWithoutMatchSelection()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        var ledger = new TreasuryLedger(db);
        var sql = ledger.Entries(Guid.NewGuid(), Guid.NewGuid(), Day).ToQueryString();
        Assert.Contains("BankAccountId", sql);
        Assert.Contains("PostingDate", sql);
        var selected = ledger.Entries(Guid.NewGuid(), Guid.NewGuid(), Day, Day.AddDays(-3), [Guid.NewGuid()]).ToQueryString();
        Assert.Contains("ANY", selected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingOpenApiContractsPreserveRoutesAndOptionalBankFieldsAreAdditive()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test");
        using var client = factory.CreateClient();
        var before = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Contracts", "phase02-openapi-before.json")))!;
        var after = (await client.GetFromJsonAsync<JsonObject>("/openapi/v1.json"))!;
        foreach (var path in before["paths"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(path.Value, after["paths"]![path.Key]), $"Route contract changed: {path.Key}");
        foreach (var schema in before["components"]!["schemas"]!.AsObject())
        {
            var actual = after["components"]!["schemas"]![schema.Key]?.DeepClone();
            if (schema.Key is "PaymentRequest" or "RefundRequest" or "PaymentResponse")
            {
                Assert.DoesNotContain(actual?["required"]?.AsArray() ?? [], x => x?.GetValue<string>() == "bankAccountId");
                Assert.True(actual!["properties"]!.AsObject().Remove("bankAccountId"));
            }
            Assert.True(ControllerCompatibilityTests.SchemaMatches(schema.Key, schema.Value, actual), $"Schema contract changed: {schema.Key}");
        }
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText!.Contains("/banking") || x.RoutePattern.RawText.Contains("/cash-management") || x.RoutePattern.RawText.Contains("/treasury")).ToList();
        Assert.NotEmpty(routes);
        Assert.All(routes, route =>
        {
            Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(route.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Contains("{companyId:guid}", route.RoutePattern.RawText);
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/organizations/{Guid.NewGuid()}/companies/{Guid.NewGuid()}/banking/accounts")).StatusCode);
    }
}
