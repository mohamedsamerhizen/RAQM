using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Helpers.FixedAssets;

namespace RAQM.Api.Services.FixedAssets;

public sealed class AssetPortfolio(RaqmDbContext db)
{
    public async Task<AssetSnapshot> Load(Guid company, DateOnly cutoff, CancellationToken ct) => new(
        await db.AssetCategories.AsNoTracking().Include(x => x.Controls).Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct),
        await db.FixedAssets.AsNoTracking().Include(x => x.Components).Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct),
        await db.AssetMovements.AsNoTracking().Where(x => x.CompanyId == company && x.Date <= cutoff).ToListAsync(ct),
        await db.AssetOperations.AsNoTracking().Where(x => x.CompanyId == company && x.Date <= cutoff && x.State != AssetOperationState.Cancelled)
            .OrderBy(x => x.Date).ThenBy(x => x.Sequence).ThenBy(x => x.Id).ToListAsync(ct));

    public async Task<IReadOnlyList<AssetReconciliation>> Reconcile(Guid company, DateOnly cutoff, AssetSnapshot snapshot, CancellationToken ct)
    {
        var ids = snapshot.Categories.SelectMany(x => x.Controls).Select(x => x.AccountId).ToArray();
        var ledger = await (from l in db.JournalLines join j in db.Journals on l.JournalId equals j.Id
            where l.CompanyId == company && j.CompanyId == company && j.State == JournalState.Posted && j.PostingDate <= cutoff && ids.Contains(l.AccountId)
            group l by l.AccountId into g select new { Account = g.Key, Signed = g.Sum(x => x.Debit - x.Credit) })
            .ToDictionaryAsync(x => x.Account, x => x.Signed, ct);
        var groups = snapshot.Assets.GroupBy(x => x.CategoryId).ToDictionary(x => x.Key, x =>
            x.SelectMany(a => a.Components).Select(snapshot.Balance).ToArray());
        return snapshot.Categories.SelectMany(category => category.Controls.OrderBy(x => x.Role).Select(control =>
        {
            var values = groups.GetValueOrDefault(category.Id) ?? [];
            var signed = control.Role switch
            {
                AssetControlRole.Cost => values.Sum(x => x.Cost),
                AssetControlRole.AccumulatedDepreciation => -values.Sum(x => x.Depreciation),
                AssetControlRole.AccumulatedImpairment => -values.Sum(x => x.Impairment),
                _ => 0
            };
            var actual = ledger.GetValueOrDefault(control.AccountId);
            return new AssetReconciliation(category.Id, category.Code, control.Role, control.AccountId, signed, actual, actual - signed, category.MandatoryClose);
        })).ToArray();
    }
}

public sealed class AssetSnapshot(IReadOnlyList<AssetCategory> categories, IReadOnlyList<FixedAsset> assets,
    IReadOnlyList<AssetMovement> movements, IReadOnlyList<AssetOperation> operations)
{
    private readonly ILookup<Guid, AssetMovement> entries = movements.ToLookup(x => x.ComponentId);
    public IReadOnlyList<AssetCategory> Categories { get; } = categories;
    public IReadOnlyList<FixedAsset> Assets { get; } = assets;
    public IReadOnlyList<AssetMovement> Movements { get; } = movements;
    public IReadOnlyList<AssetOperation> Operations { get; } = operations;

    public ComponentBalance Balance(AssetComponent component) => AssetMath.Balance(component, entries[component.Id]);
    public IEnumerable<AssetMovement> History(Guid component) => entries[component];

    public AssetPosition Position(FixedAsset asset, DateOnly at)
    {
        var recognition = asset.RecognitionDate <= at ? asset.RecognitionDate : null;
        var available = recognition.HasValue && asset.AvailableDate <= at ? asset.AvailableDate : null;
        var disposal = asset.DisposalDate <= at ? asset.DisposalDate : null;
        var status = disposal.HasValue ? asset.Status : available.HasValue ? AssetStatus.InService : recognition.HasValue ? AssetStatus.Capitalized :
            asset.Status == AssetStatus.Approved && Operations.Any(x => x.Date <= at && x.State == AssetOperationState.Approved &&
                (x.Kind is AssetOperationKind.Acquisition or AssetOperationKind.Opening) && Plan(x).Assets.Contains(asset.Id))
                ? AssetStatus.Approved : AssetStatus.Draft;
        var transfer = Operations.LastOrDefault(x => x.AssetId == asset.Id && x.Kind == AssetOperationKind.Transfer &&
            x.State == AssetOperationState.Posted && x.Date <= at);
        var assignment = (transfer is null ? null : Request(transfer).Assignment) ??
            JsonSerializer.Deserialize<AssetAssignment>(asset.AssignmentJson)!;
        if (disposal.HasValue) assignment = assignment with { PhysicalStatus = PhysicalStatus.Retired };
        return new(asset.Id, asset.CategoryId, asset.Code, asset.Name, status, recognition, available, disposal, assignment,
            asset.Components.OrderBy(x => x.Number).Select(Balance).ToArray(), asset.AcquisitionDate, asset.Description,
            asset.Currency, asset.SupplierId, asset.PurchaseEvidence);
    }

    public static AssetOperationRequest Request(AssetOperation operation) =>
        JsonSerializer.Deserialize<AssetOperationRequest>(operation.PayloadJson)!;
    public static AssetPlan Plan(AssetOperation operation) => JsonSerializer.Deserialize<AssetPlan>(operation.PlanJson!)!;
}
