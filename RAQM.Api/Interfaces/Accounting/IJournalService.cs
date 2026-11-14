using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.Interfaces.Accounting;
public interface IJournalService
{
    Task<JournalResponse> Create(Guid actor, Guid org, Guid company, JournalRequest request, CancellationToken ct);
    Task<JournalResponse> Update(Guid actor, Guid org, Guid company, Guid id, UpdateJournalRequest request, CancellationToken ct);
    Task Delete(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct);
    Task<JournalResponse> Get(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct);
    Task<PageResult<JournalResponse>> Search(Guid actor, Guid org, Guid company, DateOnly? from, DateOnly? to, JournalState? state, string? search, int page, int pageSize, CancellationToken ct);
    Task<JournalValidationResponse> Validate(Guid actor, Guid org, Guid company, Guid id, PostJournalRequest request, CancellationToken ct);
    Task<PostingReceipt> Post(Guid actor, Guid org, Guid company, Guid id, PostJournalRequest request, CancellationToken ct);
    Task<PostingReceipt> Reverse(Guid actor, Guid org, Guid company, Guid id, ReverseJournalRequest request, CancellationToken ct);
    Task<IReadOnlyList<AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid entity, CancellationToken ct);
}
