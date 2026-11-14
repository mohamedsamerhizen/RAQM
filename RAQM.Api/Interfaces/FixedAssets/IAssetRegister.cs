using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.Entities.FixedAssets;

namespace RAQM.Api.Interfaces.FixedAssets;

public interface IAssetRegister
{
    Task<AssetCategory> CreateCategory(Guid actor, Guid org, Guid company, CategoryRequest request, CancellationToken ct);
    Task<IReadOnlyList<AssetCategory>> Categories(Guid actor, Guid org, Guid company, CancellationToken ct);
    Task<FixedAsset> Register(Guid actor, Guid org, Guid company, RegisterAssetRequest request, CancellationToken ct);
    Task<PageResult<AssetPosition>> Search(Guid actor, Guid org, Guid company, DateOnly at, Guid? category, AssetStatus? status,
        string? search, string? branch, int page, int size, CancellationToken ct);
    Task<AssetPeriodReport> Report(Guid actor, Guid org, Guid company, DateOnly? from, DateOnly? to, Guid? period, CancellationToken ct);
    Task<IReadOnlyList<AssetMovement>> Movements(Guid actor, Guid org, Guid company, Guid asset, CancellationToken ct);
    Task<IReadOnlyList<DepreciationForecast>> Schedule(Guid actor, Guid org, Guid company, Guid asset, DateOnly at, DateOnly through, CancellationToken ct);
    Task<IReadOnlyList<AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid entity, CancellationToken ct);
}
