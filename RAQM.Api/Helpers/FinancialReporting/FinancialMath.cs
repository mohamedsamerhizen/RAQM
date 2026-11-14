using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.Helpers.FinancialReporting;

public sealed record Calculation(IReadOnlyList<ReportRow> Rows, decimal Difference, IReadOnlyList<string> Warnings);

public static class FinancialMath
{
    public const string YearEndSource = "FinancialClose.YearEnd";
    public static decimal Signed(FinancialLine line) => line.Debit - line.Credit;
    public static bool Nominal(AccountClass classification) => classification is AccountClass.Revenue or AccountClass.Expense;
    public static bool OperatingHistory(FinancialLine line) => (line.OriginalSource ?? line.Source) is not (YearEndSource or "OpeningBalance");

    public static AccountClass Class(StatementGroup group) => group switch
    {
        StatementGroup.CurrentAsset or StatementGroup.NoncurrentAsset => AccountClass.Asset,
        StatementGroup.CurrentLiability or StatementGroup.NoncurrentLiability => AccountClass.Liability,
        StatementGroup.Capital or StatementGroup.RetainedEarnings or StatementGroup.Distributions or StatementGroup.OtherEquity => AccountClass.Equity,
        StatementGroup.OperatingRevenue or StatementGroup.OtherIncome => AccountClass.Revenue,
        _ => AccountClass.Expense
    };

    public static Calculation Build(ReportKind kind, ReportWindow window, DateOnly fiscalStart, IReadOnlyList<LedgerAccount> accounts,
        IReadOnlyList<FinancialLine> ledger, IReadOnlyDictionary<Guid, StatementGroup> mapping, IReadOnlySet<Guid> cash,
        IReadOnlyDictionary<string, CashGroup> cashRules, IReadOnlyDictionary<Guid, decimal>? fxCashAtSpot = null)
    {
        var warnings = new List<string>();
        var rows = new List<ReportRow>();
        var accountById = accounts.ToDictionary(x => x.Id);
        var nominalIds = accounts.Where(x => Nominal(x.Classification)).Select(x => x.Id).ToHashSet();
        var relevant = ledger.Where(x => x.Date <= window.To).ToArray();
        var period = relevant.Where(x => x.Date >= window.From).ToArray();
        var closing = Balances(relevant);
        var invalid = mapping.Where(x => !accountById.TryGetValue(x.Key, out var account) || !account.IsPosting || Class(x.Value) != account.Classification)
            .Select(x => x.Key).ToHashSet();
        if (invalid.Count != 0) warnings.Add($"Invalid statement mappings: {string.Join(", ", invalid.Order())}");

        string Label(LedgerAccount account) => mapping.TryGetValue(account.Id, out var group) && !invalid.Contains(account.Id)
            ? group.ToString() : $"Unclassified {account.Classification}";
        void Row(string key, string name, decimal amount, Guid? account = null) => rows.Add(new(key, name, amount, null, null, account));
        void Accounts(string prefix, Func<LedgerAccount, bool> include, IReadOnlyDictionary<Guid, decimal> balances, Func<LedgerAccount, decimal, decimal> sign)
        {
            var eligible = accounts.Where(include).OrderBy(x => x.Code).ToList();
            var leaves = eligible.Where(x => x.IsPosting || balances.ContainsKey(x.Id)).ToList();
            if (leaves.Any(x => balances.GetValueOrDefault(x.Id) != 0 && Label(x).StartsWith("Unclassified", StringComparison.Ordinal)))
                warnings.Add("Unclassified accounts are included in class totals.");
            foreach (var g in leaves.GroupBy(Label).OrderBy(x => x.Key))
            {
                Row($"{prefix}:group:{g.Key}", g.Key, g.Sum(x => sign(x, balances.GetValueOrDefault(x.Id))));
                foreach (var a in g)
                    Row($"{prefix}:account:{a.Id}", $"{a.Code} {a.Name}", sign(a, balances.GetValueOrDefault(a.Id)), a.Id);
            }
            var hierarchy = leaves.Where(x => x.IsPosting).ToDictionary(x => x.Id, x => sign(x, balances.GetValueOrDefault(x.Id)));
            foreach (var leaf in leaves.Where(x => x.IsPosting))
            {
                var parent = leaf.ParentId;
                var seen = new HashSet<Guid> { leaf.Id };
                while (parent is { } id && seen.Add(id) && accountById.TryGetValue(id, out var node))
                {
                    hierarchy[id] = hierarchy.GetValueOrDefault(id) + sign(leaf, balances.GetValueOrDefault(leaf.Id));
                    parent = node.ParentId;
                }
            }
            foreach (var root in eligible.Where(x => !x.IsPosting && hierarchy.ContainsKey(x.Id)))
                Row($"{prefix}:rollup:{root.Id}", $"{root.Code} {root.Name} (hierarchy subtotal)", hierarchy[root.Id], root.Id);
        }

        decimal ClassTotal(AccountClass type, IReadOnlyDictionary<Guid, decimal> balances) =>
            accounts.Where(x => x.Classification == type).Sum(x => balances.GetValueOrDefault(x.Id));
        decimal NominalProfit(IEnumerable<FinancialLine> lines) => -lines.Where(x => nominalIds.Contains(x.AccountId)).Sum(Signed);
        decimal IncomeRows(string prefix, FinancialLine[] source)
        {
            var amounts = Balances(source.Where(OperatingHistory));
            var revenue = -ClassTotal(AccountClass.Revenue, amounts);
            var expense = ClassTotal(AccountClass.Expense, amounts);
            Accounts(prefix, x => Nominal(x.Classification), amounts, (a, n) => a.Classification == AccountClass.Revenue ? -n : n);
            Row($"{prefix}:revenue", "Revenue", revenue);
            Row($"{prefix}:expense", "Expense", expense);
            var material = accounts.Where(x => Nominal(x.Classification) && amounts.GetValueOrDefault(x.Id) != 0).ToArray();
            if (material.All(x => mapping.ContainsKey(x.Id) && !invalid.Contains(x.Id)))
            {
                decimal Group(StatementGroup g) => material.Where(x => mapping[x.Id] == g).Sum(x => amounts.GetValueOrDefault(x.Id));
                var operatingRevenue = -Group(StatementGroup.OperatingRevenue);
                var cost = Group(StatementGroup.CostOfSales);
                if (mapping.Values.Contains(StatementGroup.OperatingRevenue) && mapping.Values.Contains(StatementGroup.CostOfSales))
                    Row($"{prefix}:gross", "Gross profit", operatingRevenue - cost);
                if (mapping.Values.Contains(StatementGroup.OperatingRevenue))
                    Row($"{prefix}:operating", "Operating income", operatingRevenue - cost - Group(StatementGroup.OperatingExpense));
            }
            Row($"{prefix}:net", "Net profit or loss", revenue - expense);
            return revenue - expense;
        }

        decimal difference;
        switch (kind)
        {
            case ReportKind.BalanceSheet:
                var assets = ClassTotal(AccountClass.Asset, closing);
                var liabilities = -ClassTotal(AccountClass.Liability, closing);
                var retained = -accounts.Where(x => mapping.GetValueOrDefault(x.Id) == StatementGroup.RetainedEarnings && !invalid.Contains(x.Id))
                    .Sum(x => closing.GetValueOrDefault(x.Id));
                var current = NominalProfit(relevant.Where(x => x.Date >= fiscalStart));
                var prior = NominalProfit(relevant.Where(x => x.Date < fiscalStart));
                var equity = -ClassTotal(AccountClass.Equity, closing) + current + prior;
                Accounts("balance", x => !Nominal(x.Classification), closing,
                    (a, n) => a.Classification == AccountClass.Asset ? n : -n);
                Row("assets", "Assets", assets);
                Row("liabilities", "Liabilities", liabilities);
                Row("retained", "Posted retained earnings", retained);
                Row("current-profit", "Unclosed current fiscal year profit or loss", current);
                Row("prior-profit", "Unclosed prior fiscal year profit or loss", prior);
                Row("equity", "Equity including unclosed results", equity);
                difference = assets - liabilities - equity;
                break;
            case ReportKind.Income:
                var profit = IncomeRows("period", period);
                IncomeRows("ytd", relevant.Where(x => x.Date >= fiscalStart).ToArray());
                difference = profit - NominalProfit(period.Where(OperatingHistory));
                break;
            case ReportKind.Equity:
                var opening = Balances(relevant.Where(x => x.Date < window.From));
                var initial = -ClassTotal(AccountClass.Equity, opening) - nominalIds.Sum(x => opening.GetValueOrDefault(x));
                var end = -ClassTotal(AccountClass.Equity, closing) - nominalIds.Sum(x => closing.GetValueOrDefault(x));
                var permanent = Balances(period.Where(x => x.Source != YearEndSource));
                var net = NominalProfit(period.Where(OperatingHistory));
                var nominalConversion = NominalProfit(period.Where(x => (x.OriginalSource ?? x.Source) == "OpeningBalance"));
                var transfer = -period.Where(x => x.Source == YearEndSource && !nominalIds.Contains(x.AccountId)).Sum(Signed);
                Row("equity:opening", "Opening equity including unclosed results", initial);
                Accounts("equity:movement", x => x.Classification == AccountClass.Equity, permanent, (_, n) => -n);
                Row("equity:profit", "Period profit or loss", net);
                Row("equity:conversion", "Opening conversion in nominal accounts", nominalConversion);
                Row("equity:transfer", "Year-end transfer within equity (memo)", transfer);
                Row("equity:closing", "Closing equity including unclosed results", end);
                difference = end - (initial - ClassTotal(AccountClass.Equity, permanent) + net + nominalConversion);
                break;
            case ReportKind.CashMovement:
                var cashLines = relevant.Where(x => cash.Contains(x.AccountId)).ToArray();
                var cashOpen = cashLines.Where(x => x.Date < window.From).Sum(Signed);
                var cashClose = cashLines.Sum(Signed);
                var movements = cashLines.Where(x => x.Date >= window.From).GroupBy(x => x.JournalId).Select(g =>
                {
                    var line = g.First();
                    var carryingChange = g.Sum(Signed);
                    var fx = fxCashAtSpot is not null && fxCashAtSpot.ContainsKey(g.Key);
                    var cashAmount = fx ? fxCashAtSpot![g.Key] : carryingChange;
                    return (Source: line.OriginalSource ?? line.Source, Amount: cashAmount, ExchangeEffect: carryingChange - cashAmount, Fx: fx);
                }).ToArray();
                var transferNet = movements.Where(x => x.Source == "Treasury.Transfer").Sum(x => x.Amount);
                var conversion = movements.Where(x => x.Source == "OpeningBalance").Sum(x => x.Amount);
                var external = movements.Where(x => x.Source is not ("Treasury.Transfer" or "OpeningBalance") && (!x.Fx || x.Amount != 0)).ToArray();
                var exchangeEffect = movements.Sum(x => x.ExchangeEffect);
                Row("cash:opening", "Cash and bank opening", cashOpen);
                foreach (var group in external.GroupBy(x => cashRules.TryGetValue(x.Source, out var g) ? g.ToString() : "Unclassified").OrderBy(x => x.Key))
                    Row($"cash:group:{group.Key}", group.Key, group.Sum(x => x.Amount));
                foreach (var group in external.GroupBy(x => x.Source).OrderBy(x => x.Key))
                    Row($"cash:source:{group.Key}", group.Key, group.Sum(x => x.Amount));
                Row("cash:in", "External cash inflows", external.Where(x => x.Amount > 0).Sum(x => x.Amount));
                Row("cash:out", "External cash outflows", -external.Where(x => x.Amount < 0).Sum(x => x.Amount));
                Row("cash:net", "External net cash movement", external.Sum(x => x.Amount));
                Row("cash:conversion", "Opening conversion movement", conversion);
                Row("cash:transfer", "Internal transfer net (expected zero)", transferNet);
                if (movements.Any(x => x.Fx)) Row("cash:fx-effect", "Exchange effect on cash and bank carrying values", exchangeEffect);
                Row("cash:closing", "Cash and bank closing", cashClose);
                Accounts("cash:balance", x => cash.Contains(x.Id), closing, (_, n) => n);
                difference = cashClose - cashOpen - conversion - external.Sum(x => x.Amount) - exchangeEffect;
                if (movements.Any(x => x.Source == "Treasury.Transfer" && x.Amount != 0))
                    warnings.Add("An internal transfer has a nonzero cash effect within the registered scope.");
                if (cash.Count == 0) warnings.Add("No treasury accounts are registered.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (difference != 0) warnings.Add("Financial integrity difference requires investigation.");
        return new(rows, difference, warnings.Distinct().ToList());
    }

    public static IReadOnlyDictionary<Guid, decimal> Balances(IEnumerable<FinancialLine> lines) =>
        lines.GroupBy(x => x.AccountId).ToDictionary(x => x.Key, x => x.Sum(Signed));

    public static int ClosingLineCount(IReadOnlyDictionary<Guid, decimal> signedNominal) =>
        signedNominal.Count(x => x.Value != 0) + (signedNominal.Values.Sum() == 0 ? 0 : 1);

    public static IReadOnlyList<JournalDraftLine> ClosingLines(IReadOnlyDictionary<Guid, decimal> signedNominal, Guid retained)
    {
        var result = signedNominal.Where(x => x.Value != 0).OrderBy(x => x.Key)
            .Select(x => new JournalDraftLine(x.Key, Math.Max(-x.Value, 0), Math.Max(x.Value, 0), "Close nominal account")).ToList();
        var net = signedNominal.Values.Sum();
        if (net != 0) result.Add(new(retained, Math.Max(net, 0), Math.Max(-net, 0), "Transfer result to retained earnings"));
        return result;
    }
}
