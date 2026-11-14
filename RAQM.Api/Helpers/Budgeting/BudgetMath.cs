using RAQM.Api.DTOs.Budgeting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FinancialReporting;

namespace RAQM.Api.Helpers.Budgeting;

public static class BudgetMath
{
    public static ApiException Bad(string message) => new(400, message, "budget_input");
    public static ApiException Conflict(string message) => new(409, message, "budget_state");
    public static ApiException Missing(string message) => new(404, message, "budget_not_found");
    public static decimal Round(decimal amount, int units) => decimal.Round(amount, units, MidpointRounding.ToEven);

    public static decimal[] Allocate(AnnualAllocation line, int periods, int units)
    {
        CurrencyPolicy.Validate(line.Total, units);
        if (!Enum.IsDefined(line.Method) || periods is < 1 or > 24) throw Bad("Invalid fiscal allocation.");
        if (line.Method == AllocationMethod.Equal && line.Values is not null) throw Bad("Equal allocation requires no period inputs.");
        var values = line.Method == AllocationMethod.Equal ? Enumerable.Repeat(1m, periods).ToArray() : line.Values;
        if (values is null || values.Count != periods || values.Any(x => x < 0 || x > CurrencyPolicy.Maximum || Round(x, 6) != x))
            throw Bad("Supply one valid nonnegative value per fiscal period.");
        var sum = values.Sum();
        if (line.Method == AllocationMethod.Manual)
        {
            foreach (var value in values) CurrencyPolicy.Validate(value, units);
            return sum == line.Total ? values.ToArray() : throw Bad("Manual period amounts must equal the annual total.");
        }
        if (sum <= 0 || (line.Method == AllocationMethod.Percent && sum != 100))
            throw Bad("Allocation weights must be positive in total and percentages must equal 100.");
        var remaining = line.Total;
        var weight = sum;
        var output = new decimal[periods];
        for (var index = 0; index < periods; index++)
        {
            if (values[index] == 0) continue;
            output[index] = values[index] == weight ? remaining : Round(remaining * (values[index] / weight), units);
            remaining -= output[index];
            weight -= values[index];
        }
        return output;
    }

    public static bool Accepts(BudgetKind kind, AccountClass classification) => kind switch
    {
        BudgetKind.Revenue => classification == AccountClass.Revenue,
        BudgetKind.Operating => classification == AccountClass.Expense,
        BudgetKind.Capital or BudgetKind.Inventory => classification == AccountClass.Asset,
        _ => false
    };

    public static bool Counts(BudgetKind kind, string source) => kind switch
    {
        BudgetKind.Capital => source is "FixedAssets.Acquisition" or "FixedAssets.Improvement" or "AP.Bill" or "AP.Credit",
        BudgetKind.Inventory => source is "Inventory.Receipt" or "Inventory.ReceiptCorrection" or "Inventory.SupplierReturn",
        _ => source is not (FinancialMath.YearEndSource or "OpeningBalance")
    };

    public static decimal Actual(BudgetKind kind, decimal debit, decimal credit) => kind == BudgetKind.Revenue ? credit - debit : debit - credit;
    public static decimal Favorable(BudgetKind kind, decimal budget, decimal actual) =>
        kind == BudgetKind.Revenue ? actual - budget : budget - actual;
}
