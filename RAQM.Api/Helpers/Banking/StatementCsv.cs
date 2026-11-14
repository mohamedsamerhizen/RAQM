using System.Globalization;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;

namespace RAQM.Api.Helpers.Banking;

public static class StatementCsv
{
    public static StatementPreview Parse(StatementRequest request, string currency)
    {
        if (request.Csv is null || request.Csv.Length > 1_000_000)
            throw new ApiException(400, "CSV is required and must be at most 1,000,000 characters.", "csv_size");
        if (request.Delimiter is not ("," or ";" or "\t"))
            throw new ApiException(400, "Use comma, semicolon or tab as the delimiter.", "csv_delimiter");
        var rows = new List<StatementRow>();
        var errors = new List<ImportIssue>();
        var warnings = new List<ImportIssue>();
        if (request.Start.Year < 1900 || request.End < request.Start)
            errors.Add(new(0, "Invalid statement date range."));
        var units = CurrencyPolicy.Units(currency);
        if (!Money(request.Opening, units) || !Money(request.Closing, units))
            errors.Add(new(0, "Opening and closing balances must use currency minor units."));
        var columns = request.Columns ?? new();
        using var parser = new TextFieldParser(new StringReader(request.Csv.TrimStart('\uFEFF')))
        {
            TextFieldType = FieldType.Delimited, TrimWhiteSpace = false, HasFieldsEnclosedInQuotes = true
        };
        parser.SetDelimiters(request.Delimiter);
        var stableIds = new HashSet<string>(StringComparer.Ordinal);
        var seenRows = new HashSet<(DateOnly, DateOnly?, decimal, string, string)>();
        try
        {
            var header = parser.ReadFields()?.Select(x => x.Trim()).ToArray() ?? [];
            if (header.Length == 0 || header.Distinct(StringComparer.OrdinalIgnoreCase).Count() != header.Length)
                errors.Add(new(1, "CSV must have distinct column headers."));
            var required = new[] { columns.BookingDate, columns.Amount, columns.Reference, columns.Description };
            var optional = new[] { columns.ValueDate, columns.StableId, columns.Direction };
            var names = required.Concat(optional.Where(x => x is not null)).ToArray();
            if (required.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
                names.Any(name => !header.Contains(name, StringComparer.OrdinalIgnoreCase)))
                errors.Add(new(1, "Column mapping must reference distinct columns in the header."));
            if (errors.Any(x => x.Row == 1))
                return new(rows, errors, warnings, request.Opening, "");
            var indices = header.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);
            var records = 0;
            while (!parser.EndOfData)
            {
                var row = checked((int)parser.LineNumber);
                if (++records > 5000)
                {
                    errors.Add(new(row, "A statement supports up to 5,000 rows."));
                    break;
                }
                string[] fields;
                try { fields = parser.ReadFields() ?? []; }
                catch (MalformedLineException)
                {
                    errors.Add(new(row, "Malformed quoted CSV row."));
                    continue;
                }
                if (fields.Length != header.Length)
                {
                    errors.Add(new(row, "Field count does not match the header."));
                    continue;
                }
                string Read(string? name) => name is null ? "" : fields[indices[name]].Trim();
                var reference = Read(columns.Reference);
                var description = Read(columns.Description);
                var valueText = Read(columns.ValueDate);
                var stableId = Read(columns.StableId);
                var valid = Date(Read(columns.BookingDate), out var booking) && booking >= request.Start && booking <= request.End;
                DateOnly? valueDate = null;
                if (valueText.Length > 0)
                {
                    if (Date(valueText, out var value) && value.Year >= 1900) valueDate = value;
                    else valid = false;
                }
                var amountValid = decimal.TryParse(Read(columns.Amount), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var amount) && Money(amount, units) && amount != 0;
                if (columns.Direction is not null)
                {
                    var direction = Read(columns.Direction).ToUpperInvariant();
                    amountValid &= amount > 0 && direction is "CREDIT" or "DEBIT";
                    if (amountValid && direction == "DEBIT") amount = -amount;
                }
                var original = JsonSerializer.Serialize(header.Zip(fields).ToDictionary(x => x.First, x => x.Second));
                if (!valid || !amountValid || reference.Length > 160 || description.Length > 500 || stableId.Length > 160 || original.Length > 8000)
                {
                    errors.Add(new(row, "Invalid dates, direction, currency amount, field length or original row size."));
                    continue;
                }
                if (stableId.Length > 0 && !stableIds.Add(stableId))
                {
                    errors.Add(new(row, "Duplicate stable bank transaction identifier."));
                    continue;
                }
                if (!seenRows.Add((booking, valueDate, amount, reference, description)) && stableId.Length == 0)
                    warnings.Add(new(row, "Identical movement without a stable ID. Confirm with the bank before import."));
                if (valueDate.HasValue && valueDate != booking)
                    warnings.Add(new(row, "Value date differs from booking date. Both dates are retained."));
                rows.Add(new(row, booking, valueDate, amount, reference, description, stableId.Length == 0 ? null : stableId, original));
            }
        }
        catch (MalformedLineException)
        {
            errors.Add(new(1, "Malformed CSV header."));
        }
        var calculated = request.Opening;
        if (Money(request.Opening, units))
            calculated += rows.Sum(x => x.Amount);
        if (calculated != request.Closing)
            errors.Add(new(0, "Opening balance plus signed movements must equal closing balance."));
        var digest = TradeRules.Hash(new { request.Start, request.End, request.Opening, request.Closing,
            Lines = rows.Select(x => new { x.BookingDate, x.ValueDate, x.Amount, x.Reference, x.Description, x.StableId, x.OriginalFields }).ToArray() });
        return new(rows, errors, warnings, calculated, digest);
    }

    private static bool Money(decimal value, int units) =>
        value >= -CurrencyPolicy.Maximum && value <= CurrencyPolicy.Maximum && decimal.Round(value, units) == value;
    private static bool Date(string input, out DateOnly date) =>
        DateOnly.TryParseExact(input, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
