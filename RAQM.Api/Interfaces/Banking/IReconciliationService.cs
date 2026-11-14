using RAQM.Api.DTOs.Banking;
using RAQM.Api.Entities.Banking;

namespace RAQM.Api.Interfaces.Banking;

public interface IReconciliationService
{
    Task<ReconciliationView> Start(Guid actor, Guid org, Guid company, Guid bank, Guid statement, CancellationToken ct);
    Task<ReconciliationView> Get(Guid actor, Guid org, Guid company, Guid bank, Guid session, CancellationToken ct);
    Task<IReadOnlyList<ReconciliationSession>> History(Guid actor, Guid org, Guid company, Guid bank, CancellationToken ct);
    Task<IReadOnlyList<BankMatch>> Matches(Guid actor, Guid org, Guid company, Guid bank, Guid session, CancellationToken ct);
    Task<ReconciliationView> Confirm(Guid actor, Guid org, Guid company, Guid bank, Guid session, ConfirmMatch request, CancellationToken ct);
    Task<ReconciliationView> Release(Guid actor, Guid org, Guid company, Guid bank, Guid session, Guid match, ReviewAction request, CancellationToken ct);
    Task<ReconciliationView> Transition(Guid actor, Guid org, Guid company, Guid bank, Guid session, string action, ReviewAction request, CancellationToken ct);
}
