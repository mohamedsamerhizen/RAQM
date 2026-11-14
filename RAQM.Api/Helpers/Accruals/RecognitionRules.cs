using System.Text.Json;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accruals;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Helpers.Accounting;

namespace RAQM.Api.Helpers.Accruals;

public static class RecognitionRules
{
    public const string Source = "FinancialClose.Recognition";
    public static bool Deferral(RecognitionKind kind) => kind is RecognitionKind.PrepaidExpense or RecognitionKind.DeferredRevenue;

    public static IReadOnlyList<decimal> Allocate(decimal total, int count, string currency)
    {
        var units = CurrencyPolicy.Units(currency);
        CurrencyPolicy.Validate(total, units);
        if (total == 0 || count is < 1 or > 120) throw new ApiException(400, "Use a positive total and 1 to 120 occurrences.", "recognition_amount");
        var scale = units switch { 0 => 1m, 2 => 100m, 3 => 1000m, _ => throw new ArgumentOutOfRangeException(nameof(currency)) };
        var baseMinor = decimal.Floor(total * scale / count);
        if (baseMinor == 0) throw new ApiException(400, "Total must provide at least one minor unit per occurrence.", "recognition_amount");
        var allocation = new decimal[count];
        Array.Fill(allocation, baseMinor / scale);
        allocation[^1] = total - allocation[0] * (count - 1);
        return allocation;
    }

    public static (AccountClass Debit, AccountClass Credit) Classes(RecognitionKind kind) => kind switch
    {
        RecognitionKind.AccruedExpense => (AccountClass.Expense, AccountClass.Liability),
        RecognitionKind.AccruedRevenue => (AccountClass.Asset, AccountClass.Revenue),
        RecognitionKind.PrepaidExpense => (AccountClass.Expense, AccountClass.Asset),
        RecognitionKind.DeferredRevenue => (AccountClass.Liability, AccountClass.Revenue),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static IReadOnlyList<JournalDraftLine> Lines(RecognitionSchedule schedule, RecognitionOccurrence occurrence) =>
        schedule.Kind == RecognitionKind.Recurring
            ? JsonSerializer.Deserialize<JournalDraftLine[]>(schedule.TemplateJson) ?? []
            : [new(schedule.DebitAccountId!.Value, occurrence.Amount, 0, schedule.Name),
                new(schedule.CreditAccountId!.Value, 0, occurrence.Amount, schedule.Name)];
}
