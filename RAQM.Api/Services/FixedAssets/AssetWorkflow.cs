using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FixedAssets;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.FixedAssets;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.BudgetControl;

namespace RAQM.Api.Services.FixedAssets;

public sealed class AssetWorkflow(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    AssetPlanningService planning, AssetRegisterService register, IPostingEngine engine, TimeProvider clock,
    BudgetControlService budget, GovernanceGate? governance = null) : IAssetWorkflow
{
    public Task<AssetOperation> Prepare(Guid actor, Guid org, Guid company, AssetOperationRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var key = AccountingRules.Text(request.Key, 64, "Operation key");
            var reason = AccountingRules.Text(request.Reason, 500, "Business reason");
            var evidence = AccountingRules.Text(request.Evidence, 1000, "Source evidence");
            if (request.Assignment is not null) request = request with { Assignment = await register.Assignment(scope, request.Assignment, token) };
            request = request with { Key = key, Reason = reason, Evidence = evidence };
            var hash = TradeRules.Hash(request);
            var prior = await db.AssetOperations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.RequestKey == key, token);
            if (prior is not null)
            {
                if (prior.RequestHash != hash) throw AssetPlanningService.Conflict("The operation key was used for a different request.");
                return prior;
            }
            if (request.Kind == AssetOperationKind.Depreciation &&
                await db.AssetOperations.AnyAsync(x => x.CompanyId == company && x.Kind == request.Kind && x.PeriodId == request.PeriodId &&
                    x.State != AssetOperationState.Cancelled, token))
                throw AssetPlanningService.Conflict("A depreciation run already exists for the period.");
            if (request.Kind == AssetOperationKind.Acquisition &&
                await db.AssetOperations.AnyAsync(x => x.CompanyId == company && x.Kind == request.Kind && x.AssetId == request.AssetId &&
                    x.State != AssetOperationState.Cancelled, token))
                throw AssetPlanningService.Conflict("An acquisition request already exists for the asset.");
            await planning.Build(scope, request, token);
            var operation = new AssetOperation { CompanyId = company, TenantId = scope.TenantId, OrganizationId = org, Kind = request.Kind,
                AssetId = request.AssetId, PeriodId = request.PeriodId, OpeningJournalId = request.OpeningJournalId, Date = request.Date,
                RequestKey = key, RequestHash = hash, PayloadJson = JsonSerializer.Serialize(request), CreatedBy = actor };
            db.AssetOperations.Add(operation);
            access.Audit(scope, actor, operation.Id, "FixedAssetOperationPrepared", reason);
            return operation;
        }, ct);

    public async Task<AssetPlan> Preview(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var operation = await Find(company, id, ct);
        var plan = operation.State == AssetOperationState.Posted ? AssetSnapshot.Plan(operation) :
            await planning.Build(scope, AssetSnapshot.Request(operation), ct);
        await tx.CommitAsync(ct);
        return plan;
    }

    public Task<AssetOperation> Change(Guid actor, Guid org, Guid company, Guid id, string action, AssetAction request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var operation = await Find(company, id, token);
            if (action == "post" && operation.State == AssetOperationState.Posted) return operation;
            if (operation.Version != request.Version) throw AssetPlanningService.Conflict("The asset operation changed.");
            var reason = AccountingRules.Text(request.Reason, 500, "Action reason");
            var input = AssetSnapshot.Request(operation);
            if (action == "approve" && operation.State == AssetOperationState.Draft)
            {
                var plan = await planning.Build(scope, input, token);
                await budget.Capital(scope, actor, operation, plan.Lines, action, token);
                if (IsInitial(operation))
                {
                    var assets = await Assets(company, plan.Assets, token);
                    if (assets.Any(x => x.Status != AssetStatus.Draft)) throw AssetPlanningService.Conflict("A different initial operation already owns the approval.");
                    foreach (var asset in assets)
                    {
                        asset.Status = AssetStatus.Approved;
                        asset.Version = Guid.NewGuid();
                    }
                }
                operation.PlanJson = JsonSerializer.Serialize(plan);
                operation.ApprovedBy = actor;
                operation.State = AssetOperationState.Approved;
            }
            else if (action == "cancel" && operation.State is AssetOperationState.Draft or AssetOperationState.Approved)
            {
                await budget.Capital(scope, actor, operation, null, action, token);
                if (operation.State == AssetOperationState.Approved && IsInitial(operation))
                    foreach (var asset in await Assets(company, AssetSnapshot.Plan(operation).Assets, token))
                    {
                        asset.Status = AssetStatus.Draft;
                        asset.Version = Guid.NewGuid();
                    }
                operation.State = AssetOperationState.Cancelled;
            }
            else if (action == "post" && operation.State == AssetOperationState.Approved)
            {
                if (operation.Date < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) && !request.AllowBackdated)
                    throw AssetPlanningService.Bad("Backdating requires explicit authorization.");
                var plan = await planning.Build(scope, input, token);
                var approved = AssetSnapshot.Plan(operation);
                if (!plan.Lines.SequenceEqual(approved.Lines) || !plan.Movements.SequenceEqual(approved.Movements) ||
                    !plan.Sources.SequenceEqual(approved.Sources) || !plan.Assets.SequenceEqual(approved.Assets) || plan.Basis != approved.Basis)
                    throw AssetPlanningService.Conflict("The approved evidence changed. Cancel and prepare a new operation.");
                if (governance is not null && operation.Kind is AssetOperationKind.Sale or AssetOperationKind.WriteOff)
                    await governance.Enforce(scope, actor, FinancialDuty.AssetDisposal, id, token);
                await budget.Capital(scope, actor, operation, plan.Lines, action, token);
                await Post(scope, actor, operation, input, plan, request.AllowBackdated, reason, token);
            }
            else throw AssetPlanningService.Conflict("Invalid fixed asset operation transition.");
            operation.Version = Guid.NewGuid();
            access.Audit(scope, actor, operation.Id, $"FixedAssetOperation:{action}", reason);
            await db.SaveChangesAsync(token);
            return operation;
        }, ct);

    private async Task Post(Company scope, Guid actor, AssetOperation operation, AssetOperationRequest input,
        AssetPlan plan, bool backdated, string reason, CancellationToken ct)
    {
        operation.PostedBy = actor;
        await db.SaveChangesAsync(ct);
        if (plan.Lines.Count > 0)
        {
            var receipt = await engine.PostAsync(actor, new(new(scope.TenantId, scope.OrganizationId, scope.Id), AssetMath.Source(operation.Kind),
                operation.Id, PostingKey(operation.Id), operation.Date, scope.BaseCurrency, plan.Lines, null, operation.Date,
                $"Fixed assets {operation.Kind}", operation.RequestKey, backdated, reason), ct);
            operation.JournalId = receipt.JournalId;
        }
        operation.Sequence = (await db.AssetOperations.Where(x => x.CompanyId == scope.Id).MaxAsync(x => x.Sequence, ct) ?? 0) + 1;
        foreach (var m in plan.Movements)
            db.AssetMovements.Add(new() { CompanyId = scope.Id, OrganizationId = scope.OrganizationId, TenantId = scope.TenantId,
                OperationId = operation.Id, ComponentId = m.ComponentId, Date = operation.Date, Sequence = operation.Sequence.Value,
                DepreciationPeriodId = m.DepreciationPeriodId, Cost = m.Cost, Depreciation = m.Depreciation, Impairment = m.Impairment,
                ShadowDepreciation = m.ShadowDepreciation, Units = m.Units, RemainingOverride = m.RemainingOverride,
                ResidualOverride = m.ResidualOverride, DepreciationCharge = m.DepreciationCharge });
        foreach (var source in plan.Sources)
            db.AssetSourceClaims.Add(new() { CompanyId = scope.Id, OrganizationId = scope.OrganizationId, TenantId = scope.TenantId,
                OperationId = operation.Id, JournalLineId = source });
        foreach (var asset in await Assets(scope.Id, plan.Assets, ct))
        {
            switch (operation.Kind)
            {
                case AssetOperationKind.Opening:
                case AssetOperationKind.Acquisition:
                    if (asset.Status != AssetStatus.Approved) throw AssetPlanningService.Conflict("Initial recognition no longer has approval.");
                    asset.RecognitionDate = operation.Date;
                    asset.AvailableDate = operation.Kind == AssetOperationKind.Opening ? input.HistoricalAvailableDate : null;
                    asset.Status = asset.AvailableDate.HasValue ? AssetStatus.InService : AssetStatus.Capitalized;
                    break;
                case AssetOperationKind.AvailableForUse:
                    asset.AvailableDate = operation.Date;
                    asset.Status = AssetStatus.InService;
                    break;
                case AssetOperationKind.Sale:
                case AssetOperationKind.WriteOff:
                    asset.Status = operation.Kind == AssetOperationKind.Sale ? AssetStatus.Disposed : AssetStatus.WrittenOff;
                    asset.DisposalDate = operation.Date;
                    break;
            }
            if (operation.Kind == AssetOperationKind.Depreciation) continue;
            asset.Version = Guid.NewGuid();
            access.Audit(scope, actor, asset.Id, $"FixedAsset:{operation.Kind}", operation.Id.ToString());
        }
        // Flush immutable children while the approved source is staged. The coordinator still owns the commit.
        await db.SaveChangesAsync(ct);
        operation.State = AssetOperationState.Posted;
        operation.PostedAt = clock.GetUtcNow();
    }

    public async Task<PageResult<AssetOperation>> History(Guid actor, Guid org, Guid company, Guid? asset, AssetOperationKind? kind,
        int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var query = db.AssetOperations.AsNoTracking().Where(x => x.CompanyId == company);
        if (kind.HasValue) query = query.Where(x => x.Kind == kind);
        if (asset.HasValue)
            query = query.Where(x => x.AssetId == asset || (from m in db.AssetMovements join c in db.AssetComponents on m.ComponentId equals c.Id
                where m.CompanyId == company && m.OperationId == x.Id && c.AssetId == asset select m.Id).Any());
        var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.Date).ThenByDescending(x => x.Sequence).ThenBy(x => x.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return new(rows, count, page, size);
    }

    internal static string PostingKey(Guid id) => $"fixed-assets:{id:N}";
    private static bool IsInitial(AssetOperation operation) => operation.Kind is AssetOperationKind.Acquisition or AssetOperationKind.Opening;
    private async Task<AssetOperation> Find(Guid company, Guid id, CancellationToken ct) =>
        await db.AssetOperations.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw new ApiException(404, "Fixed asset operation not found.", "asset_operation");
    private Task<List<FixedAsset>> Assets(Guid company, IReadOnlyList<Guid> ids, CancellationToken ct) =>
        db.FixedAssets.Where(x => x.CompanyId == company && ids.Contains(x.Id)).ToListAsync(ct);
}
