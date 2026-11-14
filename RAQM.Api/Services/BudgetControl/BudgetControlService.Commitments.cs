using Microsoft.EntityFrameworkCore;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Budgeting;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Organizations;
using static RAQM.Api.Helpers.Budgeting.BudgetMath;

namespace RAQM.Api.Services.BudgetControl;

public sealed partial class BudgetControlService
{
    private async Task<IReadOnlyList<ProcurementDocument>> AdoptProcurement(Company scope, Guid actor, BudgetPolicy policy, CancellationToken ct)
    {
        var company = scope.Id;
        var documents = await db.ProcurementDocuments.AsNoTracking().Include(x => x.Lines)
            .Where(x => x.CompanyId == company && (x.State == ProcurementState.Approved || x.State == ProcurementState.Confirmed))
            .ToListAsync(ct);
        var activeOrders = documents.Where(x => x.IsOrder && x.RequisitionId.HasValue)
            .ToDictionary(x => x.RequisitionId!.Value);
        var itemIds = documents.SelectMany(x => x.Lines).Select(x => x.ItemId).Distinct().ToArray();
        var stocked = (await db.InventoryItems.AsNoTracking().Where(x => x.CompanyId == company &&
            itemIds.Contains(x.Id) && x.Kind == ItemKind.Stocked).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        var lines = documents.SelectMany(x => x.Lines.Where(line => stocked.Contains(line.ItemId) && line.NetAmount > 0)
                .Select(line => (Document: x, Line: line))).ToArray();
        var allIds = lines.Select(x => x.Line.Id).ToArray();
        var existing = await db.BudgetCommitments.AsNoTracking().Where(x => x.CompanyId == company &&
            x.ProcurementLineId.HasValue && allIds.Contains(x.ProcurementLineId.Value)).ToListAsync(ct);
        var byLine = existing.ToLookup(x => x.ProcurementLineId);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var policyPeriod = await db.Periods.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
            x.StartDate <= policy.EffectiveDate && x.EndDate >= policy.EffectiveDate, ct)
            ?? throw Conflict("Create the fiscal period for the new procurement policy before adopting open obligations.");
        var priorPeriodIds = existing.Select(e => e.PeriodId).Distinct().ToArray();
        var priorPeriods = await db.Periods.AsNoTracking().Where(x => x.CompanyId == company &&
            priorPeriodIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var adopted = new List<ProcurementDocument>();
        foreach (var (document, line) in lines)
        {
            var prior = byLine[line.Id].ToArray();
            if (prior.Length > 0)
            {
                if (prior.Any(x => x.AccountId != policy.AccountId) ||
                    !prior.Any(x => x.EventKey == $"reserve:{line.Id:N}"))
                    throw Conflict("The historical commitment account or source reservation is inconsistent.");
                foreach (var bucket in prior.GroupBy(x => x.PeriodId).Where(x => x.Sum(e => e.Amount) > 0 &&
                    priorPeriods[x.Key].FiscalYearId != policyPeriod.FiscalYearId &&
                    priorPeriods[x.Key].StartDate < policyPeriod.StartDate))
                {
                    var amount = bucket.Sum(x => x.Amount);
                    var sourceEvent = bucket.First();
                    Append(scope, actor, sourceEvent, -amount, today, $"rollout:{line.Id:N}:{policyPeriod.Id:N}", null);
                    Append(scope, actor, sourceEvent, amount, today, $"rollin:{line.Id:N}:{policyPeriod.Id:N}", null,
                        policyPeriod.Id);
                    access.Audit(scope, actor, line.Id, "BudgetCommitmentRolledForward",
                        FormattableString.Invariant($"Policy {policy.Id}; prior period {bucket.Key}; new period {policyPeriod.Id}; open {amount}."));
                }
                if (!document.IsOrder && activeOrders.ContainsKey(document.Id) &&
                    priorPeriods[prior.Single(x => x.EventKey == $"reserve:{line.Id:N}").PeriodId].FiscalYearId != policyPeriod.FiscalYearId &&
                    !prior.Any(x => x.EventKey == $"rollin:{line.Id:N}:{policyPeriod.Id:N}"))
                {
                    var reserve = prior.Single(x => x.EventKey == $"reserve:{line.Id:N}");
                    Append(scope, actor, reserve, line.NetAmount, today, $"rollin:{line.Id:N}:{policyPeriod.Id:N}", null,
                        policyPeriod.Id);
                    Append(scope, actor, reserve, -line.NetAmount, today,
                        $"rc:{line.Id:N}:{policyPeriod.Id:N}", null,
                        policyPeriod.Id);
                }
                continue;
            }
            var sourcePeriod = await db.Periods.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.StartDate <= document.Date && x.EndDate >= document.Date, ct)
                ?? throw Conflict("Assign a fiscal period to every open historical purchase before enabling budget control.");
            // An older fiscal year's still-open obligation consumes the year in which control begins.
            // Keep the source document and adoption date intact; only the authority bucket rolls forward.
            var period = sourcePeriod.FiscalYearId != policyPeriod.FiscalYearId &&
                sourcePeriod.StartDate < policyPeriod.StartDate ? policyPeriod : sourcePeriod;
            var origin = Scoped(scope, new BudgetCommitment { AccountId = policy.AccountId, PeriodId = period.Id,
                ProcurementLineId = line.Id, Kind = document.IsOrder ? CommitmentKind.Order : CommitmentKind.Requisition,
                Date = today, Amount = line.NetAmount, EventKey = $"reserve:{line.Id:N}", ActorId = actor });
            db.BudgetCommitments.Add(origin);
            if (!document.IsOrder && activeOrders.TryGetValue(document.Id, out var activeOrder))
                Append(scope, actor, origin, -line.NetAmount, today,
                    $"convert:{activeOrder.Id:N}:{line.Id:N}", null);
            if (document.IsOrder)
            {
                var operations = await db.StockOperations.AsNoTracking().Where(x => x.CompanyId == company &&
                    x.OrderLineId == line.Id && x.State == StockState.Posted &&
                    (x.Kind == StockKind.Receipt || x.Kind == StockKind.ReceiptCorrection || x.Kind == StockKind.SupplierReturn))
                    .OrderBy(x => x.Sequence).ToListAsync(ct);
                var relief = new Dictionary<Guid, decimal>();
                var remaining = line.NetAmount;
                foreach (var operation in operations)
                {
                    decimal effect;
                    string key;
                    if (operation.Kind == StockKind.Receipt)
                    {
                        effect = -Math.Min(operation.Accrual, remaining);
                        relief[operation.Id] = -effect;
                        key = $"stock:{operation.Id:N}";
                    }
                    else
                    {
                        var source = operation.RelatedOperationId ?? throw Conflict("Historical receipt correction has no source.");
                        effect = Math.Min(operation.Accrual, relief.GetValueOrDefault(source));
                        relief[source] = relief.GetValueOrDefault(source) - effect;
                        key = $"stock-restore:{source:N}:{operation.Id:N}";
                    }
                    if (effect == 0) continue;
                    remaining += effect;
                    Append(scope, actor, origin, effect, today, key, operation.Id);
                }
            }
            access.Audit(scope, actor, line.Id, "BudgetCommitmentAdopted",
                FormattableString.Invariant($"Policy {policy.Id}; document {document.Id}; source net {line.NetAmount}."));
            if ((document.IsOrder || !activeOrders.ContainsKey(document.Id)) && !adopted.Any(x => x.Id == document.Id))
                adopted.Add(document);
        }
        return adopted;
    }

    public async Task Procurement(Company scope, Guid actor, ProcurementDocument document, string action, CancellationToken ct)
    {
        var company = scope.Id;
        var account = await db.InventoryAccounts.AsNoTracking().Where(x => x.CompanyId == company && x.Role == InventoryRole.Inventory)
            .Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(ct);
        if (!account.HasValue) return;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var intent = $"procurement:{document.Id:N}:{document.Version:N}";
        if (action == "cancel")
        {
            foreach (var line in document.Lines)
            {
                var events = await ProcurementEvents(company, line.Id, ct);
                var current = events.Where(x => x.EventKey.StartsWith($"rollin:{line.Id:N}:", StringComparison.Ordinal))
                    .OrderByDescending(x => x.Date).FirstOrDefault() ?? events.FirstOrDefault();
                var balance = current is null ? 0 : events.Where(x => x.PeriodId == current.PeriodId).Sum(x => x.Amount);
                if (balance > 0) Append(scope, actor, current!, -balance, today, $"cancel:{line.Id:N}", null);
            }
            if (document.IsOrder && document.State != ProcurementState.Draft && document.RequisitionId is { } req)
                await ConvertRequisition(scope, actor, req, document.Id, today, restore: true, intent, ct);
            return;
        }
        if (action != "approve") return;
        IReadOnlyDictionary<Guid, decimal>? converted = null;
        if (document.RequisitionId is { } requisition)
        {
            converted = await ConvertRequisition(scope, actor, requisition, document.Id, today, restore: false, intent, ct);
            await db.SaveChangesAsync(ct);
        }
        if (!await db.BudgetPolicies.AnyAsync(x => x.CompanyId == company && x.AccountId == account.Value && x.EffectiveDate <= today, ct))
            return;
        // A Phase 06 order may legitimately predate an accounting calendar. Such a
        // document has no budget bucket until assigned an in-calendar source date.
        var fiscal = await db.FiscalYears.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
            x.StartDate <= document.Date && x.EndDate >= document.Date, ct);
        if (fiscal is null)
        {
            if ((await Policy(company, account.Value, today, ct))?.Mode == ControlMode.Off) return;
            throw Conflict("Create the fiscal calendar before approving a controlled procurement document.");
        }
        var date = ControlDate(fiscal, today);
        var policy = await Policy(company, account.Value, date, ct);
        if (policy is null || policy.Mode == ControlMode.Off) return;
        var period = await Period(company, document.Date, ct);
        var itemIds = document.Lines.Select(x => x.ItemId).ToArray();
        var stocked = (await db.InventoryItems.AsNoTracking().Where(x => x.CompanyId == company && itemIds.Contains(x.Id) &&
            x.Kind == ItemKind.Stocked).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        var lines = document.Lines.Where(x => stocked.Contains(x.ItemId) && x.NetAmount > 0).ToArray();
        var released = 0m;
        if (converted is { Count: > 0 })
        {
            var previousPeriods = converted.Keys.ToArray();
            var sameYear = await db.Periods.Where(x => x.CompanyId == company && x.FiscalYearId == fiscal.Id &&
                previousPeriods.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            released = -sameYear.Sum(x => converted[x]);
        }
        await Enforce(scope, actor, policy, fiscal, date, lines.Sum(x => x.NetAmount), intent, document.Id, ct, released);
        foreach (var line in lines)
            db.BudgetCommitments.Add(Scoped(scope, new BudgetCommitment { AccountId = account.Value, PeriodId = period.Id,
                ProcurementLineId = line.Id, Kind = document.IsOrder ? CommitmentKind.Order : CommitmentKind.Requisition,
                Date = today, Amount = line.NetAmount, EventKey = $"reserve:{line.Id:N}", ActorId = actor }));
    }

    private async Task<IReadOnlyDictionary<Guid, decimal>> ConvertRequisition(Company scope, Guid actor, Guid requisition, Guid order, DateOnly today,
        bool restore, string intent, CancellationToken ct)
    {
        var converted = await (from movement in db.BudgetCommitments.AsNoTracking()
            join line in db.ProcurementLines on movement.ProcurementLineId equals (Guid?)line.Id
            where movement.CompanyId == scope.Id && line.CompanyId == scope.Id && line.DocumentId == requisition
            select movement).ToListAsync(ct);
        var changes = converted.GroupBy(x => x.ProcurementLineId).Select(group =>
        {
            var reserve = group.Where(x => x.EventKey.StartsWith($"rollin:{x.ProcurementLineId:N}:", StringComparison.Ordinal))
                .OrderByDescending(x => x.Date).FirstOrDefault() ??
                group.Single(x => x.EventKey == $"reserve:{x.ProcurementLineId:N}");
            var amount = restore ? -group.Where(x => x.EventKey == $"convert:{order:N}:{x.ProcurementLineId:N}").Sum(x => x.Amount)
                : -group.Sum(x => x.Amount);
            return (Reserve: reserve, Amount: amount, PeriodId: reserve.PeriodId);
        }).Where(x => x.Amount != 0).ToArray();
        if (restore && changes.Length > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var group in changes.GroupBy(x => (x.Reserve.AccountId, x.PeriodId)))
            {
                var period = await db.Periods.AsNoTracking().SingleAsync(x => x.CompanyId == scope.Id && x.Id == group.Key.PeriodId, ct);
                var year = await Year(scope.Id, period.StartDate, ct);
                var date = ControlDate(year, today);
                var policy = await Policy(scope.Id, group.Key.AccountId, date, ct);
                if (policy is not null) await Enforce(scope, actor, policy, year, date, group.Sum(x => x.Amount), intent, order, ct);
            }
        }
        foreach (var change in changes)
            Append(scope, actor, change.Reserve, change.Amount, today, $"{(restore ? "restore" : "convert")}:{order:N}:{change.Reserve.ProcurementLineId:N}", null,
                change.PeriodId);
        return changes.GroupBy(x => x.PeriodId).ToDictionary(x => x.Key, x => x.Sum(y => y.Amount));
    }

    public async Task Stock(Company scope, Guid actor, StockOperation operation, decimal accrual, CancellationToken ct)
    {
        if (operation.OrderLineId is not { } lineId || accrual <= 0 ||
            operation.Kind is not (StockKind.Receipt or StockKind.ReceiptCorrection or StockKind.SupplierReturn)) return;
        var rows = await ProcurementEvents(scope.Id, lineId, ct);
        if (rows.Count == 0) return;
        var initial = rows.Where(x => x.EventKey.StartsWith($"rollin:{lineId:N}:", StringComparison.Ordinal))
            .OrderByDescending(x => x.Date).FirstOrDefault() ?? rows.Single(x => x.EventKey == $"reserve:{lineId:N}");
        var remaining = rows.Where(x => x.PeriodId == initial.PeriodId).Sum(x => x.Amount);
        var effect = operation.Kind == StockKind.Receipt ? -Math.Min(accrual, remaining) :
            Math.Min(accrual, -rows.Where(x => x.StockOperationId == operation.RelatedOperationId).Sum(x => x.Amount) -
                rows.Where(x => x.StockOperationId != operation.RelatedOperationId && x.EventKey.StartsWith($"stock-restore:{operation.RelatedOperationId:N}:",
                    StringComparison.Ordinal)).Sum(x => x.Amount));
        if (effect == 0) return;
        if (operation.Kind != StockKind.Receipt && effect < 0) throw Conflict("Receipt relief history is inconsistent.");
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var eventDate = operation.Date > initial.Date ? operation.Date : initial.Date;
        if (effect > 0)
        {
            // Same-year restoration replaces an equal release of posted spending. A later-year
            // correction restores prior-year commitment and requires prior-year funds.
            var period = await db.Periods.AsNoTracking().SingleAsync(x => x.CompanyId == scope.Id && x.Id == initial.PeriodId, ct);
            var year = await Year(scope.Id, period.StartDate, ct);
            var date = ControlDate(year, today);
            var policy = await Policy(scope.Id, initial.AccountId, date, ct);
            if (policy is not null && operation.Date > year.EndDate)
                await Enforce(scope, actor, policy, year, date, effect, $"stock:{operation.Id:N}:{operation.Version:N}", operation.Id, ct);
        }
        Append(scope, actor, initial, effect, eventDate > today ? today : eventDate,
            effect > 0 ? $"stock-restore:{operation.RelatedOperationId:N}:{operation.Id:N}" : $"stock:{operation.Id:N}", operation.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task Capital(Company scope, Guid actor, AssetOperation operation, IReadOnlyList<JournalDraftLine>? lines,
        string action, CancellationToken ct)
    {
        if (operation.Kind is not (AssetOperationKind.Acquisition or AssetOperationKind.Improvement)) return;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (action != "approve")
        {
            var rows = await db.BudgetCommitments.AsNoTracking().Where(x => x.CompanyId == scope.Id &&
                x.AssetOperationId == operation.Id).ToListAsync(ct);
            foreach (var group in rows.GroupBy(x => x.AccountId))
            {
                var amount = group.Sum(x => x.Amount);
                if (amount > 0) Append(scope, actor, group.First(), -amount, today, $"capital-release:{operation.Id:N}:{group.Key:N}", null);
            }
            if (rows.Count > 0) await db.SaveChangesAsync(ct);
            return;
        }
        if (lines is null || lines.Count == 0) return;
        var accounts = lines.Select(x => x.AccountId).Distinct().ToArray();
        if (!await db.BudgetPolicies.AnyAsync(x => x.CompanyId == scope.Id && x.Kind == BudgetKind.Capital &&
            accounts.Contains(x.AccountId) && x.EffectiveDate <= today, ct)) return;
        var period = await Period(scope.Id, operation.Date, ct);
        var year = await Year(scope.Id, operation.Date, ct);
        var date = ControlDate(year, today);
        foreach (var group in lines.GroupBy(x => x.AccountId))
        {
            var amount = group.Sum(x => x.Debit - x.Credit);
            if (amount <= 0) continue;
            var policy = await Policy(scope.Id, group.Key, date, ct);
            if (policy is null || policy.Kind != BudgetKind.Capital || policy.Mode == ControlMode.Off) continue;
            await Enforce(scope, actor, policy, year, date, amount, $"asset:{operation.Id:N}:{operation.Version:N}", operation.Id, ct);
            db.BudgetCommitments.Add(Scoped(scope, new BudgetCommitment { AccountId = group.Key, PeriodId = period.Id,
                AssetOperationId = operation.Id, Kind = CommitmentKind.Capital, Date = today, Amount = amount, ActorId = actor,
                EventKey = $"capital-reserve:{operation.Id:N}:{group.Key:N}" }));
        }
    }

    internal async Task<AccountingPeriod> Period(Guid company, DateOnly date, CancellationToken ct) =>
        await db.Periods.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.StartDate <= date && x.EndDate >= date, ct)
            ?? throw Conflict("No fiscal period contains this commitment date.");

    private Task<List<BudgetCommitment>> ProcurementEvents(Guid company, Guid line, CancellationToken ct) =>
        db.BudgetCommitments.AsNoTracking().Where(x => x.CompanyId == company && x.ProcurementLineId == line).ToListAsync(ct);

    private void Append(Company scope, Guid actor, BudgetCommitment origin, decimal amount, DateOnly date, string key, Guid? stock,
        Guid? period = null) =>
        db.BudgetCommitments.Add(Scoped(scope, new BudgetCommitment { AccountId = origin.AccountId, PeriodId = period ?? origin.PeriodId,
            ProcurementLineId = origin.ProcurementLineId, AssetOperationId = origin.AssetOperationId, StockOperationId = stock,
            Kind = origin.Kind, Date = date, Amount = amount, EventKey = key, ActorId = actor }));
}
