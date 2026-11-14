using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.Entities.FinancialReporting;

namespace RAQM.Api.Interfaces.FinancialReporting;

public interface IFinancialReportService
{
    Task<IReadOnlyList<ReportDefinition>> Definitions(Guid actor, Guid org, Guid company, CancellationToken ct);
    Task<ReportDefinition> Publish(Guid actor, Guid org, Guid company, DefinitionRequest request, CancellationToken ct);
    Task<FinancialReport> Report(Guid actor, Guid org, Guid company, ReportQuery query, CancellationToken ct);
    Task<PageResult<FinancialLine>> Drill(Guid actor, Guid org, Guid company, ReportQuery query, Guid account, bool ytd, int page, int size, CancellationToken ct);
    Task<FinancialExport> Export(Guid actor, Guid org, Guid company, ReportQuery query, string format, CancellationToken ct);
}
