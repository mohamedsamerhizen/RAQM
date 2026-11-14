using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Trading;

namespace RAQM.Api.Interfaces.Trading;
public interface ITradingSetupService
{
    Task<IReadOnlyList<ControlAccountResponse>> Configure(Guid actor, Guid org, Guid company, ControlAccountRequest request, CancellationToken ct);
    Task<IReadOnlyList<ControlAccountResponse>> Configuration(Guid actor, Guid org, Guid company, CancellationToken ct);
    Task<PartyResponse> CreateParty(Guid actor, Guid org, Guid company, PartyRequest request, CancellationToken ct);
    Task<PartyResponse> UpdateParty(Guid actor, Guid org, Guid company, Guid id, UpdatePartyRequest request, CancellationToken ct);
    Task<PartyResponse> GetParty(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct);
    Task<PageResult<PartyResponse>> Parties(Guid actor, Guid org, Guid company, PartyKind? kind, string? search, bool? active, int page, int pageSize, CancellationToken ct);
    Task<TaxCodeResponse> CreateTaxCode(Guid actor, Guid org, Guid company, TaxCodeRequest request, CancellationToken ct);
    Task<TaxCodeResponse> UpdateTaxCode(Guid actor, Guid org, Guid company, Guid id, TaxCodeRequest request, CancellationToken ct);
    Task<TaxRateResponse> AddTaxRate(Guid actor, Guid org, Guid company, Guid code, TaxRateRequest request, CancellationToken ct);
    Task<IReadOnlyList<TaxCodeResponse>> TaxCodes(Guid actor, Guid org, Guid company, CancellationToken ct);
    Task<IReadOnlyList<TaxRateResponse>> TaxRates(Guid actor, Guid org, Guid company, Guid code, CancellationToken ct);
}
