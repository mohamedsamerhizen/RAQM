using System.Data;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Treasury;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Treasury;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.Banking;

namespace RAQM.Api.Services.Treasury;

public sealed class TreasuryReportService(RaqmDbContext db, FinancialAccess access, BankingPolicy policy, TreasuryLedger ledger) : ITreasuryReportService
{
    public async Task<TreasuryActivity> Activity(Guid actor, Guid org, Guid company, Guid account, DateOnly from, DateOnly to,
        int page, int size, CancellationToken ct)
    {
        Dates(from, to);
        AccountingRules.Page(page, size);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var registered = await policy.Account(company, account, ct, active: false);
        var (position, lines) = await Read(registered, from, to, ct);
        await transaction.CommitAsync(ct);
        return new(position, lines.Skip((page - 1) * size).Take(size).ToArray(), lines.Count);
    }

    public async Task<IReadOnlyList<TreasuryPosition>> Position(Guid actor, Guid org, Guid company, DateOnly from, DateOnly to, CancellationToken ct)
    {
        Dates(from, to);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var accounts = await db.TreasuryAccounts.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct);
        var positions = new List<TreasuryPosition>();
        foreach (var account in accounts) positions.Add((await Read(account, from, to, ct)).Position);
        await transaction.CommitAsync(ct);
        return positions;
    }

    private async Task<(TreasuryPosition Position, List<BookMovement> Lines)> Read(TreasuryAccount account, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var opening = await ledger.Balance(account.CompanyId, account.LedgerAccountId, from.AddDays(-1), ct);
        var lines = await ledger.Entries(account.CompanyId, account.LedgerAccountId, to, from).ToListAsync(ct);
        var incoming = lines.Where(x => x.Amount > 0).Sum(x => x.Amount);
        var outgoing = -lines.Where(x => x.Amount < 0).Sum(x => x.Amount);
        var finalized = await (from session in db.ReconciliationSessions
            join statement in db.BankStatements on session.StatementId equals statement.Id
            where session.CompanyId == account.CompanyId && session.BankAccountId == account.Id &&
                session.State == ReconciliationState.Finalized && statement.End <= to
            select (DateOnly?)statement.End).MaxAsync(ct);
        var fees = -lines.Where(x => (x.OriginalSource ?? x.Source) == "Treasury.BankFee").Sum(x => x.Amount);
        var receipts = lines.Where(x => x.PaymentId.HasValue && x.Amount > 0).Sum(x => x.Amount);
        var payments = -lines.Where(x => x.PaymentId.HasValue && x.Amount < 0).Sum(x => x.Amount);
        return (new(account.Id, account.Code, account.Kind, account.Currency, opening, incoming, outgoing, opening + incoming - outgoing,
            fees, receipts, payments, finalized), lines);
    }

    private static void Dates(DateOnly from, DateOnly to)
    {
        if (from.Year < 1900 || to < from)
            throw new ApiException(400, "Select an ordered reporting range beginning in 1900 or later.", "treasury_dates");
    }
}
