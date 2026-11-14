using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.Entities.Banking;

namespace RAQM.Api.Interfaces.Banking;

public interface IBankAccountService
{
    Task<BankProfile> CreateBank(Guid actor, Guid org, Guid company, BankProfileRequest request, CancellationToken ct);
    Task<BankProfile> ChangeBank(Guid actor, Guid org, Guid company, Guid id, AccountChange request, CancellationToken ct);
    Task<IReadOnlyList<BankProfile>> Banks(Guid actor, Guid org, Guid company, CancellationToken ct);
    Task<TreasuryAccountResponse> Register(Guid actor, Guid org, Guid company, TreasuryAccountRequest request, CancellationToken ct);
    Task<TreasuryAccountResponse> Change(Guid actor, Guid org, Guid company, Guid id, AccountChange request, CancellationToken ct);
    Task<PageResult<TreasuryAccountResponse>> Search(Guid actor, Guid org, Guid company, TreasuryAccountKind? kind, bool? active, string? search, int page, int size, CancellationToken ct);
    Task<IReadOnlyList<AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct);
}
