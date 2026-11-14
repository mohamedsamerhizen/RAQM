using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Accruals;
using RAQM.Api.Entities.Accruals;

namespace RAQM.Api.Interfaces.Accruals;

public interface IRecognitionService
{
    Task<RecognitionSchedule> Create(Guid actor, Guid org, Guid company, ScheduleRequest request, CancellationToken ct);
    Task<RecognitionSchedule> Transition(Guid actor, Guid org, Guid company, Guid id, bool approve, ScheduleAction request, CancellationToken ct);
    Task<PageResult<RecognitionSchedule>> List(Guid actor, Guid org, Guid company, int page, int size, CancellationToken ct);
    Task<IReadOnlyList<RecognitionPreview>> Preview(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct);
    Task<RecognitionResult> Post(Guid actor, Guid org, Guid company, Guid id, Guid occurrence, RecognitionExecution request, CancellationToken ct);
    Task<RecognitionResult> Reverse(Guid actor, Guid org, Guid company, Guid id, Guid occurrence, RecognitionReversal request, CancellationToken ct);
    Task<IReadOnlyList<RecognitionResult>> Run(Guid actor, Guid org, Guid company, Guid id, RecognitionExecution request, CancellationToken ct);
    Task<ScheduleReconciliation> Reconcile(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct);
}
