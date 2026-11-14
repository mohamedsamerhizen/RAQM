using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.Entities.YearEnd;

public sealed class YearEndRecord : FinancialEvidence
{
    public Guid FiscalYearId { get; set; }
    public Guid SessionId { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid RetainedEarningsId { get; set; }
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public decimal Profit { get; set; }
    public string ClosingLinesJson { get; set; } = "[]";
    public Guid? JournalId { get; set; }
    public bool Finalized { get; set; }
    public Guid ClosedBy { get; set; }
    public DateTimeOffset ClosedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Reason { get; set; } = "";
}
