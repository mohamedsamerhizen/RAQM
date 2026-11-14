using System.Text.Json;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Helpers.Accounting;

namespace RAQM.Api.Helpers.FinancialDimensions;

public static class DimensionMath
{
    public static ApiException Bad(string message) => new(400, message, "dimension_input");
    public static ApiException Conflict(string message) => new(409, message, "dimension_conflict");
    public static ApiException Missing(string message) => new(404, message, "dimension_missing");
    public static bool Active(DimensionValue value, DateOnly date) =>
        value.EffectiveDate <= date && (value.InactiveOn == null || value.InactiveOn > date);
    public static DimensionLine[] Read(DimensionBinding? binding) =>
        binding is null ? [] : JsonSerializer.Deserialize<DimensionLine[]>(binding.ValuesJson)!;

    public static EffectiveDimension? Resolve(Guid definition, DimensionPick? selection, DimensionRule? account,
        DimensionRule? company, Guid? binding, DimensionOrigin selectedOrigin)
    {
        var rule = account ?? company;
        if (rule?.Mode == DimensionMode.Prohibited)
        {
            if (selection?.ValueId is not null) throw Conflict($"Dimension {definition} is prohibited for this account.");
            return selection is null ? null : new(definition, null, rule.Id, binding, DimensionOrigin.Unassigned);
        }
        var value = selection?.ValueId;
        var origin = selectedOrigin;
        if (rule?.Mode == DimensionMode.Fixed)
        {
            if (selection is not null && value != rule.DefaultValueId)
                throw Conflict($"Dimension {definition} conflicts with its fixed value.");
            value = rule.DefaultValueId;
            origin = DimensionOrigin.Fixed;
        }
        else if (selection is null)
        {
            value = account?.DefaultValueId ?? company?.DefaultValueId;
            origin = account?.DefaultValueId is not null ? DimensionOrigin.AccountDefault : DimensionOrigin.CompanyDefault;
        }
        if (value is null)
        {
            if (rule?.Mode == DimensionMode.Required) throw Conflict($"Dimension {definition} is required.");
            return selection is null ? null : new(definition, null, rule?.Id, binding, DimensionOrigin.Unassigned);
        }
        var allowed = rule is null ? [] : JsonSerializer.Deserialize<Guid[]>(rule.AllowedJson)!;
        if (allowed.Length > 0 && !allowed.Contains(value.Value))
            throw Conflict($"Value {value} is outside the permitted values for dimension {definition}.");
        return new(definition, value, rule?.Id, selection is null ? null : binding, origin);
    }

    public static decimal[] Distribute(decimal amount, AllocationBasis basis, IReadOnlyList<decimal> weights, int units)
    {
        CurrencyPolicy.Validate(amount, units);
        if (amount <= 0 || !Enum.IsDefined(basis) || weights.Count == 0 || weights.Any(x => x <= 0 || x > CurrencyPolicy.Maximum || decimal.Round(x, 6) != x))
            throw Bad("Provide positive supported allocation weights and expense.");
        var weight = weights.Sum();
        if (basis == AllocationBasis.Amount)
        {
            foreach (var value in weights) CurrencyPolicy.Validate(value, units);
            return weight == amount ? weights.ToArray() : throw Bad("Explicit allocations must equal the expense.");
        }
        if (basis == AllocationBasis.Percent && weight != 100) throw Bad("Percentages must total 100.");
        var remaining = amount;
        var result = new decimal[weights.Count];
        for (var i = 0; i < weights.Count; i++)
        {
            result[i] = i == weights.Count - 1 ? remaining : decimal.Round(remaining * (weights[i] / weight), units, MidpointRounding.ToEven);
            remaining -= result[i];
            weight -= weights[i];
        }
        return result;
    }
}
