using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FixedAssets;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.FixedAssets;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.FixedAssets;

public sealed class AssetRegisterService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    AssetPortfolio portfolio, TimeProvider clock) : IAssetRegister
{
    public Task<AssetCategory> CreateCategory(Guid actor, Guid org, Guid company, CategoryRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            CurrencyPolicy.Validate(request.Threshold, CurrencyPolicy.Units(scope.BaseCurrency));
            var code = AccountingRules.Text(request.Code, 40, "Category code").ToUpperInvariant();
            if (await db.AssetCategories.AnyAsync(x => x.CompanyId == company && x.Code == code, token))
                throw new ApiException(409, "Category code already exists.", "asset_category");
            var maps = new List<(Guid Id, AssetControlRole Role, bool Contra)>
            {
                (request.CostId, AssetControlRole.Cost, false), (request.AccumulatedDepreciationId, AssetControlRole.AccumulatedDepreciation, true),
                (request.AccumulatedImpairmentId, AssetControlRole.AccumulatedImpairment, true)
            };
            if (request.ClearingId is { } clearing) maps.Add((clearing, AssetControlRole.Clearing, false));
            var ids = maps.Select(x => x.Id).ToArray();
            if (ids.Distinct().Count() != ids.Length || await db.AssetControls.AnyAsync(x => x.CompanyId == company && ids.Contains(x.AccountId), token) ||
                await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == company && ids.Contains(x.LedgerAccountId), token) ||
                await db.InventoryAccounts.AnyAsync(x => x.CompanyId == company && ids.Contains(x.AccountId), token) ||
                await db.TradingAccounts.AnyAsync(x => x.CompanyId == company && ids.Contains(x.AccountId), token))
                throw new ApiException(409, "Asset control accounts must be dedicated and distinct.", "asset_mapping");
            foreach (var map in maps) await Account(company, map.Id, AccountClass.Asset, map.Contra, token);
            await Account(company, request.DepreciationExpenseId, AccountClass.Expense, false, token);
            await Account(company, request.ImpairmentLossId, AccountClass.Expense, false, token);
            await Account(company, request.LossId, AccountClass.Expense, false, token);
            await Account(company, request.GainId, AccountClass.Revenue, false, token);
            var category = new AssetCategory { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, CreatedBy = actor,
                Code = code, Name = AccountingRules.Text(request.Name, 160, "Category name"), Depreciable = request.Depreciable,
                MandatoryClose = request.MandatoryClose, Threshold = request.Threshold, DepreciationExpenseId = request.DepreciationExpenseId,
                ImpairmentLossId = request.ImpairmentLossId, GainId = request.GainId, LossId = request.LossId };
            category.Controls = maps.Select(x => new AssetControlAccount { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId,
                CategoryId = category.Id, AccountId = x.Id, Role = x.Role }).ToList();
            db.AssetCategories.Add(category);
            access.Audit(scope, actor, category.Id, "FixedAssetCategoryCreated", code);
            return category;
        }, ct);

    public async Task<IReadOnlyList<AssetCategory>> Categories(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.AssetCategories.AsNoTracking().Include(x => x.Controls).Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct);
    }

    public Task<FixedAsset> Register(Guid actor, Guid org, Guid company, RegisterAssetRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var category = await db.AssetCategories.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == request.CategoryId, token)
                ?? throw new ApiException(404, "Asset category not found.", "asset_category");
            if (request.AcquisitionDate.Year < 1900 || request.AcquisitionDate > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) ||
                request.Components is null || request.Components.Count is < 1 or > 50 || request.Components.Any(x => x is null))
                throw new ApiException(400, "Supply a valid acquisition date and one to fifty components.", "asset_components");
            var code = AccountingRules.Text(request.Code, 40, "Asset code").ToUpperInvariant();
            if (await db.FixedAssets.AnyAsync(x => x.CompanyId == company && x.Code == code, token))
                throw new ApiException(409, "Asset code already exists.", "asset_code");
            if (request.SupplierId.HasValue && !await db.TradingParties.AnyAsync(x => x.CompanyId == company && x.Id == request.SupplierId &&
                x.Kind == PartyKind.Supplier, token))
                throw new ApiException(400, "Supplier must belong to this company.", "asset_supplier");
            var assignment = await Assignment(scope, request.Assignment, token);
            var asset = new FixedAsset { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, CreatedBy = actor,
                CategoryId = category.Id, Code = code, Name = AccountingRules.Text(request.Name, 160, "Asset name"),
                Description = AccountingRules.Text(request.Description, 1000, "Asset description"), Currency = scope.BaseCurrency,
                AcquisitionDate = request.AcquisitionDate, SupplierId = request.SupplierId,
                PurchaseEvidence = AccountingRules.Text(request.PurchaseEvidence, 500, "Purchase evidence"), AssignmentJson = JsonSerializer.Serialize(assignment) };
            var units = CurrencyPolicy.Units(scope.BaseCurrency);
            foreach (var row in request.Components)
            {
                foreach (var amount in new[] { row.Cost, row.Residual, row.OpeningDepreciation, row.OpeningImpairment }) CurrencyPolicy.Validate(amount, units);
                if (row.Cost == 0 || row.Residual > row.Cost || row.OpeningDepreciation + row.OpeningImpairment > row.Cost ||
                    (category.Depreciable ? row.LifeMonths is < 0 or > 1200 : row.LifeMonths != 0 || row.OpeningDepreciation != 0))
                    throw new ApiException(400, "Invalid component cost, residual, accumulated amounts or useful life.", "asset_components");
                asset.Components.Add(new() { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, AssetId = asset.Id,
                    Number = asset.Components.Count + 1, Name = AccountingRules.Text(row.Name, 160, "Component name"), ProposedCost = row.Cost,
                    InitialResidual = row.Residual, LifeMonths = row.LifeMonths, OpeningDepreciation = row.OpeningDepreciation, OpeningImpairment = row.OpeningImpairment });
            }
            CurrencyPolicy.Validate(asset.Components.Sum(x => x.ProposedCost), units);
            db.FixedAssets.Add(asset);
            access.Audit(scope, actor, asset.Id, "FixedAssetRegistered", code);
            return asset;
        }, ct);

    internal async Task<AssetAssignment> Assignment(Company company, AssetAssignment value, CancellationToken ct)
    {
        if (value is null || !Enum.IsDefined(value.PhysicalStatus) || value.PhysicalStatus == PhysicalStatus.Retired)
            throw new ApiException(400, "A valid physical assignment is required.", "asset_assignment");
        foreach (var text in new[] { value.Branch, value.Department, value.CostCenter }) AccountingRules.OptionalText(text, 160);
        if (value.CustodianId.HasValue && !await db.OrganizationMembers.AnyAsync(x => x.TenantId == company.TenantId &&
            x.OrganizationId == company.OrganizationId && x.UserId == value.CustodianId &&
            (x.Role == OrganizationRole.Owner || db.CompanyMembers.Any(m => m.CompanyId == company.Id && m.UserId == x.UserId)), ct))
            throw new ApiException(400, "Custodian must be a current company member.", "asset_custodian");
        return value with { Location = AccountingRules.Text(value.Location, 160, "Location") };
    }

    internal async Task<LedgerAccount> Account(Guid company, Guid id, AccountClass type, bool contra, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct);
        return account is { IsActive: true, IsPosting: true } && account.Classification == type && account.IsContra == contra ? account
            : throw new ApiException(400, "An eligible company posting account of the required class and normal side is needed.", "asset_mapping");
    }

    public async Task<PageResult<AssetPosition>> Search(Guid actor, Guid org, Guid company, DateOnly at, Guid? category, AssetStatus? status,
        string? search, string? branch, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        ValidateDates(at, at);
        AccountingRules.OptionalText(search, 160);
        AccountingRules.OptionalText(branch, 160);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var snapshot = await portfolio.Load(company, at, ct);
        var rows = snapshot.Assets.Where(x => x.AcquisitionDate <= at && (!category.HasValue || x.CategoryId == category) &&
            (string.IsNullOrWhiteSpace(search) || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.Code.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Select(x => snapshot.Position(x, at)).Where(x => (!status.HasValue || x.Status == status) &&
                (branch is null || string.Equals(branch, x.Assignment.Branch, StringComparison.OrdinalIgnoreCase))).ToArray();
        await tx.CommitAsync(ct);
        return new(rows.Skip((page - 1) * size).Take(size).ToArray(), rows.Length, page, size);
    }

    public async Task<AssetPeriodReport> Report(Guid actor, Guid org, Guid company, DateOnly? from, DateOnly? to, Guid? period, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        if (period.HasValue)
        {
            if (from.HasValue || to.HasValue) throw new ApiException(400, "Select a period or explicit dates.", "asset_dates");
            var selected = await db.Periods.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == period, ct)
                ?? throw new ApiException(404, "Fiscal period not found.", "period_not_found");
            (from, to) = (selected.StartDate, selected.EndDate);
        }
        if (!from.HasValue || !to.HasValue) throw new ApiException(400, "Supply both dates or a fiscal period.", "asset_dates");
        ValidateDates(from.Value, to.Value);
        var snapshot = await portfolio.Load(company, to.Value, ct);
        var operations = snapshot.Operations.Where(x => x.Date >= from && x.State == AssetOperationState.Posted).ToDictionary(x => x.Id);
        var entries = snapshot.Movements.Where(x => operations.ContainsKey(x.OperationId)).ToArray();
        var positions = snapshot.Assets.Where(x => x.AcquisitionDate <= to).Select(x => snapshot.Position(x, to.Value)).ToArray();
        var categories = snapshot.Categories.ToDictionary(x => x.Id);
        IReadOnlyList<AssetSummary> Group(Func<AssetPosition, string> group) => positions.GroupBy(group).OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new AssetSummary(x.Key, x.Count(), x.Sum(a => a.Cost), x.Sum(a => a.Depreciation), x.Sum(a => a.Impairment), x.Sum(a => a.NetBookValue))).ToArray();
        var sold = operations.Values.Where(x => x.Kind is AssetOperationKind.Sale or AssetOperationKind.WriteOff).ToArray();
        var disposalResult = sold.Sum(x =>
        {
            var request = AssetSnapshot.Request(x);
            var carrying = AssetSnapshot.Plan(x).Movements.Sum(m => -m.Cost + m.Depreciation + m.Impairment - m.DepreciationCharge);
            return request.Amount - request.DisposalCosts - carrying;
        });
        var report = new AssetPeriodReport(from.Value, to.Value,
            entries.Where(x => operations[x.OperationId].Kind is AssetOperationKind.Acquisition or AssetOperationKind.Opening).Sum(x => x.Cost),
            entries.Where(x => operations[x.OperationId].Kind == AssetOperationKind.Improvement).Sum(x => x.Cost),
            entries.Sum(x => x.DepreciationCharge),
            entries.Where(x => operations[x.OperationId].Kind is AssetOperationKind.Impairment or AssetOperationKind.Recovery).Sum(x => x.Impairment),
            -entries.Where(x => operations[x.OperationId].Kind is AssetOperationKind.Sale or AssetOperationKind.WriteOff).Sum(x => x.Cost),
            sold.Sum(x => AssetSnapshot.Request(x).Amount), disposalResult,
            operations.Values.Where(x => x.Kind == AssetOperationKind.Repair).Sum(x => AssetSnapshot.Request(x).Amount),
            positions.Where(x => x.Status == AssetStatus.Approved).Select(x => x.Id).ToArray(),
            positions.Where(x => x.Status == AssetStatus.InService && categories[x.CategoryId].Depreciable &&
                x.Components.All(c => c.RemainingMonths == 0 || c.NetBookValue <= c.Residual)).Select(x => x.Id).ToArray(),
            positions.Where(x => x.Status == AssetStatus.InService && x.Components.Any(c => c.RemainingMonths is > 0 and <= 3)).Select(x => x.Id).ToArray(),
            Group(x => categories[x.CategoryId].Code), Group(x => x.Assignment.Branch ?? "Unassigned"), Group(x => x.Assignment.Department ?? "Unassigned"),
            await portfolio.Reconcile(company, to.Value, snapshot, ct));
        await tx.CommitAsync(ct);
        return report;
    }

    public async Task<IReadOnlyList<AssetMovement>> Movements(Guid actor, Guid org, Guid company, Guid asset, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        if (!await db.FixedAssets.AnyAsync(x => x.CompanyId == company && x.Id == asset, ct)) throw new ApiException(404, "Asset not found.", "asset_missing");
        return await (from m in db.AssetMovements.AsNoTracking() join c in db.AssetComponents on m.ComponentId equals c.Id
            where m.CompanyId == company && c.CompanyId == company && c.AssetId == asset
            orderby m.Date, m.Sequence, c.Number select m).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DepreciationForecast>> Schedule(Guid actor, Guid org, Guid company, Guid assetId,
        DateOnly at, DateOnly through, CancellationToken ct)
    {
        ValidateDates(at, through);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var snapshot = await portfolio.Load(company, at, ct);
        var asset = snapshot.Assets.SingleOrDefault(x => x.Id == assetId) ?? throw AssetPlanningService.Missing();
        var forecast = new List<DepreciationForecast>();
        if (snapshot.Categories.Single(x => x.Id == asset.CategoryId).Depreciable && snapshot.Position(asset, at).Status == AssetStatus.InService)
        {
            var periods = await db.Periods.AsNoTracking().Where(x => x.CompanyId == company && x.EndDate >= at && x.EndDate <= through)
                .OrderBy(x => x.StartDate).Take(121).ToListAsync(ct);
            if (periods.Count > 120) throw AssetPlanningService.Bad("Forecast up to 120 fiscal periods.");
            foreach (var component in asset.Components.OrderBy(x => x.Number))
            {
                var state = snapshot.Balance(component);
                foreach (var period in periods)
                {
                    if (snapshot.History(component.Id).Any(x => x.DepreciationPeriodId == period.Id) ||
                        !AssetPlanningService.Started(asset, period, out var available)) continue;
                    var (charge, shadow, used) = AssetMath.Depreciate(state,
                        AssetMath.Fraction(period.StartDate, period.EndDate, available, period.EndDate), asset.Currency);
                    if (used == 0) break;
                    state = state with { Depreciation = state.Depreciation + charge, ShadowDepreciation = state.ShadowDepreciation + shadow,
                        RemainingMonths = state.RemainingMonths - used };
                    forecast.Add(new(component.Id, period.Id, period.EndDate, charge, state.NetBookValue, state.RemainingMonths));
                }
            }
        }
        await tx.CommitAsync(ct);
        return forecast;
    }

    public async Task<IReadOnlyList<AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid entity, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.AccountingAudit.AsNoTracking().Where(x => x.CompanyId == company && x.EntityId == entity).OrderBy(x => x.At)
            .Select(x => new AuditResponse(x.Id, x.EntityId, x.Action, x.ActorId, x.At, x.Reason)).ToListAsync(ct);
    }

    private static void ValidateDates(DateOnly from, DateOnly to)
    {
        if (from.Year < 1900 || to < from) throw new ApiException(400, "Choose an ordered date range from 1900 onward.", "asset_dates");
    }
}
