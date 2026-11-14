using Microsoft.EntityFrameworkCore;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Services.MultiCurrency;
using static RAQM.Api.Helpers.MultiCurrency.FxMath;

namespace RAQM.Api.Services.ForeignExchange;

public sealed partial class FxPlanning
{
    internal const int MaxRevaluationPositions = 500;

    private async Task<FxPlan> Revaluation(Guid company, FxRequest input, Draft draft, int units, CancellationToken ct)
    {
        if (!await db.Periods.AnyAsync(x => x.CompanyId == company && x.EndDate == input.Date, ct))
            throw Bad("Revaluation must use a company period-end cutoff.");
        var latest = await db.FxOperations.AsNoTracking().Where(x => x.CompanyId == company && x.Kind == FxKind.Revaluation &&
            x.State == FxState.Posted && x.Date == input.Date).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        if (latest?.Id != input.OriginalId || (latest is not null && !await db.FxOperations.AnyAsync(x => x.CompanyId == company &&
            x.OriginalId == latest.Id && x.Kind == FxKind.RevaluationCorrection && x.State == FxState.Posted && x.Date == input.Date, ct)))
            throw Conflict("This cutoff already has a batch. Correct the latest batch before preparing a replacement.");
        var balances = (await queries.Balances(company, input.Date, ct)).Where(x => x.ForeignAmount > 0).ToDictionary(x => x.PositionId);
        var ids = balances.Keys.ToArray();
        var positions = await db.FxPositions.AsNoTracking().Where(x => x.CompanyId == company && ids.Contains(x.Id) &&
            x.Kind != FxPositionKind.CustomerAdvance && x.Kind != FxPositionKind.SupplierAdvance).OrderBy(x => x.Id).ToListAsync(ct);
        if (positions.Count == 0) throw Conflict("No open monetary items are eligible at cutoff.");
        if (positions.Count > MaxRevaluationPositions)
            throw Conflict($"This cutoff has {positions.Count} open monetary positions; a revaluation supports at most {MaxRevaluationPositions}.");
        var selected = new Dictionary<Guid, FxRate>();
        var evidence = new List<FxValuation>();
        foreach (var position in positions)
        {
            if (!selected.TryGetValue(position.CurrencyId, out var rate))
            {
                rate = await rates.Select(company, position.CurrencyId, input.Date, FxRateType.Closing, 0, ct);
                selected[position.CurrencyId] = rate;
            }
            var balance = balances[position.Id];
            var target = Convert(balance.ForeignAmount, rate.ExchangeRate, units);
            var delta = target - balance.BaseAmount;
            draft.Move(position, 0, delta, rate.Id);
            draft.Difference(Sign(position.Kind) * delta, true);
            evidence.Add(new(position.Id, rate.Id, balance.ForeignAmount, balance.BaseAmount, target));
        }
        return draft.Plan(valuations: evidence);
    }

    private async Task<FxPlan> Correct(Guid company, FxRequest input, Draft draft, CancellationToken ct)
    {
        var original = await queries.Posted(company, input.OriginalId!.Value, ct);
        if (original.Kind != FxKind.Revaluation || original.Date > input.Date ||
            await db.FxOperations.AnyAsync(x => x.CompanyId == company && x.State == FxState.Posted && x.Sequence > original.Sequence, ct))
            throw Conflict("Only the latest posted revaluation can be corrected before any subsequent FX activity.");
        var plan = FxQueries.Plan(original);
        foreach (var movement in plan.Movements)
        {
            var position = await queries.Position(company, movement.PositionId, ct);
            draft.Move(position, 0, -movement.BaseDelta, movement.RateId);
        }
        // Reverse the original P&L accounts, rather than classifying a reversed gain as a new loss.
        foreach (var line in plan.Lines.Where(x => x.AccountId == draft.Account(FxAccountRole.UnrealizedGain) ||
            x.AccountId == draft.Account(FxAccountRole.UnrealizedLoss)))
            draft.Line(line.AccountId, line.Credit - line.Debit);
        return draft.Plan() with { Unrealized = -plan.Unrealized };
    }
}
