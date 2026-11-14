using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accruals;
using RAQM.Api.Services.Inventory;
using static RAQM.Api.Helpers.FinancialDimensions.DimensionMath;

namespace RAQM.Api.Services.FinancialDimensions;

public sealed partial class DimensionPosting
{
    private async Task<(Dictionary<int, EffectiveDimension[]> Explicit, Dictionary<int, EffectiveDimension[]> Inherited)> Assignments(
        Guid actor, Guid company, PostingRequest request, CancellationToken ct)
    {
        var id = request.SourceDocumentId;
        var source = request.SourceModule;
        Dictionary<int, EffectiveDimension[]> direct = [], inherited = [];
        if (source is "Dimensions.Allocation" or "Dimensions.Correction")
        {
            var allocation = await db.CostAllocationExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct);
            if (allocation is null || allocation.Mode != AllocationMode.Financial || request.IdempotencyKey != $"{source}:{id:N}")
                throw Conflict("A coordinated financial allocation source is required.");
            var plan = JsonSerializer.Deserialize<AllocationPlan>(allocation.PlanJson)!;
            if (source == "Dimensions.Correction")
            {
                if (allocation.State != AllocationState.Executed || allocation.JournalId is null || allocation.CorrectedOn is not null ||
                    request.AccountingDate < allocation.Date || !plan.Lines.Select(x => x with { Debit = x.Credit, Credit = x.Debit }).SequenceEqual(request.Lines))
                    throw Conflict("Allocation correction must negate the executed plan exactly.");
                return (direct, await Original(company, allocation.JournalId!.Value, DimensionOrigin.Reversal, ct));
            }
            if (allocation.State != AllocationState.Approved || allocation.PostedBy != actor || allocation.Date != request.AccountingDate ||
                !plan.Lines.SequenceEqual(request.Lines)) throw Conflict("Allocation posting must match its staged approved plan.");
            return (plan.Dimensions.ToDictionary(x => x.Number, x => ToTags(x.Values, null, DimensionOrigin.Allocation)), inherited);
        }
        if (source == "ManualJournal" && request.ExpectedVersion is { } draftVersion)
            direct = await Load(company, DimensionSource.Manual, id, draftVersion, ct);
        else if (source is "AR.Invoice" or "AP.Bill" or "AR.Credit" or "AP.Credit")
        {
            var trade = await db.TradeDocuments.AsNoTracking().Include(x => x.Lines)
                .SingleAsync(x => x.CompanyId == company && x.Id == id, ct);
            var ordinals = DimensionBindings.CommercialOrdinals(trade.Lines);
            direct = (await Load(company, DimensionSource.Trading, id, trade.Version, ct))
                .Where(x => ordinals.ContainsKey(x.Key)).ToDictionary(x => ordinals[x.Key], x => x.Value);
            foreach (var line in trade.Lines.Where(x => x.OriginalLineId.HasValue && ordinals.ContainsKey(x.Number)))
                inherited[ordinals[line.Number]] = await TradeLineTags(company, line.OriginalLineId!.Value, ct);
        }
        else if (source.StartsWith("Inventory.", StringComparison.Ordinal))
        {
            var stock = await db.StockOperations.AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == id, ct);
            direct = await Load(company, DimensionSource.Stock, id, stock.Version, ct);
            if (stock.OrderLineId is { } orderLine && stock.Kind is StockKind.Receipt or StockKind.ReceiptCorrection or StockKind.SupplierReturn)
            {
                var tags = await Load(company, DimensionSource.Procurement, orderLine, null, ct);
                var inventory = await db.InventoryAccounts.Where(x => x.CompanyId == company && x.Role == InventoryRole.Inventory)
                    .Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(ct);
                if (tags.TryGetValue(1, out var line)) AddForAccount(inventory, line);
            }
            if (stock.Kind == StockKind.Issue && stock.TradeLineId is { } tradeLine)
            {
                var cogs = await db.InventoryAccounts.Where(x => x.CompanyId == company && x.Role == InventoryRole.CostOfSales)
                    .Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(ct);
                AddForAccount(cogs, await TradeLineTags(company, tradeLine, ct));
            }
            if (stock.Kind == StockKind.CustomerReturn && stock.RelatedOperationId is { } issue)
            {
                var cogs = await db.InventoryAccounts.Where(x => x.CompanyId == company && x.Role == InventoryRole.CostOfSales)
                    .Select(x => x.AccountId).SingleAsync(ct);
                var basis = await (from op in db.StockOperations join line in db.JournalLines on op.JournalId equals (Guid?)line.JournalId
                    where op.CompanyId == company && line.CompanyId == company && op.Id == issue && line.AccountId == cogs
                    orderby line.Number
                    select new { line.JournalId, line.Number }).ToListAsync(ct);
                if (basis.Count > 0)
                {
                    var original = await Original(company, basis[0].JournalId, DimensionOrigin.Reversal, ct);
                    var tags = original.GetValueOrDefault(basis[0].Number) ?? [];
                    if (basis.Any(line => !(original.GetValueOrDefault(line.Number) ?? []).OrderBy(x => x.DefinitionId)
                        .SequenceEqual(tags.OrderBy(x => x.DefinitionId))))
                        throw Conflict("The original issue has conflicting cost-of-sales attribution.");
                    AddForAccount(cogs, tags);
                    if (StockPlanning.Input(stock).Damaged)
                    {
                        var loss = await db.InventoryAccounts.Where(x => x.CompanyId == company && x.Role == InventoryRole.Loss)
                            .Select(x => x.AccountId).SingleAsync(ct);
                        AddForAccount(loss, tags);
                    }
                }
            }
        }
        else if (source.StartsWith("FixedAssets.", StringComparison.Ordinal))
        {
            var version = await db.AssetOperations.Where(x => x.CompanyId == company && x.Id == id).Select(x => x.Version).SingleAsync(ct);
            direct = await Load(company, DimensionSource.Asset, id, version, ct);
        }
        else if (source == RecognitionRules.Source)
        {
            var version = await (from row in db.RecognitionOccurrences join schedule in db.RecognitionSchedules on row.ScheduleId equals schedule.Id
                where row.CompanyId == company && schedule.CompanyId == company && row.Id == id select schedule.Version).SingleAsync(ct);
            direct = await Load(company, DimensionSource.Recognition, id, version, ct);
        }
        else if (source is "FX.Invoice" or "FX.Bill" or "FX.Credit")
        {
            var version = await db.FxOperations.Where(x => x.CompanyId == company && x.Id == id).Select(x => x.Version).SingleAsync(ct);
            direct = await Load(company, DimensionSource.ForeignExchange, id, version, ct);
        }
        return (direct, inherited);

        void AddForAccount(Guid? account, EffectiveDimension[] dimensions)
        {
            for (var i = 0; i < request.Lines.Count; i++)
                if (request.Lines[i].AccountId == account)
                {
                    if (direct.TryGetValue(i + 1, out var explicitTags) &&
                        dimensions.Any(x => explicitTags.Any(y => y.DefinitionId == x.DefinitionId && y.ValueId != x.ValueId)))
                        throw Conflict("The posting cannot override the linked procurement or sales attribution.");
                    inherited[i + 1] = dimensions;
                }
        }
    }

    private async Task<EffectiveDimension[]> TradeLineTags(Guid company, Guid lineId, CancellationToken ct)
    {
        var number = await db.TradeLines.Where(x => x.CompanyId == company && x.Id == lineId)
            .Select(x => new { x.DocumentId, x.Number }).SingleOrDefaultAsync(ct);
        if (number is null) return [];
        var document = await db.TradeDocuments.AsNoTracking().Include(x => x.Lines)
            .SingleAsync(x => x.CompanyId == company && x.Id == number.DocumentId, ct);
        if (document.JournalId is not { } journal || !DimensionBindings.CommercialOrdinals(document.Lines).TryGetValue(number.Number, out var ordinal))
            return [];
        return (await Original(company, journal, DimensionOrigin.Source, ct)).GetValueOrDefault(ordinal) ?? [];
    }

    private async Task<Dictionary<int, EffectiveDimension[]>> Load(Guid company, DimensionSource source, Guid id, Guid? version, CancellationToken ct)
    {
        var row = await bindings.Latest(company, source, id, version, ct);
        return Read(row).ToDictionary(x => x.Number, x => ToTags(x.Values, row?.Id,
            source == DimensionSource.Procurement ? DimensionOrigin.Source : DimensionOrigin.Explicit));
    }

    private static EffectiveDimension[] ToTags(IEnumerable<DimensionPick> picks, Guid? binding, DimensionOrigin origin) =>
        picks.Select(x => new EffectiveDimension(x.DefinitionId, x.ValueId, null, binding,
            x.ValueId.HasValue ? origin : DimensionOrigin.Unassigned)).ToArray();
}
