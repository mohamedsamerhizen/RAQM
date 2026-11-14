using RAQM.Api.DTOs.Treasury;

namespace RAQM.Api.Interfaces.Treasury;

public interface ITreasuryReportService
{
    Task<TreasuryActivity> Activity(Guid actor, Guid org, Guid company, Guid account, DateOnly from, DateOnly to, int page, int size, CancellationToken ct);
    Task<IReadOnlyList<TreasuryPosition>> Position(Guid actor, Guid org, Guid company, DateOnly from, DateOnly to, CancellationToken ct);
}
