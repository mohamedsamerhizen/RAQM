using RAQM.Api.DTOs.Accounting;

namespace RAQM.Api.Interfaces.Accounting;
public interface IFiscalPeriodService
{
    Task<FiscalYearResponse> Create(Guid actor, Guid org, Guid company, FiscalYearRequest request, CancellationToken ct);
    Task<IReadOnlyList<FiscalYearResponse>> List(Guid actor, Guid org, Guid company, CancellationToken ct);
    Task<PeriodResponse> ChangeState(Guid actor, Guid org, Guid company, Guid period, PeriodStateRequest request, CancellationToken ct);
}
