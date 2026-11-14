using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using static RAQM.Api.Helpers.MultiCurrency.FxMath;

namespace RAQM.Api.Services.MultiCurrency;

public sealed class FxSetup(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions, FxQueries queries, TimeProvider clock)
{
    internal static T Scoped<T>(Company scope, T row) where T : FinancialEvidence
    {
        row.TenantId = scope.TenantId;
        row.OrganizationId = scope.OrganizationId;
        row.CompanyId = scope.Id;
        return row;
    }

    public Task<FxConfiguration> Configure(Guid actor, Guid org, Guid company, FxConfigurationRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            if (await db.FxConfigurations.AnyAsync(x => x.CompanyId == company, token)) throw Conflict("FX mappings are already established.");
            var roles = Enum.GetValues<FxAccountRole>();
            if (input.SpotFallbackDays is < 0 or > 7 || input.Accounts is null || input.Accounts.Count != roles.Length ||
                roles.Any(x => !input.Accounts.ContainsKey(x)) || input.Accounts.Values.Distinct().Count() != roles.Length)
                throw Bad("Provide all ten distinct FX account mappings and zero to seven spot fallback days.");
            foreach (var role in roles)
            {
                var classification = role switch
                {
                    FxAccountRole.Receivable or FxAccountRole.SupplierAdvance or FxAccountRole.TaxInput => AccountClass.Asset,
                    FxAccountRole.Payable or FxAccountRole.CustomerAdvance or FxAccountRole.TaxOutput => AccountClass.Liability,
                    FxAccountRole.RealizedGain or FxAccountRole.UnrealizedGain => AccountClass.Revenue,
                    _ => AccountClass.Expense
                };
                await Dedicated(company, input.Accounts[role], classification, token);
                db.FxAccounts.Add(Scoped(scope, new FxAccount { Role = role, AccountId = input.Accounts[role] }));
            }
            var row = Scoped(scope, new FxConfiguration { FunctionalCurrency = scope.BaseCurrency,
                SpotFallbackDays = input.SpotFallbackDays, RequireCloseRevaluation = input.RequireCloseRevaluation });
            db.FxConfigurations.Add(row);
            access.Audit(scope, actor, row.Id, "FxConfigurationEstablished");
            return row;
        }, ct);

    public Task<FxCurrency> Enable(Guid actor, Guid org, Guid company, EnableCurrencyRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            await queries.Configuration(company, token);
            var code = AccountingRules.Text(input.Code, 3, "Currency").ToUpperInvariant();
            CurrencyPolicy.Units(code);
            if (scope.BaseCurrency == code) throw Bad("Enable a transaction currency distinct from the company functional currency.");
            var existing = await db.FxCurrencies.SingleOrDefaultAsync(x => x.CompanyId == company && x.Code == code, token);
            if (existing is not null) return existing;
            var row = Scoped(scope, new FxCurrency { Code = code });
            db.FxCurrencies.Add(row);
            access.Audit(scope, actor, row.Id, "FxCurrencyEnabled", code);
            return row;
        }, ct);

    public Task<FxPosition> Bank(Guid actor, Guid org, Guid company, FxBankRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            await queries.Currency(company, input.CurrencyId, token);
            await Dedicated(company, input.LedgerAccountId, AccountClass.Asset, token);
            var code = AccountingRules.Text(input.Code, 40, "Bank code").ToUpperInvariant();
            if (await db.FxPositions.AnyAsync(x => x.CompanyId == company && x.Code == code, token))
                throw Conflict("Bank code already exists.");
            var bank = Scoped(scope, new FxPosition { CurrencyId = input.CurrencyId, Kind = FxPositionKind.Bank, Code = code,
                AccountId = input.LedgerAccountId });
            db.FxPositions.Add(bank);
            access.Audit(scope, actor, bank.Id, "FxBankRegistered", code);
            return bank;
        }, ct);

    public Task<FxBankStatement> Statement(Guid actor, Guid org, Guid company, Guid bank, FxBankStatementRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var position = await queries.Position(company, bank, token);
            if (position.Kind != FxPositionKind.Bank || input.Cutoff.Year < 1900 || input.Cutoff > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
                throw Bad("Supply a foreign bank and a valid statement cutoff.");
            var currency = await queries.Currency(company, position.CurrencyId, token);
            CurrencyPolicy.Validate(input.StatementForeign, CurrencyPolicy.Units(currency.Code));
            if (await db.FxBankStatements.AnyAsync(x => x.CompanyId == company && x.PositionId == bank && x.Cutoff >= input.Cutoff, token))
                throw Conflict("A bank statement cutoff must advance.");
            var balance = await queries.Balance(company, bank, input.Cutoff, token);
            var row = Scoped(scope, new FxBankStatement { PositionId = bank, Cutoff = input.Cutoff, StatementForeign = input.StatementForeign,
                BookForeign = balance.ForeignAmount, BookBase = balance.BaseAmount, ActorId = actor,
                Evidence = AccountingRules.Text(input.Evidence, 1000, "Statement evidence") });
            db.FxBankStatements.Add(row);
            access.Audit(scope, actor, row.Id, "FxBankStatementRecorded");
            return row;
        }, ct);

    public async Task<object> Settings(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        return new
        {
            FunctionalCurrency = scope.BaseCurrency, Supported = CurrencyPolicy.Supported,
            Policy = await db.FxConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company, ct),
            Accounts = await db.FxAccounts.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Role).ToListAsync(ct),
            Enabled = await db.FxCurrencies.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct),
            Banks = await db.FxPositions.AsNoTracking().Where(x => x.CompanyId == company && x.Kind == FxPositionKind.Bank).OrderBy(x => x.Code).ToListAsync(ct)
        };
    }

    private async Task Dedicated(Guid company, Guid id, AccountClass classification, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct);
        if (account is not { IsActive: true, IsPosting: true, IsContra: false } || account.Classification != classification ||
            await db.FxAccounts.AnyAsync(x => x.CompanyId == company && x.AccountId == id, ct) ||
            await db.FxPositions.AnyAsync(x => x.CompanyId == company && x.AccountId == id, ct) ||
            await db.TradingAccounts.AnyAsync(x => x.CompanyId == company && x.AccountId == id, ct) ||
            await db.TradingParties.AnyAsync(x => x.CompanyId == company && x.DefaultAccountId == id, ct) ||
            await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == company && x.LedgerAccountId == id, ct) ||
            await db.AssetControls.AnyAsync(x => x.CompanyId == company && x.AccountId == id, ct) ||
            await db.AssetCategories.AnyAsync(x => x.CompanyId == company && (x.DepreciationExpenseId == id || x.ImpairmentLossId == id ||
                x.GainId == id || x.LossId == id), ct) ||
            await db.InventoryAccounts.AnyAsync(x => x.CompanyId == company && x.AccountId == id, ct) ||
            await db.JournalLines.AnyAsync(x => x.CompanyId == company && x.AccountId == id &&
                db.Journals.Any(j => j.Id == x.JournalId && j.State == JournalState.Posted), ct))
            throw Bad("FX mappings require dedicated unused active posting accounts of the indicated class.");
    }
}
