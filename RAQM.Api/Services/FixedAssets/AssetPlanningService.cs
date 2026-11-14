using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FixedAssets;
using RAQM.Api.Services.FinancialDimensions;

namespace RAQM.Api.Services.FixedAssets;

public sealed class AssetPlanningService(RaqmDbContext db, AssetPortfolio portfolio, AssetRegisterService register, TimeProvider clock)
{
    public async Task<AssetPlan> Build(Company company, AssetOperationRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Kind) || request.Date.Year < 1900 || request.Date > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            throw Bad("Select a valid operation and an accounting date through today.");
        var companyId = company.Id;
        var units = CurrencyPolicy.Units(company.BaseCurrency);
        CurrencyPolicy.Validate(request.Amount, units);
        CurrencyPolicy.Validate(request.DisposalCosts, units);
        if (request.Residual.HasValue) CurrencyPolicy.Validate(request.Residual.Value, units);
        if (request.ReplacesExistingComponent)
            throw Bad("Replacement that requires removing an existing component needs separate derecognition and is unsupported.");
        var periods = await db.Periods.AsNoTracking().Where(x => x.CompanyId == companyId && x.StartDate <= request.Date)
            .OrderBy(x => x.StartDate).ToListAsync(ct);
        var period = periods.SingleOrDefault(x => x.StartDate <= request.Date && x.EndDate >= request.Date)
            ?? throw Bad("The accounting date must belong to a fiscal period.");
        if (period.State != PeriodState.Open) throw Conflict("The asset accounting period is closed or locked.");
        if (await (from y in db.YearEndRecords join fy in db.FiscalYears on y.FiscalYearId equals fy.Id
            where y.CompanyId == companyId && y.Finalized && fy.EndDate >= request.Date select y.Id).AnyAsync(ct))
            throw Conflict("The asset accounting date is covered by a finalized fiscal year.");
        var snapshot = await portfolio.Load(companyId, request.Date, ct);
        if (request.Kind == AssetOperationKind.Depreciation)
        {
            if (request.AssetId.HasValue || request.PeriodId != period.Id || request.Date != period.EndDate)
                throw Bad("A depreciation run is company-wide and uses the selected fiscal period end.");
            if (await db.AssetOperations.AnyAsync(x => x.CompanyId == companyId && x.Kind == AssetOperationKind.Depreciation &&
                x.State == AssetOperationState.Posted && x.Date > request.Date, ct))
                throw Conflict("A later depreciation period already posted.");
            return Depreciation(snapshot, periods, period, company.BaseCurrency);
        }
        if (request.Kind == AssetOperationKind.Opening)
            return await Opening(companyId, request, snapshot, ct);

        var asset = snapshot.Assets.SingleOrDefault(x => x.Id == request.AssetId) ?? throw Missing();
        var category = snapshot.Categories.Single(x => x.Id == asset.CategoryId);
        var costId = Control(category, AssetControlRole.Cost);
        var depId = Control(category, AssetControlRole.AccumulatedDepreciation);
        var impId = Control(category, AssetControlRole.AccumulatedImpairment);
        if (request.Date < asset.AcquisitionDate || asset.DisposalDate.HasValue)
            throw Conflict("The operation predates acquisition or the asset is already derecognized.");
        if (request.Kind is not (AssetOperationKind.Transfer or AssetOperationKind.Repair) &&
            await db.AssetOperations.AnyAsync(x => x.CompanyId == companyId && x.Kind == AssetOperationKind.Depreciation &&
                x.State == AssetOperationState.Posted && x.Date >= request.Date, ct))
            throw Conflict("An approved run already posted through this date. Use an eligible later period.");
        if (request.Kind != AssetOperationKind.Transfer && await (from m in db.AssetMovements join c in db.AssetComponents on m.ComponentId equals c.Id
            where m.CompanyId == companyId && c.AssetId == asset.Id && m.Date > request.Date select m.Id).AnyAsync(ct))
            throw Conflict("A later asset movement is already posted.");
        if (request.Kind == AssetOperationKind.Acquisition)
        {
            if (asset.Status is not (AssetStatus.Draft or AssetStatus.Approved) || asset.RecognitionDate.HasValue)
                throw Conflict("The asset has already been capitalized.");
            var amount = asset.Components.Sum(x => x.ProposedCost);
            if (asset.Components.Any(x => x.OpeningDepreciation != 0 || x.OpeningImpairment != 0 || category.Depreciable && x.LifeMonths == 0) ||
                request.Amount != amount || amount < category.Threshold)
                throw Bad("New capitalization must meet the category threshold and equal the components, with valid new useful lives.");
            var funding = await Funding(companyId, request, category, asset.SupplierId, ct);
            return new(funding.Lines, asset.Components.OrderBy(x => x.Number).Select(x => new AssetMovementDraft(x.Id, Cost: x.ProposedCost)).ToArray(),
                funding.Sources, [asset.Id], "Acquisition");
        }
        if (request.Kind == AssetOperationKind.Repair)
        {
            var expense = request.RepairExpenseId ?? throw Bad("Select the repair expense account explicitly.");
            await register.Account(companyId, expense, AccountClass.Expense, false, ct);
            if (request.Amount == 0) throw Bad("Repairs require a positive amount.");
            if (request.SourceLines is { Count: > 0 })
            {
                var used = await BillSources(companyId, request, expense, asset.SupplierId, ct);
                return new([], [], used, [asset.Id], "Existing repair expense");
            }
            var cash = await Cash(companyId, request.OffsetAccountId, ct);
            return new([new(expense, request.Amount, 0, "Repair expense"), new(cash, 0, request.Amount, "Pay repair")], [], [], [asset.Id], "Repair expense");
        }
        if (asset.RecognitionDate is null || request.Date < asset.RecognitionDate)
            throw Conflict("Capitalize the asset before performing this operation.");
        if (request.Kind == AssetOperationKind.AvailableForUse)
        {
            if (asset.Status != AssetStatus.Capitalized || asset.AvailableDate.HasValue) throw Conflict("Availability is already recorded.");
            return new([], [], [], [asset.Id], "Available for use");
        }
        if (request.Kind == AssetOperationKind.Transfer)
        {
            await register.Assignment(company, request.Assignment ?? throw Bad("Supply the new physical assignment."), ct);
            if (await db.AssetOperations.AnyAsync(x => x.CompanyId == companyId && x.AssetId == asset.Id && x.Kind == AssetOperationKind.Transfer &&
                x.State == AssetOperationState.Posted && x.Date > request.Date, ct))
                throw Conflict("A later administrative transfer is already posted.");
            return new([], [], [], [asset.Id], "Administrative transfer");
        }
        if (request.Kind is AssetOperationKind.Sale or AssetOperationKind.WriteOff)
            return await Disposal(company, request, snapshot, asset, category, periods, period, ct);

        if (request.Date != period.StartDate)
            throw Bad("Improvements, estimates and impairment changes take effect at the start of a fiscal period.");
        var component = asset.Components.SingleOrDefault(x => x.Id == request.ComponentId)
            ?? throw Bad("Select a component of this asset.");
        PriorPeriods(snapshot, asset, component, periods, period);
        var balance = snapshot.Balance(component);
        switch (request.Kind)
        {
            case AssetOperationKind.Estimate:
                Terms(category, request, balance.NetBookValue, true);
                return new([], [new(component.Id, RemainingOverride: request.RemainingMonths, ResidualOverride: request.Residual)],
                    [], [asset.Id], "Prospective estimate");
            case AssetOperationKind.Improvement:
                if (request.Amount == 0 || request.Amount < category.Threshold) throw Bad("Improvement must meet the capitalization threshold.");
                Terms(category, request, balance.NetBookValue + request.Amount, category.Depreciable && balance.RemainingMonths == 0);
                var funding = await Funding(companyId, request, category, asset.SupplierId, ct);
                return new(funding.Lines, [new(component.Id, Cost: request.Amount, RemainingOverride: request.RemainingMonths,
                    ResidualOverride: request.Residual)], funding.Sources, [asset.Id], "Capital improvement");
            case AssetOperationKind.Impairment:
            case AssetOperationKind.Recovery:
                var recovery = request.Kind == AssetOperationKind.Recovery;
                var amount = recovery ? AssetMath.Recovery(balance, request.Amount) : Math.Max(0, balance.NetBookValue - request.Amount);
                if (amount == 0) throw Bad("The assessed recoverable amount does not support a financial change.");
                return new(recovery ? [new(impId, amount, 0, "Reverse accumulated impairment"), new(category.ImpairmentLossId, 0, amount, "Impairment recovery")] :
                    [new(category.ImpairmentLossId, amount, 0, "Impairment loss"), new(impId, 0, amount, "Accumulated impairment")],
                    [new(component.Id, Impairment: recovery ? -amount : amount)], [], [asset.Id], recovery ? "Capped impairment recovery" : "Impairment assessment");
            default:
                throw Bad("Unsupported fixed asset operation.");
        }
    }

    public static AssetPlan Depreciation(AssetSnapshot snapshot, IReadOnlyList<AccountingPeriod> periods, AccountingPeriod period, string currency)
    {
        var movements = new List<AssetMovementDraft>();
        var debits = new Dictionary<Guid, decimal>();
        var credits = new Dictionary<Guid, decimal>();
        var assets = new List<Guid>();
        var categories = snapshot.Categories.ToDictionary(x => x.Id);
        foreach (var asset in snapshot.Assets.OrderBy(x => x.Id))
        {
            var category = categories[asset.CategoryId];
            if (!category.Depreciable || !Started(asset, period, out var available)) continue;
            foreach (var component in asset.Components.OrderBy(x => x.Number))
            {
                if (snapshot.History(component.Id).Any(x => x.DepreciationPeriodId == period.Id)) continue;
                PriorPeriods(snapshot, asset, component, periods, period);
                var state = snapshot.Balance(component);
                var (charge, shadow, used) = AssetMath.Depreciate(state, AssetMath.Fraction(period.StartDate, period.EndDate, available, period.EndDate), currency);
                if (used == 0) continue;
                movements.Add(new(component.Id, period.Id, Depreciation: charge, ShadowDepreciation: shadow, Units: used, DepreciationCharge: charge));
                debits[category.DepreciationExpenseId] = debits.GetValueOrDefault(category.DepreciationExpenseId) + charge;
                var id = Control(category, AssetControlRole.AccumulatedDepreciation);
                credits[id] = credits.GetValueOrDefault(id) + charge;
                assets.Add(asset.Id);
            }
        }
        var lines = debits.Where(x => x.Value > 0).OrderBy(x => x.Key).Select(x => new JournalDraftLine(x.Key, x.Value, 0, "Depreciation expense"))
            .Concat(credits.Where(x => x.Value > 0).OrderBy(x => x.Key).Select(x => new JournalDraftLine(x.Key, 0, x.Value, "Accumulated depreciation"))).ToArray();
        return new(lines, movements, [], assets.Distinct().ToArray(), "Fiscal period depreciation");
    }

    internal static bool Started(FixedAsset asset, AccountingPeriod period, out DateOnly available)
    {
        available = default;
        if (asset.AvailableDate is not { } inService || asset.RecognitionDate is not { } recognized ||
            (asset.DisposalDate.HasValue && asset.DisposalDate <= period.EndDate)) return false;
        available = inService > recognized ? inService : recognized;
        return available <= period.EndDate;
    }

    internal static bool Due(AssetSnapshot snapshot, FixedAsset asset, AssetComponent component, AccountingPeriod period)
    {
        if (!Started(asset, period, out _) || snapshot.History(component.Id).Any(x => x.DepreciationPeriodId == period.Id)) return false;
        var balance = AssetMath.Balance(component, snapshot.History(component.Id).Where(x => x.Date <= period.EndDate));
        return balance.Cost > 0 && balance.RemainingMonths > 0;
    }

    private static void PriorPeriods(AssetSnapshot snapshot, FixedAsset asset, AssetComponent component,
        IReadOnlyList<AccountingPeriod> periods, AccountingPeriod period)
    {
        if (periods.Any(x => x.EndDate < period.StartDate && Due(snapshot, asset, component, x)))
            throw Conflict($"Earlier depreciation is due for asset {asset.Code}.");
        if (!Started(asset, period, out var available) || snapshot.Balance(component).RemainingMonths == 0) return;
        var cursor = available;
        foreach (var p in periods.Where(x => x.EndDate >= available && x.StartDate <= period.StartDate))
        {
            if (p.StartDate > cursor) throw Conflict($"A fiscal calendar gap affects asset {asset.Code}.");
            cursor = p.EndDate.AddDays(1);
        }
    }

    private async Task<AssetPlan> Opening(Guid company, AssetOperationRequest request, AssetSnapshot snapshot, CancellationToken ct)
    {
        var ids = request.OpeningAssets?.Distinct().Order().ToArray() ?? [];
        if (ids.Length is < 1 or > 100 || ids.Length != request.OpeningAssets!.Count || request.AssetId.HasValue ||
            request.HistoricalAvailableDate > request.Date || request.OpeningJournalId is null)
            throw Bad("Supply an opening journal and a distinct batch of assets. The historical availability date is optional.");
        var assets = ids.Select(id => snapshot.Assets.SingleOrDefault(x => x.Id == id) ?? throw Missing()).ToArray();
        if (assets.Any(x => x.RecognitionDate.HasValue || x.AcquisitionDate > request.Date ||
            (request.HistoricalAvailableDate.HasValue && request.HistoricalAvailableDate < x.AcquisitionDate)))
            throw Conflict("Opening assets must be uncapitalized and dates must reflect the historical evidence.");
        if (await db.AssetOperations.AnyAsync(x => x.CompanyId == company && x.Kind == AssetOperationKind.Depreciation &&
            x.State == AssetOperationState.Posted && x.Date >= request.Date, ct))
            throw Conflict("Use a conversion cutoff later than existing depreciation runs.");
        var openingAccounts = await (from l in db.JournalLines join j in db.Journals on l.JournalId equals j.Id
            where l.CompanyId == company && j.CompanyId == company && j.Id == request.OpeningJournalId && j.SourceModule == "OpeningBalance" &&
                j.State == JournalState.Posted && j.PostingDate <= request.Date && l.Debit > 0 select l.AccountId).ToListAsync(ct);
        var reconciled = await portfolio.Reconcile(company, request.Date, snapshot, ct);
        foreach (var group in assets.GroupBy(x => x.CategoryId))
        {
            var components = group.SelectMany(x => x.Components).ToArray();
            var category = snapshot.Categories.Single(x => x.Id == group.Key);
            if (!openingAccounts.Contains(Control(category, AssetControlRole.Cost)))
                throw Bad("The posted opening journal must include the category cost account.");
            foreach (var row in reconciled.Where(x => x.CategoryId == group.Key && x.Role != AssetControlRole.Clearing))
            {
                var signed = row.Role switch
                {
                    AssetControlRole.Cost => components.Sum(x => x.ProposedCost),
                    AssetControlRole.AccumulatedDepreciation => -components.Sum(x => x.OpeningDepreciation),
                    _ => -components.Sum(x => x.OpeningImpairment)
                };
                if (row.Difference != signed) throw Conflict($"Opening batch does not reconcile {category.Code} {row.Role} to the posted ledger.");
            }
        }
        return new([], assets.SelectMany(a => a.Components.OrderBy(c => c.Number)).Select(c =>
            new AssetMovementDraft(c.Id, Cost: c.ProposedCost, Depreciation: c.OpeningDepreciation,
                Impairment: c.OpeningImpairment, ShadowDepreciation: c.OpeningDepreciation)).ToArray(), [], ids, "Register-only opening");
    }

    private async Task<(IReadOnlyList<JournalDraftLine> Lines, IReadOnlyList<Guid> Sources)> Funding(Guid company,
        AssetOperationRequest request, AssetCategory category, Guid? supplier, CancellationToken ct)
    {
        var cost = Control(category, AssetControlRole.Cost);
        switch (request.Funding)
        {
            case CapitalFunding.Direct:
                if (request.SourceLines is { Count: > 0 }) throw Bad("A direct acquisition cannot reuse posted bill costs.");
                var offset = request.OffsetAccountId ?? throw Bad("Select the acquisition offset account.");
                var liability = await db.Accounts.AnyAsync(a => a.CompanyId == company && a.Id == offset &&
                    a.Classification == AccountClass.Liability && a.IsPosting && a.IsActive && !a.IsContra, ct);
                if (!liability) await Cash(company, offset, ct);
                return ([new(cost, request.Amount, 0, "Capitalize fixed asset"), new(offset, 0, request.Amount, "Acquisition consideration")], []);
            case CapitalFunding.ExistingCost:
                if (request.OffsetAccountId.HasValue) throw Bad("Existing bill cost has no new offset.");
                return ([], await BillSources(company, request, cost, supplier, ct));
            case CapitalFunding.ClearBill:
                if (request.OffsetAccountId.HasValue) throw Bad("Use the configured category clearing account.");
                var clearing = Control(category, AssetControlRole.Clearing);
                return ([new(cost, request.Amount, 0, "Capitalize cleared asset"), new(clearing, 0, request.Amount, "Clear posted bill cost")],
                    await BillSources(company, request, clearing, supplier, ct));
            default:
                throw Bad("Choose direct acquisition, existing GL cost or bill clearing.");
        }
    }

    private async Task<Guid[]> BillSources(Guid company, AssetOperationRequest request, Guid account, Guid? supplier, CancellationToken ct)
    {
        var ids = await UnusedSources(company, request.SourceLines, ct);
        var source = await (from l in db.JournalLines join j in db.Journals on l.JournalId equals j.Id
            join bill in db.TradeDocuments on j.Id equals bill.JournalId
            where l.CompanyId == company && j.CompanyId == company && bill.CompanyId == company && ids.Contains(l.Id) &&
                l.AccountId == account && l.Debit > 0 && l.Credit == 0 && j.PostingDate <= request.Date && j.State == JournalState.Posted &&
                bill.State == TradeState.Posted && bill.Kind == TradeKind.Bill && j.SourceModule == "AP.Bill" && (!supplier.HasValue || bill.PartyId == supplier)
            select new { l.Debit, l.Number, Bill = bill.Id }).ToListAsync(ct);
        if (source.Count != ids.Length || source.Sum(x => x.Debit) != request.Amount)
            throw Bad("Link entire posted AP bill net-cost journal lines on the correct account. Recoverable tax and supplier control are excluded.");
        var bills = source.Select(x => x.Bill).Distinct().ToArray();
        var commercial = await db.TradeLines.AsNoTracking().Where(x => x.CompanyId == company && bills.Contains(x.DocumentId)).ToListAsync(ct);
        var basis = commercial.GroupBy(x => x.DocumentId).SelectMany(lines =>
        {
            var ordinals = DimensionBindings.CommercialOrdinals(lines);
            return lines.Where(line => ordinals.TryGetValue(line.Number, out var ordinal) &&
                source.Any(claim => claim.Bill == line.DocumentId && claim.Number == ordinal));
        }).Select(x => x.Id).ToArray();
        if (basis.Length != source.Count || await (from credit in db.TradeDocuments join line in db.TradeLines
                on credit.Id equals line.DocumentId
            where credit.CompanyId == company && line.CompanyId == company && credit.Kind == TradeKind.SupplierCredit &&
                credit.State == TradeState.Posted && line.OriginalLineId.HasValue && basis.Contains(line.OriginalLineId.Value)
            select line.Id).AnyAsync(ct))
            throw Conflict("A supplier credit or missing line evidence affects the exact acquisition cost basis.");
        return ids;
    }

    private async Task<Guid[]> UnusedSources(Guid company, IReadOnlyList<Guid>? sources, CancellationToken ct)
    {
        if (sources is null || sources.Count is < 1 or > 100 || sources.Distinct().Count() != sources.Count)
            throw Bad("Link one to one hundred distinct whole source lines.");
        var ids = sources.Order().ToArray();
        if (await db.AssetSourceClaims.AnyAsync(x => x.CompanyId == company && ids.Contains(x.JournalLineId), ct))
            throw Conflict("A source journal line was already consumed by an asset operation.");
        return ids;
    }

    private async Task<AssetPlan> Disposal(Company company, AssetOperationRequest request, AssetSnapshot snapshot, FixedAsset asset,
        AssetCategory category, IReadOnlyList<AccountingPeriod> periods, AccountingPeriod period, CancellationToken ct)
    {
        var physical = request.PhysicalDisposalDate ?? request.Date;
        if (physical < period.StartDate || physical > request.Date || physical < asset.RecognitionDate ||
            (asset.AvailableDate.HasValue && physical < asset.AvailableDate) ||
            (request.Kind == AssetOperationKind.Sale ? request.Amount == 0 : request.Amount != 0))
            throw Bad("Disposal dates must be ordered in one fiscal period. Sales have proceeds and write-offs have none.");
        if (snapshot.Movements.Any(x => asset.Components.Any(c => c.Id == x.ComponentId) && x.Date > physical))
            throw Conflict("The physical disposal predates recognized component movements.");
        Guid? cash = null;
        Guid? consideration = null;
        Guid[] sources = [];
        if (request.SourceLines is { Count: > 0 })
        {
            if (request.Amount == 0) throw Bad("A write-off cannot reuse sale proceeds.");
            sources = await UnusedSources(company.Id, request.SourceLines, ct);
            var receipts = await (from l in db.JournalLines join j in db.Journals on l.JournalId equals j.Id
                join a in db.Accounts on l.AccountId equals a.Id
                where l.CompanyId == company.Id && j.CompanyId == company.Id && a.CompanyId == company.Id && sources.Contains(l.Id) &&
                    j.State == JournalState.Posted && j.SourceModule == "ManualJournal" && j.ReversesJournalId == null &&
                    j.PostingDate <= request.Date && a.Classification == AccountClass.Liability && !a.IsContra && l.Credit > 0 &&
                    !db.Journals.Any(r => r.CompanyId == company.Id && r.State == JournalState.Posted && r.ReversesJournalId == j.Id) &&
                    db.JournalLines.Any(c => c.JournalId == j.Id && c.CompanyId == company.Id && c.Debit > 0 &&
                        db.TreasuryAccounts.Any(t => t.CompanyId == company.Id && t.LedgerAccountId == c.AccountId && t.Kind != TreasuryAccountKind.PettyCash))
                select new { l.AccountId, l.Credit }).ToListAsync(ct);
            if (receipts.Count != sources.Length || receipts.Sum(x => x.Credit) != request.Amount ||
                receipts.Select(x => x.AccountId).Distinct().Count() != 1)
                throw Bad("Existing proceeds require unreversed receipt lines on one ordinary disposal liability clearing account.");
            consideration = receipts[0].AccountId;
            if (request.OffsetAccountId.HasValue && request.DisposalCosts == 0) throw Bad("Cash is already recorded for this disposal.");
        }
        else if (request.Amount > 0) consideration = cash = await Cash(company.Id, request.OffsetAccountId, ct);
        if (request.DisposalCosts > 0) cash ??= await Cash(company.Id, request.OffsetAccountId, ct);
        decimal gross = 0, dep = 0, imp = 0, charge = 0;
        var movements = new List<AssetMovementDraft>();
        foreach (var component in asset.Components.OrderBy(x => x.Number))
        {
            PriorPeriods(snapshot, asset, component, periods, period);
            var state = snapshot.Balance(component);
            var prorated = 0m;
            if (category.Depreciable && Started(asset, period, out var available))
            {
                if (snapshot.History(component.Id).Any(x => x.DepreciationPeriodId == period.Id))
                    throw Conflict("Use a later period after depreciation has posted.");
                prorated = AssetMath.Depreciate(state, AssetMath.Fraction(period.StartDate, period.EndDate, available, physical.AddDays(-1)), company.BaseCurrency).Charge;
            }
            gross += state.Cost;
            dep += state.Depreciation + prorated;
            imp += state.Impairment;
            charge += prorated;
            movements.Add(new(component.Id, Cost: -state.Cost, Depreciation: -state.Depreciation, Impairment: -state.Impairment,
                ShadowDepreciation: -state.ShadowDepreciation, RemainingOverride: 0, DepreciationCharge: prorated));
        }
        var cost = Control(category, AssetControlRole.Cost);
        var accumulated = Control(category, AssetControlRole.AccumulatedDepreciation);
        var lines = new List<JournalDraftLine>();
        if (charge > 0)
        {
            lines.Add(new(category.DepreciationExpenseId, charge, 0, "Depreciation to disposal"));
            lines.Add(new(accumulated, 0, charge, "Accumulate depreciation to disposal"));
        }
        lines.AddRange(AssetMath.Disposal(cost, accumulated, Control(category, AssetControlRole.AccumulatedImpairment), category.GainId,
            category.LossId, consideration, gross, dep, imp, request.Amount, cash, request.DisposalCosts));
        return new(lines, movements, sources, [asset.Id], sources.Length > 0 ? "Derecognize using existing cash receipt" : "Derecognize fixed asset");
    }

    private async Task<Guid> Cash(Guid company, Guid? account, CancellationToken ct)
    {
        if (account is not { } id || !await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == company && x.LedgerAccountId == id &&
            x.Active && x.Kind != TreasuryAccountKind.PettyCash, ct))
            throw Bad("Select an active registered company bank or cash GL account.");
        return id;
    }

    private static void Terms(AssetCategory category, AssetOperationRequest request, decimal carrying, bool required)
    {
        if (required && !request.RemainingMonths.HasValue && !request.Residual.HasValue ||
            request.Residual > carrying || request.RemainingMonths is < 1 or > 1200 ||
            (!category.Depreciable && request.RemainingMonths.HasValue))
            throw Bad("Supply eligible prospective remaining life or residual terms within the revised carrying amount.");
    }

    internal static Guid Control(AssetCategory category, AssetControlRole role) =>
        category.Controls.SingleOrDefault(x => x.Role == role)?.AccountId ?? throw Bad($"Category requires a {role} mapping.");
    internal static ApiException Conflict(string detail) => new(409, detail, "asset_state");
    internal static ApiException Bad(string detail) => new(400, detail, "asset_policy");
    internal static ApiException Missing() => new(404, "Fixed asset not found.", "asset_missing");
}
