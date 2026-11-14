using System.Data;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Banking;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.CashManagement;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Banking;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Banking;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.Treasury;

namespace RAQM.Api.Services.Banking;

public sealed class ReconciliationService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    BankingPolicy policy, TreasuryLedger ledger) : IReconciliationService
{
    public Task<ReconciliationView> Start(Guid actor, Guid org, Guid company, Guid bank, Guid statement, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var account = await policy.Bank(company, bank, token, active: false);
            var external = await Statement(company, bank, statement, token);
            var prior = await db.ReconciliationSessions.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.StatementId == statement, token);
            if (prior is not null) return await View(prior, external, account, token);
            var earlier = await db.BankStatements.AnyAsync(s => s.CompanyId == company && s.BankAccountId == bank && s.End < external.Start &&
                !db.ReconciliationSessions.Any(r => r.CompanyId == company && r.StatementId == s.Id && r.State == ReconciliationState.Finalized), token);
            if (earlier || await db.ReconciliationSessions.AnyAsync(s => s.CompanyId == company && s.BankAccountId == bank && s.State != ReconciliationState.Finalized, token))
                throw new ApiException(409, "Finalize earlier statements before starting the next session.", "reconciliation_order");
            var session = new ReconciliationSession { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, BankAccountId = bank, StatementId = statement };
            db.ReconciliationSessions.Add(session);
            access.Audit(scope, actor, session.Id, "BankReconciliationStarted", $"Statement {statement}");
            await db.SaveChangesAsync(token);
            return await View(session, external, account, token);
        }, ct);

    public async Task<ReconciliationView> Get(Guid actor, Guid org, Guid company, Guid bank, Guid session, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var row = await Session(company, bank, session, ct);
        var account = await policy.Bank(company, bank, ct, active: false);
        var result = await View(row, await Statement(company, bank, row.StatementId, ct), account, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<ReconciliationSession>> History(Guid actor, Guid org, Guid company, Guid bank, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        await policy.Bank(company, bank, ct, active: false);
        return await (from session in db.ReconciliationSessions.AsNoTracking()
            join statement in db.BankStatements.AsNoTracking() on session.StatementId equals statement.Id
            where session.CompanyId == company && session.BankAccountId == bank
            orderby statement.End descending
            select session).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BankMatch>> Matches(Guid actor, Guid org, Guid company, Guid bank, Guid session, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        await Session(company, bank, session, ct);
        return await db.BankMatches.AsNoTracking().Include(x => x.Items).Where(x => x.CompanyId == company && x.SessionId == session)
            .OrderBy(x => x.ConfirmedAt).ToListAsync(ct);
    }

    public Task<ReconciliationView> Confirm(Guid actor, Guid org, Guid company, Guid bank, Guid sessionId, ConfirmMatch request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var session = await Session(company, bank, sessionId, token);
            RequireMatching(session, request.Version);
            var reason = AccountingRules.Text(request.Reason, 500, "Match reason");
            var statement = await Statement(company, bank, session.StatementId, token);
            var account = await policy.Bank(company, bank, token, active: false);
            var line = await db.BankStatementLines.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.StatementId == statement.Id && x.Id == request.StatementLineId, token)
                ?? throw new ApiException(404, "Statement line not found in this session.", "statement_line");
            var ids = request.JournalLineIds?.ToArray() ?? [];
            if (ids.Length is < 1 or > 50 || ids.Distinct().Count() != ids.Length)
                throw new ApiException(400, "Select one to fifty distinct journal lines.", "match_items");
            if (await db.BankMatches.AnyAsync(x => x.CompanyId == company && x.StatementLineId == line.Id && x.ReleasedAt == null, token) ||
                await db.BankMatchItems.AnyAsync(x => x.CompanyId == company && !x.Released && ids.Contains(x.JournalLineId), token))
                throw new ApiException(409, "A statement or journal line is already reconciled.", "match_duplicate");
            var first = await FirstStatementDate(company, bank, token);
            var entries = await ledger.Entries(company, account.LedgerAccountId, statement.End, first, ids).ToListAsync(token);
            if (entries.Count != ids.Length)
                throw new ApiException(400, "Book lines must be posted to this bank and within the reconciliation cutoff.", "match_scope");
            var journals = entries.Select(x => x.JournalId).ToArray();
            if (await db.TreasuryMovements.AnyAsync(x => x.CompanyId == company && x.JournalId.HasValue && journals.Contains(x.JournalId.Value) &&
                x.StatementLineId != null && (x.StatementLineId != line.Id || db.Journals.Any(reversal =>
                    reversal.Id == x.ReversalJournalId && reversal.CompanyId == company && reversal.PostingDate <= statement.End)), token))
                throw new ApiException(409, "An adjustment can match only its source statement line while effective at the cutoff.", "match_adjustment");
            ReconciliationRules.Match(line.Amount, entries.Select(x => x.Amount).ToArray());
            var match = new BankMatch { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, SessionId = session.Id,
                StatementLineId = line.Id, Amount = line.Amount, Reason = reason, ConfirmedBy = actor };
            match.Items = entries.Select(e => new BankMatchItem { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId,
                MatchId = match.Id, JournalLineId = e.LineId, Amount = e.Amount }).ToList();
            db.BankMatches.Add(match);
            session.Version = Guid.NewGuid();
            access.Audit(scope, actor, match.Id, "BankMatchConfirmed", reason);
            access.Audit(scope, actor, sessionId, "BankMatchConfirmed", match.Id.ToString());
            await db.SaveChangesAsync(token);
            return await View(session, statement, account, token);
        }, ct);

    public Task<ReconciliationView> Release(Guid actor, Guid org, Guid company, Guid bank, Guid sessionId, Guid matchId, ReviewAction request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var session = await Session(company, bank, sessionId, token);
            RequireMatching(session, request.Version);
            var match = await db.BankMatches.Include(x => x.Items).SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.SessionId == sessionId && x.Id == matchId && x.ReleasedAt == null, token)
                ?? throw new ApiException(404, "Active match not found.", "match_missing");
            var reason = AccountingRules.Text(request.Reason, 500, "Release reason");
            match.ReleasedAt = DateTimeOffset.UtcNow;
            match.ReleasedBy = actor;
            match.ReleaseReason = reason;
            foreach (var item in match.Items) item.Released = true;
            session.Version = Guid.NewGuid();
            access.Audit(scope, actor, matchId, "BankMatchReleased", reason);
            access.Audit(scope, actor, sessionId, "BankMatchReleased", matchId.ToString());
            await db.SaveChangesAsync(token);
            return await View(session, await Statement(company, bank, session.StatementId, token),
                await policy.Bank(company, bank, token, active: false), token);
        }, ct);

    public Task<ReconciliationView> Transition(Guid actor, Guid org, Guid company, Guid bank, Guid sessionId, string action, ReviewAction request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var session = await Session(company, bank, sessionId, token);
            BankAccountService.CheckVersion(session.Version, request.Version);
            var reason = AccountingRules.Text(request.Reason, 500, "Reason");
            var statement = await Statement(company, bank, session.StatementId, token);
            var account = await policy.Bank(company, bank, token, active: false);
            if (action is "finalize" or "reopen") await access.Resolve(actor, org, company, true, true, token);
            switch (action, session.State)
            {
                case ("review", ReconciliationState.Matching):
                    session.State = ReconciliationState.Review;
                    break;
                case ("resume", ReconciliationState.Review):
                    session.State = ReconciliationState.Matching;
                    break;
                case ("finalize", ReconciliationState.Review):
                    var view = await View(session, statement, account, token);
                    if (view.UnmatchedStatement.Count > 0 || view.Difference != 0)
                        throw new ApiException(409, "Finalization requires all statement lines matched and no unexplained difference.", "reconciliation_difference");
                    session.State = ReconciliationState.Finalized;
                    session.FinalizedAt = DateTimeOffset.UtcNow;
                    session.FinalizedBy = actor;
                    session.FinalizedBook = view.BookClosing;
                    session.FinalizedDeposits = view.DepositsInTransit;
                    session.FinalizedWithdrawals = view.OutstandingWithdrawals;
                    break;
                case ("reopen", ReconciliationState.Finalized):
                    if (await db.Periods.AnyAsync(period => period.CompanyId == company &&
                        period.State != RAQM.Api.Entities.Accounting.PeriodState.Open &&
                        period.StartDate <= statement.End && period.EndDate >= statement.Start, token))
                        throw new ApiException(409, "Reopen the affected financial period before withdrawing finalized bank evidence.",
                            "reconciliation_close_finality");
                    if (await (from other in db.ReconciliationSessions
                        join s in db.BankStatements on other.StatementId equals s.Id
                        where other.CompanyId == company && other.BankAccountId == bank && s.End > statement.End
                        select other.Id).AnyAsync(token))
                        throw new ApiException(409, "Later sessions depend on this reconciliation.", "reconciliation_order");
                    var snapshot = FormattableString.Invariant($"Bank {statement.Closing}, book {session.FinalizedBook}, deposits {session.FinalizedDeposits}, withdrawals {session.FinalizedWithdrawals}, finalized {session.FinalizedAt:O} by {session.FinalizedBy}");
                    access.Audit(scope, actor, sessionId, "BankReconciliationPriorFinalization", snapshot);
                    session.State = ReconciliationState.Matching;
                    session.FinalizedAt = null;
                    session.FinalizedBy = null;
                    session.FinalizedBook = null;
                    session.FinalizedDeposits = null;
                    session.FinalizedWithdrawals = null;
                    break;
                default:
                    throw new ApiException(409, "This reconciliation transition is unavailable.", "reconciliation_state");
            }
            session.Version = Guid.NewGuid();
            access.Audit(scope, actor, sessionId, $"BankReconciliation:{action}", reason);
            await db.SaveChangesAsync(token);
            return await View(session, statement, account, token);
        }, ct);

    private async Task<ReconciliationSession> Session(Guid company, Guid bank, Guid id, CancellationToken ct) =>
        await db.ReconciliationSessions.SingleOrDefaultAsync(x => x.CompanyId == company && x.BankAccountId == bank && x.Id == id, ct)
            ?? throw new ApiException(404, "Reconciliation session not found.", "reconciliation_missing");

    private async Task<BankStatement> Statement(Guid company, Guid bank, Guid id, CancellationToken ct) =>
        await db.BankStatements.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.BankAccountId == bank && x.Id == id, ct)
            ?? throw new ApiException(404, "Bank statement not found.", "statement_missing");

    private Task<DateOnly> FirstStatementDate(Guid company, Guid bank, CancellationToken ct) =>
        db.BankStatements.Where(x => x.CompanyId == company && x.BankAccountId == bank).MinAsync(x => x.Start, ct);

    internal static void RequireMatching(ReconciliationSession session, Guid version)
    {
        BankAccountService.CheckVersion(session.Version, version);
        if (session.State != ReconciliationState.Matching)
            throw new ApiException(409, "Move this reconciliation into matching before changing evidence.", "reconciliation_state");
    }

    private async Task<ReconciliationView> View(ReconciliationSession session, BankStatement statement, TreasuryAccount account, CancellationToken ct)
    {
        var active = await db.BankMatches.AsNoTracking().Where(x => x.CompanyId == session.CompanyId && x.SessionId == session.Id &&
            x.ReleasedAt == null).Select(x => x.StatementLineId).ToListAsync(ct);
        var unmatched = await db.BankStatementLines.AsNoTracking().Where(x => x.CompanyId == session.CompanyId && x.StatementId == statement.Id &&
            !active.Contains(x.Id)).OrderBy(x => x.Row).ToListAsync(ct);
        var matchedItems = await (from item in db.BankMatchItems
            join match in db.BankMatches on item.MatchId equals match.Id
            join other in db.ReconciliationSessions on match.SessionId equals other.Id
            join external in db.BankStatements on other.StatementId equals external.Id
            where item.CompanyId == session.CompanyId && other.BankAccountId == session.BankAccountId && !item.Released &&
                external.End <= statement.End select item.JournalLineId).ToListAsync(ct);
        var first = await FirstStatementDate(session.CompanyId, account.Id, ct);
        var book = await ledger.Balance(session.CompanyId, account.LedgerAccountId, statement.End, ct);
        var entries = await ledger.Entries(session.CompanyId, account.LedgerAccountId, statement.End, first).ToListAsync(ct);
        var used = matchedItems.ToHashSet();
        var outstanding = entries.Where(x => !used.Contains(x.LineId)).ToArray();
        var (deposits, withdrawals, difference) = ReconciliationRules.Totals(statement.Closing, book, outstanding.Select(x => x.Amount));
        var suggestions = session.State == ReconciliationState.Matching ? ReconciliationRules.Suggest(unmatched, outstanding) : [];
        return new(session.Id, statement.Id, session.State, session.Version, statement.Closing, book, deposits, withdrawals,
            difference, active.Count, unmatched, outstanding, suggestions, session.FinalizedAt,
            ReconciliationRules.Age(statement.End, unmatched, outstanding));
    }
}
