using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.Entities.FixedAssets;

namespace RAQM.Api.Interfaces.FixedAssets;

public interface IAssetWorkflow
{
    Task<AssetOperation> Prepare(Guid actor, Guid org, Guid company, AssetOperationRequest request, CancellationToken ct);
    Task<AssetPlan> Preview(Guid actor, Guid org, Guid company, Guid operation, CancellationToken ct);
    Task<AssetOperation> Change(Guid actor, Guid org, Guid company, Guid operation, string action, AssetAction request, CancellationToken ct);
    Task<PageResult<AssetOperation>> History(Guid actor, Guid org, Guid company, Guid? asset, AssetOperationKind? kind,
        int page, int size, CancellationToken ct);
}
