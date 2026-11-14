using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;

namespace RAQM.Api.Helpers.Trading;
public sealed record CalculatedLine(decimal Gross, decimal Discount, decimal Net, decimal Tax, decimal Total);
public static class TradeRules
{
    public static bool IsCredit(TradeKind kind) => kind is TradeKind.CustomerCredit or TradeKind.SupplierCredit;
    public static bool IsOpening(TradeKind kind) => kind is TradeKind.OpeningReceivable or TradeKind.OpeningPayable;
    public static PartyKind Side(TradeKind kind) => kind is TradeKind.Invoice or TradeKind.CustomerCredit or TradeKind.OpeningReceivable ? PartyKind.Customer : PartyKind.Supplier;
    public static string Module(TradeKind kind) => kind switch
    {
        TradeKind.Invoice => "AR.Invoice",
        TradeKind.Bill => "AP.Bill",
        TradeKind.CustomerCredit => "AR.Credit",
        TradeKind.SupplierCredit => "AP.Credit",
        TradeKind.OpeningReceivable => "AR.Opening",
        TradeKind.OpeningPayable => "AP.Opening",
        _ => throw new ApiException(400, "Invalid document kind.", "document_kind")};
    public static string Prefix(TradeKind kind) => kind switch
    {
        TradeKind.Invoice => "SI",
        TradeKind.Bill => "PB",
        TradeKind.CustomerCredit => "SC",
        TradeKind.SupplierCredit => "PC",
        TradeKind.OpeningReceivable => "OR",
        _ => "OP"
    };
    public static string Key(string prefix, Guid id, string key) => $"{prefix}:{id:N}:{AccountingRules.Text(key, 64, "Idempotency key")}";
    private sealed class CanonicalDecimal : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteRawValue(value.ToString("0.######", CultureInfo.InvariantCulture));
    }

    private static readonly JsonSerializerOptions HashOptions = new()
    {
        Converters =
        {
            new CanonicalDecimal()
        }
    };
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, HashOptions))));
    public static void Rate(decimal percent)
    {
        if (percent < 0 || percent > 100 || decimal.Round(percent, 6) != percent)
            throw new ApiException(400, "Rates/discounts must be 0..100 with at most six decimals.", "tax_rate");
    }

    public static CalculatedLine Calculate(decimal quantity, decimal price, decimal discount, decimal rate, bool inclusive, string currency)
    {
        var units = CurrencyPolicy.Units(currency);
        Rate(discount);
        Rate(rate);
        if (quantity <= 0 || quantity > 1000000000m || decimal.Round(quantity, 6) != quantity || price < 0 || price > CurrencyPolicy.Maximum || decimal.Round(price, 4) != price)
            throw new ApiException(400, "Quantity/price is outside the supported positive quantity(6) and nonnegative price(4) precision.", "line_precision");
        if (price > CurrencyPolicy.Maximum / quantity)
            throw new ApiException(400, "Line amount exceeds supported precision.", "amount_precision");
        decimal Round(decimal v) => decimal.Round(v, units, MidpointRounding.ToEven);
        var gross = Round(quantity * price);
        var deduction = Round(gross * discount / 100);
        var discounted = gross - deduction;
        var net = inclusive ? Round(discounted / (1 + rate / 100)) : discounted;
        var tax = inclusive ? discounted - net : Round(net * rate / 100);
        CurrencyPolicy.Validate(net + tax, units);
        return new(gross, deduction, net, tax, net + tax);
    }

    public static CalculatedLine Credit(TradeDocumentLine original, decimal quantity, decimal usedQuantity, decimal usedNet, decimal usedTax, decimal usedDiscount, string currency)
    {
        if (quantity <= 0 || quantity > original.Quantity - usedQuantity)
            throw new ApiException(409, "Credit quantity exceeds the original uncredited quantity.", "over_credit");
        var result = Calculate(quantity, original.UnitPrice, original.DiscountPercent, original.TaxPercent, original.TaxInclusive, currency);
        if (quantity == original.Quantity - usedQuantity)
            result = new(result.Gross, original.DiscountAmount - usedDiscount, original.NetAmount - usedNet, original.TaxAmount - usedTax, original.Total - usedNet - usedTax);
        if (result.Net < 0 || result.Tax < 0 || result.Discount < 0 || result.Discount > original.DiscountAmount - usedDiscount || result.Net > original.NetAmount - usedNet || result.Tax > original.TaxAmount - usedTax)
            throw new ApiException(409, "Credit would exceed an original net/tax component.", "over_credit");
        return result;
    }

    public static decimal AllocationSign(SettlementAllocation a) => a.ReversesAllocationId.HasValue ? -1m : 1m;
    public static bool Active(SettlementPayment p, DateOnly at) => p.State != SettlementState.Prepared && p.PostingDate <= at && (p.State != SettlementState.Reversed || p.ReversalDate > at);
    public static string AgingBucket(int days) => days <= 0 ? "Current" : days <= 30 ? "1-30" : days <= 60 ? "31-60" : days <= 90 ? "61-90" : "90+";
    public static IReadOnlyList<JournalDraftLine> DocumentPosting(TradeDocument d)
    {
        var controlDebit = (Side(d.Kind) == PartyKind.Customer) != IsCredit(d.Kind);
        var result = new List<JournalDraftLine>
        {
            new(d.ControlAccountId, controlDebit ? d.Total : 0, controlDebit ? 0 : d.Total, "Counterparty control")
        };
        foreach (var line in d.Lines.OrderBy(x => x.Number))
        {
            if (line.NetAmount > 0)
                result.Add(new(line.AccountId, controlDebit ? 0 : line.NetAmount, controlDebit ? line.NetAmount : 0, line.Description));
            if (line.TaxAmount > 0)
                result.Add(new(line.TaxAccountId ?? throw new ApiException(400, "Tax account missing.", "tax_account"), controlDebit ? 0 : line.TaxAmount, controlDebit ? line.TaxAmount : 0, "Document tax"));
        }

        AccountingRules.Lines(result, d.Currency, true);
        return result;
    }

    public static IReadOnlyList<JournalDraftLine> PaymentPosting(SettlementPayment p)
    {
        var cashDebit = (p.PartyKind == PartyKind.Customer) != p.IsRefund;
        var result = new List<JournalDraftLine>
        {
            new(p.CashAccountId, cashDebit ? p.Amount : 0, cashDebit ? 0 : p.Amount, "Cash settlement")
        };
        if (p.ControlAmount > 0)
            result.Add(new(p.ControlAccountId, cashDebit ? 0 : p.ControlAmount, cashDebit ? p.ControlAmount : 0, "Control settlement"));
        if (p.AdvanceAmount > 0)
            result.Add(new(p.AdvanceAccountId, cashDebit ? 0 : p.AdvanceAmount, cashDebit ? p.AdvanceAmount : 0, "Advance settlement"));
        AccountingRules.Lines(result, p.Currency, true);
        return result;
    }

    public static IReadOnlyList<JournalDraftLine> AllocationPosting(SettlementAllocation a)
    {
        if (a.AdvanceAmount <= 0)
            return[];
        var customer = a.PartyKind == PartyKind.Customer;
        return[new(a.AdvanceAccountId!.Value, customer ? a.AdvanceAmount : 0, customer ? 0 : a.AdvanceAmount, "Apply advance"), new(a.ControlAccountId, customer ? 0 : a.AdvanceAmount, customer ? a.AdvanceAmount : 0, "Settle control")];
    }
}
