using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.Trading;
public sealed class TradingPolicy(RaqmDbContext db, FinancialAccess access, TimeProvider clock)
{
    public async Task<Dictionary<AccountRole, Guid>> RequiredMappings(Guid company, CancellationToken ct)
    {
        var rows = await db.TradingAccounts.AsNoTracking().Where(x => x.CompanyId == company && x.Role != AccountRole.Cash).ToListAsync(ct);
        var result = rows.ToDictionary(x => x.Role, x => x.AccountId);
        if (result.Count != 9)
            throw new ApiException(409, "Configure all trading/control accounts first.", "trading_configuration");
        return result;
    }

    public async Task<LedgerAccount> Account(Guid company, Guid id, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct);
        if (account is null || !account.IsActive || !account.IsPosting || account.IsContra)
            throw new ApiException(400, "Account must be an active non-contra posting account in this company.", "trading_account");
        return account;
    }

    public async Task Cash(Guid company, Guid id, CancellationToken ct)
    {
        var account = await Account(company, id, ct);
        if (account.Classification != AccountClass.Asset || !await db.TradingAccounts.AnyAsync(x => x.CompanyId == company && x.AccountId == id && x.Role == AccountRole.Cash, ct))
            throw new ApiException(400, "Use a configured cash/bank asset account.", "cash_account");
    }

    public async Task Date(Guid actor, Company company, DateOnly date, bool allowBackdated, string? reason, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (date.Year < 1900 || date > today)
            throw new ApiException(400, "Settlement date must be 1900..current UTC date.", "settlement_date");
        if (date < today)
        {
            if (!allowBackdated)
                throw new ApiException(400, "Backdating requires explicit approval.", "backdate_approval");
            AccountingRules.Text(reason, 500, "Backdating reason");
            await access.Resolve(actor, company.OrganizationId, company.Id, true, true, ct);
        }

        if (!await db.Periods.AnyAsync(x => x.CompanyId == company.Id && x.State == PeriodState.Open && x.StartDate <= date && x.EndDate >= date, ct))
            throw new ApiException(409, "An open accounting period is required.", "period_closed");
    }

    public static void Positive(decimal amount, string currency)
    {
        CurrencyPolicy.Validate(amount, CurrencyPolicy.Units(currency));
        if (amount <= 0)
            throw new ApiException(400, "Amount must be positive.", "settlement_amount");
    }
}
