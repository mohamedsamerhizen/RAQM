using RAQM.Api.DTOs.Forecasting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Helpers.Accounting;
using static RAQM.Api.Helpers.Budgeting.BudgetMath;

namespace RAQM.Api.Helpers.Forecasting;

public static class ForecastMath
{
    public static decimal Adjust(decimal amount, decimal percent, int units)
    {
        if (percent is < -100 or > 1000 || Round(percent, 4) != percent) throw Bad("Forecast change must be between -100 and 1,000 percent.");
        var result = Round(amount * (1 + percent / 100), units);
        CurrencyPolicy.Validate(Math.Abs(result), units);
        return result;
    }

    public static DateOnly ExpectedDate(DateOnly due, DateOnly asOf, int delay)
    {
        if (delay is < 0 or > 365) throw Bad("Forecast timing shift must be 0 to 365 days.");
        var start = due > asOf ? due : asOf.AddDays(1);
        return DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, start.DayNumber + delay));
    }

    public static CashBucket[] Project(decimal opening, IEnumerable<(DateOnly Start, DateOnly End)> periods, IReadOnlyList<CashEvent> events)
    {
        var result = new List<CashBucket>();
        foreach (var (start, end) in periods)
        {
            var rows = events.Where(x => x.Date >= start && x.Date <= end).ToArray();
            var inflows = rows.Where(x => x.Amount > 0).Sum(x => x.Amount);
            var outflows = -rows.Where(x => x.Amount < 0).Sum(x => x.Amount);
            var running = opening;
            var low = opening;
            foreach (var day in rows.GroupBy(x => x.Date).OrderBy(x => x.Key))
            {
                running += day.Sum(x => x.Amount);
                low = Math.Min(low, running);
            }
            result.Add(new(start, end, opening, inflows, outflows, running, low, low < 0));
            opening = running;
        }
        return result.ToArray();
    }

    public static ForecastSnapshot Scenario(ForecastSnapshot basis, ScenarioRequest input, int units)
    {
        var rows = basis.Rows.Select(row => row with { Amount = Adjust(row.Amount,
            row.Kind == BudgetKind.Revenue ? input.RevenueChange : input.ExpenseChange, units) }).ToArray();
        var cash = basis.Cash.Select(row => row.Kind switch
        {
            "AR" => row with { Date = ExpectedDate(row.Date, basis.AsOf, input.CustomerDelayDays) },
            "PO" or "GRNI" => row with { Amount = Adjust(row.Amount, input.ProcurementChange, units) },
            _ => row
        }).OrderBy(x => x.Date).ThenBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.SourceId).ToArray();
        return basis with { Rows = rows, Cash = cash, CashBuckets = Project(basis.OpeningCash,
            basis.CashBuckets.Select(x => (x.Start, x.End)), cash) };
    }
}
