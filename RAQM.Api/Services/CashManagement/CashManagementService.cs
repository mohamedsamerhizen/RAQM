using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.CashManagement;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.CashManagement;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.CashManagement;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.CashManagement;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.Banking;
using RAQM.Api.Services.Trading;
using RAQM.Api.Services.Treasury;

namespace RAQM.Api.Services.CashManagement;

public sealed class CashManagementService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    IPostingEngine engine, BankingPolicy policy, TradingPolicy trading, TreasuryLedger ledger, TimeProvider clock, GovernanceGate? governance = null) : ICashManagementService
{
    public Task<TreasuryMovement> PrepareTransfer(Guid actor, Guid org, Guid company, TransferRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var key = Key(request.IdempotencyKey);
            var hash = TradeRules.Hash(request);
            var prior = await Retry(company, key, hash, token);
            if (prior is not null) return prior;
            if (request.From == request.To)
                throw new ApiException(400, "Transfer accounts must be distinct.", "transfer_accounts");
            var from = await policy.Account(company, request.From, token);
            var to = await policy.Account(company, request.To, token);
            TradingPolicy.Positive(request.Amount, scope.BaseCurrency);
            ValidDate(request.Date);
            var movement = Create(scope, actor, TreasuryMovementKind.Transfer, from.Id, to.LedgerAccountId, from.LedgerAccountId,
                request.Amount, request.Date, request.Reference, request.Reason, key, hash);
            movement.DestinationId = to.Id;
            db.TreasuryMovements.Add(movement);
            access.Audit(scope, actor, movement.Id, "TreasuryTransferPrepared", movement.Reason);
            return movement;
        }, ct);

    public Task<TreasuryMovement> ApproveTransfer(Guid actor, Guid org, Guid company, Guid id, MovementAction request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var movement = await Find(company, id, token);
            if (movement.Kind != TreasuryMovementKind.Transfer)
                throw new ApiException(400, "Only transfers use this approval action.", "movement_kind");
            var key = Key(request.IdempotencyKey);
            var reason = AccountingRules.Text(request.Reason, 500, "Approval reason");
            var hash = TradeRules.Hash(new { key, request.Date, reason, request.AllowBackdated });
            if (movement.State != TreasuryMovementState.PendingApproval)
            {
                if (movement.ApprovalHash != hash) throw Conflict("Transfer already approved with different intent.");
                return movement;
            }
            BankAccountService.CheckVersion(movement.Version, request.Version);
            if (movement.PostingDate != request.Date)
                throw new ApiException(400, "Approval date must match the requested posting date.", "transfer_date");
            await policy.Account(company, movement.AccountId, token);
            await policy.Account(company, movement.DestinationId!.Value, token);
            if (governance is not null) await governance.Enforce(scope, actor, FinancialDuty.BankTransfer, id, token);
            movement.ApprovalHash = hash;
            return await Post(scope, actor, movement, request.AllowBackdated, reason, token);
        }, ct);

    public Task<TreasuryMovement> Record(Guid actor, Guid org, Guid company, CashMovementRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var key = Key(request.IdempotencyKey);
            var hash = TradeRules.Hash(request);
            var prior = await Retry(company, key, hash, token);
            if (prior is not null) return prior;
            var account = await policy.Account(company, request.AccountId, token);
            var receipt = request.Kind == TreasuryMovementKind.CashReceipt;
            if (request.Kind == TreasuryMovementKind.PettyExpense && account.Kind == TreasuryAccountKind.PettyCash)
                await Custodian(scope, actor, account, token);
            else if (request.Kind is TreasuryMovementKind.CashReceipt or TreasuryMovementKind.CashPayment && account.Kind == TreasuryAccountKind.Cash)
                await access.Resolve(actor, org, company, true, true, token);
            else
                throw new ApiException(400, "Choose a cash receipt/payment or a petty cash expense for the corresponding account type.", "movement_kind");
            await Offset(company, request.OffsetLedgerId, receipt ? AccountClass.Revenue : AccountClass.Expense, token);
            TradingPolicy.Positive(request.Amount, scope.BaseCurrency);
            var movement = Create(scope, actor, request.Kind, account.Id,
                receipt ? account.LedgerAccountId : request.OffsetLedgerId, receipt ? request.OffsetLedgerId : account.LedgerAccountId,
                request.Amount, request.Date, request.Reference, request.Reason, key, hash);
            movement.OffsetLedgerId = request.OffsetLedgerId;
            db.TreasuryMovements.Add(movement);
            return await Post(scope, actor, movement, request.AllowBackdated, movement.Reason, token);
        }, ct);

    public Task<TreasuryMovement> Adjust(Guid actor, Guid org, Guid company, Guid bank, Guid sessionId, AdjustmentRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var key = Key(request.IdempotencyKey);
            var hash = TradeRules.Hash(new { bank, sessionId, request.StatementLineId, request.Kind, request.OffsetLedgerId, request.Reason, request.AllowBackdated });
            var prior = await Retry(company, key, hash, token);
            if (prior is not null) return prior;
            var session = await db.ReconciliationSessions.SingleOrDefaultAsync(x => x.CompanyId == company && x.BankAccountId == bank && x.Id == sessionId, token)
                ?? throw new ApiException(404, "Reconciliation session not found.", "reconciliation_missing");
            ReconciliationService.RequireMatching(session, request.SessionVersion);
            var account = await policy.Bank(company, bank, token);
            var line = await db.BankStatementLines.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.StatementId == session.StatementId && x.Id == request.StatementLineId, token)
                ?? throw new ApiException(404, "Statement line not found in this session.", "statement_line");
            var fee = request.Kind == TreasuryMovementKind.BankFee;
            if (!(fee && line.Amount < 0 || request.Kind == TreasuryMovementKind.BankInterest && line.Amount > 0))
                throw new ApiException(400, "Fees require a bank debit and interest requires a bank credit.", "adjustment_direction");
            if (await db.BankMatches.AnyAsync(x => x.CompanyId == company && x.StatementLineId == line.Id && x.ReleasedAt == null, token) ||
                await db.TreasuryMovements.AnyAsync(x => x.CompanyId == company && x.StatementLineId == line.Id && x.State != TreasuryMovementState.Reversed, token))
                throw Conflict("This statement line has an active match or adjustment.");
            var candidateBook = await ledger.Entries(company, account.LedgerAccountId, line.BookingDate.AddDays(3)).ToListAsync(token);
            var priorAdjustmentJournals = await db.TreasuryMovements.Where(x => x.CompanyId == company && x.StatementLineId == line.Id &&
                x.State == TreasuryMovementState.Reversed).Select(x => x.JournalId).ToListAsync(token);
            if (candidateBook.Any(x => x.Amount == line.Amount && !string.IsNullOrWhiteSpace(line.Reference) &&
                string.Equals(x.Reference?.Trim(), line.Reference, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(x.Date.DayNumber - line.BookingDate.DayNumber) <= 3 && !priorAdjustmentJournals.Contains(x.JournalId)))
                throw Conflict("A book movement with this amount and reference already exists. Investigate or match it first.");
            await Offset(company, request.OffsetLedgerId, fee ? AccountClass.Expense : AccountClass.Revenue, token);
            var movement = Create(scope, actor, request.Kind, account.Id,
                fee ? request.OffsetLedgerId : account.LedgerAccountId, fee ? account.LedgerAccountId : request.OffsetLedgerId,
                Math.Abs(line.Amount), line.BookingDate, string.IsNullOrWhiteSpace(line.Reference) ? $"Statement line {line.Id}" : line.Reference,
                request.Reason, key, hash);
            movement.OffsetLedgerId = request.OffsetLedgerId;
            movement.StatementLineId = line.Id;
            db.TreasuryMovements.Add(movement);
            session.Version = Guid.NewGuid();
            return await Post(scope, actor, movement, request.AllowBackdated, movement.Reason, token);
        }, ct);

    public Task<TreasuryMovement> Reverse(Guid actor, Guid org, Guid company, Guid id, MovementAction request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var movement = await Find(company, id, token);
            var key = Key(request.IdempotencyKey);
            var reason = AccountingRules.Text(request.Reason, 500, "Reversal reason");
            var hash = TradeRules.Hash(new { key, request.Date, reason, request.AllowBackdated });
            if (movement.State == TreasuryMovementState.Reversed)
            {
                if (movement.ReversalHash != hash) throw Conflict("Reversal already posted with different intent.");
                return movement;
            }
            BankAccountService.CheckVersion(movement.Version, request.Version);
            if (movement.State != TreasuryMovementState.Posted || request.Date < movement.PostingDate)
                throw Conflict("Reverse a posted movement on or after its posting date.");
            var original = await db.Journals.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.CompanyId == company && x.Id == movement.JournalId, token);
            movement.ReversalKey = key;
            movement.ReversalHash = hash;
            movement.ReversedBy = actor;
            await db.SaveChangesAsync(token);
            var receipt = await engine.PostAsync(actor, new(new(scope.TenantId, org, company), "Reversal", original.Id,
                TradeRules.Key("TreasuryReverse", id, key), request.Date, scope.BaseCurrency,
                original.Lines.OrderBy(x => x.Number).Select(x => new JournalDraftLine(x.AccountId, x.Credit, x.Debit, x.Description)).ToArray(),
                original.Id, request.Date, "Reverse treasury movement", original.Reference, request.AllowBackdated, reason), token);
            movement.State = TreasuryMovementState.Reversed;
            movement.ReversalJournalId = receipt.JournalId;
            movement.Version = Guid.NewGuid();
            access.Audit(scope, actor, id, "TreasuryMovementReversed", reason);
            return movement;
        }, ct);

    public Task<CashCount> Count(Guid actor, Guid org, Guid company, CashCountRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var account = await policy.Account(company, request.AccountId, token);
            await Custodian(scope, actor, account, token);
            ValidDate(request.Date);
            CurrencyPolicy.Validate(request.CountedAmount, CurrencyPolicy.Units(scope.BaseCurrency));
            var book = await ledger.Balance(company, account.LedgerAccountId, request.Date, token);
            BankingPolicy.SignedAmount(book, scope.BaseCurrency);
            var count = new CashCount { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, AccountId = account.Id,
                CountDate = request.Date, BookAmount = book, CountedAmount = request.CountedAmount, CountedBy = actor,
                Evidence = AccountingRules.Text(request.Evidence, 500, "Count evidence") };
            db.CashCounts.Add(count);
            access.Audit(scope, actor, count.Id, "PettyCashCounted", FormattableString.Invariant($"Variance {count.CountedAmount - book}"));
            return count;
        }, ct);

    public Task<TreasuryMovement> Correct(Guid actor, Guid org, Guid company, Guid countId, CountCorrectionRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var key = Key(request.IdempotencyKey);
            var hash = TradeRules.Hash(new { countId, request });
            var prior = await Retry(company, key, hash, token);
            if (prior is not null) return prior;
            var count = await db.CashCounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == countId, token)
                ?? throw new ApiException(404, "Cash count not found.", "cash_count");
            var account = await policy.Account(company, count.AccountId, token);
            var balance = await ledger.Balance(company, account.LedgerAccountId, count.CountDate, token);
            if (await db.CashCounts.AnyAsync(x => x.CompanyId == company && x.AccountId == account.Id && x.CountedAt > count.CountedAt, token) ||
                balance != count.BookAmount || await db.TreasuryMovements.AnyAsync(x => x.CompanyId == company && x.CashCountId == countId &&
                    x.State != TreasuryMovementState.Reversed, token))
                throw Conflict("Cash count is stale or already corrected. Record a new count.");
            var variance = count.CountedAmount - balance;
            if (variance == 0) throw new ApiException(400, "This count has no variance to correct.", "cash_count_balanced");
            await Offset(company, request.OffsetLedgerId, variance > 0 ? AccountClass.Revenue : AccountClass.Expense, token);
            TradingPolicy.Positive(Math.Abs(variance), scope.BaseCurrency);
            var movement = Create(scope, actor, TreasuryMovementKind.CountCorrection, account.Id,
                variance > 0 ? account.LedgerAccountId : request.OffsetLedgerId, variance > 0 ? request.OffsetLedgerId : account.LedgerAccountId,
                Math.Abs(variance), count.CountDate, $"Cash count {count.Id}", request.Reason, key, hash);
            movement.OffsetLedgerId = request.OffsetLedgerId;
            movement.CashCountId = countId;
            db.TreasuryMovements.Add(movement);
            return await Post(scope, actor, movement, request.AllowBackdated, movement.Reason, token);
        }, ct);

    private async Task<TreasuryMovement> Post(Company scope, Guid actor, TreasuryMovement movement, bool backdated, string reason, CancellationToken ct)
    {
        movement.PostedBy = actor;
        await db.SaveChangesAsync(ct);
        var receipt = await engine.PostAsync(actor, new(new(scope.TenantId, scope.OrganizationId, scope.Id), TreasuryPosting.Source(movement.Kind),
            movement.Id, TradeRules.Key("Treasury", movement.Id, movement.RequestKey), movement.PostingDate, scope.BaseCurrency,
            TreasuryPosting.Lines(movement), null, movement.PostingDate, movement.Kind.ToString(), movement.Reference, backdated, reason), ct);
        movement.State = TreasuryMovementState.Posted;
        movement.JournalId = receipt.JournalId;
        movement.Version = Guid.NewGuid();
        access.Audit(scope, actor, movement.Id, "TreasuryMovementPosted", reason);
        return movement;
    }

    public async Task<PageResult<TreasuryMovement>> Movements(Guid actor, Guid org, Guid company, Guid? account, int page, int size, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        AccountingRules.Page(page, size);
        var query = db.TreasuryMovements.AsNoTracking().Where(x => x.CompanyId == company);
        if (account.HasValue) query = query.Where(x => x.AccountId == account || x.DestinationId == account);
        var total = await query.CountAsync(ct);
        return new(await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct), total, page, size);
    }

    public async Task<PageResult<CashCount>> Counts(Guid actor, Guid org, Guid company, Guid account, int page, int size, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        await policy.Account(company, account, ct, active: false);
        AccountingRules.Page(page, size);
        var query = db.CashCounts.AsNoTracking().Where(x => x.CompanyId == company && x.AccountId == account);
        var total = await query.CountAsync(ct);
        return new(await query.OrderByDescending(x => x.CountDate).ThenByDescending(x => x.CountedAt).ThenBy(x => x.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct), total, page, size);
    }

    private async Task Custodian(Company scope, Guid actor, TreasuryAccount account, CancellationToken ct)
    {
        if (account.Kind != TreasuryAccountKind.PettyCash)
            throw new ApiException(400, "Select a petty cash account.", "petty_cash");
        if (account.CustodianId != actor)
            await access.Resolve(actor, scope.OrganizationId, scope.Id, true, true, ct);
    }

    private async Task Offset(Guid company, Guid id, AccountClass classification, CancellationToken ct)
    {
        var account = await trading.Account(company, id, ct);
        if (account.Classification != classification || await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == company && x.LedgerAccountId == id, ct))
            throw new ApiException(400, $"Select an ordinary {classification} posting account.", "treasury_offset");
    }

    private async Task<TreasuryMovement?> Retry(Guid company, string key, string hash, CancellationToken ct)
    {
        var row = await db.TreasuryMovements.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.RequestKey == key, ct);
        if (row is not null && row.RequestHash != hash) throw Conflict("The movement key has a different payload.");
        return row;
    }

    private async Task<TreasuryMovement> Find(Guid company, Guid id, CancellationToken ct) =>
        await db.TreasuryMovements.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw new ApiException(404, "Treasury movement not found.", "treasury_movement");

    private void ValidDate(DateOnly date)
    {
        if (date.Year < 1900 || date > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            throw new ApiException(400, "Use a date from 1900 through today in UTC.", "treasury_date");
    }

    private static TreasuryMovement Create(Company scope, Guid actor, TreasuryMovementKind kind, Guid account, Guid debit, Guid credit,
        decimal amount, DateOnly date, string reference, string reason, string key, string hash) =>
        new() { CompanyId = scope.Id, OrganizationId = scope.OrganizationId, TenantId = scope.TenantId, CreatedBy = actor, Kind = kind,
            AccountId = account, DebitLedgerId = debit, CreditLedgerId = credit, Amount = amount, PostingDate = date,
            Reference = AccountingRules.Text(reference, 160, "Evidence reference"), Reason = AccountingRules.Text(reason, 500, "Reason"),
            RequestKey = key, RequestHash = hash };
    private static string Key(string key) => AccountingRules.Text(key, 64, "Idempotency key");
    private static ApiException Conflict(string message) => new(409, message, "treasury_conflict");
}
