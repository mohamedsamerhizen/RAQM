using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.Helpers.Accounting;
public static class CurrencyPolicy
{
    public const int Version = 1;
    // Versioned supported subset of ISO 4217; expanding it requires explicit policy review.
    public static IReadOnlyDictionary<string, int> Supported { get; } = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal) { ["IQD"] = 3, ["KWD"] = 3, ["BHD"] = 3, ["JOD"] = 3, ["OMR"] = 3, ["TND"] = 3, ["USD"] = 2, ["EUR"] = 2, ["GBP"] = 2, ["AED"] = 2, ["SAR"] = 2, ["CAD"] = 2, ["AUD"] = 2, ["CHF"] = 2, ["CNY"] = 2, ["JPY"] = 0, ["KRW"] = 0 });

    public static int Units(string? currency) => currency is not null && Supported.TryGetValue(currency, out var units) ? units : throw new ApiException(400, "Currency is not supported by accounting currency policy v1.", "currency_unsupported");
    public const decimal Maximum = 9999999999999999.9999m; // numeric(20,4)
    public static void Validate(decimal value, int units)
    {
        if (value < 0 || value > Maximum || decimal.Round(value, units, MidpointRounding.ToEven) != value)
            throw new ApiException(400, "Amount exceeds numeric(20,4) or the currency's minor-unit precision; round explicitly before submission.", "amount_precision");
    }
}

public static class AccountingRules
{
    // PostgreSQL timestamps retain microseconds; make the initial receipt identical to a DB-loaded retry.
    public static DateTimeOffset StoredUtcInstant(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
    public static string Text(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max)
            throw new ApiException(400, $"{field} must contain 1 to {max} characters.", "invalid_configuration");
        return value.Trim();
    }

    public static void OptionalText(string? value, int max)
    {
        if (value?.Length > max)
            throw new ApiException(400, $"Text cannot exceed {max} characters.", "invalid_configuration");
    }

    public static BalanceSide Normal(AccountClass type, bool contra) => ((type is AccountClass.Asset or AccountClass.Expense) != contra) ? BalanceSide.Debit : BalanceSide.Credit;
    public static (decimal Debit, decimal Credit) Lines(IReadOnlyList<JournalDraftLine>? lines, string currency, bool requireBalance)
    {
        var units = CurrencyPolicy.Units(currency);
        if (lines is null || lines.Count < (requireBalance ? 2 : 1) || lines.Count > 1000)
            throw new ApiException(400, "A journal requires 1..1000 draft lines and at least two lines to post.", "journal_lines");
        decimal debit = 0, credit = 0;
        foreach (var line in lines)
        {
            if (line is null || line.AccountId == Guid.Empty)
                throw new ApiException(400, "An account is required on every line.", "account_required");
            CurrencyPolicy.Validate(line.Debit, units);
            CurrencyPolicy.Validate(line.Credit, units);
            if ((line.Debit > 0) == (line.Credit > 0))
                throw new ApiException(400, "Each line must have exactly one positive debit or credit.", "line_sides");
            OptionalText(line.Description, 500);
            debit += line.Debit;
            credit += line.Credit;
            if (debit > CurrencyPolicy.Maximum || credit > CurrencyPolicy.Maximum)
                throw new ApiException(400, "Journal total exceeds supported precision.", "amount_precision");
        }

        if (requireBalance && debit != credit)
            throw new ApiException(400, "Journal debits and credits must balance exactly.", "journal_unbalanced");
        return (debit, credit);
    }

    public static void Hierarchy(Guid accountId, Guid? parent, IReadOnlyDictionary<Guid, Guid?> parents)
    {
        var seen = new HashSet<Guid>
        {
            accountId
        };
        while (parent is { } id)
        {
            if (!seen.Add(id))
                throw new ApiException(400, "Account hierarchy contains a cycle.", "account_cycle");
            if (!parents.TryGetValue(id, out parent))
                throw new ApiException(400, "Parent account is outside this company.", "account_parent");
        }
    }

    public static void Page(int page, int size)
    {
        if (page < 1 || page > 100000 || size < 1 || size > 200)
            throw new ApiException(400, "Page must be 1..100000; pageSize must be 1..200.", "invalid_pagination");
    }

    public static HashSet<Guid> Descendants(Guid root, IReadOnlyDictionary<Guid, Guid?> parents)
    {
        var children = parents.Where(x => x.Value.HasValue).ToLookup(x => x.Value.GetValueOrDefault(), x => x.Key);
        var result = new HashSet<Guid>
        {
            root
        };
        var pending = new Queue<Guid>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var parent))
            foreach (var child in children[parent])
            {
                if (!result.Add(child))
                    throw new ApiException(400, "Account hierarchy contains a cycle.", "account_cycle");
                pending.Enqueue(child);
            }

        return result;
    }

    public static string Fingerprint(PostingRequest r) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { r.Scope, r.SourceModule, r.SourceDocumentId, r.AccountingDate, r.DocumentDate, r.Currency, r.Description, r.Reference, r.ReversesJournalId, Lines = r.Lines.Select(l => new { l.AccountId, Debit = l.Debit.ToString("0.####", CultureInfo.InvariantCulture), Credit = l.Credit.ToString("0.####", CultureInfo.InvariantCulture), l.Description }).ToArray() }))));
}
