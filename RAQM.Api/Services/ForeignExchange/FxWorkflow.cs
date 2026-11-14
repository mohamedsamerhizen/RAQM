using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.MultiCurrency;
using static RAQM.Api.Helpers.MultiCurrency.FxMath;
using static RAQM.Api.Services.MultiCurrency.FxSetup;

namespace RAQM.Api.Services.ForeignExchange;

public sealed class FxWorkflow(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions, FxQueries queries,
    FxPlanning planning, IPostingEngine posting, TimeProvider clock, GovernanceGate? governance = null)
{
    public Task<FxOperation> Prepare(Guid actor, Guid org, Guid company, FxRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            Validate(input);
            input = input with { Key = AccountingRules.Text(input.Key, 64, "Operation key"),
                Evidence = AccountingRules.Text(input.Evidence, 1000, "Source evidence"), Reference = input.Reference?.Trim() };
            var hash = TradeRules.Hash(input);
            var prior = await db.FxOperations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.RequestKey == input.Key, token);
            if (prior is not null)
                return prior.RequestHash == hash ? prior : throw Conflict("The FX request key already identifies a different operation.");
            var operation = Scoped(scope, new FxOperation { Kind = input.Kind, Date = input.Date, OriginalId = input.OriginalId,
                RequestKey = input.Key, RequestHash = hash, CreatedBy = actor, InputJson = JsonSerializer.Serialize(input) });
            var position = await SourcePosition(scope, operation, input, token);
            if (position is not null)
            {
                operation.PositionId = position.Id;
                operation.PartyId = position.PartyId;
            }
            if (input.Kind is FxKind.Invoice or FxKind.Bill)
            {
                var reference = AccountingRules.Text(input.Reference, 160, "Commercial reference").ToUpperInvariant();
                operation.ReferenceKey = reference;
                if (await db.FxOperations.AnyAsync(x => x.CompanyId == company && x.PartyId == operation.PartyId &&
                    x.ReferenceKey == reference && x.State != FxState.Cancelled, token))
                    throw Conflict("This counterparty reference is already registered.");
            }
            await planning.Build(scope, operation, position, token);
            db.FxOperations.Add(operation);
            access.Audit(scope, actor, operation.Id, "FxOperationPrepared", input.Kind.ToString());
            return operation;
        }, ct);

    public Task<FxOperation> Change(Guid actor, Guid org, Guid company, Guid id, string action, FxAction input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var operation = await Find(company, id, token);
            if (action == "post" && operation.State == FxState.Posted) return operation;
            if (operation.Version != input.Version) throw Conflict("The FX operation changed.");
            var reason = AccountingRules.Text(input.Reason, 500, "Action reason");
            if (action == "cancel" && operation.State is FxState.Draft or FxState.Approved)
                operation.State = FxState.Cancelled;
            else if (action == "approve" && operation.State == FxState.Draft)
            {
                if (actor == operation.CreatedBy) throw Conflict("An independent Owner must approve.");
                var plan = await Build(scope, operation, token);
                operation.PlanJson = JsonSerializer.Serialize(plan);
                operation.ApprovedBy = actor;
                operation.State = FxState.Approved;
            }
            else if (action == "post" && operation.State == FxState.Approved)
            {
                if (operation.Date < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) && !input.AllowBackdated)
                    throw Bad("Backdated posting requires explicit authorization.");
                var plan = await Build(scope, operation, token);
                if (TradeRules.Hash(plan) != TradeRules.Hash(FxQueries.Plan(operation)))
                    throw Conflict("The approved rates or balances changed. Cancel and prepare a new operation.");
                if (governance is not null && operation.Kind == FxKind.Revaluation)
                    await governance.Enforce(scope, actor, FinancialDuty.FxRevaluation, id, token);
                await Post(scope, actor, operation, plan, reason, input.AllowBackdated, token);
            }
            else throw Conflict("Invalid FX action for this state.");
            operation.Version = Guid.NewGuid();
            access.Audit(scope, actor, id, $"FxOperation:{action}", reason);
            return operation;
        }, ct);

    public async Task<FxPlan> Preview(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var operation = await Find(company, id, ct);
        var result = operation.State == FxState.Posted ? FxQueries.Plan(operation) : await Build(scope, operation, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<PageResult<FxOperation>> List(Guid actor, Guid org, Guid company, FxKind? kind, Guid? party,
        int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        await access.Resolve(actor, org, company, false, false, ct);
        var query = db.FxOperations.AsNoTracking().Where(x => x.CompanyId == company);
        if (kind.HasValue) query = query.Where(x => x.Kind == kind);
        if (party.HasValue) query = query.Where(x => x.PartyId == party);
        return new(await query.OrderByDescending(x => x.Date).ThenByDescending(x => x.Sequence).ThenBy(x => x.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct), await query.CountAsync(ct), page, size);
    }

    private async Task Post(Company scope, Guid actor, FxOperation operation, FxPlan plan, string reason, bool allowBackdated, CancellationToken ct)
    {
        operation.PostedBy = actor;
        await db.SaveChangesAsync(ct);
        if (plan.Lines.Count > 0)
        {
            var receipt = await posting.PostAsync(actor, new(new(scope.TenantId, scope.OrganizationId, scope.Id), Source(operation.Kind),
                operation.Id, Key(operation.Id), operation.Date, scope.BaseCurrency, plan.Lines, null, operation.Date, $"FX {operation.Kind}",
                operation.ReferenceKey ?? operation.RequestKey, allowBackdated, reason), ct);
            operation.JournalId = receipt.JournalId;
        }
        var sequence = (await db.FxOperations.Where(x => x.CompanyId == scope.Id).MaxAsync(x => x.Sequence, ct) ?? 0) + 1;
        foreach (var movement in plan.Movements)
            db.FxEffects.Add(Scoped(scope, new FxEffect { OperationId = operation.Id, PositionId = movement.PositionId, Date = operation.Date,
                RateId = movement.RateId, Sequence = sequence, BaseDelta = movement.BaseDelta, ForeignDelta = movement.ForeignDelta }));
        await db.SaveChangesAsync(ct);
        operation.Sequence = sequence;
        operation.State = FxState.Posted;
    }

    private async Task<FxPosition?> SourcePosition(Company scope, FxOperation operation, FxRequest input, CancellationToken ct)
    {
        var company = scope.Id;
        if (input.Kind is FxKind.Revaluation or FxKind.RevaluationCorrection) return null;
        if (input.Kind == FxKind.Settlement) return await queries.Position(company, input.PositionId!.Value, ct);
        if (input.Kind is FxKind.BankPurchase or FxKind.BankSale) return await queries.Position(company, input.BankPositionId!.Value, ct);
        var accounts = await queries.Accounts(company, ct);
        if (accounts.Count == 0) throw Conflict("Configure FX accounts first.");
        FxPosition position;
        if (input.Kind == FxKind.Credit)
        {
            var source = await queries.Posted(company, input.OriginalId!.Value, ct);
            if (source.Kind is not (FxKind.Invoice or FxKind.Bill)) throw Bad("Select a posted foreign invoice or bill.");
            var original = await queries.Position(company, source.PositionId!.Value, ct);
            position = Scoped(scope, new FxPosition { CurrencyId = original.CurrencyId, PartyId = original.PartyId, AccountId = original.AccountId,
                Kind = original.Kind == FxPositionKind.Receivable ? FxPositionKind.CustomerRefund : FxPositionKind.SupplierRefund });
        }
        else
        {
            var party = await db.TradingParties.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == input.PartyId && x.IsActive, ct)
                ?? throw Bad("Select an active company customer or supplier.");
            await queries.Currency(company, input.CurrencyId!.Value, ct);
            var (kind, role) = input.Kind switch
            {
                FxKind.Invoice => (FxPositionKind.Receivable, FxAccountRole.Receivable),
                FxKind.Bill => (FxPositionKind.Payable, FxAccountRole.Payable),
                _ when party.Kind == PartyKind.Customer => (FxPositionKind.CustomerAdvance, FxAccountRole.CustomerAdvance),
                _ => (FxPositionKind.SupplierAdvance, FxAccountRole.SupplierAdvance)
            };
            position = Scoped(scope, new FxPosition { CurrencyId = input.CurrencyId.Value, PartyId = party.Id, Kind = kind, AccountId = accounts[role] });
        }
        position.Code = $"FX-{operation.Id:N}";
        position.DueDate = input.DueDate ?? input.Date;
        if (position.DueDate < input.Date) throw Bad("Due date cannot precede recognition.");
        db.FxPositions.Add(position);
        return position;
    }

    private async Task<FxPlan> Build(Company scope, FxOperation operation, CancellationToken ct) =>
        await planning.Build(scope, operation, operation.PositionId.HasValue ? await queries.Position(scope.Id, operation.PositionId.Value, ct) : null, ct);
    private async Task<FxOperation> Find(Guid company, Guid id, CancellationToken ct) =>
        await db.FxOperations.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw Missing("FX operation was not found.");
    internal static string Source(FxKind kind) => $"FX.{kind}";
    internal static string Key(Guid id) => $"fx:{id:N}";

    private static void Validate(FxRequest input)
    {
        if (!Enum.IsDefined(input.Kind)) throw Bad("Unknown FX action.");
        var recognition = input.Kind is FxKind.Invoice or FxKind.Bill;
        var advance = input.Kind == FxKind.Advance;
        var settlement = input.Kind == FxKind.Settlement;
        var exchange = input.Kind is FxKind.BankPurchase or FxKind.BankSale;
        var source = input.Kind is FxKind.Credit or FxKind.RevaluationCorrection;
        if ((recognition != (input.Lines is not null)) ||
            (input.Lines is { } lines && (lines.Count is < 1 or > 100 || lines.Any(x => x is null || x.OriginalLineId.HasValue ||
                Round(x.Quantity, 6) != x.Quantity || Round(x.UnitPrice, 4) != x.UnitPrice || Round(x.DiscountPercent, 6) != x.DiscountPercent))) ||
            ((recognition || advance) != input.PartyId.HasValue) || ((recognition || advance) != input.CurrencyId.HasValue) ||
            (settlement != input.PositionId.HasValue) || (source && !input.OriginalId.HasValue) ||
            (!source && input.Kind != FxKind.Revaluation && input.OriginalId.HasValue) ||
            (exchange && (!input.BankPositionId.HasValue || !input.FunctionalCashAccountId.HasValue)) ||
            (settlement && input.BankPositionId.HasValue == input.FunctionalCashAccountId.HasValue) ||
            (!exchange && !settlement && input.BankPositionId.HasValue) ||
            (advance != input.NonRefundableAdvance) || (advance && !input.FunctionalCashAccountId.HasValue) ||
            (!exchange && !settlement && !advance && input.FunctionalCashAccountId.HasValue) ||
            ((exchange || advance || settlement) != (input.ForeignAmount > 0)) || input.ForeignAmount < 0 ||
            (input.AdvancePositionId.HasValue != (input.AppliedAdvance > 0)) || input.AppliedAdvance < 0 ||
            (!recognition && input.AdvancePositionId.HasValue) ||
            (input.FeeAccountId.HasValue != (input.BaseFee > 0)) || input.BaseFee < 0 || (!exchange && input.FeeAccountId.HasValue) ||
            (input.DueDate.HasValue && !recognition && input.Kind != FxKind.Credit))
            throw Bad("Fields do not match the selected FX operation.");
        foreach (var amount in new[] { input.ForeignAmount, input.AppliedAdvance, input.BaseFee })
            if (Round(amount, 4) != amount) throw Bad("Invalid monetary precision.");
        AccountingRules.OptionalText(input.Reference, 160);
    }
}
