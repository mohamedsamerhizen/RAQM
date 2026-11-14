namespace RAQM.Api.Helpers.Accounting;
public sealed record FiscalDateRange(int Number, DateOnly Start, DateOnly End);
public static class FiscalCalendar
{
    public static IReadOnlyList<FiscalDateRange> Generate(DateOnly start)
    {
        if (start.Year is < 1900 or > 9998)
            throw new ApiException(400, "Fiscal start year must be 1900..9998.", "fiscal_year");
        return Enumerable.Range(0, 12).Select(i => new FiscalDateRange(i + 1, start.AddMonths(i), start.AddMonths(i + 1).AddDays(-1))).ToList();
    }
}
