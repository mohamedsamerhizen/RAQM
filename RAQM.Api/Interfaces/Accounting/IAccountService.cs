using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.Interfaces.Accounting;
public interface IAccountService
{
    Task<AccountResponse> Create(Guid actor, Guid org, Guid company, AccountRequest request, CancellationToken ct);
    Task<AccountResponse> Update(Guid actor, Guid org, Guid company, Guid account, UpdateAccountRequest request, CancellationToken ct);
    Task<PageResult<AccountResponse>> Search(Guid actor, Guid org, Guid company, string? search, AccountClass? classification, bool? active, bool? posting, int page, int pageSize, CancellationToken ct);
    Task<AccountResponse> Get(Guid actor, Guid org, Guid company, Guid account, CancellationToken ct);
    Task<IReadOnlyList<AccountResponse>> Initialize(Guid actor, Guid org, Guid company, string template, CancellationToken ct);
    Task<CurrencyPolicyResponse> Policy(Guid actor, Guid org, Guid company, CancellationToken ct);
}
