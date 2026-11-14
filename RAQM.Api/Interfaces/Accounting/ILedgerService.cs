using RAQM.Api.DTOs.Accounting;

namespace RAQM.Api.Interfaces.Accounting;
public interface ILedgerService
{
    Task<AccountLedgerResponse> AccountLedger(Guid actor, Guid org, Guid company, Guid account, DateOnly from, DateOnly to, int page, int pageSize, CancellationToken ct);
    Task<TrialBalanceResponse> TrialBalance(Guid actor, Guid org, Guid company, DateOnly from, DateOnly to, bool includeHierarchy, CancellationToken ct);
}
