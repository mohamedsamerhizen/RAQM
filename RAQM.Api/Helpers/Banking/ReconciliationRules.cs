using RAQM.Api.DTOs.Banking;
using RAQM.Api.DTOs.Treasury;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Helpers;

namespace RAQM.Api.Helpers.Banking;

public static class ReconciliationRules
{
    public static void Match(decimal statement, IReadOnlyList<decimal> amounts)
    {
        if (amounts.Count is < 1 or > 50 || amounts.Any(x => x == 0 || Math.Sign(x) != Math.Sign(statement)) || amounts.Sum() != statement)
            throw new ApiException(400, "Select one to fifty whole book movements of the same direction that exactly equal the bank movement.", "match_conservation");
    }

    public static (decimal Deposits, decimal Withdrawals, decimal Difference) Totals(decimal bank, decimal book, IEnumerable<decimal> outstanding)
    {
        decimal deposits = 0, withdrawals = 0;
        foreach (var amount in outstanding)
        {
            if (amount > 0) deposits += amount;
            else withdrawals -= amount;
        }
        return (deposits, withdrawals, bank + deposits - withdrawals - book);
    }

    public static IReadOnlyList<MatchSuggestion> Suggest(IReadOnlyList<BankStatementLine> unmatched, IReadOnlyList<BookMovement> book)
    {
        static string Normalize(string? value) => (value ?? "").Trim().ToUpperInvariant();
        var keyed = book.Where(x => !string.IsNullOrWhiteSpace(x.Reference)).ToLookup(x => (Normalize(x.Reference), x.Amount));
        var suggestions = new List<MatchSuggestion>();
        foreach (var line in unmatched.Where(x => !string.IsNullOrWhiteSpace(x.Reference)))
        {
            var options = keyed[(Normalize(line.Reference), line.Amount)]
                .Where(x => Math.Abs(x.Date.DayNumber - line.BookingDate.DayNumber) <= 3).Take(2).ToList();
            if (options.Count == 1)
                suggestions.Add(new(line.Id, options[0].LineId, "High", "Exact signed amount and reference within three booking days."));
        }
        var unique = suggestions.GroupBy(x => x.JournalLineId).Where(x => x.Count() == 1).Select(x => x.Single()).ToList();
        return unique;
    }

    public static IReadOnlyList<OutstandingAge> Age(DateOnly cutoff, IReadOnlyList<BankStatementLine> statement, IReadOnlyList<BookMovement> book)
    {
        var aged = statement.Select(x => (Days: cutoff.DayNumber - x.BookingDate.DayNumber, Statement: true, x.Amount))
            .Concat(book.Select(x => (Days: cutoff.DayNumber - x.Date.DayNumber, Statement: false, x.Amount)));
        return aged.GroupBy(x => x.Days <= 7 ? 0 : x.Days <= 30 ? 1 : x.Days <= 60 ? 2 : 3).OrderBy(x => x.Key)
            .Select(g => new OutstandingAge(g.Key switch { 0 => "0–7", 1 => "8–30", 2 => "31–60", _ => "61+" },
                g.Count(x => x.Statement), g.Count(x => !x.Statement), g.Where(x => !x.Statement && x.Amount > 0).Sum(x => x.Amount),
                -g.Where(x => !x.Statement && x.Amount < 0).Sum(x => x.Amount))).ToArray();
    }
}
