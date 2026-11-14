using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.Entities.Forecasting;

public enum ForecastState { Draft, Approved, Rejected }

public sealed class ForecastVersion : FinancialEvidence
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Number { get; set; }
    public Guid? BasisId { get; set; }
    public Guid? ScenarioBaseId { get; set; }
    public DateOnly AsOf { get; set; }
    public int Horizon { get; set; }
    public ForecastState State { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string RequestKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string AssumptionsJson { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
}
