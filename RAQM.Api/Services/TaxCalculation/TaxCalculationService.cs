using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.TaxAccounting;
using RAQM.Api.DTOs.Trading;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.TaxJurisdictions;
using static RAQM.Api.Services.TaxJurisdictions.TaxConfigurationService;

namespace RAQM.Api.Services.TaxCalculation;

public sealed class TaxCalculationService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transaction,
    TaxConfigurationService configuration, ITradeDocumentService documents)
{
    public async Task<TaxQuote> Preview(Guid actor, Guid org, Guid company, Guid registration, TaxQuoteRequest input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var quote = await Calculate(scope, registration, input, ct);
        await tx.CommitAsync(ct);
        return quote;
    }

    public Task<TaxCommercialResponse> Create(Guid actor, Guid org, Guid company, Guid registration, TaxCommercialRequest input, CancellationToken ct) =>
        transaction.Execute(actor, org, company, async (scope, token) =>
        {
            var quote = await Calculate(scope, registration, input.Quote, token);
            var profile = await configuration.Registration(company, registration, token, quote.Date);
            if (profile.Currency != quote.Currency || quote.Lines.Any(x => x.Components.Length != 1))
                throw Conflict("Commercial posting requires functional currency and one applicable component per line.");
            var role = quote.Side == PartyKind.Customer ? TaxControlRole.Output : TaxControlRole.Input;
            var control = await db.Set<TaxControl>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.RegistrationId == registration && x.Role == role, token);
            foreach (var rateId in quote.Lines.Select(x => x.Components[0].RateId).OfType<Guid>().Distinct())
            {
                var account = await (from rate in db.TradingTaxRates join code in db.TradingTaxCodes on rate.TaxCodeId equals code.Id
                    where rate.CompanyId == company && code.CompanyId == company && rate.Id == rateId
                    select quote.Side == PartyKind.Customer ? code.OutputAccountId : code.InputAccountId).SingleAsync(token);
                if (control?.AccountId != account) throw Conflict("The effective tax rate must use this registration's designated control.");
            }
            var lines = input.Quote.Lines.Select((x, i) => new TradeLineRequest(x.Description, x.Quantity, x.UnitPrice, x.DiscountPercent,
                x.AccountId, quote.Lines[i].Components[0].RateId, x.Inclusive && quote.Lines[i].Components[0].RateId.HasValue)).ToArray();
            var result = await documents.Create(actor, org, company, new(quote.PartyId,
                quote.Side == PartyKind.Customer ? TradeKind.Invoice : TradeKind.Bill, quote.Date, input.DueDate, input.Description,
                input.ExternalReference, lines), token);
            if (!result.Lines.Select(x => (x.NetAmount, x.TaxAmount, x.Total)).SequenceEqual(quote.Lines.Select(x => (x.Net, x.Tax, x.Total))))
                throw Conflict("Commercial draft amounts differ from the tax quote.");
            var record = Scoped(scope, new CommercialTaxEvidence { RegistrationId = registration, DocumentId = result.Id, ActorId = actor,
                SnapshotJson = JsonSerializer.Serialize(quote) });
            db.Add(record);
            access.Audit(scope, actor, record.Id, "CommercialTaxEvidenceRetained");
            return new TaxCommercialResponse(result, record.Id, quote);
        }, ct);

    private async Task<TaxQuote> Calculate(Company scope, Guid registration, TaxQuoteRequest input, CancellationToken ct)
    {
        if (input is null || input.Lines is null || input.Lines.Count is < 1 or > 300 || input.Lines.Any(x => x is null) || input.Date.Year < 1900)
            throw Bad("Provide a tax date and 1 to 300 valid pricing lines.");
        await configuration.Registration(scope.Id, registration, ct, input.Date);
        var party = await db.TradingParties.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == scope.Id && x.Id == input.PartyId && x.IsActive, ct)
            ?? throw Missing();
        var group = Code(input.Group);
        var candidates = await db.Set<TaxRule>().AsNoTracking().Where(x => x.CompanyId == scope.Id && x.RegistrationId == registration &&
            x.Group == group && x.Side == party.Kind && x.ApprovedBy != null && x.EffectiveOn <= input.Date &&
            x.EndsOn >= input.Date && (x.PartyId == null || x.PartyId == party.Id)).ToListAsync(ct);
        var rateIds = candidates.Select(x => x.RateId).OfType<Guid>().Distinct().ToArray();
        var rates = await (from rate in db.TradingTaxRates join code in db.TradingTaxCodes on rate.TaxCodeId equals code.Id
            where rate.CompanyId == scope.Id && code.CompanyId == scope.Id && rateIds.Contains(rate.Id) && code.IsActive &&
                rate.EffectiveFrom <= input.Date && rate.EffectiveTo >= input.Date select rate).ToDictionaryAsync(x => x.Id, ct);
        var lines = input.Lines.Select((line, index) =>
        {
            var item = Code(line.ItemClass);
            var rules = candidates.Where(x => x.ItemClass is null || x.ItemClass == item).GroupBy(x => x.Order)
                .OrderBy(x => x.Key).Select(x => x.MaxBy(y => (y.PartyId.HasValue ? 2 : 0) + (y.ItemClass is not null ? 1 : 0))!).ToArray();
            if (rules.Length == 0 || rules.Any(x => x.RateId is { } id && !rates.ContainsKey(id)))
                throw Conflict("An effective approved rule and active rate are required for every line.");
            var exemption = rules.Any(x => x.Treatment == TaxTreatment.Exempt)
                ? AccountingRules.Text(line.ExemptionEvidence, 500, "Exemption evidence") : null;
            return Price(index + 1, line, rules.Select(x => (x, x.RateId is { } id ? rates[id].RatePercent : 0m)).ToArray(), scope.BaseCurrency, exemption);
        }).ToArray();
        return new(scope.BaseCurrency, party.Kind, party.Id, input.Date, group, lines, scope.Name);
    }

    public static QuotedTaxLine Price(int number, TaxPricingLine input, (TaxRule Rule, decimal Rate)[] components,
        string currency, string? exemption)
    {
        if (components.Length == 0 || components.Length > 1 && components.Any(x => x.Rule.Treatment is TaxTreatment.Exempt or TaxTreatment.OutsideScope))
            throw Conflict("Exempt and outside-scope rules must be exclusive.");
        var totalRate = components.Sum(x => x.Rate);
        var amount = TradeRules.Calculate(input.Quantity, input.UnitPrice, input.DiscountPercent, totalRate, input.Inclusive, currency);
        var units = CurrencyPolicy.Units(currency);
        var remaining = amount.Tax;
        var remainingRate = totalRate;
        var applied = new List<AppliedTax>();
        foreach (var (rule, rate) in components)
        {
            var tax = rate == 0 ? 0 : rate == remainingRate ? remaining : decimal.Round(remaining * rate / remainingRate, units, MidpointRounding.ToEven);
            applied.Add(new(rule.Id, rule.RateId, rule.Treatment, rate, amount.Net, tax, rule.Evidence));
            remaining -= tax;
            remainingRate -= rate;
        }
        return new(number, amount.Net, amount.Tax, amount.Total, applied.ToArray(), exemption);
    }
}
