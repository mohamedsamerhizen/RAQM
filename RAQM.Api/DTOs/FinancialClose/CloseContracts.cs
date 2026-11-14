using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialClose;

namespace RAQM.Api.DTOs.FinancialClose;

public enum FindingSeverity { Blocking, Review, Information }
public sealed record CloseFinding(string Rule, FindingSeverity Severity, string Detail, Guid? SourceId = null);
public sealed record CloseReadiness(Guid SessionId, Guid Version, PeriodState AccountingState, IReadOnlyList<CloseFinding> Findings, string Fingerprint);
public sealed record CloseView(PeriodCloseSession Session, PeriodState AccountingState, IReadOnlyList<CloseException> Exceptions);
public sealed record CloseTaskRequest(Guid Version, string Code, string Title, Guid ReviewerId, bool Required = true);
public sealed record CloseAction(Guid Version, string Reason);
public sealed record ExceptionRequest(Guid Version, string Detail, bool Blocking = true);
public sealed record YearEndRequest(Guid SessionId, Guid Version, string Key, string Reason, bool AllowBackdated = false);
