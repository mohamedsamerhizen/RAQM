using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Accruals;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.FinancialClose;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Intercompany;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.FinancialReporting;
using RAQM.Api.Services.FixedAssets;
using RAQM.Api.Services.Inventory;
using RAQM.Api.Services.FinancialAnalytics;
using RAQM.Api.Services.MultiCurrency;
using RAQM.Api.Services.AnalyticalReporting;
using RAQM.Api.Services.TaxReporting;
using RAQM.Api.Services.InternalControls;

namespace RAQM.Api.Services.FinancialClose;

public sealed class CloseReadinessService(RaqmDbContext db, FinancialLedger ledger, FinancialReportService reports,
    ITradingReportService trading, TimeProvider clock, AssetCloseCheck assets, InventoryReporting inventory, BudgetReporting budgets,
    FxReporting fx, DimensionReporting dimensions, TaxReportingService taxReports, ControlService? controlService = null)
{
    public async Task<CloseReadiness> Check(Company scope, Guid actor, PeriodCloseSession session, AccountingPeriod period,
        IReadOnlyList<CloseException> exceptions, CancellationToken ct)
    {
        var company = scope.Id;
        var findings = new List<CloseFinding>();
        void Add(string rule, FindingSeverity severity, string detail, Guid? source = null) => findings.Add(new(rule, severity, detail, source));
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (period.State != PeriodState.Open || period.EndDate > today)
            Add("period-state", FindingSeverity.Blocking, "The period must be open and its ending date must have arrived.", period.Id);
        foreach (var task in session.Tasks.Where(x => x.CompletedAt == null))
            Add("checklist", task.Required ? FindingSeverity.Blocking : FindingSeverity.Review, $"Checklist task {task.Code} is incomplete.", task.Id);
        foreach (var item in exceptions.Where(x => x.ResolvedAt == null))
            Add("exception", item.Blocking ? FindingSeverity.Blocking : FindingSeverity.Review, item.Detail, item.Id);

        var accounts = await ledger.Accounts(company, ct);
        var byAccount = accounts.ToDictionary(x => x.Id);
        var drafts = await db.Journals.AsNoTracking().Include(x => x.Lines).Where(x => x.CompanyId == company &&
            x.State == JournalState.Draft && x.PostingDate >= period.StartDate && x.PostingDate <= period.EndDate).ToListAsync(ct);
        foreach (var draft in drafts)
        {
            try
            {
                AccountingRules.Lines(draft.Lines.Select(x => new DTOs.Accounting.JournalDraftLine(x.AccountId, x.Debit, x.Credit, x.Description)).ToArray(), scope.BaseCurrency, true);
                if (draft.Currency != scope.BaseCurrency || draft.Lines.Any(x => !byAccount.TryGetValue(x.AccountId, out var a) || !a.IsActive || !a.IsPosting))
                    Add("invalid-draft", FindingSeverity.Blocking, "A draft uses an ineligible account.", draft.Id);
                else Add("unposted-draft", FindingSeverity.Review, "A balanced journal remains in draft.", draft.Id);
            }
            catch (ApiException e) { Add("invalid-draft", FindingSeverity.Blocking, e.Message, draft.Id); }
        }
        var tradingDrafts = await db.TradeDocuments.AsNoTracking().Where(x => x.CompanyId == company && x.DocumentDate >= period.StartDate &&
            x.DocumentDate <= period.EndDate && (x.State == TradeState.Draft || x.State == TradeState.Approved))
            .Select(x => new { x.Id, x.Version, x.State }).ToListAsync(ct);
        foreach (var draft in tradingDrafts) Add("unposted-trade", FindingSeverity.Review, "A trading document remains unposted.", draft.Id);

        var tradingMappings = await db.TradingAccounts.AsNoTracking().Where(x => x.CompanyId == company && x.Role != AccountRole.Cash).ToListAsync(ct);
        var parties = await db.TradingParties.AnyAsync(x => x.CompanyId == company, ct);
        TradingReconciliation? reconciliation = null;
        if (parties || tradingMappings.Count != 0)
        {
            if (tradingMappings.Count != 9) Add("trading-mapping", FindingSeverity.Blocking, "Configured AR/AP requires all control mappings.");
            else
            {
                reconciliation = await trading.Reconcile(actor, scope.OrganizationId, company, period.EndDate, ct);
                if (!reconciliation.Agrees) Add("ar-ap-reconciliation", FindingSeverity.Blocking, "AR/AP, advance, cash or funding reconciliation differs from the ledger.");
                foreach (var control in reconciliation.Controls.Where(x => x.Difference != 0))
                    Add("ar-ap-control", FindingSeverity.Blocking, FormattableString.Invariant($"{control.Name} difference {control.Difference}."));
            }
        }
        else Add("ar-ap-optional", FindingSeverity.Information, "No AR/AP subledger is configured.");

        var entries = await ledger.Lines(company, period.EndDate).ToListAsync(ct);
        var assetEvidence = await assets.Check(company, period, ct);
        findings.AddRange(assetEvidence.Findings);
        var inventoryEvidence = await inventory.Check(company, period, ct);
        findings.AddRange(inventoryEvidence.Findings);
        var budgetEvidence = await budgets.Check(company, period, ct);
        findings.AddRange(budgetEvidence.Findings);
        var fxEvidence = await fx.Check(company, period, ct);
        findings.AddRange(fxEvidence.Findings);
        var dimensional = await dimensions.Check(company, period, ct);
        findings.AddRange(dimensional.Findings);
        var governance = controlService is null || period.EndDate > today ? (Findings: Array.Empty<CloseFinding>(), Fingerprint: "") :
            await controlService.Close(scope, actor, period, ct);
        findings.AddRange(governance.Findings);
        var taxEvidence = new List<string>();
        if (period.EndDate <= today)
        {
            var profiles = await db.Set<TaxRegistration>().AsNoTracking().Where(x => x.CompanyId == company && x.ApprovedBy != null &&
                x.EffectiveOn <= period.EndDate && x.Currency == scope.BaseCurrency).OrderBy(x => x.Id).ToListAsync(ct);
            foreach (var profile in profiles)
            {
                if (!await db.Set<TaxControl>().AnyAsync(x => x.CompanyId == company && x.RegistrationId == profile.Id, ct))
                {
                    Add("tax-control-missing", FindingSeverity.Review, "An approved tax registration has no reconciliation controls.", profile.Id);
                    continue;
                }
                var snapshot = await taxReports.Capture(scope, profile.Id,
                    period.StartDate < profile.EffectiveOn ? profile.EffectiveOn : period.StartDate, period.EndDate, ct);
                taxEvidence.Add(TradeRules.Hash(snapshot));
                if (!snapshot.Agrees)
                    Add("tax-control-difference", FindingSeverity.Blocking, "Tax control balances differ from retained source evidence.", profile.Id);
                if (snapshot.Pending.Length > 0 || snapshot.UnprofiledDocuments.Length > 0 || snapshot.UnassignedTaxLines.Length > 0)
                    Add("tax-evidence-review", FindingSeverity.Review, "Pending tax work or unassigned tax documents require review.", profile.Id);
                if (snapshot.Reconciliation.Any(x => x.Role == TaxControlRole.Withholding && x.Closing < 0))
                    Add("tax-withholding-balance", FindingSeverity.Information, "A withholding liability remains outstanding at cutoff.", profile.Id);
            }
        }
        var pendingPairs = await db.Set<IntercompanyPair>().AsNoTracking().Where(x =>
            (x.CreditorId == company || x.DebtorId == company) && x.Date <= period.EndDate &&
            (x.State == PairState.Draft || x.State == PairState.Approved)).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.State }).ToListAsync(ct);
        if (pendingPairs.Count > 0)
            Add("intercompany-pending", FindingSeverity.Review, "One or more reciprocal postings involving this company await completion.");
        var signed = FinancialMath.Balances(entries);
        var banks = await db.TreasuryAccounts.AsNoTracking().Where(x => x.CompanyId == company && x.Kind == TreasuryAccountKind.Bank).ToListAsync(ct);
        var bankEvidence = await (from b in db.BankStatements.AsNoTracking()
            join s in db.ReconciliationSessions.AsNoTracking() on b.Id equals s.StatementId into sessions
            from s in sessions.DefaultIfEmpty()
            where b.CompanyId == company && b.Start <= period.EndDate && b.End >= period.StartDate
            select new { b.Id, b.BankAccountId, b.Start, b.End, Session = (Guid?)s.Id, State = (ReconciliationState?)s.State, Version = (Guid?)s.Version }).ToListAsync(ct);
        foreach (var bank in banks)
        {
            var evidence = bankEvidence.Where(x => x.BankAccountId == bank.Id).ToArray();
            var required = evidence.Length > 0 || signed.GetValueOrDefault(bank.LedgerAccountId) != 0 ||
                entries.Any(x => x.AccountId == bank.LedgerAccountId && x.Date >= period.StartDate);
            if (!required) continue;
            var finalized = evidence.Where(x => x.State == ReconciliationState.Finalized).OrderBy(x => x.Start).ToArray();
            var coveredThrough = period.StartDate.AddDays(-1);
            foreach (var item in finalized)
                if (item.Start.DayNumber <= coveredThrough.DayNumber + 1 && item.End > coveredThrough) coveredThrough = item.End;
            if (coveredThrough < period.EndDate)
                Add("bank-reconciliation", FindingSeverity.Blocking, $"Bank {bank.Code} needs continuous finalized statements through period end.", bank.Id);
        }

        var recognition = await (from row in db.RecognitionOccurrences.AsNoTracking()
            join schedule in db.RecognitionSchedules.AsNoTracking() on row.ScheduleId equals schedule.Id
            where row.CompanyId == company && row.Date <= period.EndDate
            select new { row.Id, row.Date, row.Amount, row.ReverseDue, row.JournalId, row.ReversalJournalId,
                ReversalDate = db.Journals.Where(j => j.CompanyId == company && j.Id == row.ReversalJournalId)
                    .Select(j => (DateOnly?)j.PostingDate).FirstOrDefault(), schedule.State, schedule.Version })
            .ToListAsync(ct);
        foreach (var occurrence in recognition)
        {
            if (occurrence.JournalId is null && occurrence.State != RecognitionState.Cancelled)
                Add(occurrence.State == RecognitionState.Draft ? "recognition-approval" : "recognition-due",
                    FindingSeverity.Blocking, "A due schedule occurrence lacks approved posted recognition.", occurrence.Id);
            if (occurrence.JournalId.HasValue && occurrence.ReverseDue <= period.EndDate && occurrence.ReversalJournalId is null)
                Add("recognition-reversal", FindingSeverity.Blocking, "A scheduled reversal is overdue.", occurrence.Id);
            if (occurrence.ReverseDue <= period.EndDate && occurrence.ReversalDate > period.EndDate)
                Add("recognition-late-reversal", FindingSeverity.Review, "The scheduled reversal was posted after the close cutoff.", occurrence.Id);
        }
        if (await (from row in db.RecognitionOccurrences join schedule in db.RecognitionSchedules on row.ScheduleId equals schedule.Id
            where row.CompanyId == company && row.Date > period.EndDate && row.JournalId == null && schedule.State == RecognitionState.Approved
            select row.Id).AnyAsync(ct))
            Add("future-recognition", FindingSeverity.Information, "Approved schedules have future occurrences.");

        var definition = await reports.Definition(company, null, ct);
        var mapping = definition?.Mappings.ToDictionary(x => x.AccountId, x => x.Group) ?? [];
        var year = await db.FiscalYears.AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == period.FiscalYearId, ct);
        var balance = FinancialMath.Build(ReportKind.BalanceSheet, new(period.StartDate, period.EndDate), year.StartDate, accounts, entries, mapping, new HashSet<Guid>(), new Dictionary<string, CashGroup>());
        if (balance.Difference != 0)
            Add("balance-sheet", FindingSeverity.Blocking, FormattableString.Invariant($"Balance sheet integrity difference {balance.Difference}."));
        if (mapping.Any(x => !byAccount.TryGetValue(x.Key, out var account) || !account.IsPosting || FinancialMath.Class(x.Value) != account.Classification))
            Add("report-mapping", FindingSeverity.Blocking, "A published report mapping is incompatible with the chart.", definition?.Id);
        if (balance.Warnings.Contains("Unclassified accounts are included in class totals."))
            Add("report-classification", FindingSeverity.Review, "Some balances use unclassified report groups.", definition?.Id);

        var priorPeriods = await db.Periods.AsNoTracking().Where(x => x.CompanyId == company && x.EndDate < period.StartDate)
            .Select(x => new { x.Id, x.State, x.Version }).ToListAsync(ct);
        if (period.EndDate == year.EndDate)
        {
            var closingBalances = FinancialMath.Balances(entries.Where(x => x.Date >= year.StartDate &&
                byAccount.TryGetValue(x.AccountId, out var account) && FinancialMath.Nominal(account.Classification)));
            if (FinancialMath.ClosingLineCount(closingBalances) > 1000)
                Add("year-end-capacity", FindingSeverity.Blocking, "The year-end closing journal exceeds the supported 1000-line limit. Reduce the closing account population before approval.");
            if (definition?.RetainedEarningsId is not { } retained || !byAccount.TryGetValue(retained, out var equity) ||
                equity is not { IsActive: true, IsPosting: true, IsContra: false, Classification: AccountClass.Equity })
                Add("retained-earnings", FindingSeverity.Blocking, "Publish an eligible retained earnings mapping before year-end.");
            if (priorPeriods.Any(x => x.State == PeriodState.Open))
                Add("year-end-prior-periods", FindingSeverity.Blocking, "Close all earlier periods before year-end.");
            if (FinancialMath.Balances(entries.Where(x => x.Date < year.StartDate)).Any(x => x.Value != 0 &&
                byAccount.TryGetValue(x.Key, out var account) && FinancialMath.Nominal(account.Classification)))
                Add("year-end-prior-results", FindingSeverity.Blocking, "Prior-year nominal accounts still carry balances.");
            foreach (var account in accounts.Where(x => FinancialMath.Nominal(x.Classification) && signed.GetValueOrDefault(x.Id) != 0 &&
                (!x.IsActive || !x.IsPosting)))
                Add("year-end-ineligible-account", FindingSeverity.Blocking, $"Nominal account {account.Code} has a balance and cannot accept the close entry.", account.Id);
        }
        var result = findings.OrderBy(x => x.Rule, StringComparer.Ordinal).ThenBy(x => x.SourceId).ThenBy(x => x.Detail, StringComparer.Ordinal).ToArray();
        var fingerprint = TradeRules.Hash(new
        {
            PeriodVersion = period.Version, Definition = definition?.Id,
            Accounts = accounts.Select(x => new { x.Id, x.Version }).OrderBy(x => x.Id).ToArray(),
            Posted = entries.Select(x => new { x.JournalId, x.Sequence }).Distinct().OrderBy(x => x.Sequence).ToArray(),
            Drafts = drafts.Select(x => new { x.Id, x.Version }).OrderBy(x => x.Id).ToArray(),
            TradeDrafts = tradingDrafts.OrderBy(x => x.Id).ToArray(),
            TradingMappings = tradingMappings.Select(x => x.Id).Order().ToArray(), Reconciliation = reconciliation,
            Banks = banks.Select(x => new { x.Id, x.Version }).OrderBy(x => x.Id).ToArray(),
            BankEvidence = bankEvidence.OrderBy(x => x.Id).ToArray(), Recognition = recognition.OrderBy(x => x.Id).ToArray(),
            Earlier = priorPeriods.OrderBy(x => x.Id).ToArray(), Assets = assetEvidence.Fingerprint,
            Tasks = session.Tasks.OrderBy(x => x.Id).Select(x => new { x.Id, x.ReviewerId, x.Required, x.Evidence, x.CompletedBy, x.CompletedAt }).ToArray(),
            Exceptions = exceptions.OrderBy(x => x.Id).Select(x => new { x.Id, x.Blocking, x.Resolution, x.ResolvedAt }).ToArray(), Findings = result
        });
        if (inventoryEvidence.Fingerprint.Length != 0)
            fingerprint = TradeRules.Hash(new { Close = fingerprint, Inventory = inventoryEvidence.Fingerprint });
        if (budgetEvidence.Fingerprint.Length != 0)
            fingerprint = TradeRules.Hash(new { Close = fingerprint, Budget = budgetEvidence.Fingerprint });
        if (fxEvidence.Fingerprint.Length != 0)
            fingerprint = TradeRules.Hash(new { Close = fingerprint, FX = fxEvidence.Fingerprint });
        if (dimensional.Fingerprint.Length != 0)
            fingerprint = TradeRules.Hash(new { Close = fingerprint, Dimensions = dimensional.Fingerprint });
        if (pendingPairs.Count > 0)
            fingerprint = TradeRules.Hash(new { Close = fingerprint, Intercompany = pendingPairs });
        if (taxEvidence.Count > 0)
            fingerprint = TradeRules.Hash(new { Close = fingerprint, Tax = taxEvidence });
        if (governance.Fingerprint.Length != 0)
            fingerprint = TradeRules.Hash(new { Close = fingerprint, Governance = governance.Fingerprint });
        return new(session.Id, session.Version, period.State, result, fingerprint);
    }
}
