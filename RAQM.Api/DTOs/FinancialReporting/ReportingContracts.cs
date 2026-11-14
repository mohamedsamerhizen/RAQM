using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.DTOs.FinancialReporting;

public enum ReportKind { BalanceSheet, Income, Equity, CashMovement }
public sealed record StatementMapping(Guid AccountId, StatementGroup Group);
public sealed record CashSourceRule(string Source, CashGroup Group);
public sealed record DefinitionRequest(int ExpectedRevision, Guid? RetainedEarningsId, IReadOnlyList<StatementMapping> Accounts,
    IReadOnlyList<CashSourceRule> CashSources, string Reason);
public sealed record ReportQuery(ReportKind Kind, DateOnly? From = null, DateOnly? To = null, Guid? FiscalYearId = null,
    Guid? PeriodId = null, DateOnly? CompareFrom = null, DateOnly? CompareTo = null, int? Revision = null, int? FiscalQuarter = null,
    long? AsOfSequence = null);
public sealed record ReportRow(string Key, string Label, decimal Amount, decimal? Comparative, decimal? ChangePercent, Guid? AccountId = null);
public sealed record ReportWindow(DateOnly From, DateOnly To);
public sealed record FinancialReport(ReportKind Kind, Guid CompanyId, string CompanyName, string Currency, int DefinitionRevision,
    Guid Actor, DateTimeOffset GeneratedAt, long PostedSequenceCutoff, ReportWindow Period, ReportWindow? Comparison,
    IReadOnlyList<ReportRow> Rows, IReadOnlyList<string> Warnings, decimal IntegrityDifference, decimal? ComparativeIntegrityDifference);
public sealed record FinancialLine(Guid LineId, Guid JournalId, Guid AccountId, DateOnly Date, long Sequence, int Number,
    string Source, string? Reference, string? JournalNumber, decimal Debit, decimal Credit, Guid? ReversesJournalId, string? OriginalSource = null)
{
    // EF needs a member initializer for composable queries over the projection.
    internal FinancialLine() : this(default, default, default, default, default, default, string.Empty, null, null, default, default, null)
    {
    }
}
public sealed record FinancialExport(byte[] Content, string MediaType, string FileName);
