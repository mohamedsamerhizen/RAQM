using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.DTOs.Inventory;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Accruals;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Accruals;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.MultiCurrency;
using static RAQM.Api.Helpers.FinancialDimensions.DimensionMath;
using static RAQM.Api.Services.FinancialDimensions.DimensionRegister;

namespace RAQM.Api.Services.FinancialDimensions;

public sealed class DimensionBindings(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions)
{
    private sealed record Source(Guid Version, IReadOnlySet<int> Lines);

    public Task<DimensionBinding> Attach(Guid actor, Guid org, Guid company, DimensionSource source, Guid id,
        DimensionBindingRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var subject = await Editable(company, source, id, token);
            if (subject.Version != input.SourceVersion) throw Conflict("Source version changed.");
            var prior = await Latest(company, source, id, subject.Version, token);
            if ((prior?.Revision ?? 0) != input.ExpectedRevision) throw Conflict("Dimension binding changed.");
            if (input.Lines is null || input.Lines.Count > 1000 || input.Lines.Any(x => x is null || !subject.Lines.Contains(x.Number) ||
                x.Values is null || x.Values.Count > 20 || x.Values.Any(v => v is null) ||
                x.Values.Select(v => v.DefinitionId).Distinct().Count() != x.Values.Count) ||
                input.Lines.Select(x => x.Number).Distinct().Count() != input.Lines.Count)
                throw Bad("Each eligible source line may select one value per dimension.");
            var picks = input.Lines.SelectMany(x => x.Values).Distinct().ToArray();
            var values = await db.DimensionValues.AsNoTracking().Where(x => x.CompanyId == company).ToDictionaryAsync(x => x.Id, token);
            var definitions = await db.DimensionDefinitions.AsNoTracking().Where(x => x.CompanyId == company).Select(x => x.Id).ToListAsync(token);
            if (picks.Any(x => !definitions.Contains(x.DefinitionId) || x.ValueId is { } value &&
                (!values.TryGetValue(value, out var selected) || selected.DefinitionId != x.DefinitionId)))
                throw Bad("Dimension selections must match company definitions and values.");
            var row = Scoped(scope, new DimensionBinding { Source = source, SourceId = id, SourceVersion = subject.Version,
                Revision = 1 + input.ExpectedRevision, ActorId = actor, ValuesJson = JsonSerializer.Serialize(input.Lines),
                Reason = AccountingRules.Text(input.Reason, 500, "Attribution reason") });
            db.DimensionBindings.Add(row);
            access.Audit(scope, actor, id, "DimensionBindingPrepared", row.Reason);
            return row;
        }, ct);

    public async Task<IReadOnlyList<DimensionBinding>> History(Guid actor, Guid org, Guid company, DimensionSource source, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.DimensionBindings.AsNoTracking().Where(x => x.CompanyId == company && x.Source == source && x.SourceId == id)
            .OrderBy(x => x.SourceVersion).ThenBy(x => x.Revision).ToListAsync(ct);
    }

    internal Task<DimensionBinding?> Latest(Guid company, DimensionSource kind, Guid id, Guid? version, CancellationToken ct) =>
        db.DimensionBindings.AsNoTracking().Where(x => x.CompanyId == company && x.Source == kind && x.SourceId == id &&
            (version == null || x.SourceVersion == version)).OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);

    private async Task<Source> Editable(Guid company, DimensionSource source, Guid id, CancellationToken ct)
    {
        switch (source)
        {
            case DimensionSource.Manual:
                var journal = await db.Journals.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == company &&
                    x.Id == id && x.SourceModule == "ManualJournal" && x.State == JournalState.Draft, ct);
                if (journal is not null) return new(journal.Version, journal.Lines.Select(x => x.Number).ToHashSet());
                break;
            case DimensionSource.Trading:
                var document = await db.TradeDocuments.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x =>
                    x.CompanyId == company && x.Id == id && x.State == TradeState.Approved &&
                    (x.Kind == TradeKind.Invoice || x.Kind == TradeKind.Bill || x.Kind == TradeKind.CustomerCredit || x.Kind == TradeKind.SupplierCredit), ct);
                if (document is not null) return new(document.Version, document.Lines.Where(x => x.NetAmount > 0).Select(x => x.Number).ToHashSet());
                break;
            case DimensionSource.Procurement:
                var purchase = await (from line in db.ProcurementLines join doc in db.ProcurementDocuments on line.DocumentId equals doc.Id
                    where line.CompanyId == company && doc.CompanyId == company && line.Id == id && doc.State == ProcurementState.Draft
                    select doc.Version).SingleOrDefaultAsync(ct);
                if (purchase != Guid.Empty) return new(purchase, new HashSet<int> { 1 });
                break;
            case DimensionSource.Stock:
                var stock = await db.StockOperations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id && x.State == StockState.Approved, ct);
                if (stock is not null) return new(stock.Version, await Nominal(company, JsonSerializer.Deserialize<StockPlan>(stock.PlanJson!)!.Lines, ct, stock.Kind == StockKind.Receipt));
                break;
            case DimensionSource.Asset:
                var asset = await db.AssetOperations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id && x.State == AssetOperationState.Approved, ct);
                if (asset is not null) return new(asset.Version, await Nominal(company, JsonSerializer.Deserialize<AssetPlan>(asset.PlanJson!)!.Lines, ct,
                    asset.Kind is AssetOperationKind.Acquisition or AssetOperationKind.Improvement));
                break;
            case DimensionSource.Recognition:
                var occurrence = await db.RecognitionOccurrences.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                    x.Id == id && x.JournalId == null, ct);
                if (occurrence is not null)
                {
                    var schedule = await db.RecognitionSchedules.AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == occurrence.ScheduleId, ct);
                    if (schedule.State == RecognitionState.Approved)
                        return new(schedule.Version, await Nominal(company, RecognitionRules.Lines(schedule, occurrence), ct));
                }
                break;
            case DimensionSource.ForeignExchange:
                var fx = await db.FxOperations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id &&
                    x.State == FxState.Approved && (x.Kind == FxKind.Invoice || x.Kind == FxKind.Bill || x.Kind == FxKind.Credit), ct);
                if (fx is not null) return new(fx.Version, await Nominal(company, FxQueries.Plan(fx).Lines, ct));
                break;
        }
        throw Missing("No eligible source is available for attribution in this company.");
    }

    private async Task<IReadOnlySet<int>> Nominal(Guid company, IReadOnlyList<JournalDraftLine> lines, CancellationToken ct, bool capitalization = false)
    {
        var ids = lines.Select(x => x.AccountId).ToArray();
        var accounts = (await db.Accounts.Where(x => x.CompanyId == company && ids.Contains(x.Id) &&
            (x.Classification == AccountClass.Revenue || x.Classification == AccountClass.Expense))
            .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        var fx = await db.FxAccounts.Where(x => x.CompanyId == company && ids.Contains(x.AccountId)).Select(x => x.AccountId).ToListAsync(ct);
        accounts.ExceptWith(fx);
        if (capitalization)
        {
            accounts.UnionWith(await db.InventoryAccounts.Where(x => x.CompanyId == company && ids.Contains(x.AccountId) &&
                x.Role == InventoryRole.Inventory).Select(x => x.AccountId).ToListAsync(ct));
            accounts.UnionWith(await db.AssetControls.Where(x => x.CompanyId == company && ids.Contains(x.AccountId) &&
                x.Role == AssetControlRole.Cost).Select(x => x.AccountId).ToListAsync(ct));
        }
        return lines.Select((line, index) => (line.AccountId, Number: index + 1)).Where(x => accounts.Contains(x.AccountId))
            .Select(x => x.Number).ToHashSet();
    }

    internal static Dictionary<int, int> CommercialOrdinals(IEnumerable<TradeDocumentLine> lines)
    {
        var result = new Dictionary<int, int>();
        var ordinal = 2;
        foreach (var line in lines.OrderBy(x => x.Number))
        {
            if (line.NetAmount > 0) result.Add(line.Number, ordinal++);
            if (line.TaxAmount > 0) ordinal++;
        }
        return result;
    }
}
