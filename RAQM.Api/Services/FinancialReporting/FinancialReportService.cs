using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.FinancialReporting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.MultiCurrency;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Services.SaaS;

namespace RAQM.Api.Services.FinancialReporting;

public sealed class FinancialReportService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    FinancialLedger ledger, TimeProvider clock) : IFinancialReportService
{
    private const int MaxReportLines = 50_000;

    public async Task<IReadOnlyList<ReportDefinition>> Definitions(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.ReportDefinitions.AsNoTracking().Include(x => x.Mappings).Where(x => x.CompanyId == company)
            .OrderByDescending(x => x.Revision).ToListAsync(ct);
    }

    public Task<ReportDefinition> Publish(Guid actor, Guid org, Guid company, DefinitionRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var prior = await db.ReportDefinitions.Where(x => x.CompanyId == company).MaxAsync(x => (int?)x.Revision, token) ?? 0;
            if (prior != request.ExpectedRevision) throw new ApiException(409, "The report definition revision changed.", "report_version");
            var accounts = (await ledger.Accounts(company, token)).ToDictionary(x => x.Id);
            if (request.Accounts is null || request.CashSources is null || request.Accounts.Count > 2000 || request.CashSources.Count > 200 ||
                request.Accounts.Any(x => x is null) || request.CashSources.Any(x => x is null) ||
                request.Accounts.Select(x => x.AccountId).Distinct().Count() != request.Accounts.Count)
                throw new ApiException(400, "Supply unique account mappings and bounded cash source rules.", "report_mapping");
            foreach (var mapping in request.Accounts)
                if (!Enum.IsDefined(mapping.Group) || !accounts.TryGetValue(mapping.AccountId, out var account) ||
                    !account.IsPosting || account.Classification != FinancialMath.Class(mapping.Group))
                    throw new ApiException(400, "Each mapping must reference a posting account of the matching company class.", "report_mapping");
            if (request.RetainedEarningsId is { } retained &&
                (!accounts.TryGetValue(retained, out var equity) || equity is not { IsActive: true, IsPosting: true, IsContra: false, Classification: AccountClass.Equity } ||
                    !request.Accounts.Any(x => x.AccountId == retained && x.Group == StatementGroup.RetainedEarnings)))
                throw new ApiException(400, "Retained earnings must be an active, non-contra posting equity account mapped to retained earnings.", "retained_earnings");
            var cashRules = request.CashSources.Select(x => new CashSourceRule(AccountingRules.Text(x.Source, 50, "Cash source"), x.Group)).ToArray();
            if (cashRules.Any(x => !Enum.IsDefined(x.Group) || x.Source is FinancialMath.YearEndSource or "OpeningBalance" or "Treasury.Transfer") ||
                cashRules.Select(x => x.Source).Distinct(StringComparer.Ordinal).Count() != cashRules.Length)
                throw new ApiException(400, "Use unique external source codes with valid cash classifications.", "cash_mapping");
            var definition = new ReportDefinition { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId,
                Revision = checked(prior + 1), MappingCount = request.Accounts.Count, RetainedEarningsId = request.RetainedEarningsId, CashRulesJson = JsonSerializer.Serialize(cashRules),
                CreatedBy = actor, Reason = AccountingRules.Text(request.Reason, 500, "Publication reason") };
            definition.Mappings = request.Accounts.Select(x => new ReportAccountMapping { CompanyId = company, OrganizationId = org,
                TenantId = scope.TenantId, DefinitionId = definition.Id, AccountId = x.AccountId, Group = x.Group }).ToList();
            db.ReportDefinitions.Add(definition);
            access.Audit(scope, actor, definition.Id, "FinancialDefinitionPublished", definition.Reason);
            return definition;
        }, ct);

    public async Task<FinancialReport> Report(Guid actor, Guid org, Guid company, ReportQuery query, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await BuildReport(actor, org, company, query, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ApiException(503, "The financial report exceeded its execution time limit.", "report_timeout");
        }
    }

    private async Task<FinancialReport> BuildReport(Guid actor, Guid org, Guid company, ReportQuery query, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var window = await Window(company, query, ct);
        var fiscal = await FiscalStart(company, window.To, ct);
        ReportWindow? comparison = null;
        if (query.CompareFrom.HasValue || query.CompareTo.HasValue)
        {
            if (!query.CompareTo.HasValue) throw BadDates();
            var from = query.CompareFrom ?? await FiscalStart(company, query.CompareTo.Value, ct);
            comparison = ValidateWindow(from, query.CompareTo.Value);
        }
        var cutoff = comparison is not null && comparison.To > window.To ? comparison.To : window.To;
        ValidateSequence(query);
        var entries = await ledger.Lines(company, cutoff, asOfSequence: query.AsOfSequence).Take(MaxReportLines + 1).ToListAsync(ct);
        if (entries.Count > MaxReportLines)
            throw new ApiException(422, "The report exceeds its 50,000 posted-line limit. Use an earlier cutoff or ledger drilldown.", "report_capacity");
        var accounts = await ledger.Accounts(company, ct);
        var definition = await Definition(company, query.Revision, ct);
        var mappings = definition?.Mappings.ToDictionary(x => x.AccountId, x => x.Group) ?? [];
        var inventoryCogs = await db.InventoryAccounts.AsNoTracking().Where(x => x.CompanyId == company && x.Role == InventoryRole.CostOfSales)
            .Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(ct);
        if (inventoryCogs.HasValue) mappings.TryAdd(inventoryCogs.Value, StatementGroup.CostOfSales);
        var fxMappings = await db.FxAccounts.AsNoTracking().Where(x => x.CompanyId == company).ToListAsync(ct);
        foreach (var mapping in fxMappings)
        {
            var group = mapping.Role switch
            {
                Entities.MultiCurrency.FxAccountRole.RealizedGain or Entities.MultiCurrency.FxAccountRole.UnrealizedGain => StatementGroup.OtherIncome,
                Entities.MultiCurrency.FxAccountRole.RealizedLoss or Entities.MultiCurrency.FxAccountRole.UnrealizedLoss => StatementGroup.OtherExpense,
                Entities.MultiCurrency.FxAccountRole.Receivable or Entities.MultiCurrency.FxAccountRole.SupplierAdvance or
                    Entities.MultiCurrency.FxAccountRole.TaxInput => StatementGroup.CurrentAsset,
                _ => StatementGroup.CurrentLiability
            };
            mappings.TryAdd(mapping.AccountId, group);
        }
        var fxBanks = await db.FxPositions.AsNoTracking().Where(x => x.CompanyId == company &&
            x.Kind == Entities.MultiCurrency.FxPositionKind.Bank).Select(x => x.AccountId).ToListAsync(ct);
        foreach (var account in fxBanks) mappings.TryAdd(account, StatementGroup.CurrentAsset);
        var cash = (await db.TreasuryAccounts.AsNoTracking().Where(x => x.CompanyId == company).Select(x => x.LedgerAccountId).ToListAsync(ct)).ToHashSet();
        cash.UnionWith(await db.TradingAccounts.AsNoTracking().Where(x => x.CompanyId == company && x.Role == Entities.Trading.AccountRole.Cash)
            .Select(x => x.AccountId).ToListAsync(ct));
        Dictionary<Guid, decimal>? fxCashAtSpot = null;
        if (query.Kind == ReportKind.CashMovement)
        {
            cash.UnionWith(fxBanks);
            fxCashAtSpot = await FxCashFlows(company, cutoff, cash, query.AsOfSequence, ct);
        }
        var cashRules = (JsonSerializer.Deserialize<CashSourceRule[]>(definition?.CashRulesJson ?? "[]") ?? [])
            .ToDictionary(x => x.Source, x => x.Group, StringComparer.Ordinal);
        var actual = FinancialMath.Build(query.Kind, window, fiscal, accounts, entries, mappings, cash, cashRules, fxCashAtSpot);
        Calculation? comparative = null;
        if (comparison is not null)
            comparative = FinancialMath.Build(query.Kind, comparison, await FiscalStart(company, comparison.To, ct), accounts, entries, mappings, cash, cashRules, fxCashAtSpot);
        var rows = actual.Rows.ToList();
        if (comparative is not null)
        {
            var old = comparative.Rows.ToDictionary(x => x.Key);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (!old.TryGetValue(row.Key, out var prior)) continue;
                rows[i] = row with { Comparative = prior.Amount, ChangePercent = prior.Amount == 0 ? null :
                    decimal.Round((row.Amount - prior.Amount) / Math.Abs(prior.Amount) * 100, 2, MidpointRounding.ToEven) };
            }
            var currentKeys = rows.Select(x => x.Key).ToHashSet();
            rows.AddRange(comparative.Rows.Where(x => !currentKeys.Contains(x.Key)).Select(x => x with { Amount = 0, Comparative = x.Amount }));
        }
        var warnings = actual.Warnings.Concat(comparative?.Warnings ?? []).Distinct().ToList();
        if (query.Kind == ReportKind.Income && window.From < fiscal)
            warnings.Add("The selected period spans fiscal years. YTD starts in the fiscal year of the ending date.");
        var report = new FinancialReport(query.Kind, company, scope.Name, scope.BaseCurrency, definition?.Revision ?? 0, actor,
            clock.GetUtcNow(), entries.Select(x => x.Sequence).DefaultIfEmpty(0).Max(), window, comparison, rows, warnings, actual.Difference, comparative?.Difference);
        await transaction.CommitAsync(ct);
        return report;
    }

    public async Task<PageResult<FinancialLine>> Drill(Guid actor, Guid org, Guid company, ReportQuery query, Guid account,
        bool ytd, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var window = await Window(company, query, ct);
        var accounts = await ledger.Accounts(company, ct);
        if (accounts.All(x => x.Id != account)) throw new ApiException(404, "Account not found.", "account_not_found");
        var descendants = AccountingRules.Descendants(account, accounts.ToDictionary(x => x.Id, x => x.ParentId)).ToArray();
        DateOnly? earliest = query.Kind switch
        {
            ReportKind.BalanceSheet => null,
            ReportKind.Income when ytd => await FiscalStart(company, window.To, ct),
            _ => window.From
        };
        ValidateSequence(query);
        var lines = ledger.Lines(company, window.To, earliest, descendants, query.Kind == ReportKind.Income, query.AsOfSequence);
        var count = await lines.CountAsync(ct);
        var result = await lines.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return new(result, count, page, size);
    }

    public async Task<FinancialExport> Export(Guid actor, Guid org, Guid company, ReportQuery query, string format, CancellationToken ct)
    {
        if (format is not ("pdf" or "csv" or "xlsx")) throw new ApiException(400, "Export format must be pdf, csv or xlsx.", "report_format");
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        if (!await TenantBoundary.CanOperate(db, scope.TenantId, ct))
            throw new ApiException(403, "New financial report files are unavailable while tenant operations are restricted.", "report_export_restricted");
        var report = await Report(actor, org, company, query, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var (bytes, type) = format switch
        {
            "pdf" => (FinancialExportWriter.Pdf(report), "application/pdf"),
            "xlsx" => (FinancialExportWriter.Xlsx(report), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            _ => (FinancialExportWriter.Csv(report), "text/csv; charset=utf-8")
        };
        access.Audit(scope, actor, company, "FinancialReportExported", FormattableString.Invariant(
            $"{query.Kind} {format} {report.Period.From:yyyy-MM-dd} to {report.Period.To:yyyy-MM-dd}, revision {report.DefinitionRevision}, sequence {report.PostedSequenceCutoff}"));
        await db.SaveChangesAsync(ct);
        return new(bytes, type, $"RAQM-{query.Kind}.{format}");
    }

    private async Task<Dictionary<Guid, decimal>> FxCashFlows(Guid company, DateOnly cutoff, HashSet<Guid> cash, long? asOfSequence, CancellationToken ct)
    {
        var operations = await (from operation in db.FxOperations.AsNoTracking()
            join journal in db.Journals.AsNoTracking() on operation.JournalId equals journal.Id
            where operation.CompanyId == company && journal.CompanyId == company && operation.State == FxState.Posted &&
                operation.Date <= cutoff && (!asOfSequence.HasValue || journal.Sequence <= asOfSequence.Value)
            select operation).ToListAsync(ct);
        var split = new Dictionary<Guid, decimal>();
        foreach (var operation in operations)
        {
            var input = FxQueries.Input(operation);
            if (input.FunctionalCashAccountId is { } functional) cash.Add(functional);
            if (operation.Kind is FxKind.Revaluation or FxKind.RevaluationCorrection)
                split.Add(operation.JournalId!.Value, 0);
            else if (operation.Kind is FxKind.BankPurchase or FxKind.BankSale)
                split.Add(operation.JournalId!.Value, -input.BaseFee);
            else if (operation.Kind == FxKind.Settlement && input.BankPositionId.HasValue)
            {
                var plan = FxQueries.Plan(operation);
                var bank = plan.Movements.Single(x => x.PositionId == input.BankPositionId);
                split.Add(operation.JournalId!.Value, Math.Sign(bank.ForeignDelta) * plan.BaseAmount);
            }
        }
        return split;
    }

    private static void ValidateSequence(ReportQuery query)
    {
        if (query.AsOfSequence is < 0) throw new ApiException(400, "Posted sequence cutoff cannot be negative.", "report_sequence");
    }

    internal async Task<ReportDefinition?> Definition(Guid company, int? revision, CancellationToken ct)
    {
        if (revision == 0) return null;
        var query = db.ReportDefinitions.AsNoTracking().Include(x => x.Mappings).Where(x => x.CompanyId == company);
        if (!revision.HasValue) return await query.OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
        return await query.SingleOrDefaultAsync(x => x.Revision == revision, ct)
            ?? throw new ApiException(404, "Report revision not found.", "report_version");
    }

    private async Task<ReportWindow> Window(Guid company, ReportQuery query, CancellationToken ct)
    {
        if (!Enum.IsDefined(query.Kind) || (query.PeriodId.HasValue && query.FiscalYearId.HasValue) ||
            ((query.PeriodId.HasValue || query.FiscalYearId.HasValue) && (query.From.HasValue || query.To.HasValue)) ||
            (query.FiscalQuarter.HasValue && (!query.FiscalYearId.HasValue || query.FiscalQuarter is < 1 or > 4))) throw BadDates();
        if (query.PeriodId is { } period)
        {
            var p = await db.Periods.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == period, ct)
                ?? throw new ApiException(404, "Period not found.", "period_not_found");
            return new(p.StartDate, p.EndDate);
        }
        if (query.FiscalYearId is { } id)
        {
            var y = await db.FiscalYears.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
                ?? throw new ApiException(404, "Fiscal year not found.", "fiscal_year");
            if (query.FiscalQuarter is { } q)
            {
                var first = (q - 1) * 3 + 1;
                var periods = await db.Periods.Where(x => x.CompanyId == company && x.FiscalYearId == id && x.Number >= first && x.Number < first + 3)
                    .OrderBy(x => x.Number).ToListAsync(ct);
                if (periods.Count != 3) throw BadDates();
                return new(periods[0].StartDate, periods[^1].EndDate);
            }
            return new(y.StartDate, y.EndDate);
        }
        var to = query.To ?? throw BadDates();
        return ValidateWindow(query.From ?? await FiscalStart(company, to, ct), to);
    }

    private async Task<DateOnly> FiscalStart(Guid company, DateOnly to, CancellationToken ct) =>
        await db.FiscalYears.Where(x => x.CompanyId == company && x.StartDate <= to && x.EndDate >= to)
            .Select(x => (DateOnly?)x.StartDate).SingleOrDefaultAsync(ct)
            ?? throw new ApiException(400, "The reporting cutoff must be in a configured fiscal year.", "report_fiscal_year");

    private static ReportWindow ValidateWindow(DateOnly from, DateOnly to) => from.Year >= 1900 && to >= from ? new(from, to) : throw BadDates();
    private static ApiException BadDates() => new(400, "Specify a valid cutoff or range, fiscal year and optional quarter, or accounting period.", "report_dates");
}
