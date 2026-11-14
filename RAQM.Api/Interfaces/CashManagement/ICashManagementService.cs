using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.CashManagement;
using RAQM.Api.Entities.CashManagement;

namespace RAQM.Api.Interfaces.CashManagement;

public interface ICashManagementService
{
    Task<TreasuryMovement> PrepareTransfer(Guid actor, Guid org, Guid company, TransferRequest request, CancellationToken ct);
    Task<TreasuryMovement> ApproveTransfer(Guid actor, Guid org, Guid company, Guid id, MovementAction request, CancellationToken ct);
    Task<TreasuryMovement> Reverse(Guid actor, Guid org, Guid company, Guid id, MovementAction request, CancellationToken ct);
    Task<TreasuryMovement> Record(Guid actor, Guid org, Guid company, CashMovementRequest request, CancellationToken ct);
    Task<TreasuryMovement> Adjust(Guid actor, Guid org, Guid company, Guid bank, Guid session, AdjustmentRequest request, CancellationToken ct);
    Task<CashCount> Count(Guid actor, Guid org, Guid company, CashCountRequest request, CancellationToken ct);
    Task<TreasuryMovement> Correct(Guid actor, Guid org, Guid company, Guid count, CountCorrectionRequest request, CancellationToken ct);
    Task<PageResult<TreasuryMovement>> Movements(Guid actor, Guid org, Guid company, Guid? account, int page, int size, CancellationToken ct);
    Task<PageResult<CashCount>> Counts(Guid actor, Guid org, Guid company, Guid account, int page, int size, CancellationToken ct);
}
