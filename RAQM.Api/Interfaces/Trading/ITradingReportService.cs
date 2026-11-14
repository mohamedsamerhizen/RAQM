using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Trading;

namespace RAQM.Api.Interfaces.Trading;
public interface ITradingReportService
{
    Task<PartyStatement> Statement(Guid actor, Guid org, Guid company, Guid party, DateOnly from, DateOnly to, CancellationToken ct);
    Task<AgingResponse> Aging(Guid actor, Guid org, Guid company, PartyKind? kind, DateOnly asOf, CancellationToken ct);
    Task<TradingReconciliation> Reconcile(Guid actor, Guid org, Guid company, DateOnly asOf, CancellationToken ct);
    Task<IReadOnlyList<AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid entity, CancellationToken ct);
}
