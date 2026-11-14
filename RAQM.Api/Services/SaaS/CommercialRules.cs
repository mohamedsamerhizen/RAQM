using RAQM.Api.DTOs.SaaS;
using RAQM.Api.Entities.SaaS;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;

namespace RAQM.Api.Services.SaaS;

public static class CommercialRules
{
    public static readonly string[] Features = ["fixed-assets", "inventory", "budgeting", "foreign-exchange", "dimensions",
        "consolidation", "tax", "governance", "integrations", "webhooks", "exports"];

    public static ApiException Invalid(string message) => new(400, message, "saas_input");
    public static ApiException Conflict(string message) => new(409, message, "saas_conflict");
    public static ApiException Missing() => new(404, "Commercial resource not found.", "saas_missing");

    public static DateOnly Boundary(DateOnly anchor, int cycle, CommercialInterval interval) =>
        anchor.AddMonths(checked(cycle * (interval == CommercialInterval.Annual ? 12 : 1)));

    public static DateTimeOffset Midnight(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    public static DateOnly Today(DateTimeOffset utc) => DateOnly.FromDateTime(utc.UtcDateTime);

    public static BillingLine[] Charges(CommercialPlan plan, int seats)
    {
        var extra = Math.Max(0, seats - plan.IncludedSeats);
        var total = plan.ExtraSeatPrice * extra;
        CurrencyPolicy.Validate(plan.Price + total, CurrencyPolicy.Units(plan.Currency));
        return [new("Recurring plan", 1, plan.Price, plan.Price), new("Additional organization seats", extra, plan.ExtraSeatPrice, total)];
    }

    public static BillingLine[] Prorate(CommercialPlan current, CommercialPlan next, int seats, DateOnly from, DateOnly to, DateOnly at)
    {
        if (at < from || at >= to || to <= from || current.Currency != next.Currency || current.Interval != next.Interval)
            throw Invalid("Use an active period and equivalent currency and billing interval.");
        var units = CurrencyPolicy.Units(current.Currency);
        var fraction = (to.DayNumber - at.DayNumber) / (decimal)(to.DayNumber - from.DayNumber);
        var oldValue = decimal.Round(Charges(current, seats).Sum(x => x.Amount) * fraction, units, MidpointRounding.ToEven);
        var newValue = decimal.Round(Charges(next, seats).Sum(x => x.Amount) * fraction, units, MidpointRounding.ToEven);
        return [new("Unused plan credit", 1, -oldValue, -oldValue), new("Remaining new plan", 1, newValue, newValue)];
    }

    public static string Status(CommercialSubscription sub, DateTimeOffset now, decimal open)
    {
        if (sub.Cancelled) return "cancelled";
        if (sub.Trial) return now < sub.AccessUntil ? "trialing" : "expired";
        if (sub.CancelAtEnd && now < Midnight(sub.End)) return "cancellation-scheduled";
        if (now >= sub.AccessUntil) return open > 0 ? "past-due" : "expired";
        return open > 0 ? "grace" : "active";
    }

    public static decimal Outstanding(decimal total, IEnumerable<CommercialMovement> movements) =>
        total - movements.Where(x => x.VerifiedAt.HasValue).Sum(x => x.Kind == CommercialMovementKind.Refund ? -x.Amount : x.Amount);
}
