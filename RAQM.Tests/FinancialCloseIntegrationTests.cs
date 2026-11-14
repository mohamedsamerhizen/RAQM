using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Accruals;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.DTOs.CashManagement;
using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Accruals;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.CashManagement;
using RAQM.Api.Entities.FinancialClose;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Entities.YearEnd;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.FinancialClose;
using Xunit;

namespace RAQM.Tests;

[Collection("PostgreSQL"), Trait("Category", "Integration")]
public sealed class FinancialCloseIntegrationTests(PostgreSqlFixture fixture)
{
    private sealed record Setup(HttpClient Client, Guid Actor, Guid Org, Company Company, FiscalYearResponse Year,
        Dictionary<string, AccountResponse> Accounts) : IDisposable
    {
        public string Root => $"/api/v1/organizations/{Org}/companies/{Company.Id}";
        public string Core => Root + "/accounting";
        public string Report => Root + "/financial-reporting";
        public string Close => Root + "/financial-close";
        public string Recognition => Root + "/recognition/schedules";
        public void Dispose() => Client.Dispose();
    }

    private static async Task<T> Send<T>(HttpClient client, string path, object body, HttpMethod? method = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == expected, $"{path}: expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task Denied(HttpClient client, string path, object body, HttpStatusCode status, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        Assert.True(response.StatusCode == status, $"{path}: expected {(int)status}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<Setup> Arrange(int fiscalMonth = 1)
    {
        var client = fixture.Factory.CreateClient();
        var tokens = await Send<TokenResponse>(client, "/api/v1/auth/onboard", new OnboardRequest($"{Guid.NewGuid():N}@finance.example",
            "Finance_Test-Password1!", "Finance Org", ApiFactory.OnboardingKey));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        var org = me.GetProperty("organizations")[0].GetProperty("organizationId").GetGuid();
        var company = await Send<Company>(client, $"/api/v1/organizations/{org}/companies",
            new CompanyRequest("Financial close company", Guid.NewGuid().ToString("N"), "USD", fiscalMonth, 1), expected: HttpStatusCode.Created);
        var core = $"/api/v1/organizations/{org}/companies/{company.Id}/accounting";
        var accounts = (await Send<List<AccountResponse>>(client, core + "/accounts/initialize", new InitializeAccountsRequest())).ToDictionary(x => x.Code);
        foreach (var (code, name, type, parent, contra) in new[]
        {
            ("1150","Contra asset",AccountClass.Asset,"1000",true), ("1200","Prepaid asset",AccountClass.Asset,"1000",false),
            ("1300","Accrued asset",AccountClass.Asset,"1000",false), ("1400","Input tax",AccountClass.Asset,"1000",false),
            ("1500","Receivables",AccountClass.Asset,"1000",false), ("1600","Supplier advance",AccountClass.Asset,"1000",false),
            ("2200","Accrued liability",AccountClass.Liability,"2000",false), ("2300","Deferred revenue",AccountClass.Liability,"2000",false),
            ("2400","Output tax",AccountClass.Liability,"2000",false), ("2500","Customer advance",AccountClass.Liability,"2000",false),
            ("3200","Retained earnings",AccountClass.Equity,"3000",false)
        })
            accounts[code] = await Send<AccountResponse>(client, core + "/accounts", new AccountRequest(code, name, null, type,
                true, accounts[parent].Id, IsContra: contra), expected: HttpStatusCode.Created);
        var year = await Send<FiscalYearResponse>(client, core + "/fiscal-years",
            new FiscalYearRequest(DateTime.UtcNow.Year - 2, "Historical fiscal year"), expected: HttpStatusCode.Created);
        return new(client, me.GetProperty("userId").GetGuid(), org, company, year, accounts);
    }

    private static Task<JournalResponse> Draft(Setup s, DateOnly date, string debit, string credit, decimal amount) =>
        Send<JournalResponse>(s.Client, s.Core + "/journals", new JournalRequest(date, date, "Financial test", Guid.NewGuid().ToString("N"),
            [new(s.Accounts[debit].Id, amount, 0, null), new(s.Accounts[credit].Id, 0, amount, null)]), expected: HttpStatusCode.Created);

    private static async Task<PostingReceipt> Post(Setup s, DateOnly date, string debit, string credit, decimal amount)
    {
        var journal = await Draft(s, date, debit, credit, amount);
        return await Send<PostingReceipt>(s.Client, $"{s.Core}/journals/{journal.Id}/post", new PostJournalRequest(Guid.NewGuid().ToString("N"),
            journal.Version, true, "Authorized historical financial test"));
    }

    private static Task<ReportDefinition> Definition(Setup s) => Send<ReportDefinition>(s.Client, s.Report + "/definitions",
        new DefinitionRequest(0, s.Accounts["3200"].Id, [
            new(s.Accounts["1100"].Id, StatementGroup.CurrentAsset), new(s.Accounts["1150"].Id, StatementGroup.NoncurrentAsset),
            new(s.Accounts["3100"].Id, StatementGroup.Capital), new(s.Accounts["3200"].Id, StatementGroup.RetainedEarnings),
            new(s.Accounts["4100"].Id, StatementGroup.OperatingRevenue), new(s.Accounts["5100"].Id, StatementGroup.OperatingExpense)
        ], [new("ManualJournal", CashGroup.Operating)], "Approved financial definition"));

    private static Task<FinancialReport> Report(Setup s, ReportQuery q) => Send<FinancialReport>(s.Client, s.Report + "/query", q);
    private static decimal Amount(FinancialReport report, string key) => report.Rows.Single(x => x.Key == key).Amount;
    private static Task<CloseView> Start(Setup s, PeriodResponse period) =>
        Send<CloseView>(s.Client, $"{s.Close}/periods/{period.Id}/sessions", new { });
    private static Task<CloseView> Complete(Setup s, CloseView view, PeriodCloseTask task) =>
        Send<CloseView>(s.Client, $"{s.Close}/sessions/{view.Session.Id}/tasks/{task.Id}/complete", new CloseAction(view.Session.Version, "Reviewed underlying journals"));
    private static Task<CloseView> Change(Setup s, CloseView view, string action) =>
        Send<CloseView>(s.Client, $"{s.Close}/sessions/{view.Session.Id}/state/{action}", new CloseAction(view.Session.Version, "Reviewed controls and accepted documented warnings"));
    private static Task<RecognitionSchedule> Create(Setup s, ScheduleRequest request) =>
        Send<RecognitionSchedule>(s.Client, s.Recognition, request);
    private static Task<RecognitionSchedule> Approve(Setup s, RecognitionSchedule schedule) =>
        Send<RecognitionSchedule>(s.Client, $"{s.Recognition}/{schedule.Id}/approve", new ScheduleAction(schedule.Version, "Reviewed schedule terms"));
    private static string PostingPath(Setup s, RecognitionSchedule schedule, RecognitionOccurrence occurrence) =>
        $"{s.Recognition}/{schedule.Id}/occurrences/{occurrence.Id}/post";
    private static Task<RecognitionResult> Recognize(Setup s, RecognitionSchedule schedule, RecognitionOccurrence occurrence) =>
        Send<RecognitionResult>(s.Client, PostingPath(s, schedule, occurrence), new RecognitionExecution(occurrence.Date, true, "Authorized recognition"));

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
    public async Task FinancialReportsCompareFiscalPeriodsDrillDownAndExportSameValues()
    {
        using var s = await Arrange(7);
        var july = s.Year.StartDate;
        var august = july.AddMonths(1);
        await Definition(s);
        await Post(s, july, "1100", "3100", 1000);
        var julySale = await Post(s, july.AddDays(1), "1100", "4100", 700);
        await Post(s, august, "1100", "4100", 300);
        await Post(s, august, "5100", "1100", 200);
        await Post(s, august, "5100", "1150", 50);
        var comparison = new ReportQuery(ReportKind.Income, august, august.AddMonths(1).AddDays(-1), CompareFrom: july, CompareTo: august.AddDays(-1));
        var income = await Report(s, comparison);
        var net = income.Rows.Single(x => x.Key == "period:net");
        Assert.Equal(50, net.Amount);
        Assert.Equal(700, net.Comparative);
        Assert.Equal(-92.86m, net.ChangePercent);
        Assert.Equal(750, Amount(income, "ytd:net"));
        var quarter = await Report(s, new(ReportKind.Income, FiscalYearId: s.Year.Id, FiscalQuarter: 1));
        Assert.Equal(750, Amount(quarter, "period:net"));
        var balance = await Report(s, new(ReportKind.BalanceSheet, To: august));
        Assert.Equal(1750, Amount(balance, "assets"));
        Assert.Equal(750, Amount(balance, "current-profit"));
        Assert.Equal(1750, Amount(balance, "equity"));
        Assert.Equal(0, balance.IntegrityDifference);
        var drill = await Send<PageResult<FinancialLine>>(s.Client, $"{s.Report}/accounts/{s.Accounts["4100"].Id}/drill?ytd=true&pageSize=1", comparison);
        Assert.Equal(2, drill.Total);
        Assert.Equal(julySale.JournalId, Assert.Single(drill.Items).JournalId);
        using var csv = await s.Client.PostAsJsonAsync(s.Report + "/export/csv", comparison);
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Contains("\"period:net\",\"Net profit or loss\",50,700,-92.86", await csv.Content.ReadAsStringAsync());
        using var pdf = await s.Client.PostAsJsonAsync(s.Report + "/export/pdf", comparison);
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Contains("Net profit or loss: 50", Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync()));
        using var foreign = await Arrange();
        await Denied(foreign.Client, s.Report + "/query", comparison, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IssuedReportSequenceRetainsValuesAndDrillAfterBackdatedPosting()
    {
        using var s = await Arrange();
        var date = s.Year.StartDate;
        await Definition(s);
        await Post(s, date, "1100", "4100", 100);
        var query = new ReportQuery(ReportKind.Income, date, date, Revision: 1);
        var issued = await Report(s, query);
        Assert.Equal(100, Amount(issued, "period:net"));
        await Post(s, date, "1100", "4100", 50);
        Assert.Equal(150, Amount(await Report(s, query), "period:net"));
        var pinned = query with { AsOfSequence = issued.PostedSequenceCutoff };
        Assert.Equal(100, Amount(await Report(s, pinned), "period:net"));
        var drill = await Send<PageResult<FinancialLine>>(s.Client,
            $"{s.Report}/accounts/{s.Accounts["4100"].Id}/drill", pinned);
        Assert.Equal(1, drill.Total);
        using var csv = await s.Client.PostAsJsonAsync(s.Report + "/export/csv", pinned);
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Contains("\"period:net\",\"Net profit or loss\",100", await csv.Content.ReadAsStringAsync());
        await Denied(s.Client, s.Report + "/query", query with { AsOfSequence = -1 }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CashMovementReconcilesRegisteredAssetsAndEliminatesInternalTransfers()
    {
        using var s = await Arrange();
        var date = s.Year.StartDate;
        var cash = await Send<TreasuryAccountResponse>(s.Client, s.Root + "/banking/accounts",
            new TreasuryAccountRequest(TreasuryAccountKind.Cash, "CASH", "Till", s.Accounts["1100"].Id));
        var other = await Send<TreasuryAccountResponse>(s.Client, s.Root + "/banking/accounts",
            new TreasuryAccountRequest(TreasuryAccountKind.Cash, "CASH2", "Safe", s.Accounts["1300"].Id));
        await Post(s, date, "1100", "4100", 750);
        var transfer = await Send<TreasuryMovement>(s.Client, s.Root + "/cash-management/transfers",
            new TransferRequest(cash.Id, other.Id, 250, date, "T1", "Move money", "transfer"));
        await Send<TreasuryMovement>(s.Client, $"{s.Root}/cash-management/transfers/{transfer.Id}/approve",
            new MovementAction(transfer.Version, date, "Approved", "approval", true));
        var r = await Report(s, new(ReportKind.CashMovement, date, date));
        Assert.Equal(750, Amount(r, "cash:net"));
        Assert.Equal(0, Amount(r, "cash:transfer"));
        Assert.Equal(750, Amount(r, "cash:closing"));
        Assert.Equal(0, r.IntegrityDifference);
    }

    [Fact]
    public async Task CashMovementIncludesTradingCashWithoutTreasuryRegistration()
    {
        using var s = await Arrange();
        var date = s.Year.StartDate;
        await Definition(s);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.TradingAccounts.Add(new TradingAccountMapping { TenantId = s.Company.TenantId, OrganizationId = s.Org,
                CompanyId = s.Company.Id, AccountId = s.Accounts["1100"].Id, Role = AccountRole.Cash,
                ActorId = s.Actor, Reason = "Legacy trading cash mapping" });
            await db.SaveChangesAsync();
        }
        await Post(s, date, "1100", "4100", 90);
        var report = await Report(s, new(ReportKind.CashMovement, date, date));
        Assert.Equal(90, Amount(report, "cash:net"));
        Assert.Equal(90, Amount(report, "cash:closing"));
        Assert.Equal(0, report.IntegrityDifference);
    }

    [Fact]
    public async Task ChecklistAndExceptionsRequireEvidenceAndStaleApprovalCannotClose()
    {
        using var s = await Arrange();
        var period = s.Year.Periods.First();
        var view = await Start(s, period);
        var path = $"{s.Close}/sessions/{view.Session.Id}";
        await Denied(s.Client, path + "/state/approve", new CloseAction(view.Session.Version, "Premature"), HttpStatusCode.Conflict);
        view = await Complete(s, view, Assert.Single(view.Session.Tasks));
        view = await Send<CloseView>(s.Client, path + "/tasks", new CloseTaskRequest(view.Session.Version, "ADJUSTMENTS", "Inspect adjustments", s.Actor));
        view = await Complete(s, view, view.Session.Tasks.Single(x => x.Code == "ADJUSTMENTS"));
        view = await Send<CloseView>(s.Client, path + "/exceptions", new ExceptionRequest(view.Session.Version, "Investigation pending"));
        var exception = Assert.Single(view.Exceptions);
        await Denied(s.Client, path + "/state/approve", new CloseAction(view.Session.Version, "Open exception"), HttpStatusCode.Conflict);
        view = await Send<CloseView>(s.Client, path + $"/exceptions/{exception.Id}/resolve", new CloseAction(view.Session.Version, "Matched to evidence"));
        view = await Change(s, view, "approve");
        Assert.Equal(PeriodState.Open, view.AccountingState);
        await Denied(s.Client, $"{s.Core}/periods/{period.Id}/state", new PeriodStateRequest(PeriodState.Closed, "Bypass", period.Version),
            HttpStatusCode.Conflict, HttpMethod.Put);
        await Post(s, period.StartDate, "1100", "4100", 100);
        await Denied(s.Client, path + "/state/close", new CloseAction(view.Session.Version, "Stale approval"), HttpStatusCode.Conflict);
        view = await Change(s, view, "revalidate");
        view = await Change(s, view, "approve");
        view = await Change(s, view, "close");
        Assert.Equal(CloseWorkflowState.Completed, view.Session.State);
        Assert.Equal(PeriodState.Closed, view.AccountingState);
        Assert.NotNull(view.Session.EvidenceJson);
        var journal = await Draft(s, period.StartDate, "1100", "4100", 10);
        await Denied(s.Client, $"{s.Core}/journals/{journal.Id}/post", new PostJournalRequest("closed", journal.Version, true, "Closed period"),
            HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task R01_001_DirectCloseRequiresTheSameBankEvidenceAsFullReadiness()
    {
        using var s = await Arrange();
        var period = s.Year.Periods.First();
        var profile = await Send<BankProfile>(s.Client, s.Root + "/banking/banks", new BankProfileRequest("BANK", "Bank", null));
        await Send<TreasuryAccountResponse>(s.Client, s.Root + "/banking/accounts",
            new TreasuryAccountRequest(TreasuryAccountKind.Bank, "BANK-1", "Bank", s.Accounts["1100"].Id, profile.Id, "123456"));
        var journal = await Post(s, period.StartDate, "1100", "4100", 100);
        await Denied(s.Client, $"{s.Core}/periods/{period.Id}/state",
            new PeriodStateRequest(PeriodState.Closed, "Direct period close", period.Version), HttpStatusCode.Conflict, HttpMethod.Put);
        Assert.Equal(100m, (await Lines(journal.JournalId)).Sum(x => x.Debit));
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
        Assert.False(await db.PeriodCloseSessions.AnyAsync(x => x.CompanyId == s.Company.Id && x.PeriodId == period.Id));
        Assert.Equal(PeriodState.Open, (await db.Periods.AsNoTracking().SingleAsync(x => x.Id == period.Id)).State);
    }

    [Theory]
    [InlineData(true, "bank-reconciliation")]
    [InlineData(false, "ar-ap-reconciliation")]
    public async Task RequiredReconciliationEvidenceBlocksApproval(bool bank, string rule)
    {
        using var s = await Arrange();
        var period = s.Year.Periods.First();
        if (bank)
        {
            var profile = await Send<BankProfile>(s.Client, s.Root + "/banking/banks", new BankProfileRequest("BANK", "Bank", null));
            await Send<TreasuryAccountResponse>(s.Client, s.Root + "/banking/accounts",
                new TreasuryAccountRequest(TreasuryAccountKind.Bank, "BANK-1", "Bank", s.Accounts["1100"].Id, profile.Id, "123456"));
            await Post(s, period.StartDate, "1100", "4100", 100);
        }
        else
        {
            var a = s.Accounts;
            await Post(s, period.StartDate, "1500", "4100", 100);
            await Send<List<ControlAccountResponse>>(s.Client, s.Root + "/trading/configuration",
                new ControlAccountRequest(a["1500"].Id, a["2100"].Id, a["4100"].Id, a["5100"].Id, a["2400"].Id,
                    a["1400"].Id, a["2500"].Id, a["1600"].Id, a["3100"].Id, [a["1100"].Id], true, "Acknowledge legacy GL"), HttpMethod.Put);
        }
        var view = await Start(s, period);
        view = await Complete(s, view, Assert.Single(view.Session.Tasks));
        var path = $"{s.Close}/sessions/{view.Session.Id}";
        var ready = (await s.Client.GetFromJsonAsync<CloseReadiness>(path + "/readiness"))!;
        Assert.Contains(ready.Findings, x => x.Rule == rule && x.Severity == FindingSeverity.Blocking);
        await Denied(s.Client, path + "/state/approve", new CloseAction(view.Session.Version, "Cannot override reconciliation"), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ManagerCanPrepareButCannotApproveOrClose()
    {
        using var s = await Arrange();
        using var manager = await Arrange();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RaqmDbContext>();
            db.OrganizationMembers.Add(new() { TenantId = s.Company.TenantId, OrganizationId = s.Org, UserId = manager.Actor, Role = OrganizationRole.Member });
            db.CompanyMembers.Add(new() { TenantId = s.Company.TenantId, OrganizationId = s.Org, CompanyId = s.Company.Id,
                UserId = manager.Actor, Role = CompanyRole.Manager });
            await db.SaveChangesAsync();
        }
        var period = s.Year.Periods.First();
        var view = await Send<CloseView>(manager.Client, $"{s.Close}/periods/{period.Id}/sessions", new { });
        var task = Assert.Single(view.Session.Tasks);
        Assert.Equal(manager.Actor, task.ReviewerId);
        view = await Send<CloseView>(manager.Client, $"{s.Close}/sessions/{view.Session.Id}/tasks/{task.Id}/complete",
            new CloseAction(view.Session.Version, "Reviewed"));
        await Denied(manager.Client, $"{s.Close}/sessions/{view.Session.Id}/state/approve", new CloseAction(view.Session.Version, "Unauthorized"),
            HttpStatusCode.Forbidden);
        view = await Change(s, view, "approve");
        await Denied(manager.Client, $"{s.Close}/sessions/{view.Session.Id}/state/close", new CloseAction(view.Session.Version, "Unauthorized"),
            HttpStatusCode.Forbidden);
        Assert.Equal(PeriodState.Closed, (await Change(s, view, "close")).AccountingState);
    }

    [Fact]
    public async Task AccrualRecognitionAndScheduledReversalAreExactAndIdempotent()
    {
        using var s = await Arrange();
        var date = s.Year.StartDate;
        var schedule = await Create(s, new(RecognitionKind.AccruedExpense, "Accrued rent", "ACCRUAL-1", date, 1, 1, 120,
            s.Accounts["5100"].Id, s.Accounts["2200"].Id, ReverseAfterDays: 31));
        var occurrence = Assert.Single(schedule.Occurrences);
        await Denied(s.Client, PostingPath(s, schedule, occurrence), new RecognitionExecution(date, true, "Unapproved"), HttpStatusCode.Conflict);
        schedule = await Approve(s, schedule);
        var booked = await Recognize(s, schedule, occurrence);
        Assert.Equal(booked.JournalId, (await Recognize(s, schedule, occurrence)).JournalId);
        var lines = await Lines(booked.JournalId!.Value);
        Assert.Equal(120, lines.Single(x => x.AccountId == s.Accounts["5100"].Id).Debit);
        Assert.Equal(120, lines.Single(x => x.AccountId == s.Accounts["2200"].Id).Credit);
        var run = await Send<List<RecognitionResult>>(s.Client, $"{s.Recognition}/{schedule.Id}/run",
            new RecognitionExecution(date.AddDays(31), true, "Execute approved reversal"));
        var reversal = await Lines(Assert.Single(run).JournalId!.Value);
        Assert.Equal(120, reversal.Single(x => x.AccountId == s.Accounts["5100"].Id).Credit);
        Assert.Equal(120, reversal.Single(x => x.AccountId == s.Accounts["2200"].Id).Debit);
        Assert.Empty(await Send<List<RecognitionResult>>(s.Client, $"{s.Recognition}/{schedule.Id}/run",
            new RecognitionExecution(date.AddDays(31), true, "Retry")));
        var result = (await s.Client.GetFromJsonAsync<ScheduleReconciliation>($"{s.Recognition}/{schedule.Id}/reconcile"))!;
        Assert.Equal(0, result.ExpectedNet);
        Assert.Equal(0, result.Difference);
        Assert.Equal(2, await Posted(s));
    }

    [Theory]
    [InlineData(RecognitionKind.PrepaidExpense)]
    [InlineData(RecognitionKind.DeferredRevenue)]
    public async Task DeferralConsumesPostedBasisMonthlyAndProtectsOriginal(RecognitionKind kind)
    {
        using var s = await Arrange();
        var prepaid = kind == RecognitionKind.PrepaidExpense;
        var start = s.Year.StartDate;
        var basis = await Post(s, start, prepaid ? "1200" : "1100", prepaid ? "1100" : "2300", 1200);
        var basisId = (await Lines(basis.JournalId)).Single(x => x.AccountId == s.Accounts[prepaid ? "1200" : "2300"].Id).Id;
        var debit = s.Accounts[prepaid ? "5100" : "2300"].Id;
        var credit = s.Accounts[prepaid ? "1200" : "4100"].Id;
        var request = new ScheduleRequest(kind, "Recognition policy", "DEF-1", start, 12, 1, 1200, debit, credit, basisId);
        var schedule = await Approve(s, await Create(s, request));
        await Denied(s.Client, s.Recognition, request with { Reference = "DUPLICATE-BASIS" }, HttpStatusCode.Conflict);
        var preview = (await s.Client.GetFromJsonAsync<List<RecognitionPreview>>($"{s.Recognition}/{schedule.Id}/preview"))!;
        Assert.Equal(12, preview.Count);
        Assert.All(preview, x => Assert.Equal(100, x.Amount));
        var recognized = await Recognize(s, schedule, schedule.Occurrences.Single(x => x.Number == 1));
        var lines = await Lines(recognized.JournalId!.Value);
        Assert.Equal(100, lines.Single(x => x.AccountId == debit).Debit);
        Assert.Equal(100, lines.Single(x => x.AccountId == credit).Credit);
        var result = (await s.Client.GetFromJsonAsync<ScheduleReconciliation>($"{s.Recognition}/{schedule.Id}/reconcile"))!;
        Assert.Equal(1100, result.RemainingBasis);
        Assert.Equal(100, result.LedgerNet);
        Assert.Equal(0, result.Difference);
        await Denied(s.Client, $"{s.Core}/journals/{basis.JournalId}/reverse",
            new ReverseJournalRequest(start, "Basis remains in use", "blocked-basis", true), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CancellationStopsFutureRecognitionAndExplicitCorrectionRetainsOriginal()
    {
        using var s = await Arrange();
        var date = s.Year.StartDate;
        var schedule = await Approve(s, await Create(s, new(RecognitionKind.AccruedRevenue, "Accrued work", "ACCRUAL-2", date, 2, 1, 500,
            s.Accounts["1300"].Id, s.Accounts["4100"].Id)));
        var first = schedule.Occurrences.Single(x => x.Number == 1);
        var second = schedule.Occurrences.Single(x => x.Number == 2);
        var original = await Recognize(s, schedule, first);
        schedule = await Send<RecognitionSchedule>(s.Client, $"{s.Recognition}/{schedule.Id}/cancel", new ScheduleAction(schedule.Version, "Cancel future work"));
        await Denied(s.Client, PostingPath(s, schedule, second), new RecognitionExecution(second.Date, true, "Cancelled"), HttpStatusCode.Conflict);
        var path = $"{s.Recognition}/{schedule.Id}/occurrences/{first.Id}/reverse";
        var correction = new RecognitionReversal(date.AddDays(1), "correct", "Correct posted source", true);
        var reversed = await Send<RecognitionResult>(s.Client, path, correction);
        Assert.Equal(reversed.JournalId, (await Send<RecognitionResult>(s.Client, path, correction)).JournalId);
        Assert.Equal(250, (await Lines(original.JournalId!.Value)).Single(x => x.AccountId == s.Accounts["4100"].Id).Credit);
        Assert.Equal(250, (await Lines(reversed.JournalId!.Value)).Single(x => x.AccountId == s.Accounts["4100"].Id).Debit);
        Assert.Equal(2, await Posted(s));
    }

    [Fact]
    public async Task ConcurrentRecurringExecutionsCommitOneJournalAndDueWorkBlocksDirectClose()
    {
        using var s = await Arrange();
        var start = s.Year.StartDate;
        var schedule = await Approve(s, await Create(s, new(RecognitionKind.Recurring, "Monthly charge", "RECUR-1", start, 2, 1, 100,
            Template: [new(s.Accounts["5100"].Id, 50, 0, "Charge"), new(s.Accounts["2200"].Id, 0, 50, "Liability")])));
        var first = schedule.Occurrences.Single(x => x.Number == 1);
        var result = await Task.WhenAll(Recognize(s, schedule, first), Recognize(s, schedule, first));
        Assert.NotNull(result[0].JournalId);
        Assert.Equal(result[0].JournalId, result[1].JournalId);
        var feb = s.Year.Periods.Single(x => x.Number == 2);
        await Denied(s.Client, $"{s.Core}/periods/{feb.Id}/state", new PeriodStateRequest(PeriodState.Closed, "Historical close", feb.Version),
            HttpStatusCode.Conflict, HttpMethod.Put);
        var completed = Assert.Single(await Send<List<RecognitionResult>>(s.Client, $"{s.Recognition}/{schedule.Id}/run",
            new RecognitionExecution(feb.StartDate, true, "Scheduled run")));
        Assert.NotNull(completed.JournalId);
        Assert.Equal(2, await Posted(s));
    }

    [Fact]
    public async Task NonCalendarYearEndIsAtomicIdempotentAndPreservesHistoricalProfit()
    {
        using var s = await Arrange(7);
        await Definition(s);
        await Post(s, s.Year.StartDate, "1100", "4100", 700);
        await Post(s, s.Year.StartDate, "5100", "1100", 250);
        var before = await Report(s, new(ReportKind.BalanceSheet, FiscalYearId: s.Year.Id));
        Assert.Equal(450, Amount(before, "current-profit"));
        foreach (var p in s.Year.Periods.OrderBy(x => x.Number).Take(11))
            await Send<PeriodResponse>(s.Client, $"{s.Core}/periods/{p.Id}/state", new PeriodStateRequest(PeriodState.Closed, "Reviewed", p.Version), HttpMethod.Put);
        var final = s.Year.Periods.Single(x => x.Number == 12);
        var view = await Start(s, final);
        view = await Change(s, await Complete(s, view, Assert.Single(view.Session.Tasks)), "approve");
        var request = new YearEndRequest(view.Session.Id, view.Session.Version, "year-end", "Approved year-end", true);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var tx = scope.ServiceProvider.GetRequiredService<IFinancialTransaction>();
            var close = scope.ServiceProvider.GetRequiredService<IFinancialCloseService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => tx.Execute<int>(s.Actor, s.Org, s.Company.Id, async (_, ct) =>
            {
                await close.YearEnd(s.Actor, s.Org, s.Company.Id, s.Year.Id, request, ct);
                throw new InvalidOperationException("Injected failure after closing journal and period update");
            }, default));
        }
        Assert.Equal(2, await Posted(s));
        Assert.Empty((await s.Client.GetFromJsonAsync<List<YearEndRecord>>(s.Close + "/years"))!);
        var path = $"{s.Close}/years/{s.Year.Id}/close";
        var completed = await Send<YearEndRecord>(s.Client, path, request);
        Assert.True(completed.Finalized);
        Assert.Equal(450, completed.Profit);
        Assert.Equal(completed.Id, (await Send<YearEndRecord>(s.Client, path, request)).Id);
        await Denied(s.Client, path, request with { Key = "duplicate" }, HttpStatusCode.Conflict);
        var lines = await Lines(completed.JournalId!.Value);
        Assert.All(lines, line => Assert.True(line.Debit == 0 || line.Credit == 0));
        Assert.Equal(700, lines.Single(x => x.AccountId == s.Accounts["4100"].Id).Debit);
        Assert.Equal(250, lines.Single(x => x.AccountId == s.Accounts["5100"].Id).Credit);
        Assert.Equal(450, lines.Single(x => x.AccountId == s.Accounts["3200"].Id).Credit);
        Assert.Equal(6, s.Year.EndDate.Month);
        Assert.Equal(30, s.Year.EndDate.Day);
        var historical = await Report(s, new(ReportKind.Income, FiscalYearId: s.Year.Id));
        Assert.Equal(450, Amount(historical, "period:net"));
        var balance = await Report(s, new(ReportKind.BalanceSheet, FiscalYearId: s.Year.Id));
        Assert.Equal(0, Amount(balance, "current-profit"));
        Assert.Equal(450, Amount(balance, "retained"));
        Assert.Equal(450, Amount(balance, "equity"));
        var equity = await Report(s, new(ReportKind.Equity, FiscalYearId: s.Year.Id));
        Assert.Equal(450, Amount(equity, "equity:closing"));
        Assert.Equal(0, equity.IntegrityDifference);
        var fiscal = Assert.Single((await s.Client.GetFromJsonAsync<List<FiscalYearResponse>>(s.Core + "/fiscal-years"))!);
        var current = fiscal.Periods.Single(x => x.Number == 12);
        await Denied(s.Client, $"{s.Core}/periods/{current.Id}/state", new PeriodStateRequest(PeriodState.Open, "Forbidden", current.Version),
            HttpStatusCode.Conflict, HttpMethod.Put);
        var next = await Send<FiscalYearResponse>(s.Client, s.Core + "/fiscal-years",
            new FiscalYearRequest(s.Year.StartDate.Year + 1, "Next fiscal year"), expected: HttpStatusCode.Created);
        Assert.Equal(450, Amount(await Report(s, new(ReportKind.BalanceSheet, To: next.StartDate)), "retained"));
        Assert.Equal(3, await Posted(s));
    }

    [Fact]
    public async Task DatabaseRejectsMutationOfPublishedReportEvidence()
    {
        using var s = await Arrange();
        var definition = await Definition(s);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE financial_close.\"ReportDefinitions\" SET \"Revision\"=5 WHERE \"Id\"=@id", connection);
        command.Parameters.AddWithValue("id", definition.Id);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }
}
