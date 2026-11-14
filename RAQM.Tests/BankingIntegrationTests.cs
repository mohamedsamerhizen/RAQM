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
using RAQM.Api.DTOs.CashManagement;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.DTOs.Treasury;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.CashManagement;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.CashManagement;
using Xunit;

namespace RAQM.Tests;

[Collection("PostgreSQL"), Trait("Category", "Integration")]
public sealed class BankingIntegrationTests(PostgreSqlFixture fixture)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    private sealed record Setup(HttpClient Client, Guid Actor, Guid Org, Company Company, Dictionary<string, AccountResponse> Accounts,
        FiscalYearResponse Year, BankProfile Profile, TreasuryAccountResponse Bank) : IDisposable
    {
        public string Root => $"/api/v1/organizations/{Org}/companies/{Company.Id}";
        public string Core => Root + "/accounting";
        public string Banking => Root + "/banking";
        public string Cash => Root + "/cash-management";
        public string BankRoot => $"{Banking}/accounts/{Bank.Id}";
        public string Sessions => BankRoot + "/reconciliations";
        public void Dispose() => Client.Dispose();
    }

    private static async Task<T> Send<T>(HttpClient client, string path, object body, HttpMethod? method = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == expected, $"{path}: expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task Denied(HttpClient client, string path, object body, HttpStatusCode status)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        Assert.True(response.StatusCode == status, $"{path}: expected {(int)status}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<Setup> Arrange()
    {
        var client = fixture.Factory.CreateClient();
        var tokens = await Send<TokenResponse>(client, "/api/v1/auth/onboard",
            new OnboardRequest($"{Guid.NewGuid():N}@banking.example", "Banking_Test-Password1!", "Treasury Org", ApiFactory.OnboardingKey));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        var org = me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid();
        var company = await Send<Company>(client, $"/api/v1/organizations/{org}/companies",
            new CompanyRequest("Banking Company", Guid.NewGuid().ToString("N"), "USD", 1, 1), expected: HttpStatusCode.Created);
        var root = $"/api/v1/organizations/{org}/companies/{company.Id}";
        var core = root + "/accounting";
        var accounts = (await Send<List<AccountResponse>>(client, core + "/accounts/initialize", new InitializeAccountsRequest())).ToDictionary(x => x.Code);
        foreach (var (code, name, type, parent) in new[]
        {
            ("1200","Bank A",AccountClass.Asset,"1000"), ("1250","Bank B",AccountClass.Asset,"1000"),
            ("1300","Petty cash",AccountClass.Asset,"1000"), ("1400","Recoverable tax",AccountClass.Asset,"1000"),
            ("1500","Receivables",AccountClass.Asset,"1000"), ("1600","Supplier advances",AccountClass.Asset,"1000"),
            ("2200","Tax payable",AccountClass.Liability,"2000"), ("2300","Customer advances",AccountClass.Liability,"2000")
        })
            accounts[code] = await Send<AccountResponse>(client, core + "/accounts",
                new AccountRequest(code, name, null, type, true, accounts[parent].Id), expected: HttpStatusCode.Created);
        if (Today.AddDays(-3).Year != Today.Year)
            await Send<FiscalYearResponse>(client, core + "/fiscal-years", new FiscalYearRequest(Today.Year - 1, "Prior fiscal year"), expected: HttpStatusCode.Created);
        var year = await Send<FiscalYearResponse>(client, core + "/fiscal-years", new FiscalYearRequest(Today.Year, "Fiscal year"), expected: HttpStatusCode.Created);
        var profile = await Send<BankProfile>(client, root + "/banking/banks", new BankProfileRequest("BANK", "Local Bank", "ABCDUS33"));
        var bank = await Send<TreasuryAccountResponse>(client, root + "/banking/accounts",
            new TreasuryAccountRequest(TreasuryAccountKind.Bank, "BANK-A", "Bank A", accounts["1200"].Id, profile.Id, "1234567890"));
        return new(client, me.GetProperty("userId").GetGuid(), org, company, accounts, year, profile, bank);
    }

    private static Task<TreasuryAccountResponse> Account(Setup s, TreasuryAccountKind kind, string ledger, string code, string? reference = null) =>
        Send<TreasuryAccountResponse>(s.Client, s.Banking + "/accounts", new TreasuryAccountRequest(kind, code, code, s.Accounts[ledger].Id,
            kind == TreasuryAccountKind.Bank ? s.Profile.Id : null, reference, CustodianId: kind == TreasuryAccountKind.PettyCash ? s.Actor : null));

    private static Task<TreasuryMovement> Transfer(Setup s, Guid from, Guid to, decimal amount, string key = "transfer") =>
        Send<TreasuryMovement>(s.Client, s.Cash + "/transfers", new TransferRequest(from, to, amount, Today, "Internal transfer", "Funding", key));

    private static Task<TreasuryMovement> Approve(Setup s, TreasuryMovement movement) =>
        Send<TreasuryMovement>(s.Client, $"{s.Cash}/transfers/{movement.Id}/approve", new MovementAction(movement.Version, movement.PostingDate, "Approved", "approve"));

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

    private static async Task<PostingReceipt> Book(Setup s, decimal amount, string reference, DateOnly date)
    {
        var asset = s.Bank.LedgerAccountId;
        var other = s.Accounts[amount > 0 ? "4100" : "5100"].Id;
        JournalDraftLine[] lines = amount > 0 ? [new(asset, amount, 0, null), new(other, 0, amount, null)] :
            [new(other, -amount, 0, null), new(asset, 0, -amount, null)];
        var draft = await Send<JournalResponse>(s.Client, s.Core + "/journals", new JournalRequest(date, date, "Book movement", reference, lines), expected: HttpStatusCode.Created);
        return await Send<PostingReceipt>(s.Client, $"{s.Core}/journals/{draft.Id}/post", new PostJournalRequest(Guid.NewGuid().ToString("N"), draft.Version,
            date < Today, "Historical test accounting date"));
    }

    private static StatementRequest Statement(DateOnly start, DateOnly end, decimal opening, decimal closing, string key, params (DateOnly Date, decimal Amount, string Reference)[] rows)
    {
        var csv = "booking_date,value_date,amount,reference,description\n" + string.Join('\n', rows.Select(x => FormattableString.Invariant($"{x.Date:yyyy-MM-dd},,{x.Amount},{x.Reference},Bank evidence")));
        return new(start, end, opening, closing, key, csv, key);
    }

    private static async Task<ReconciliationView> Start(Setup s, StatementRequest request)
    {
        var statement = await Send<StatementSummary>(s.Client, s.BankRoot + "/statements", request);
        return await Send<ReconciliationView>(s.Client, $"{s.Sessions}/statements/{statement.Id}", new { });
    }

    private static Task<ReconciliationView> Transition(Setup s, ReconciliationView session, string action) =>
        Send<ReconciliationView>(s.Client, $"{s.Sessions}/{session.Id}/state/{action}", new ReviewAction(session.Version, "Reconciliation evidence reviewed"));

    [Fact]
    public async Task DedicatedBankMappingIsUniqueAndReadOnlyCannotCreate()
    {
        using var s = await Arrange();
        Assert.Equal("****7890", s.Bank.AccountMask);
        Assert.Equal(s.Accounts["1200"].Id, s.Bank.LedgerAccountId);
        await Denied(s.Client, s.Banking + "/accounts", new TreasuryAccountRequest(TreasuryAccountKind.Bank, "DUP", "Duplicate", s.Bank.LedgerAccountId,
            s.Profile.Id, "00009999"), HttpStatusCode.Conflict);
        await Denied(s.Client, s.Banking + "/accounts", new TreasuryAccountRequest(TreasuryAccountKind.Bank, "DUP-ID", "Duplicate", s.Accounts["1250"].Id,
            s.Profile.Id, "1234 567890"), HttpStatusCode.Conflict);

        using var reader = await Arrange();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.OrganizationMembers.Add(new() { OrganizationId = s.Org, TenantId = s.Company.TenantId, UserId = reader.Actor, Role = OrganizationRole.Member });
            db.CompanyMembers.Add(new() { CompanyId = s.Company.Id, OrganizationId = s.Org, TenantId = s.Company.TenantId, UserId = reader.Actor, Role = CompanyRole.Reader });
            await db.SaveChangesAsync();
        }
        using var response = await reader.Client.GetAsync(s.Banking + "/accounts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Denied(reader.Client, s.Banking + "/banks", new BankProfileRequest("DENIED", "Unauthorized", null), HttpStatusCode.Forbidden);
        await Denied(reader.Client, s.Cash + "/transfers", new TransferRequest(s.Bank.Id, Guid.NewGuid(), 1, Today, "X", "Unauthorized", "forbidden"), HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(TreasuryAccountKind.Bank)]
    [InlineData(TreasuryAccountKind.Cash)]
    public async Task TransfersPostExactlyTwoLinesAreIdempotentAndReverseExactly(TreasuryAccountKind targetKind)
    {
        using var s = await Arrange();
        var target = await Account(s, targetKind, targetKind == TreasuryAccountKind.Bank ? "1250" : "1100", "TARGET",
            targetKind == TreasuryAccountKind.Bank ? "00002222" : null);
        var draft = await Transfer(s, s.Bank.Id, target.Id, 5000);
        Assert.Equal(draft.Id, (await Transfer(s, s.Bank.Id, target.Id, 5000)).Id);
        var posted = await Approve(s, draft);
        Assert.Equal(posted.JournalId, (await Approve(s, draft)).JournalId);
        var lines = await Lines(posted.JournalId!.Value);
        Assert.Equal(2, lines.Count);
        Assert.Equal(5000, lines.Single(x => x.AccountId == target.LedgerAccountId).Debit);
        Assert.Equal(5000, lines.Single(x => x.AccountId == s.Bank.LedgerAccountId).Credit);
        Assert.Equal(1, await Posted(s));
        var reversed = await Send<TreasuryMovement>(s.Client, $"{s.Cash}/movements/{posted.Id}/reverse",
            new MovementAction(posted.Version, Today, "Correct funding", "reverse"));
        var reversal = await Lines(reversed.ReversalJournalId!.Value);
        Assert.Equal(5000, reversal.Single(x => x.AccountId == s.Bank.LedgerAccountId).Debit);
        Assert.Equal(5000, reversal.Single(x => x.AccountId == target.LedgerAccountId).Credit);
        Assert.Equal(2, await Posted(s));
    }

    [Fact]
    public async Task TransferSourceAndJournalRollbackAfterInjectedFailure()
    {
        using var s = await Arrange();
        var target = await Account(s, TreasuryAccountKind.Cash, "1100", "CASH");
        var draft = await Transfer(s, s.Bank.Id, target.Id, 250);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var tx = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
            var cash = scope.ServiceProvider.GetRequiredService<ICashManagementService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => tx.Execute<int>(s.Actor, s.Org, s.Company.Id, async (_, ct) =>
            {
                await cash.ApproveTransfer(s.Actor, s.Org, s.Company.Id, draft.Id, new(draft.Version, Today, "Approved", "approve"), ct);
                throw new InvalidOperationException("Injected fault after journal flush");
            }, default));
        }
        Assert.Equal(0, await Posted(s));
        Assert.NotNull((await Approve(s, draft)).JournalId);
    }

    [Fact]
    public async Task TransferRejectsAnotherTenantsBankAccount()
    {
        using var s = await Arrange();
        using var foreign = await Arrange();
        await Denied(s.Client, s.Cash + "/transfers", new TransferRequest(s.Bank.Id, foreign.Bank.Id, 1, Today, "X", "Foreign target", "foreign"),
            HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.Client.GetAsync(s.Banking + "/accounts")).StatusCode);
        Assert.Equal(0, await Posted(s));
    }

    [Fact]
    public async Task ImportPreviewRetryOverlapAndEvidenceMutationAreControlled()
    {
        using var s = await Arrange();
        var request = Statement(Today, Today, 100, 140, "stmt", (Today, 40, "BANKREF"));
        var preview = await Send<StatementPreview>(s.Client, s.BankRoot + "/statements/preview", request);
        Assert.Empty(preview.Errors);
        var first = await Send<StatementSummary>(s.Client, s.BankRoot + "/statements", request);
        Assert.Equal(first.Id, (await Send<StatementSummary>(s.Client, s.BankRoot + "/statements", request)).Id);
        await Denied(s.Client, s.BankRoot + "/statements", request with { IdempotencyKey = "new" }, HttpStatusCode.BadRequest);
        await Denied(s.Client, s.BankRoot + "/statements", request with { Closing = 141 }, HttpStatusCode.Conflict);
        Assert.Equal(0, await Posted(s));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var update = new NpgsqlCommand("UPDATE banking.\"Statements\" SET \"Closing\"=141 WHERE \"Id\"=@id", connection);
        update.Parameters.AddWithValue("id", first.Id);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task ConcurrentAggregateMatchRetainsOutstandingAmountsAndEnforcesFinalizedCutoff()
    {
        using var s = await Arrange();
        var cutoff = Today.AddDays(-1);
        var a = await Book(s, 300, "A", cutoff);
        var b = await Book(s, 700, "B", cutoff);
        await Book(s, 200, "Deposit in transit", cutoff);
        await Book(s, -50, "Outstanding withdrawal", cutoff);
        await Book(s, 11, "After cutoff", Today);
        var session = await Start(s, Statement(cutoff, cutoff, 0, 1000, "aggregate", (cutoff, 1000, "Group")));
        var lineA = (await Lines(a.JournalId)).Single(x => x.AccountId == s.Bank.LedgerAccountId);
        var lineB = (await Lines(b.JournalId)).Single(x => x.AccountId == s.Bank.LedgerAccountId);
        var external = Assert.Single(session.UnmatchedStatement);
        var path = $"{s.Sessions}/{session.Id}/matches";
        await Denied(s.Client, path, new ConfirmMatch(session.Version, external.Id, [lineA.Id], "Partial"), HttpStatusCode.BadRequest);
        var command = new ConfirmMatch(session.Version, external.Id, [lineA.Id, lineB.Id], "Bank grouped two deposits");
        var replies = await Task.WhenAll(s.Client.PostAsJsonAsync(path, command), s.Client.PostAsJsonAsync(path, command));
        Assert.Single(replies, x => x.StatusCode == HttpStatusCode.Conflict);
        var success = Assert.Single(replies, x => x.StatusCode == HttpStatusCode.OK);
        session = (await success.Content.ReadFromJsonAsync<ReconciliationView>())!;
        foreach (var reply in replies) reply.Dispose();
        Assert.Equal(1150, session.BookClosing);
        Assert.Equal(200, session.DepositsInTransit);
        Assert.Equal(50, session.OutstandingWithdrawals);
        Assert.Equal(0, session.Difference);
        Assert.Equal(2, session.UnmatchedBook.Count);
        session = await Transition(s, await Transition(s, session, "review"), "finalize");
        Assert.Equal(ReconciliationState.Finalized, session.State);
        Assert.Equal(5, await Posted(s));
        var draft = await Send<JournalResponse>(s.Client, s.Core + "/journals", new JournalRequest(cutoff, cutoff, "Late", "Late",
            [new(s.Bank.LedgerAccountId, 1, 0, null), new(s.Accounts["4100"].Id, 0, 1, null)]), expected: HttpStatusCode.Created);
        await Denied(s.Client, $"{s.Core}/journals/{draft.Id}/post", new PostJournalRequest("late", draft.Version, true, "Late correction"), HttpStatusCode.Conflict);
        var report = (await s.Client.GetFromJsonAsync<TreasuryActivity>($"{s.Root}/treasury/accounts/{s.Bank.Id}/activity?from={cutoff:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))!;
        Assert.Equal(1161, report.Position.Closing);
        Assert.Equal(cutoff, report.Position.LastReconciledThrough);
    }

    [Theory]
    [InlineData(PartyKind.Customer)]
    [InlineData(PartyKind.Supplier)]
    public async Task BankAttributedPaymentMatchesExistingJournalAndLegacyCashRequestStillWorks(PartyKind side)
    {
        using var s = await Arrange();
        var trading = s.Root + "/trading";
        var a = s.Accounts;
        await Send<List<ControlAccountResponse>>(s.Client, trading + "/configuration",
            new ControlAccountRequest(a["1500"].Id, a["2100"].Id, a["4100"].Id, a["5100"].Id, a["2200"].Id,
                a["1400"].Id, a["2300"].Id, a["1600"].Id, a["3100"].Id, [a["1100"].Id], false, "Trading setup"), HttpMethod.Put);
        var customer = side == PartyKind.Customer;
        var party = await Send<PartyResponse>(s.Client, trading + "/parties", new PartyRequest(side, "PARTY", "Counterparty",
            null, null, null, null, 30, null, a[customer ? "1500" : "2100"].Id, a[customer ? "4100" : "5100"].Id), expected: HttpStatusCode.Created);
        var doc = await Send<TradeDocumentResponse>(s.Client, trading + "/documents",
            new TradeDocumentRequest(party.Id, customer ? TradeKind.Invoice : TradeKind.Bill, Today, null, "Trade", "BILL-1", [new("Service", 1, 1000)]),
            expected: HttpStatusCode.Created);
        doc = await Send<TradeDocumentResponse>(s.Client, $"{trading}/documents/{doc.Id}/approve", new DocumentTransitionRequest(doc.Version, "Approved"));
        doc = await Send<TradeDocumentResponse>(s.Client, $"{trading}/documents/{doc.Id}/post", new PostTradeDocumentRequest(doc.Version, Today, "trade"));
        var payment = new PaymentRequest(party.Id, s.Bank.LedgerAccountId, 1000, Today, "BANKREF", "payment", Allocations: [new(doc.Id, 1000)], BankAccountId: s.Bank.Id);
        await Denied(s.Client, trading + "/payments", payment with { BankAccountId = null }, HttpStatusCode.BadRequest);
        var paid = await Send<PaymentResponse>(s.Client, trading + "/payments", payment);
        Assert.Equal(s.Bank.Id, paid.BankAccountId);
        Assert.Equal(1000, paid.ControlAmount);
        var bankLine = (await Lines(paid.JournalId)).Single(x => x.AccountId == s.Bank.LedgerAccountId);
        Assert.Equal(customer ? 1000 : 0, bankLine.Debit);
        Assert.Equal(customer ? 0 : 1000, bankLine.Credit);
        var signed = customer ? 1000 : -1000;
        var session = await Start(s, Statement(Today, Today, 0, signed, "trade-bank", (Today, signed, "BANKREF")));
        var suggestion = Assert.Single(session.Suggestions);
        Assert.Equal(bankLine.Id, suggestion.JournalLineId);
        Assert.Equal(paid.Id, Assert.Single(session.UnmatchedBook).PaymentId);
        session = await Send<ReconciliationView>(s.Client, $"{s.Sessions}/{session.Id}/matches",
            new ConfirmMatch(session.Version, suggestion.StatementLineId, [bankLine.Id], "Statement reference and amount"));
        session = await Transition(s, await Transition(s, session, "review"), "finalize");
        Assert.Equal(0, session.Difference);
        Assert.Equal(2, await Posted(s));

        var oldRequest = new PaymentRequest(party.Id, a["1100"].Id, 100, Today, "Legacy cash", "cash");
        var unassigned = await Send<PaymentResponse>(s.Client, trading + "/payments", oldRequest);
        Assert.Null(unassigned.BankAccountId);
        Assert.Equal(100, unassigned.AdvanceAmount);
        Assert.Equal(unassigned.Id, (await Send<PaymentResponse>(s.Client, trading + "/payments", oldRequest)).Id);
    }

    [Theory]
    [InlineData(TreasuryMovementKind.BankFee, -25)]
    [InlineData(TreasuryMovementKind.BankInterest, 35)]
    public async Task BankAdjustmentPostsExpectedAccountsAndCanBeMatched(TreasuryMovementKind kind, int signed)
    {
        using var s = await Arrange();
        var session = await Start(s, Statement(Today, Today, 0, signed, "adjustment", (Today, signed, "ADJ")));
        var line = Assert.Single(session.UnmatchedStatement);
        var offset = s.Accounts[signed < 0 ? "5100" : "4100"].Id;
        var command = new AdjustmentRequest(line.Id, kind, offset, session.Version, "Bank fee or interest evidence", "adjust");
        var path = $"{s.Sessions}/{session.Id}/adjustments";
        var move = await Send<TreasuryMovement>(s.Client, path, command);
        Assert.Equal(move.Id, (await Send<TreasuryMovement>(s.Client, path, command)).Id);
        var lines = await Lines(move.JournalId!.Value);
        var bank = lines.Single(x => x.AccountId == s.Bank.LedgerAccountId);
        Assert.Equal(signed, bank.Debit - bank.Credit);
        var counter = lines.Single(x => x.AccountId == offset);
        Assert.Equal(-signed, counter.Debit - counter.Credit);
        session = (await s.Client.GetFromJsonAsync<ReconciliationView>($"{s.Sessions}/{session.Id}"))!;
        session = await Send<ReconciliationView>(s.Client, $"{s.Sessions}/{session.Id}/matches",
            new ConfirmMatch(session.Version, line.Id, [bank.Id], "Posted the identified adjustment"));
        session = await Transition(s, await Transition(s, session, "review"), "finalize");
        Assert.Equal(0, session.Difference);
        Assert.Equal(1, await Posted(s));
    }

    [Fact]
    public async Task ClosedPeriodRejectsAdjustmentWithoutChangingReconciliation()
    {
        using var s = await Arrange();
        var period = s.Year.Periods.First();
        await Send<PeriodResponse>(s.Client, $"{s.Core}/periods/{period.Id}/state", new PeriodStateRequest(PeriodState.Closed, "Close", period.Version), HttpMethod.Put);
        var session = await Start(s, Statement(period.StartDate, period.EndDate, 0, -20, "fee",
            (period.StartDate, -20, "FEE")));
        await Denied(s.Client, $"{s.Sessions}/{session.Id}/adjustments", new AdjustmentRequest(Assert.Single(session.UnmatchedStatement).Id,
            TreasuryMovementKind.BankFee, s.Accounts["5100"].Id, session.Version, "Statement fee", "closed", true), HttpStatusCode.Conflict);
        Assert.Equal(0, await Posted(s));
        var current = (await s.Client.GetFromJsonAsync<ReconciliationView>($"{s.Sessions}/{session.Id}"))!;
        Assert.Equal(session.Version, current.Version);
    }

    [Fact]
    public async Task FinalizedMatchRequiresExplicitReopenAndRetainsReleaseEvidence()
    {
        using var s = await Arrange();
        var book = await Book(s, 80, "BANK", Today);
        var journalLine = (await Lines(book.JournalId)).Single(x => x.AccountId == s.Bank.LedgerAccountId);
        var session = await Start(s, Statement(Today, Today, 0, 80, "match", (Today, 80, "BANK")));
        var line = Assert.Single(session.UnmatchedStatement);
        session = await Send<ReconciliationView>(s.Client, $"{s.Sessions}/{session.Id}/matches", new ConfirmMatch(session.Version, line.Id, [journalLine.Id], "Initial confirmation"));
        await Denied(s.Client, $"{s.Sessions}/{session.Id}/matches", new ConfirmMatch(session.Version, line.Id, [journalLine.Id], "Duplicate confirmation"), HttpStatusCode.Conflict);
        session = await Transition(s, await Transition(s, session, "review"), "finalize");
        var history = (await s.Client.GetFromJsonAsync<List<BankMatch>>($"{s.Sessions}/{session.Id}/matches"))!;
        var match = Assert.Single(history);
        var release = $"{s.Sessions}/{session.Id}/matches/{match.Id}/release";
        await Denied(s.Client, release, new ReviewAction(session.Version, "Correction"), HttpStatusCode.Conflict);
        session = await Transition(s, session, "reopen");
        session = await Send<ReconciliationView>(s.Client, release, new ReviewAction(session.Version, "Correction"));
        Assert.Single(session.UnmatchedStatement);
        Assert.Single(session.UnmatchedBook);
        history = (await s.Client.GetFromJsonAsync<List<BankMatch>>($"{s.Sessions}/{session.Id}/matches"))!;
        Assert.NotNull(Assert.Single(history).ReleasedAt);
        var audit = (await s.Client.GetFromJsonAsync<List<AuditResponse>>($"{s.Banking}/audit/{session.Id}"))!;
        Assert.Contains(audit, x => x.Action == "BankReconciliationPriorFinalization");
        Assert.Contains(audit, x => x.Action == "BankMatchReleased");
        Assert.Equal(1, await Posted(s));
    }

    [Fact]
    public async Task R01_003_ReopenRequiresGovernedPeriodReopenAfterCompletedClose()
    {
        using var s = await Arrange();
        var period = s.Year.Periods[0];
        var book = await Book(s, 80, "BANK", period.StartDate);
        var bookLine = (await Lines(book.JournalId)).Single(x => x.AccountId == s.Bank.LedgerAccountId);
        var session = await Start(s, Statement(period.StartDate, period.EndDate, 0, 80, "close", (period.StartDate, 80, "BANK")));
        var statementLine = Assert.Single(session.UnmatchedStatement);
        session = await Send<ReconciliationView>(s.Client, $"{s.Sessions}/{session.Id}/matches",
            new ConfirmMatch(session.Version, statementLine.Id, [bookLine.Id], "Matched"));
        session = await Transition(s, await Transition(s, session, "review"), "finalize");
        var close = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Client,
            $"{s.Root}/financial-close/periods/{period.Id}/sessions", new { });
        var route = $"{s.Root}/financial-close/sessions/{close.Session.Id}";
        var task = Assert.Single(close.Session.Tasks);
        close = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Client, $"{route}/tasks/{task.Id}/complete",
            new RAQM.Api.DTOs.FinancialClose.CloseAction(close.Session.Version, "Bank reconciled"));
        close = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Client, $"{route}/state/approve",
            new RAQM.Api.DTOs.FinancialClose.CloseAction(close.Session.Version, "Reviewed"));
        close = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Client, $"{route}/state/close",
            new RAQM.Api.DTOs.FinancialClose.CloseAction(close.Session.Version, "Close"));
        Assert.Equal(PeriodState.Closed, close.AccountingState);
        Assert.NotNull(close.Session.EvidenceJson);
        await Denied(s.Client, $"{s.Sessions}/{session.Id}/state/reopen",
            new ReviewAction(session.Version, "Cannot withdraw closed evidence"), HttpStatusCode.Conflict);
        using (var periodScope = fixture.Factory.Services.CreateScope())
        {
            var current = await periodScope.ServiceProvider.GetRequiredService<RaqmDbContext>().Periods.AsNoTracking()
                .SingleAsync(x => x.Id == period.Id);
            await Send<PeriodResponse>(s.Client, $"{s.Core}/periods/{period.Id}/state",
                new PeriodStateRequest(PeriodState.Open, "Authorized correction", current.Version), HttpMethod.Put);
        }
        session = await Transition(s, session, "reopen");
        Assert.Equal(ReconciliationState.Matching, session.State);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var retained = await db.PeriodCloseSessions.SingleAsync(x => x.Id == close.Session.Id);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(close.Session.EvidenceJson),
            System.Text.Json.Nodes.JsonNode.Parse(retained.EvidenceJson!)));
        Assert.Equal(1, await Posted(s));
    }

    [Fact]
    public async Task CloseAndBankEvidenceWithdrawalCannotBothCommit()
    {
        using var s = await Arrange();
        var period = s.Year.Periods[0];
        var book = await Book(s, 80, "BANK", period.StartDate);
        var bookLine = (await Lines(book.JournalId)).Single(x => x.AccountId == s.Bank.LedgerAccountId);
        var session = await Start(s, Statement(period.StartDate, period.EndDate, 0, 80, "race", (period.StartDate, 80, "BANK")));
        session = await Send<ReconciliationView>(s.Client, $"{s.Sessions}/{session.Id}/matches",
            new ConfirmMatch(session.Version, Assert.Single(session.UnmatchedStatement).Id, [bookLine.Id], "Matched"));
        session = await Transition(s, await Transition(s, session, "review"), "finalize");
        var close = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Client,
            $"{s.Root}/financial-close/periods/{period.Id}/sessions", new { });
        var route = $"{s.Root}/financial-close/sessions/{close.Session.Id}";
        close = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Client,
            $"{route}/tasks/{Assert.Single(close.Session.Tasks).Id}/complete",
            new RAQM.Api.DTOs.FinancialClose.CloseAction(close.Session.Version, "Bank reconciled"));
        close = await Send<RAQM.Api.DTOs.FinancialClose.CloseView>(s.Client, $"{route}/state/approve",
            new RAQM.Api.DTOs.FinancialClose.CloseAction(close.Session.Version, "Reviewed"));
        var closeCall = s.Client.PostAsJsonAsync($"{route}/state/close",
            new RAQM.Api.DTOs.FinancialClose.CloseAction(close.Session.Version, "Close"));
        var reopenCall = s.Client.PostAsJsonAsync($"{s.Sessions}/{session.Id}/state/reopen",
            new ReviewAction(session.Version, "Withdraw evidence"));
        using var closeResponse = await closeCall;
        using var reopenResponse = await reopenCall;
        Assert.Equal(1, new[] { closeResponse, reopenResponse }.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, new[] { closeResponse, reopenResponse }.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        var actualPeriod = await db.Periods.AsNoTracking().SingleAsync(x => x.Id == period.Id);
        var actualBank = await db.ReconciliationSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
        Assert.Equal(actualPeriod.State == PeriodState.Closed, actualBank.State == ReconciliationState.Finalized);
    }

    [Fact]
    public async Task R03_004_AuditClosureRequiresRetestOfFullFailedPopulation()
    {
        using var s = await Arrange();
        using var reviewer = await Arrange();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.OrganizationMembers.Add(new() { OrganizationId = s.Org, TenantId = s.Company.TenantId,
                UserId = reviewer.Actor, Role = OrganizationRole.Owner });
            await db.SaveChangesAsync();
        }
        var root = s.Root + "/governance";
        var period = s.Year.Periods[0];
        var from = period.StartDate;
        var cutoff = from.AddDays(1);
        var control = await Send<RAQM.Api.Entities.InternalControls.InternalControl>(s.Client, root + "/controls",
            new RAQM.Api.DTOs.Governance.InternalControlInput("BANK-COVER", "Continuous coverage", "Bank reconciliation",
                "Monthly", "Finalized statements", false, false, RAQM.Api.Entities.InternalControls.ControlCheck.BankCoverage,
                null, s.Actor, from));
        await Send<RAQM.Api.Entities.InternalControls.InternalControl>(reviewer.Client, $"{root}/controls/{control.Id}/approve", new { });
        var initial = await Evaluate(from);
        Assert.Equal(RAQM.Api.Entities.InternalControls.ControlOutcome.Fail, initial.Outcome);
        var work = await Send<RAQM.Api.Entities.InternalControls.GovernanceWork>(s.Client, root + "/work",
            new RAQM.Api.DTOs.Governance.WorkInput(RAQM.Api.Entities.InternalControls.GovernanceWorkKind.Finding,
                "Missing bank coverage", s.Actor, Today.AddDays(1), "Bank", "Reconciliation", "Restore coverage",
                "Confirm complete period", ControlId: control.Id, EvaluationId: initial.Id));
        var workPath = $"{root}/work/{work.Id}";
        work = await Change(reviewer.Client, "approve");
        work = await Change(s.Client, "start");
        await Send<RAQM.Api.Entities.InternalControls.EvidenceReference>(s.Client, root + "/evidence",
            new RAQM.Api.DTOs.Governance.EvidenceInput(work.Id, null, null, "External", "Bank evidence",
                "Synthetic review record", new string('A', 64), false, Today.AddYears(1)));
        work = await Change(s.Client, "submit");
        await Finalize(from, "first");
        Assert.Equal(RAQM.Api.Entities.InternalControls.ControlOutcome.Fail, (await Evaluate(from)).Outcome);
        await Finalize(cutoff, "second");
        await Review(await Evaluate(cutoff));
        await Denied(reviewer.Client, workPath + "/close",
            new RAQM.Api.DTOs.Governance.WorkAction(work.Version, "Incomplete retest"), HttpStatusCode.Conflict);
        await Review(await Evaluate(from));
        work = await Change(reviewer.Client, "close");
        Assert.Equal(RAQM.Api.Entities.InternalControls.GovernanceWorkState.Closed, work.State);

        Task<RAQM.Api.Entities.InternalControls.GovernanceWork> Change(HttpClient client, string action) =>
            Send<RAQM.Api.Entities.InternalControls.GovernanceWork>(client, workPath + "/" + action,
                new RAQM.Api.DTOs.Governance.WorkAction(work.Version, "Reviewed"));
        Task<RAQM.Api.Entities.InternalControls.ControlEvaluation> Evaluate(DateOnly start) =>
            Send<RAQM.Api.Entities.InternalControls.ControlEvaluation>(s.Client, $"{root}/controls/{control.Id}/evaluate",
                new RAQM.Api.DTOs.Governance.EvaluateControlInput(start, cutoff, null, "Test coverage", null));
        Task<RAQM.Api.Entities.InternalControls.ControlEvaluation> Review(RAQM.Api.Entities.InternalControls.ControlEvaluation test) =>
            Send<RAQM.Api.Entities.InternalControls.ControlEvaluation>(reviewer.Client, $"{root}/evaluations/{test.Id}/review",
                new RAQM.Api.DTOs.Governance.ReviewControlInput(RAQM.Api.Entities.InternalControls.ControlOutcome.Pass, "Independently checked"));
        async Task Finalize(DateOnly date, string key)
        {
            var book = await Book(s, 80, key, date);
            var line = (await Lines(book.JournalId)).Single(x => x.AccountId == s.Bank.LedgerAccountId);
            var opening = date == from ? 0 : 80;
            var session = await Start(s, Statement(date, date, opening, opening + 80, key, (date, 80, key)));
            var statementLine = Assert.Single(session.UnmatchedStatement);
            session = await Send<ReconciliationView>(s.Client, $"{s.Sessions}/{session.Id}/matches",
                new ConfirmMatch(session.Version, statementLine.Id, [line.Id], "Matched"));
            await Transition(s, await Transition(s, session, "review"), "finalize");
        }
    }

    [Fact]
    public async Task PettyCashReplenishmentExpenseCountAndVarianceAgreeWithGl()
    {
        using var s = await Arrange();
        var petty = await Account(s, TreasuryAccountKind.PettyCash, "1300", "PETTY");
        var replenishment = await Approve(s, await Transfer(s, s.Bank.Id, petty.Id, 100));
        var funding = await Lines(replenishment.JournalId!.Value);
        Assert.Equal(100, funding.Single(x => x.AccountId == petty.LedgerAccountId).Debit);
        Assert.DoesNotContain(funding, x => x.AccountId == s.Accounts["5100"].Id);
        var expense = await Send<TreasuryMovement>(s.Client, s.Cash + "/movements", new CashMovementRequest(petty.Id,
            TreasuryMovementKind.PettyExpense, s.Accounts["5100"].Id, 30, Today, "Receipt 1", "Stationery", "expense"));
        Assert.Equal(30, (await Lines(expense.JournalId!.Value)).Single(x => x.AccountId == s.Accounts["5100"].Id).Debit);
        await Denied(s.Client, s.Cash + "/movements", new CashMovementRequest(petty.Id, TreasuryMovementKind.PettyExpense,
            s.Accounts["5100"].Id, 71, Today, "Receipt 2", "Overdraw", "overdraw"), HttpStatusCode.Conflict);
        var count = await Send<CashCount>(s.Client, s.Cash + "/counts", new CashCountRequest(petty.Id, Today, 65, "Signed cash count"));
        Assert.Equal(70, count.BookAmount);
        var correction = await Send<TreasuryMovement>(s.Client, $"{s.Cash}/counts/{count.Id}/correct",
            new CountCorrectionRequest(s.Accounts["5100"].Id, "Approved shortage", "variance"));
        Assert.Equal(5, (await Lines(correction.JournalId!.Value)).Single(x => x.AccountId == petty.LedgerAccountId).Credit);
        var activity = (await s.Client.GetFromJsonAsync<TreasuryActivity>($"{s.Root}/treasury/accounts/{petty.Id}/activity?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}"))!;
        Assert.Equal(65, activity.Position.Closing);
        Assert.Equal(100, activity.Position.Incoming);
        Assert.Equal(35, activity.Position.Outgoing);
        Assert.Equal(3, await Posted(s));
    }
}
