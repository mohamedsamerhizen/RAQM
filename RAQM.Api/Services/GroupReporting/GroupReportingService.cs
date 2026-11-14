using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Consolidation;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Consolidation;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Services.Consolidation;
using RAQM.Api.Services.CorporateGroups;
using static RAQM.Api.Services.CorporateGroups.CorporateGroupService;

namespace RAQM.Api.Services.GroupReporting;

public sealed record GroupBalance(Guid Id, string Code, string Name, AccountClass Classification, Guid? ParentId, decimal Translated,
    decimal Eliminations, decimal Worksheet, decimal Ownership, decimal Translation, bool Rollup = false)
{
    public decimal Final => Translated + Eliminations + Worksheet + Ownership + Translation;
}

public sealed class GroupReportingService(RaqmDbContext db, CorporateGroupService groups, ConsolidationService consolidation)
{
    public async Task<object> History(Guid actor, Guid org, Guid group, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        var owner = await groups.ReportAccess(actor, org, group, ct);
        return await db.Set<ConsolidationRun>().AsNoTracking().Where(x => x.GroupId == group && (owner || x.State == GroupRunState.Published))
            .OrderByDescending(x => x.Cutoff).ThenByDescending(x => x.Version).Skip((page - 1) * size).Take(size)
            .Select(x => new { x.Id, x.From, x.Cutoff, x.Version, x.State, x.SupersedesId, x.PublishedAt }).ToListAsync(ct);
    }

    public async Task<object> Report(Guid actor, Guid org, Guid group, Guid id, Guid? compare, CancellationToken ct)
    {
        var (run, source) = await Read(actor, org, group, id, ct);
        var balances = Balances(source);
        var direct = balances.Where(x => !x.Rollup).ToArray();
        var opening = source.Contributions.Where(x => x.Date < run.From &&
            source.Accounts.Any(a => a.Id == x.GroupAccountId && a.Classification == AccountClass.Equity)).Sum(x => -x.Translated);
        var income = -direct.Where(x => x.Classification is AccountClass.Revenue or AccountClass.Expense).Sum(x => x.Final);
        var assets = direct.Where(x => x.Classification == AccountClass.Asset).Sum(x => x.Final);
        var liabilities = -direct.Where(x => x.Classification == AccountClass.Liability).Sum(x => x.Final);
        var equity = -direct.Where(x => x.Classification == AccountClass.Equity).Sum(x => x.Final);
        var cash = source.Accounts.Where(x => x.Cash).Select(x => x.Id).ToHashSet();
        var previous = compare is { } previousId ? await Read(actor, org, group, previousId, ct) : ((ConsolidationRun, ConsolidationSnapshot)?)null;
        return new
        {
            run.Id, run.Version, run.From, run.Cutoff, run.PublishedAt, run.SupersedesId, source.Currency, source.Companies, source.Ownership,
            source.AccountingPolicyEvidence, source.ReciprocalReviews,
            TrialBalance = balances, BalanceSheet = new { Assets = assets, Liabilities = liabilities, Equity = equity,
                CurrentResult = income, Difference = assets - liabilities - equity - income },
            IncomeStatement = new { Revenue = -direct.Where(x => x.Classification == AccountClass.Revenue).Sum(x => x.Final),
                Expense = direct.Where(x => x.Classification == AccountClass.Expense).Sum(x => x.Final), Net = income },
            Equity = new { TranslatedOpeningBeforeAdjustments = opening,
                TranslatedMovementsBeforeAdjustments = -source.Contributions.Where(x => x.Date >= run.From &&
                    source.Accounts.Any(a => a.Id == x.GroupAccountId && a.Classification == AccountClass.Equity)).Sum(x => x.Translated),
                Adjustments = -source.Adjustments.Where(x => source.Accounts.Any(a => a.Id == x.GroupAccountId && a.Classification == AccountClass.Equity)).Sum(x => x.Signed),
                ClosingBeforeCurrentResult = equity },
            Cash = new { TranslatedExternalLedgerMovement = source.Contributions.Where(x => !x.InternalPair && x.Date >= run.From &&
                (x.OriginalSource ?? x.Source) != "OpeningBalance" && cash.Contains(x.GroupAccountId)).Sum(x => x.Translated),
                Closing = direct.Where(x => cash.Contains(x.Id)).Sum(x => x.Final),
                Basis = "External cash ledger movement translated using the balance-sheet rate. Statutory cash-flow classification is outside this view." },
            Comparison = previous is { } p ? new { p.Item1.Id, p.Item1.Version, p.Item1.From, p.Item1.Cutoff, Balances = Balances(p.Item2) } : null
        };
    }

    public async Task<object> Drill(Guid actor, Guid org, Guid group, Guid id, Guid account, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        var (_, source) = await Read(actor, org, group, id, ct);
        if (!source.Accounts.Any(x => x.Id == account)) throw Missing();
        var children = AccountingRules.Descendants(account, source.Accounts.ToDictionary(x => x.Id, x => x.ParentId));
        var lines = source.Contributions.Where(x => children.Contains(x.GroupAccountId)).ToArray();
        return new { Total = lines.Length, Lines = lines.Skip((page - 1) * size).Take(size).ToArray(),
            Adjustments = source.Adjustments.Where(x => children.Contains(x.GroupAccountId)).ToArray() };
    }

    public async Task<string> Export(Guid actor, Guid org, Guid group, Guid id, CancellationToken ct)
    {
        var (_, source) = await Read(actor, org, group, id, ct);
        var csv = new StringBuilder("code,classification,rollup,translated,eliminations,worksheet,ownership,translation,final\r\n");
        foreach (var row in Balances(source))
        {
            var code = row.Code.TrimStart();
            if (code.Length > 0 && "=+-@".Contains(code[0])) code = "'" + code;
            csv.Append(CultureInfo.InvariantCulture, $"\"{code.Replace("\"", "\"\"")}\",{row.Classification},{row.Rollup},{row.Translated},{row.Eliminations},{row.Worksheet},{row.Ownership},{row.Translation},{row.Final}\r\n");
        }
        return csv.ToString();
    }

    private async Task<(ConsolidationRun, ConsolidationSnapshot)> Read(Guid actor, Guid org, Guid group, Guid id, CancellationToken ct)
    {
        await groups.ReportAccess(actor, org, group, ct);
        var row = await consolidation.Find(group, id, ct);
        if (row.State != GroupRunState.Published) throw Missing();
        return (row, JsonSerializer.Deserialize<ConsolidationSnapshot>(row.SnapshotJson)!);
    }

    public static GroupBalance[] Balances(ConsolidationSnapshot snapshot)
    {
        var parents = snapshot.Accounts.ToDictionary(x => x.Id, x => x.ParentId);
        var contributions = snapshot.Contributions.ToLookup(x => x.GroupAccountId);
        var adjustments = snapshot.Adjustments.ToLookup(x => (x.GroupAccountId, x.Layer));
        var direct = new List<GroupBalance>();
        foreach (var account in snapshot.Accounts.OrderBy(x => x.Code))
            direct.Add(new(account.Id, account.Code, account.Name, account.Classification, account.ParentId,
                contributions[account.Id].Sum(x => x.Translated),
                Sum(account.Id, "Elimination"), Sum(account.Id, "Worksheet"), Sum(account.Id, "Ownership"), Sum(account.Id, "Translation")));
        var totals = new List<GroupBalance>(direct);
        foreach (var account in direct.Where(x => parents.ContainsValue(x.Id)))
        {
            var children = AccountingRules.Descendants(account.Id, parents);
            var rows = direct.Where(x => children.Contains(x.Id)).ToArray();
            totals.Add(account with { Translated = rows.Sum(x => x.Translated), Eliminations = rows.Sum(x => x.Eliminations),
                Worksheet = rows.Sum(x => x.Worksheet), Ownership = rows.Sum(x => x.Ownership), Translation = rows.Sum(x => x.Translation), Rollup = true });
        }
        return totals.ToArray();

        decimal Sum(Guid account, string layer) => adjustments[(account, layer)].Sum(x => x.Signed);
    }
}
