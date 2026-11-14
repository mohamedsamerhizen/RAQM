using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;

namespace RAQM.Api.Services.Banking;

public sealed class BankingPolicy(RaqmDbContext db)
{
    public async Task<TreasuryAccount> Account(Guid company, Guid id, CancellationToken ct, bool active = true)
    {
        var account = await db.TreasuryAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw new ApiException(404, "Treasury account not found.", "treasury_account");
        if (active && !account.Active)
            throw new ApiException(409, "The treasury account is inactive.", "treasury_inactive");
        return account;
    }

    public async Task<TreasuryAccount> Bank(Guid company, Guid id, CancellationToken ct, bool active = true)
    {
        var account = await Account(company, id, ct, active);
        if (account.Kind != TreasuryAccountKind.Bank)
            throw new ApiException(400, "Select a bank account.", "bank_required");
        return account;
    }

    public static async Task PaymentAccount(RaqmDbContext db, Guid company, Guid ledger, Guid? bank, CancellationToken ct)
    {
        var account = await db.TreasuryAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == company && x.LedgerAccountId == ledger, ct);
        if (account is { Active: false } || account?.Kind == TreasuryAccountKind.PettyCash)
            throw new ApiException(409, "This account cannot be used for counterparty settlements.", "bank_payment_account");
        if (account?.Kind == TreasuryAccountKind.Bank ? bank != account.Id : bank.HasValue)
            throw new ApiException(400, "Bank attribution must identify the registered account for the cash GL.", "bank_attribution");
    }

    public static void SignedAmount(decimal amount, string currency)
    {
        if (amount > CurrencyPolicy.Maximum || amount < -CurrencyPolicy.Maximum)
            throw new ApiException(400, "Amount exceeds supported precision.", "amount_precision");
        CurrencyPolicy.Validate(Math.Abs(amount), CurrencyPolicy.Units(currency));
    }
}
