using RAQM.Api.DTOs.Accounting;

namespace RAQM.Api.Interfaces.Accounting;
// The actor and persisted company membership are checked even for in-process callers.
public interface IPostingEngine
{
    Task<PostingReceipt> PostAsync(Guid actorId, PostingRequest request, CancellationToken cancellationToken);
}
