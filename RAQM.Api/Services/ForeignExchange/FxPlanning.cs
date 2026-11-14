using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Services.ExchangeRates;
using RAQM.Api.Services.MultiCurrency;
using static RAQM.Api.Helpers.MultiCurrency.FxMath;

namespace RAQM.Api.Services.ForeignExchange;

public sealed partial class FxPlanning(RaqmDbContext db, FxQueries queries, ExchangeRateService rates, TimeProvider clock)
{
    public async Task<FxPlan> Build(Company scope, FxOperation operation, FxPosition? position, CancellationToken ct)
    {
        var input = FxQueries.Input(operation);
        var company = scope.Id;
        var setup = await queries.Configuration(company, ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (input.Date.Year < 1900 || input.Date > today ||
            !await db.Periods.AnyAsync(x => x.CompanyId == company && x.State == PeriodState.Open &&
                x.StartDate <= input.Date && x.EndDate >= input.Date, ct))
            throw Conflict("FX requires an open accounting date no later than today.");
        if (await db.FxOperations.AnyAsync(x => x.CompanyId == company && x.State == FxState.Posted && x.Date > input.Date, ct))
            throw Conflict("FX accounting dates cannot precede the latest posted company FX movement.");
        if (input.Kind is not (FxKind.Revaluation or FxKind.RevaluationCorrection) &&
            await db.FxOperations.AnyAsync(x => x.CompanyId == company && x.State == FxState.Posted &&
                x.Date == input.Date && x.Kind == FxKind.Revaluation, ct))
            throw Conflict("The closing FX batch is the final monetary action of its accounting day.");
        if (await (from close in db.YearEndRecords join year in db.FiscalYears on close.FiscalYearId equals year.Id
            where close.CompanyId == company && close.Finalized && year.EndDate >= input.Date select close.Id).AnyAsync(ct))
            throw Conflict("The accounting date is in a finalized fiscal year.");
        var map = await queries.Accounts(company, ct);
        var units = CurrencyPolicy.Units(scope.BaseCurrency);
        var draft = new Draft(map, input.Kind);
        var plan = input.Kind switch
        {
            FxKind.Invoice or FxKind.Bill => await Recognition(company, position!, input, setup, draft, units, ct),
            FxKind.Settlement => await Settlement(company, position!, input, setup, draft, units, ct),
            FxKind.Advance => await Advance(company, position!, input, setup, draft, units, ct),
            FxKind.Credit => await Credit(company, position!, input, draft, units, ct),
            FxKind.BankPurchase or FxKind.BankSale => await Exchange(company, position!, input, setup, draft, units, ct),
            FxKind.Revaluation => await Revaluation(company, input, draft, units, ct),
            FxKind.RevaluationCorrection => await Correct(company, input, draft, ct),
            _ => throw Bad("Unsupported FX operation.")
        };
        if (plan.Lines.Count > 0) AccountingRules.Lines(plan.Lines, scope.BaseCurrency, true);
        return plan;
    }

    private sealed class Draft(Dictionary<FxAccountRole, Guid> accounts, FxKind kind)
    {
        private readonly List<JournalDraftLine> lines = [];
        private readonly List<FxMovement> movements = [];
        private decimal realized;
        private decimal unrealized;
        public Guid Account(FxAccountRole role) => accounts[role];
        public bool IsFxAccount(Guid id) => accounts.ContainsValue(id);
        public void Line(Guid account, decimal signed)
        {
            if (signed != 0) lines.Add(new(account, Math.Max(signed, 0), Math.Max(-signed, 0), $"FX {kind}"));
        }
        public void Move(FxPosition position, decimal foreign, decimal carrying, Guid? rate)
        {
            if (foreign == 0 && carrying == 0) return;
            movements.Add(new(position.Id, rate, foreign, carrying));
            Line(position.AccountId, Sign(position.Kind) * carrying);
        }
        public void Difference(decimal gain, bool closing)
        {
            if (closing) unrealized += gain;
            else realized += gain;
            var role = (closing, gain >= 0) switch
            {
                (true, true) => FxAccountRole.UnrealizedGain,
                (true, false) => FxAccountRole.UnrealizedLoss,
                (false, true) => FxAccountRole.RealizedGain,
                _ => FxAccountRole.RealizedLoss
            };
            Line(accounts[role], -gain);
        }
        public FxPlan Plan(Guid? rate = null, decimal foreign = 0, decimal carrying = 0,
            IReadOnlyList<FxCommercialLine>? commercial = null, IReadOnlyList<FxValuation>? valuations = null, decimal advance = 0) =>
            new(lines, movements, commercial, valuations, rate, foreign, carrying, realized, unrealized, advance);
    }

    private async Task<decimal> Amount(Guid company, FxPosition position, decimal amount, CancellationToken ct)
    {
        var currency = await queries.Currency(company, position.CurrencyId, ct);
        CurrencyPolicy.Validate(amount, CurrencyPolicy.Units(currency.Code));
        if (amount == 0) throw Bad("Provide a positive transaction-currency amount.");
        return amount;
    }

    private async Task<Guid> Cash(Guid company, Guid? id, CancellationToken ct)
    {
        if (id is not { } cash) throw Bad("Choose a functional-currency bank or cash ledger account.");
        var ledger = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == cash, ct);
        var treasury = await db.TreasuryAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.LedgerAccountId == cash, ct);
        if (ledger is not { IsActive: true, IsPosting: true, IsContra: false, Classification: AccountClass.Asset } ||
            (treasury is not null ? !treasury.Active || treasury.Kind == TreasuryAccountKind.PettyCash :
                !await db.TradingAccounts.AnyAsync(x => x.CompanyId == company && x.AccountId == cash && x.Role == AccountRole.Cash, ct)))
            throw Bad("Choose eligible company functional bank or cash, excluding petty cash and foreign banks.");
        return cash;
    }

    private async Task BankCutoff(Guid company, Guid position, DateOnly date, CancellationToken ct)
    {
        if (await db.FxBankStatements.AnyAsync(x => x.CompanyId == company && x.PositionId == position && x.Cutoff >= date &&
            x.StatementForeign == x.BookForeign, ct))
            throw Conflict("Use a later date than the reconciled foreign bank cutoff.");
    }

    private async Task<FxPlan> Settlement(Guid company, FxPosition position, FxRequest input, FxConfiguration setup,
        Draft draft, int units, CancellationToken ct)
    {
        if (!Monetary(position.Kind) || position.Kind == FxPositionKind.Bank)
            throw Bad("Settlement requires an open foreign obligation or credit refund.");
        var amount = await Amount(company, position, input.ForeignAmount, ct);
        var balance = await queries.Balance(company, position.Id, input.Date, ct);
        var relieved = Relief(amount, balance.ForeignAmount, balance.BaseAmount, units);
        var rate = await rates.Select(company, position.CurrencyId, input.Date, FxRateType.Spot, setup.SpotFallbackDays, ct);
        var cash = Convert(amount, rate.ExchangeRate, units);
        if (cash == 0) throw Bad("The settled cash amount rounds to zero functional currency.");
        var sign = Sign(position.Kind);
        draft.Move(position, -amount, -relieved, rate.Id);
        draft.Difference(SettlementGain(position.Kind, cash, relieved), false);
        if (input.BankPositionId is { } bankId)
        {
            var bank = await queries.Position(company, bankId, ct);
            if (bank.Kind != FxPositionKind.Bank || bank.CurrencyId != position.CurrencyId)
                throw Bad("Settlement bank must have the same transaction currency as the obligation.");
            await BankCutoff(company, bank.Id, input.Date, ct);
            if (sign > 0) draft.Move(bank, amount, cash, rate.Id);
            else
            {
                var bankBalance = await queries.Balance(company, bank.Id, input.Date, ct);
                var bankRelief = Relief(amount, bankBalance.ForeignAmount, bankBalance.BaseAmount, units);
                draft.Move(bank, -amount, -bankRelief, rate.Id);
                draft.Difference(cash - bankRelief, false);
            }
        }
        else draft.Line(await Cash(company, input.FunctionalCashAccountId, ct), sign * cash);
        return draft.Plan(rate.Id, amount, cash);
    }

    private async Task<FxPlan> Exchange(Guid company, FxPosition bank, FxRequest input, FxConfiguration setup,
        Draft draft, int units, CancellationToken ct)
    {
        if (bank.Kind != FxPositionKind.Bank) throw Bad("Choose a registered foreign bank account.");
        await BankCutoff(company, bank.Id, input.Date, ct);
        var amount = await Amount(company, bank, input.ForeignAmount, ct);
        var cashAccount = await Cash(company, input.FunctionalCashAccountId, ct);
        var rate = await rates.Select(company, bank.CurrencyId, input.Date, FxRateType.Spot, setup.SpotFallbackDays, ct);
        var cash = Convert(amount, rate.ExchangeRate, units);
        CurrencyPolicy.Validate(input.BaseFee, units);
        if (cash == 0 || (input.Kind == FxKind.BankSale && input.BaseFee >= cash))
            throw Bad("Exchange cash must be positive after bank sale fees.");
        if (input.FeeAccountId is { } fee)
        {
            await CommercialAccount(company, fee, AccountClass.Expense, draft, ct);
            draft.Line(fee, input.BaseFee);
        }
        if (input.Kind == FxKind.BankPurchase)
        {
            draft.Move(bank, amount, cash, rate.Id);
            draft.Line(cashAccount, -cash - input.BaseFee);
        }
        else
        {
            var balance = await queries.Balance(company, bank.Id, input.Date, ct);
            var relief = Relief(amount, balance.ForeignAmount, balance.BaseAmount, units);
            draft.Move(bank, -amount, -relief, rate.Id);
            draft.Line(cashAccount, cash - input.BaseFee);
            draft.Difference(cash - relief, false);
        }
        return draft.Plan(rate.Id, amount, cash);
    }
}
