using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.CashManagement;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.CashManagement;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.Treasury;

namespace RAQM.Api.Services.CashManagement;

public sealed class TreasuryPostingPolicy(RaqmDbContext db)
{
    public async Task Validate(Guid actor, Company company, PostingRequest request, CancellationToken ct)
    {
        var ledgerIds = request.Lines.Select(x => x.AccountId).Distinct().ToArray();
        var accounts = await db.TreasuryAccounts.AsNoTracking().Where(x => x.CompanyId == company.Id &&
            ledgerIds.Contains(x.LedgerAccountId)).ToListAsync(ct);
        var treasurySource = request.SourceModule.StartsWith("Treasury.", StringComparison.Ordinal);
        if (treasurySource)
        {
            var movement = await db.TreasuryMovements.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company.Id &&
                x.Id == request.SourceDocumentId && x.State == TreasuryMovementState.PendingApproval, ct);
            if (movement is null || movement.PostedBy != actor || movement.PostingDate != request.AccountingDate ||
                request.SourceModule != TreasuryPosting.Source(movement.Kind) ||
                request.IdempotencyKey != TradeRules.Key("Treasury", movement.Id, movement.RequestKey) ||
                !TreasuryPosting.Lines(movement).SequenceEqual(request.Lines))
                throw new ApiException(403, "Treasury posting requires a coordinated matching source.", "treasury_source");
        }

        if (accounts.Count == 0) return;
        if (accounts.Any(x => !x.Active) && request.ReversesJournalId is null)
            throw new ApiException(409, "New postings require active treasury accounts.", "treasury_inactive");
        var ids = accounts.Select(x => x.Id).ToArray();
        if (await (from session in db.ReconciliationSessions
            join statement in db.BankStatements on session.StatementId equals statement.Id
            where session.CompanyId == company.Id && ids.Contains(session.BankAccountId) &&
                session.State == ReconciliationState.Finalized && statement.End >= request.AccountingDate
            select session.Id).AnyAsync(ct))
            throw new ApiException(409, "Posting would change a finalized bank cutoff. Reopen the eligible reconciliation or use a later accounting date.", "bank_cutoff");

        string? originalSource = null;
        if (request.ReversesJournalId is { } originalId)
        {
            var original = await db.Journals.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company.Id && x.Id == originalId, ct);
            originalSource = original?.SourceModule;
            if (originalSource?.StartsWith("Treasury.", StringComparison.Ordinal) == true)
            {
                var movement = await db.TreasuryMovements.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company.Id &&
                    x.JournalId == originalId && x.State == TreasuryMovementState.Posted, ct);
                if (movement?.ReversalKey is null || movement.ReversedBy != actor ||
                    request.IdempotencyKey != TradeRules.Key("TreasuryReverse", movement.Id, movement.ReversalKey))
                    throw new ApiException(403, "Use the treasury reversal workflow.", "treasury_reversal");
            }
            if (await (from item in db.BankMatchItems
                join line in db.JournalLines on item.JournalLineId equals line.Id
                join match in db.BankMatches on item.MatchId equals match.Id
                join session in db.ReconciliationSessions on match.SessionId equals session.Id
                join statement in db.BankStatements on session.StatementId equals statement.Id
                where item.CompanyId == company.Id && line.JournalId == originalId && !item.Released &&
                    (session.State != ReconciliationState.Finalized || request.AccountingDate <= statement.End)
                select item.Id).AnyAsync(ct))
                throw new ApiException(409, "Release the active match before reversing within an open reconciliation.", "bank_matched_reversal");
        }

        foreach (var petty in accounts.Where(x => x.Kind == TreasuryAccountKind.PettyCash))
        {
            if (!treasurySource && request.SourceModule != "OpeningBalance" &&
                !(originalSource == "OpeningBalance" || originalSource?.StartsWith("Treasury.", StringComparison.Ordinal) == true))
                throw new ApiException(403, "Use petty cash movements or the approved opening balance workflow.", "petty_cash_source");
            var delta = request.Lines.Where(x => x.AccountId == petty.LedgerAccountId).Sum(x => x.Debit - x.Credit);
            if (delta >= 0) continue;
            var book = new TreasuryLedger(db);
            var balance = await book.Balance(company.Id, petty.LedgerAccountId, request.AccountingDate, ct) + delta;
            if (balance < 0) throw InsufficientCash();
            var future = await book.Entries(company.Id, petty.LedgerAccountId, DateOnly.MaxValue, request.AccountingDate.AddDays(1)).ToListAsync(ct);
            foreach (var day in future.GroupBy(x => x.Date).OrderBy(x => x.Key))
            {
                balance += day.Sum(x => x.Amount);
                if (balance < 0) throw InsufficientCash();
            }
        }
    }

    private static ApiException InsufficientCash() => new(409, "The posting would overdraw petty cash.", "petty_cash_insufficient");
}
