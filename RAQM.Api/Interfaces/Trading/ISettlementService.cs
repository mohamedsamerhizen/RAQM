using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Trading;

namespace RAQM.Api.Interfaces.Trading;
public interface ISettlementService
{
    Task<PaymentResponse> ReceiveOrPay(Guid actor, Guid org, Guid company, PaymentRequest request, CancellationToken ct);
    Task<PaymentResponse> Refund(Guid actor, Guid org, Guid company, RefundRequest request, CancellationToken ct);
    Task<PaymentResponse> ReversePayment(Guid actor, Guid org, Guid company, Guid id, ReversePaymentRequest request, CancellationToken ct);
    Task<PaymentResponse> GetPayment(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct);
    Task<PageResult<PaymentResponse>> Payments(Guid actor, Guid org, Guid company, Guid? party, int page, int pageSize, CancellationToken ct);
    Task<AllocationResponse> Allocate(Guid actor, Guid org, Guid company, AllocationRequest request, CancellationToken ct);
    Task<AllocationResponse> ReverseAllocation(Guid actor, Guid org, Guid company, Guid id, ReverseAllocationRequest request, CancellationToken ct);
    Task<PageResult<AllocationResponse>> Allocations(Guid actor, Guid org, Guid company, Guid? party, int page, int pageSize, CancellationToken ct);
}
