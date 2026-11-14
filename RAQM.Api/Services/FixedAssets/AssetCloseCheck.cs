using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Helpers.Trading;

namespace RAQM.Api.Services.FixedAssets;

public sealed record AssetCloseEvidence(IReadOnlyList<CloseFinding> Findings, string Fingerprint);

public sealed class AssetCloseCheck(AssetPortfolio portfolio)
{
    public async Task<AssetCloseEvidence> Check(Guid company, AccountingPeriod period, CancellationToken ct)
    {
        var data = await portfolio.Load(company, period.EndDate, ct);
        if (data.Categories.Count == 0) return new([], "");
        var findings = new List<CloseFinding>();
        var reconciliation = await portfolio.Reconcile(company, period.EndDate, data, ct);
        foreach (var r in reconciliation.Where(x => x.Difference != 0))
            findings.Add(new("fixed-asset-gl", r.Mandatory ? FindingSeverity.Blocking : FindingSeverity.Review,
                FormattableString.Invariant($"{r.Code} {r.Role} differs from the register by {r.Difference}."), r.CategoryId));
        var categories = data.Categories.ToDictionary(x => x.Id);
        var assets = data.Assets.ToDictionary(x => x.Id);
        foreach (var asset in data.Assets.Where(x => categories[x.CategoryId].Depreciable &&
            x.Components.Any(c => AssetPlanningService.Due(data, x, c, period))))
            findings.Add(new("fixed-asset-depreciation", categories[asset.CategoryId].MandatoryClose ? FindingSeverity.Blocking : FindingSeverity.Review,
                $"Asset {asset.Code} has unposted depreciation for this period.", asset.Id));
        foreach (var operation in data.Operations.Where(x => x.State is AssetOperationState.Draft or AssetOperationState.Approved &&
            x.Kind is AssetOperationKind.Acquisition or AssetOperationKind.Improvement or AssetOperationKind.Impairment or
                AssetOperationKind.Recovery or AssetOperationKind.Sale or AssetOperationKind.WriteOff or AssetOperationKind.Opening))
        {
            var mandatory = operation.AssetId is { } id && assets.TryGetValue(id, out var asset) && categories[asset.CategoryId].MandatoryClose ||
                operation.Kind == AssetOperationKind.Opening && (AssetSnapshot.Request(operation).OpeningAssets ?? []).Any(x =>
                    assets.TryGetValue(x, out var opening) && categories[opening.CategoryId].MandatoryClose);
            findings.Add(new("fixed-asset-open-operation", mandatory ? FindingSeverity.Blocking : FindingSeverity.Review,
                $"{operation.Kind} remains unposted at the close cutoff.", operation.Id));
        }
        var hash = TradeRules.Hash(new
        {
            Reconciliation = reconciliation.OrderBy(x => x.AccountId).ToArray(),
            Operations = data.Operations.Select(x => new { x.Id, x.Version, x.State }).OrderBy(x => x.Id).ToArray(),
            Categories = data.Categories.Select(x => x.Id).Order().ToArray()
        });
        return new(findings, hash);
    }
}
