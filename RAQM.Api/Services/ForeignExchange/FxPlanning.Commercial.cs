using Microsoft.EntityFrameworkCore;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.MultiCurrency;
using RAQM.Api.Services.Trading;
using static RAQM.Api.Helpers.MultiCurrency.FxMath;

namespace RAQM.Api.Services.ForeignExchange;

public sealed partial class FxPlanning
{
    private async Task<FxPlan> Recognition(Guid company, FxPosition position, FxRequest input, FxConfiguration setup,
        Draft draft, int units, CancellationToken ct)
    {
        var party = await db.TradingParties.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == position.PartyId && x.IsActive, ct)
            ?? throw Bad("Choose an active customer or supplier in this company.");
        var currency = await queries.Currency(company, position.CurrencyId, ct);
        var isInvoice = input.Kind == FxKind.Invoice;
        if (party.Kind != (isInvoice ? PartyKind.Customer : PartyKind.Supplier))
            throw Bad("The counterparty side is inconsistent with the document.");
        var side = isInvoice ? 1 : -1;
        var details = new List<FxCommercialLine>();
        foreach (var line in input.Lines!)
        {
            var id = line.AccountId ?? party.DefaultAccountId;
            await CommercialAccount(company, id, isInvoice ? AccountClass.Revenue : AccountClass.Expense, draft, ct);
            decimal percent = 0;
            Guid? taxAccount = null;
            if (line.TaxRateId is { } taxRate)
            {
                var tax = await (from row in db.TradingTaxRates join code in db.TradingTaxCodes on row.TaxCodeId equals code.Id
                    where row.CompanyId == company && code.CompanyId == company && row.Id == taxRate &&
                        row.EffectiveFrom <= input.Date && row.EffectiveTo >= input.Date && code.IsActive
                    select row).SingleOrDefaultAsync(ct) ?? throw Bad("Select a company tax rate effective on the recognition date.");
                percent = tax.RatePercent;
                taxAccount = draft.Account(isInvoice ? FxAccountRole.TaxOutput : FxAccountRole.TaxInput);
            }
            var calculated = TradeRules.Calculate(line.Quantity, line.UnitPrice, line.DiscountPercent, percent, line.TaxInclusive, currency.Code);
            details.Add(new(details.Count + 1, AccountingRules.Text(line.Description, 250, "Line description"), id,
                line.TaxRateId, taxAccount, calculated.Net, calculated.Tax, calculated.Discount, 0, 0));
        }
        var foreign = details.Sum(x => x.ForeignNet + x.ForeignTax);
        var foreignUnits = CurrencyPolicy.Units(currency.Code);
        CurrencyPolicy.Validate(foreign, foreignUnits);
        CurrencyPolicy.Validate(input.AppliedAdvance, foreignUnits);
        if (foreign == 0 || input.AppliedAdvance > foreign) throw Bad("Invoice total must be positive and cover the applied advance.");
        decimal historic = 0;
        if (input.AdvancePositionId is { } advanceId)
        {
            if (details.Any(x => x.TaxRateId.HasValue)) throw Bad("Advance application is supported for untaxed documents only.");
            var advance = await queries.Position(company, advanceId, ct);
            if (advance.CurrencyId != position.CurrencyId || advance.PartyId != party.Id ||
                advance.Kind != (isInvoice ? FxPositionKind.CustomerAdvance : FxPositionKind.SupplierAdvance))
                throw Bad("Choose a matching advance for this party, currency and side.");
            var existing = await queries.Balance(company, advance.Id, input.Date, ct);
            historic = Relief(input.AppliedAdvance, existing.ForeignAmount, existing.BaseAmount, units);
            var originalRate = await db.FxEffects.Where(x => x.CompanyId == company && x.PositionId == advance.Id && x.ForeignDelta > 0)
                .Select(x => x.RateId).SingleAsync(ct);
            draft.Move(advance, -input.AppliedAdvance, -historic, originalRate);
        }
        var unpaid = foreign - input.AppliedAdvance;
        var spot = unpaid == 0 ? null : await rates.Select(company, position.CurrencyId, input.Date, FxRateType.Spot, setup.SpotFallbackDays, ct);
        var carrying = spot is null ? 0 : Convert(unpaid, spot.ExchangeRate, units);
        var baseAmount = historic + carrying;
        if (baseAmount == 0 || (unpaid > 0 && carrying == 0)) throw Bad("The recognized amount rounds to zero functional currency.");
        CurrencyPolicy.Validate(baseAmount, units);
        if (isInvoice && carrying > 0 && party.CreditLimit is { } limit)
        {
            var legacy = await TradingSnapshot.Load(db, company, ct);
            if (Math.Max(0, legacy.ControlBalance(party.Id, DateOnly.MaxValue)) +
                await queries.CustomerExposure(company, party.Id, ct) + carrying > limit)
                throw new RAQM.Api.Helpers.ApiException(409, "Customer credit limit exceeded across functional and foreign obligations.", "credit_limit");
        }
        draft.Move(position, unpaid, carrying, spot?.Id);
        var amounts = Split(baseAmount, details.SelectMany(x => new[] { x.ForeignNet, x.ForeignTax }).ToArray(), units);
        for (var i = 0; i < details.Count; i++)
        {
            var line = details[i] with { BaseNet = amounts[2 * i], BaseTax = amounts[2 * i + 1] };
            draft.Line(line.AccountId, -side * line.BaseNet);
            if (line.TaxAccountId is { } tax) draft.Line(tax, -side * line.BaseTax);
            details[i] = line;
        }
        return draft.Plan(spot?.Id, foreign, baseAmount, details, advance: historic);
    }

    private async Task CommercialAccount(Guid company, Guid id, AccountClass classification, Draft draft, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct);
        if (account is not { IsActive: true, IsPosting: true, IsContra: false } || account.Classification != classification || draft.IsFxAccount(id) ||
            await db.InventoryAccounts.AnyAsync(x => x.CompanyId == company && x.AccountId == id, ct) ||
            await db.AssetCategories.AnyAsync(x => x.CompanyId == company && (x.DepreciationExpenseId == id || x.ImpairmentLossId == id ||
                x.GainId == id || x.LossId == id), ct))
            throw Bad("Foreign commercial lines require eligible ordinary revenue or expense accounts. Inventory, fixed assets and clearing are excluded.");
    }

    private async Task<FxPlan> Advance(Guid company, FxPosition position, FxRequest input, FxConfiguration setup,
        Draft draft, int units, CancellationToken ct)
    {
        if (!input.NonRefundableAdvance) throw Bad("Only contractual non-refundable advance consideration is supported.");
        if (!await db.TradingParties.AnyAsync(x => x.CompanyId == company && x.Id == position.PartyId && x.IsActive, ct))
            throw Bad("Choose an active counterparty for the advance.");
        var amount = await Amount(company, position, input.ForeignAmount, ct);
        var rate = await rates.Select(company, position.CurrencyId, input.Date, FxRateType.Spot, setup.SpotFallbackDays, ct);
        var carrying = Convert(amount, rate.ExchangeRate, units);
        if (carrying == 0) throw Bad("Advance rounds to zero functional currency.");
        draft.Move(position, amount, carrying, rate.Id);
        draft.Line(await Cash(company, input.FunctionalCashAccountId, ct), -Sign(position.Kind) * carrying);
        return draft.Plan(rate.Id, amount, carrying);
    }

    private async Task<FxPlan> Credit(Guid company, FxPosition refund, FxRequest input, Draft draft, int units, CancellationToken ct)
    {
        var original = await queries.Posted(company, input.OriginalId!.Value, ct);
        if (original.Kind is not (FxKind.Invoice or FxKind.Bill) || FxQueries.Input(original).AdvancePositionId.HasValue)
            throw Bad("Credit requires an original invoice or bill without advance application.");
        if (await db.FxOperations.AnyAsync(x => x.CompanyId == company && x.Kind == FxKind.Credit && x.State == FxState.Posted &&
            x.OriginalId == original.Id, ct)) throw Conflict("The foreign document already has a full credit.");
        var initial = FxQueries.Plan(original);
        var position = await queries.Position(company, original.PositionId!.Value, ct);
        var current = await queries.Balance(company, position.Id, input.Date, ct);
        var paid = initial.ForeignAmount - current.ForeignAmount;
        var refundBase = paid == 0 ? 0 : Relief(paid, initial.ForeignAmount, initial.BaseAmount, units);
        var sign = Sign(position.Kind);
        draft.Move(position, -current.ForeignAmount, -current.BaseAmount, initial.AppliedRateId);
        draft.Move(refund, paid, refundBase, initial.AppliedRateId);
        foreach (var line in initial.Commercial!)
        {
            draft.Line(line.AccountId, sign * line.BaseNet);
            if (line.TaxAccountId is { } tax) draft.Line(tax, sign * line.BaseTax);
        }
        draft.Difference(sign * (initial.BaseAmount - current.BaseAmount - refundBase), false);
        return draft.Plan(initial.AppliedRateId, initial.ForeignAmount, initial.BaseAmount, initial.Commercial);
    }
}
