using System.Data;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.FinancialReporting;
using static RAQM.Api.Helpers.MultiCurrency.FxMath;

namespace RAQM.Api.Services.MultiCurrency;

public sealed record FxControl(Guid AccountId, decimal Subledger, decimal Ledger)
{
    public decimal Difference => Ledger - Subledger;
}
public sealed record FxExposure(Guid PositionId, string Currency, FxPositionKind Kind, Guid AccountId, Guid? PartyId,
    string Code, DateOnly? DueDate, decimal ForeignAmount, decimal BaseAmount, int? DaysOverdue, string Aging);

public sealed class FxReporting(RaqmDbContext db, FxQueries queries, FinancialLedger ledger, FinancialAccess access, TimeProvider clock)
{
    private async Task<FxExposure[]> Exposure(Guid company, DateOnly at, CancellationToken ct)
    {
        var balances = (await queries.Balances(company, at, ct)).ToDictionary(x => x.PositionId);
        var positions = await (from position in db.FxPositions.AsNoTracking()
            join currency in db.FxCurrencies.AsNoTracking() on position.CurrencyId equals currency.Id
            where position.CompanyId == company && currency.CompanyId == company
            orderby currency.Code, position.Code
            select new { Position = position, Currency = currency.Code }).ToListAsync(ct);
        return positions.Where(x => balances.ContainsKey(x.Position.Id) || x.Position.Kind == FxPositionKind.Bank).Select(x =>
        {
            var position = x.Position;
            var balance = balances.GetValueOrDefault(position.Id) ?? new FxBalance(position.Id, 0, 0);
            var days = position.DueDate.HasValue && Monetary(position.Kind) ? Math.Max(0, at.DayNumber - position.DueDate.Value.DayNumber) : (int?)null;
            var aging = days switch { null => "NotApplicable", 0 => "Current", <= 30 => "1–30", <= 60 => "31–60", <= 90 => "61–90", _ => "91+" };
            return new FxExposure(position.Id, x.Currency, position.Kind, position.AccountId, position.PartyId, position.Code,
                position.DueDate, balance.ForeignAmount, balance.BaseAmount, days, aging);
        }).ToArray();
    }

    private async Task<FxControl[]> Reconcile(Guid company, DateOnly at, IReadOnlyList<FxExposure> positions, CancellationToken ct)
    {
        var controls = await db.FxAccounts.AsNoTracking().Where(x => x.CompanyId == company && (int)x.Role < 4)
            .Select(x => x.AccountId).ToListAsync(ct);
        var ids = controls.Concat(positions.Select(x => x.AccountId)).Distinct().ToArray();
        var actual = (await ledger.Lines(company, at, accounts: ids).ToListAsync(ct)).ToLookup(x => x.AccountId);
        var expected = positions.ToLookup(x => x.AccountId);
        return ids.Order().Select(id => new FxControl(id, expected[id].Sum(x => Sign(x.Kind) * x.BaseAmount),
            actual[id].Sum(x => x.Debit - x.Credit))).ToArray();
    }

    public async Task<object> Report(Guid actor, Guid org, Guid company, DateOnly at, DateOnly? from, DateOnly? compare,
        Guid? party, string? currency, CancellationToken ct)
    {
        if (at.Year < 1900 || at > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) ||
            (from.HasValue && (from.Value.Year < 1900 || from > at)) || (compare.HasValue && (compare.Value.Year < 1900 || compare > at)))
            throw Bad("Use valid, ordered FX report cutoffs no later than today.");
        if (currency is not null) { currency = currency.ToUpperInvariant(); RAQM.Api.Helpers.Accounting.CurrencyPolicy.Units(currency); }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var positions = await Exposure(company, at, ct);
        var controls = await Reconcile(company, at, positions, ct);
        var selected = positions.Where(x => (!party.HasValue || x.PartyId == party) && (currency is null || x.Currency == currency)).ToArray();
        var previous = compare.HasValue ? (await Exposure(company, compare.Value, ct)).Where(x =>
            (!party.HasValue || x.PartyId == party) && (currency is null || x.Currency == currency)).ToArray() : [];
        var start = from ?? DateOnly.MinValue;
        var positionsById = positions.ToDictionary(x => x.PositionId);
        var activity = await db.FxOperations.AsNoTracking().Where(x => x.CompanyId == company && x.State == FxState.Posted &&
            x.Date >= start && x.Date <= at && (!party.HasValue || x.PartyId == party)).OrderBy(x => x.Sequence).ToListAsync(ct);
        if (currency is not null)
            activity = activity.Where(x => x.PositionId.HasValue && positionsById.TryGetValue(x.PositionId.Value, out var p) && p.Currency == currency ||
                FxQueries.Plan(x).Movements.Any(m => positionsById.TryGetValue(m.PositionId, out var related) && related.Currency == currency)).ToList();
        var fxMap = await db.FxAccounts.AsNoTracking().Where(x => x.CompanyId == company && (int)x.Role >= 4 && (int)x.Role <= 7)
            .ToDictionaryAsync(x => x.Role, x => x.AccountId, ct);
        var ids = fxMap.Values.ToArray();
        var lines = await ledger.Lines(company, at, start, ids, performance: true).ToListAsync(ct);
        object Totals(IEnumerable<FxExposure> input) => input.Where(x => x.ForeignAmount != 0 || x.BaseAmount != 0)
            .GroupBy(x => new { x.Currency, x.Kind, x.Aging }).Select(x => new
            { x.Key.Currency, x.Key.Kind, x.Key.Aging, ForeignAmount = x.Sum(y => y.ForeignAmount), BaseAmount = x.Sum(y => y.BaseAmount) }).ToArray();
        var statements = await db.FxBankStatements.AsNoTracking().Where(x => x.CompanyId == company && x.Cutoff <= at)
            .OrderByDescending(x => x.Cutoff).ThenBy(x => x.Id).ToListAsync(ct);
        var result = new
        {
            FunctionalCurrency = scope.BaseCurrency, At = at, From = from, Compare = compare, Positions = selected, Totals = Totals(selected),
            PriorTotals = Totals(previous), Reconciliation = controls, Agrees = controls.All(x => x.Difference == 0),
            Operations = activity, BankStatements = statements.Where(x => selected.Any(p => p.PositionId == x.PositionId))
                .Select(x => new { Evidence = x, Difference = x.StatementForeign - x.BookForeign }).ToArray(),
            CompanyExchangeIncome = fxMap.OrderBy(x => x.Key).Select(x => new
            {
                Role = x.Key, AccountId = x.Value, SignedIncome = lines.Where(l => l.AccountId == x.Value).Sum(l => l.Credit - l.Debit)
            }).ToArray(),
            MonthlyExchangeIncome = lines.GroupBy(x => new { x.Date.Year, x.Date.Month, x.AccountId }).OrderBy(x => x.Key.Year)
                .ThenBy(x => x.Key.Month).ThenBy(x => x.Key.AccountId).Select(x =>
                new { x.Key.Year, x.Key.Month, x.Key.AccountId, SignedIncome = x.Sum(y => y.Credit - y.Debit) }).ToArray()
        };
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<(IReadOnlyList<CloseFinding> Findings, string Fingerprint)> Check(Guid company, AccountingPeriod period, CancellationToken ct)
    {
        var setup = await db.FxConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company, ct);
        if (setup is null) return ([], "");
        var positions = await Exposure(company, period.EndDate, ct);
        var controls = await Reconcile(company, period.EndDate, positions, ct);
        var findings = controls.Where(x => x.Difference != 0).Select(x => new CloseFinding("fx-control", FindingSeverity.Blocking,
            FormattableString.Invariant($"FX control differs from posted evidence by {x.Difference}."), x.AccountId)).ToList();
        var severity = setup.RequireCloseRevaluation ? FindingSeverity.Blocking : FindingSeverity.Review;
        var open = positions.Where(x => Monetary(x.Kind) && x.ForeignAmount > 0).ToArray();
        if (open.Length > ForeignExchange.FxPlanning.MaxRevaluationPositions)
            findings.Add(new("fx-revaluation-capacity", severity,
                $"{open.Length} open monetary positions exceed the supported {ForeignExchange.FxPlanning.MaxRevaluationPositions} position revaluation cutoff. Settle or reduce the population before close."));
        var rates = await (from rate in db.FxRates.AsNoTracking() join currency in db.FxCurrencies on rate.CurrencyId equals currency.Id
            where rate.CompanyId == company && currency.CompanyId == company && rate.State == FxRateState.Approved &&
                rate.Date == period.EndDate && rate.Type == FxRateType.Closing select new { rate.Id, currency.Code }).ToListAsync(ct);
        foreach (var code in open.Select(x => x.Currency).Distinct().Where(x => !rates.Any(r => r.Code == x)))
            findings.Add(new("fx-closing-rate", severity, $"Exact closing rate is missing for {code}."));
        var activity = await db.FxOperations.AsNoTracking().Where(x => x.CompanyId == company && x.Date <= period.EndDate &&
            x.State != FxState.Cancelled).OrderBy(x => x.Id).ToListAsync(ct);
        var corrected = activity.Where(x => x.State == FxState.Posted && x.Kind == FxKind.RevaluationCorrection).Select(x => x.OriginalId).ToHashSet();
        var batch = activity.SingleOrDefault(x => x.Date == period.EndDate && x.Kind == FxKind.Revaluation &&
            x.State == FxState.Posted && !corrected.Contains(x.Id));
        if (open.Length > 0)
        {
            var valuations = batch is null ? [] : FxQueries.Plan(batch).Valuations ?? [];
            if (batch is null || open.Any(x => !valuations.Any(v => v.PositionId == x.PositionId && v.ForeignAmount == x.ForeignAmount &&
                v.ClosingBase == x.BaseAmount && rates.Any(r => r.Id == v.RateId && r.Code == x.Currency))))
                findings.Add(new("fx-revaluation", severity, "Open monetary items require current approved revaluation evidence at cutoff.", batch?.Id));
        }
        foreach (var op in activity.Where(x => x.State == FxState.Approved || x.Kind == FxKind.Revaluation && x.State == FxState.Draft))
            findings.Add(new("fx-pending", FindingSeverity.Review, $"Review pending FX {op.Kind} evidence.", op.Id));
        var banks = open.Where(x => x.Kind == FxPositionKind.Bank).Select(x => x.PositionId).ToArray();
        var statements = await db.FxBankStatements.AsNoTracking().Where(x => x.CompanyId == company && banks.Contains(x.PositionId) &&
            x.Cutoff <= period.EndDate).OrderByDescending(x => x.Cutoff).ThenBy(x => x.Id).ToListAsync(ct);
        foreach (var bank in banks)
            if (!statements.Any(x => x.PositionId == bank && x.Cutoff == period.EndDate && x.StatementForeign == x.BookForeign))
                findings.Add(new("fx-bank-statement", FindingSeverity.Review, "Foreign bank statement balance needs cutoff reconciliation.", bank));
        if (activity.Count == 0 && positions.All(x => x.ForeignAmount == 0) && findings.Count == 0) return ([], "");
        return (findings, TradeRules.Hash(new { setup.Id, Positions = positions, Controls = controls, Batch = batch?.Id,
            Rates = rates.OrderBy(x => x.Code), Statements = statements.Select(x => x.Id),
            Activity = activity.Select(x => new { x.Id, x.State, x.Version, x.Sequence }) }));
    }

    public async Task<IReadOnlyList<DTOs.Accounting.AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.AccountingAudit.AsNoTracking().Where(x => x.CompanyId == company && x.EntityId == id && x.Action.StartsWith("Fx"))
            .OrderBy(x => x.At).ThenBy(x => x.Id)
            .Select(x => new DTOs.Accounting.AuditResponse(x.Id, x.EntityId, x.Action, x.ActorId, x.At, x.Reason)).ToListAsync(ct);
    }
}
