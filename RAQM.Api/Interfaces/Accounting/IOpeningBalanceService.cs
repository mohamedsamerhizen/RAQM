using RAQM.Api.DTOs.Accounting;

namespace RAQM.Api.Interfaces.Accounting;
public interface IOpeningBalanceService
{
    Task<OpeningBatchResponse> Create(Guid actor, Guid org, Guid company, OpeningBalanceRequest request, CancellationToken ct);
    Task<OpeningBatchResponse?> Get(Guid actor, Guid org, Guid company, CancellationToken ct);
    Task<PostingReceipt> Post(Guid actor, Guid org, Guid company, Guid batch, PostJournalRequest request, CancellationToken ct);
}
