using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FixedAssets;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Helpers.Accounting;

namespace RAQM.Api.Helpers.FixedAssets;

public static class AssetMath
{
    public static string Source(AssetOperationKind kind) => $"FixedAssets.{kind}";

    public static ComponentBalance Balance(AssetComponent component, IEnumerable<AssetMovement> entries)
    {
        var state = new ComponentBalance(component.Id, 0, 0, 0, 0, component.InitialResidual, component.LifeMonths, 0);
        foreach (var row in entries.OrderBy(x => x.Date).ThenBy(x => x.Sequence))
        {
            state = state with
            {
                Cost = state.Cost + row.Cost,
                Depreciation = state.Depreciation + row.Depreciation,
                Impairment = state.Impairment + row.Impairment,
                ShadowDepreciation = state.ShadowDepreciation + row.ShadowDepreciation,
                Residual = row.ResidualOverride ?? state.Residual,
                RemainingMonths = row.RemainingOverride ?? Math.Max(0, state.RemainingMonths - row.Units),
                ReversibleImpairment = row.Cost < 0 ? 0 : state.ReversibleImpairment + (row.Cost == 0 ? row.Impairment : 0)
            };
            var termsChanged = row.Cost != 0 || row.RemainingOverride.HasValue || row.ResidualOverride.HasValue;
            if (termsChanged || row.Impairment != 0)
                state = state with { DepreciationBasis = Math.Max(0, state.NetBookValue - state.Residual), BasisMonths = state.RemainingMonths };
            if (termsChanged)
                state = state with { ShadowBasis = Math.Max(0, state.Cost - state.ShadowDepreciation - state.Residual), ShadowBasisMonths = state.RemainingMonths };
        }
        return state;
    }

    public static decimal Fraction(DateOnly periodStart, DateOnly periodEnd, DateOnly available, DateOnly through)
    {
        var start = available > periodStart ? available : periodStart;
        var end = through < periodEnd ? through : periodEnd;
        if (end < start) return 0;
        return decimal.Round((decimal)(end.DayNumber - start.DayNumber + 1) /
            (periodEnd.DayNumber - periodStart.DayNumber + 1), 6, MidpointRounding.ToEven);
    }

    public static (decimal Charge, decimal Shadow, decimal Used) Depreciate(ComponentBalance state, decimal fraction, string currency)
    {
        if (state.Cost == 0 || state.RemainingMonths <= 0 || fraction <= 0) return (0, 0, 0);
        var used = Math.Min(fraction, state.RemainingMonths);
        var units = CurrencyPolicy.Units(currency);
        decimal Charge(decimal carrying, decimal? basis, decimal? basisMonths)
        {
            var room = Math.Max(0, carrying - state.Residual);
            return used == state.RemainingMonths ? room :
                Math.Min(room, decimal.Round((basis ?? room) / (basisMonths is > 0 ? basisMonths.Value : state.RemainingMonths) * used,
                    units, MidpointRounding.ToEven));
        }
        return (Charge(state.NetBookValue, state.DepreciationBasis, state.BasisMonths),
            Charge(state.Cost - state.ShadowDepreciation, state.ShadowBasis, state.ShadowBasisMonths), used);
    }

    public static decimal Recovery(ComponentBalance balance, decimal recoverable) =>
        Math.Max(0, Math.Min(Math.Min(balance.Impairment, balance.ReversibleImpairment),
            Math.Min(recoverable, balance.Cost - balance.ShadowDepreciation) - balance.NetBookValue));

    public static IReadOnlyList<JournalDraftLine> Disposal(Guid cost, Guid accumulatedDepreciation, Guid accumulatedImpairment,
        Guid gain, Guid loss, Guid? consideration, decimal gross, decimal depreciation, decimal impairment, decimal proceeds,
        Guid? cash = null, decimal costs = 0)
    {
        var result = new List<JournalDraftLine>();
        if (proceeds > 0) result.Add(new(consideration!.Value, proceeds, 0, "Disposal consideration"));
        if (depreciation > 0) result.Add(new(accumulatedDepreciation, depreciation, 0, "Clear accumulated depreciation"));
        if (impairment > 0) result.Add(new(accumulatedImpairment, impairment, 0, "Clear accumulated impairment"));
        result.Add(new(cost, 0, gross, "Derecognize fixed asset cost"));
        if (costs > 0) result.Add(new(cash!.Value, 0, costs, "Pay direct disposal costs"));
        var net = proceeds - costs - (gross - depreciation - impairment);
        if (net > 0) result.Add(new(gain, 0, net, "Gain on disposal"));
        if (net < 0) result.Add(new(loss, -net, 0, "Loss on disposal"));
        return result;
    }
}
