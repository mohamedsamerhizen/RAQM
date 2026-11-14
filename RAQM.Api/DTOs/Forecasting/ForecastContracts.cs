using RAQM.Api.Entities.Budgeting;

namespace RAQM.Api.DTOs.Forecasting;

public enum ForecastMethod { Manual, HistoricalMonthlyAverage, ApprovedBudget }
public sealed record ForecastDriver(Guid AccountId, ForecastMethod Method, decimal GrowthPercent,
    IReadOnlyList<decimal>? Monthly = null, int HistoryMonths = 3);
public sealed record CashAssumption(string Evidence, DateOnly FirstDate, decimal Amount, int Occurrences = 1);
public sealed record ForecastRequest(string Key, string Code, string Name, DateOnly AsOf, int Horizon,
    IReadOnlyList<ForecastDriver> Drivers, IReadOnlyList<CashAssumption>? Cash = null, Guid? BasisId = null,
    int CustomerDelayDays = 0, int SupplierDelayDays = 0);
public sealed record ScenarioRequest(string Key, string Code, string Name, Guid BaseId, decimal RevenueChange,
    decimal ExpenseChange, decimal ProcurementChange, int CustomerDelayDays, string Evidence, Guid? BasisId = null);
public sealed record ForecastAction(Guid Version, string Reason);
public sealed record ForecastRow(Guid AccountId, BudgetKind Kind, DateOnly Start, DateOnly End, decimal Amount, decimal Budget,
    decimal Input, Guid? BudgetVersionId);
public sealed record CashEvent(string Kind, Guid? SourceId, string Evidence, DateOnly Date, decimal Amount, bool Estimated);
public sealed record CashBucket(DateOnly Start, DateOnly End, decimal Opening, decimal Inflows, decimal Outflows, decimal Closing,
    decimal Lowest, bool Shortfall);
public sealed record ForecastSnapshot(string Currency, DateOnly AsOf, IReadOnlyList<ForecastRow> Rows, decimal OpeningCash,
    IReadOnlyList<CashEvent> Cash, IReadOnlyList<CashBucket> CashBuckets, long PostedSequence, IReadOnlyList<string> Warnings);
