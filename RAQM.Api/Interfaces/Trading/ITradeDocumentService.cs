using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Trading;

namespace RAQM.Api.Interfaces.Trading;
public interface ITradeDocumentService
{
    Task<TradeDocumentResponse> Create(Guid actor, Guid org, Guid company, TradeDocumentRequest request, CancellationToken ct);
    Task<TradeDocumentResponse> Update(Guid actor, Guid org, Guid company, Guid id, UpdateTradeDocumentRequest request, CancellationToken ct);
    Task<TradeDocumentResponse> Transition(Guid actor, Guid org, Guid company, Guid id, string action, DocumentTransitionRequest request, CancellationToken ct);
    Task<TradeDocumentResponse> Post(Guid actor, Guid org, Guid company, Guid id, PostTradeDocumentRequest request, CancellationToken ct);
    Task<TradeDocumentResponse> Get(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct);
    Task<PageResult<TradeDocumentResponse>> Search(Guid actor, Guid org, Guid company, Guid? party, TradeKind? kind, TradeState? state, DateOnly? dueBefore, string? search, int page, int pageSize, CancellationToken ct);
}
