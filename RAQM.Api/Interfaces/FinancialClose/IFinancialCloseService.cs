using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.Entities.YearEnd;

namespace RAQM.Api.Interfaces.FinancialClose;

public interface IFinancialCloseService
{
    Task<CloseView> Start(Guid actor, Guid org, Guid company, Guid period, CancellationToken ct);
    Task<IReadOnlyList<CloseView>> History(Guid actor, Guid org, Guid company, Guid period, CancellationToken ct);
    Task<CloseReadiness> Readiness(Guid actor, Guid org, Guid company, Guid session, CancellationToken ct);
    Task<CloseView> AddTask(Guid actor, Guid org, Guid company, Guid session, CloseTaskRequest request, CancellationToken ct);
    Task<CloseView> CompleteTask(Guid actor, Guid org, Guid company, Guid session, Guid task, CloseAction request, CancellationToken ct);
    Task<CloseView> Raise(Guid actor, Guid org, Guid company, Guid session, ExceptionRequest request, CancellationToken ct);
    Task<CloseView> Resolve(Guid actor, Guid org, Guid company, Guid session, Guid exception, CloseAction request, CancellationToken ct);
    Task<CloseView> Transition(Guid actor, Guid org, Guid company, Guid session, string action, CloseAction request, CancellationToken ct);
    Task<YearEndRecord> YearEnd(Guid actor, Guid org, Guid company, Guid year, YearEndRequest request, CancellationToken ct);
    Task<IReadOnlyList<YearEndRecord>> Years(Guid actor, Guid org, Guid company, CancellationToken ct);
}
