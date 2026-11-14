using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.Accruals;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Accruals;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.Accruals;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.Accruals;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.Accruals;

public sealed class RecognitionService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    IPostingEngine engine, TimeProvider clock) : IRecognitionService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public Task<RecognitionSchedule> Create(Guid actor, Guid org, Guid company, ScheduleRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            if (!Enum.IsDefined(request.Kind) || request.Occurrences is < 1 or > 120 || request.EveryMonths is not (1 or 3 or 12) ||
                request.FirstDate.Year is < 1900 or > 9877 ||
                request.ReverseAfterDays is < 1 or > 366 || RecognitionRules.Deferral(request.Kind) && request.ReverseAfterDays.HasValue)
                throw new ApiException(400, "Invalid recognition schedule, frequency, date or reversal policy.", "recognition_schedule");
            var reference = AccountingRules.Text(request.Reference, 160, "Source reference").ToUpperInvariant();
            if (await db.RecognitionSchedules.AnyAsync(x => x.CompanyId == company && x.Reference == reference, token))
                throw Conflict("A schedule already uses this source reference.");
            var schedule = new RecognitionSchedule { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId,
                Kind = request.Kind, Name = AccountingRules.Text(request.Name, 160, "Schedule name"), Reference = reference,
                Currency = scope.BaseCurrency, BasisLineId = request.BasisLineId, DebitAccountId = request.DebitAccountId,
                CreditAccountId = request.CreditAccountId, Total = request.Total, ReverseAfterDays = request.ReverseAfterDays, CreatedBy = actor };
            var amounts = RecognitionRules.Allocate(schedule.Total, request.Occurrences, scope.BaseCurrency);
            if (schedule.Kind == RecognitionKind.Recurring)
            {
                if (request.Template is null || request.Template.Count > 100 || request.BasisLineId.HasValue ||
                    request.DebitAccountId.HasValue || request.CreditAccountId.HasValue)
                    throw new ApiException(400, "Recurring schedules require a journal template and no separate basis or accounts.", "recurring_template");
                var (debit, _) = AccountingRules.Lines(request.Template, scope.BaseCurrency, true);
                if (debit * request.Occurrences != schedule.Total)
                    throw new ApiException(400, "Total must equal template debit multiplied by the number of occurrences.", "recurring_total");
                schedule.TemplateJson = JsonSerializer.Serialize(request.Template);
                amounts = Enumerable.Repeat(debit, request.Occurrences).ToArray();
            }
            else if (request.Template is not null || !request.DebitAccountId.HasValue || !request.CreditAccountId.HasValue ||
                request.DebitAccountId == request.CreditAccountId || RecognitionRules.Deferral(schedule.Kind) != request.BasisLineId.HasValue)
                throw new ApiException(400, "Select distinct accounts and a posted basis line for a deferral.", "recognition_accounts");
            for (var i = 0; i < amounts.Count; i++)
            {
                var date = request.FirstDate.AddMonths(i * request.EveryMonths);
                schedule.Occurrences.Add(new RecognitionOccurrence { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId,
                    ScheduleId = schedule.Id, Number = i + 1, Date = date, Amount = amounts[i],
                    ReverseDue = request.ReverseAfterDays is { } delay ? date.AddDays(delay) : null });
            }
            await Validate(schedule, token);
            if (schedule.BasisLineId.HasValue && await db.RecognitionSchedules.AnyAsync(x => x.CompanyId == company && x.BasisLineId == schedule.BasisLineId, token))
                throw Conflict("This posted basis line already funds a schedule.");
            db.RecognitionSchedules.Add(schedule);
            access.Audit(scope, actor, schedule.Id, "RecognitionScheduleCreated", reference);
            return schedule;
        }, ct);

    public Task<RecognitionSchedule> Transition(Guid actor, Guid org, Guid company, Guid id, bool approve, ScheduleAction request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var schedule = await Find(company, id, token);
            if (schedule.Version != request.Version) throw Conflict("Schedule version changed.");
            var reason = AccountingRules.Text(request.Reason, 500, "Reason");
            if (approve)
            {
                if (schedule.State != RecognitionState.Draft) throw Conflict("Only drafts can be approved.");
                await Validate(schedule, token);
                schedule.State = RecognitionState.Approved;
                schedule.ApprovedBy = actor;
            }
            else
            {
                if (schedule.State == RecognitionState.Cancelled) throw Conflict("The schedule was cancelled.");
                schedule.State = RecognitionState.Cancelled;
            }
            schedule.Version = Guid.NewGuid();
            access.Audit(scope, actor, id, approve ? "RecognitionApproved" : "RecognitionFutureCancelled", reason);
            return schedule;
        }, ct);

    public Task<RecognitionResult> Post(Guid actor, Guid org, Guid company, Guid id, Guid occurrence, RecognitionExecution request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var schedule = await Find(company, id, token);
            var row = FindOccurrence(schedule, occurrence);
            if (row.JournalId.HasValue) return new RecognitionResult(row.Id, row.JournalId, null);
            if (schedule.State != RecognitionState.Approved) throw Conflict("The schedule must be approved and active.");
            if (request.Through > Today || row.Date > request.Through) throw Conflict("The recognition occurrence is not due.");
            if (schedule.Occurrences.Any(x => x.Number < row.Number && x.JournalId == null))
                throw Conflict("Recognize earlier occurrences first.");
            var reason = AccountingRules.Text(request.Reason, 500, "Recognition reason");
            await Validate(schedule, token);
            if (RecognitionRules.Deferral(schedule.Kind))
            {
                var basisAccount = (schedule.Kind == RecognitionKind.PrepaidExpense ? schedule.CreditAccountId : schedule.DebitAccountId)!.Value;
                var signed = await (from line in db.JournalLines join j in db.Journals on line.JournalId equals j.Id
                    where line.CompanyId == company && j.CompanyId == company && line.AccountId == basisAccount &&
                        j.State == JournalState.Posted && j.PostingDate <= row.Date select line.Debit - line.Credit).SumAsync(token);
                if ((schedule.Kind == RecognitionKind.PrepaidExpense ? signed : -signed) < row.Amount)
                    throw Conflict("The posted deferral account does not hold enough remaining basis at the recognition date.");
            }
            var receipt = await engine.PostAsync(actor, new(new(scope.TenantId, org, company), RecognitionRules.Source, row.Id,
                $"recognition:{row.Id:N}", row.Date, scope.BaseCurrency, RecognitionRules.Lines(schedule, row), null,
                row.Date, schedule.Name, schedule.Reference, request.AllowBackdated, reason), token);
            row.JournalId = receipt.JournalId;
            access.Audit(scope, actor, schedule.Id, "RecognitionPosted", $"{row.Id} → {receipt.JournalId}");
            return new RecognitionResult(row.Id, row.JournalId, null);
        }, ct);

    public Task<RecognitionResult> Reverse(Guid actor, Guid org, Guid company, Guid id, Guid occurrence, RecognitionReversal request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var schedule = await Find(company, id, token);
            var row = FindOccurrence(schedule, occurrence);
            var key = AccountingRules.Text(request.Key, 64, "Reversal key");
            var reason = AccountingRules.Text(request.Reason, 500, "Reversal reason");
            var hash = TradeRules.Hash(new { key, reason, request.Date });
            if (row.ReversalJournalId.HasValue)
            {
                if (row.ReversalHash != hash) throw Conflict("The occurrence already has a different reversal.");
                return new RecognitionResult(row.Id, row.ReversalJournalId, null);
            }
            if (!row.JournalId.HasValue || request.Date < row.Date) throw Conflict("Reverse a posted occurrence on or after its recognition date.");
            var original = await db.Journals.AsNoTracking().Include(x => x.Lines)
                .SingleAsync(x => x.CompanyId == company && x.Id == row.JournalId, token);
            row.ReversalHash = hash;
            await db.SaveChangesAsync(token);
            var receipt = await engine.PostAsync(actor, new(new(scope.TenantId, org, company), "Reversal", original.Id,
                ReversalKey(row), request.Date, scope.BaseCurrency, original.Lines.OrderBy(x => x.Number)
                    .Select(x => new JournalDraftLine(x.AccountId, x.Credit, x.Debit, x.Description)).ToArray(),
                original.Id, request.Date, "Reverse recognition", schedule.Reference, request.AllowBackdated, reason), token);
            row.ReversalJournalId = receipt.JournalId;
            access.Audit(scope, actor, schedule.Id, "RecognitionReversed", $"{row.Id} → {receipt.JournalId}");
            return new RecognitionResult(row.Id, row.ReversalJournalId, null);
        }, ct);

    internal static string ReversalKey(RecognitionOccurrence row) => $"recognition:reverse:{row.Id:N}:{row.ReversalHash}";

    public async Task<IReadOnlyList<RecognitionResult>> Run(Guid actor, Guid org, Guid company, Guid id, RecognitionExecution request, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, true, false, ct);
        if (request.Through.Year < 1900 || request.Through > Today) throw Conflict("Choose a run cutoff no later than today.");
        var reason = AccountingRules.Text(request.Reason, 500, "Run reason");
        var schedule = await db.RecognitionSchedules.AsNoTracking().Include(x => x.Occurrences)
            .SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw Missing();
        if (schedule.State == RecognitionState.Draft) throw Conflict("Approve the schedule before running it.");
        var results = new List<RecognitionResult>();
        var events = schedule.Occurrences.SelectMany(x => new[]
        {
            (Row: x, Date: (DateOnly?)x.Date, Reverse: false),
            (Row: x, Date: x.ReverseDue, Reverse: true)
        }).Where(x => x.Date <= request.Through).OrderBy(x => x.Date).ThenBy(x => x.Reverse).ThenBy(x => x.Row.Number);
        var failedPosts = new HashSet<Guid>();
        foreach (var item in events)
        {
            if (item.Reverse && (item.Row.ReversalJournalId.HasValue || failedPosts.Contains(item.Row.Id) ||
                item.Row.JournalId == null && schedule.State == RecognitionState.Cancelled)) continue;
            if (!item.Reverse && (schedule.State != RecognitionState.Approved || item.Row.JournalId.HasValue)) continue;
            db.ChangeTracker.Clear(); // Each occurrence observes state after acquiring the company lock again.
            try
            {
                results.Add(item.Reverse
                    ? await Reverse(actor, org, company, id, item.Row.Id, new(item.Date!.Value, "scheduled", "Scheduled reversal", request.AllowBackdated), ct)
                    : await Post(actor, org, company, id, item.Row.Id, request, ct));
            }
            catch (Exception ex) when (ex is ApiException or DbUpdateException or PostgresException)
            {
                var message = ex is ApiException ? ex.Message : "Database rejected the financial operation.";
                if (!item.Reverse) failedPosts.Add(item.Row.Id);
                results.Add(new(item.Row.Id, null, message));
                await transactions.Execute(actor, org, company, (scope, _) =>
                {
                    access.Audit(scope, actor, id, "RecognitionExecutionFailed", $"{item.Row.Id}. {reason}. {message}"[..Math.Min(500, $"{item.Row.Id}. {reason}. {message}".Length)]);
                    return Task.FromResult(true);
                }, ct);
            }
        }
        return results;
    }

    public async Task<PageResult<RecognitionSchedule>> List(Guid actor, Guid org, Guid company, int page, int size, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        AccountingRules.Page(page, size);
        var query = db.RecognitionSchedules.AsNoTracking().Where(x => x.CompanyId == company);
        var count = await query.CountAsync(ct);
        return new(await query.Include(x => x.Occurrences).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct), count, page, size);
    }

    public async Task<IReadOnlyList<RecognitionPreview>> Preview(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        var schedule = await Find(company, id, ct);
        return schedule.Occurrences.OrderBy(x => x.Number).Select(x =>
            new RecognitionPreview(x.Id, x.Number, x.Date, x.Amount, x.ReverseDue, RecognitionRules.Lines(schedule, x))).ToList();
    }

    public async Task<ScheduleReconciliation> Reconcile(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var schedule = await Find(company, id, ct);
        var posted = schedule.Occurrences.Where(x => x.JournalId.HasValue).ToArray();
        var reversed = posted.Where(x => x.ReversalJournalId.HasValue).ToArray();
        var originals = posted.Select(x => x.JournalId!.Value).ToArray();
        var reversals = reversed.Select(x => x.ReversalJournalId!.Value).ToArray();
        var ids = originals.Concat(reversals).ToArray();
        var ledger = await (from line in db.JournalLines join j in db.Journals on line.JournalId equals j.Id
            where line.CompanyId == company && j.CompanyId == company && j.State == JournalState.Posted && ids.Contains(j.Id)
            group line by j.Id into g select new { Id = g.Key, Debit = g.Sum(x => x.Debit) }).ToListAsync(ct);
        var expected = posted.Sum(x => x.Amount) - reversed.Sum(x => x.Amount);
        var actual = ledger.Where(x => originals.Contains(x.Id)).Sum(x => x.Debit) - ledger.Where(x => reversals.Contains(x.Id)).Sum(x => x.Debit);
        await transaction.CommitAsync(ct);
        return new(id, schedule.Total, posted.Sum(x => x.Amount), reversed.Sum(x => x.Amount),
            schedule.State == RecognitionState.Cancelled ? 0 : schedule.Total - posted.Sum(x => x.Amount),
            schedule.Total - expected, expected, actual, actual - expected);
    }

    private async Task Validate(RecognitionSchedule schedule, CancellationToken ct)
    {
        var lines = RecognitionRules.Lines(schedule, schedule.Occurrences[0]);
        var ids = lines.Select(x => x.AccountId).Distinct().ToArray();
        var accounts = await db.Accounts.AsNoTracking().Where(x => x.CompanyId == schedule.CompanyId && ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        if (accounts.Count != ids.Length || accounts.Values.Any(x => !x.IsActive || !x.IsPosting))
            throw new ApiException(400, "Use active company posting accounts.", "recognition_accounts");
        if (schedule.Kind != RecognitionKind.Recurring)
        {
            var debit = accounts[schedule.DebitAccountId!.Value];
            var credit = accounts[schedule.CreditAccountId!.Value];
            if ((debit.Classification, credit.Classification) != RecognitionRules.Classes(schedule.Kind) || debit.IsContra || credit.IsContra)
                throw new ApiException(400, "The schedule requires ordinary debit and credit accounts of the appropriate classes.", "recognition_accounts");
        }
        if (schedule.BasisLineId is { } basis)
        {
            var expectedAccount = schedule.Kind == RecognitionKind.PrepaidExpense ? schedule.CreditAccountId : schedule.DebitAccountId;
            var source = await (from line in db.JournalLines join journal in db.Journals on line.JournalId equals journal.Id
                where line.CompanyId == schedule.CompanyId && journal.CompanyId == schedule.CompanyId && line.Id == basis &&
                    journal.State == JournalState.Posted && journal.ReversesJournalId == null && line.AccountId == expectedAccount
                select new { line.Debit, line.Credit, journal.Id, journal.PostingDate }).SingleOrDefaultAsync(ct);
            if (source is null || source.PostingDate > schedule.Occurrences.Min(x => x.Date) ||
                (schedule.Kind == RecognitionKind.PrepaidExpense ? source.Debit - source.Credit : source.Credit - source.Debit) != schedule.Total ||
                await db.Journals.AnyAsync(x => x.CompanyId == schedule.CompanyId && x.ReversesJournalId == source.Id && x.State == JournalState.Posted, ct))
                throw new ApiException(400, "Use an unreversed posted basis line on the correct account for the full scheduled amount.", "recognition_basis");
        }
    }

    private async Task<RecognitionSchedule> Find(Guid company, Guid id, CancellationToken ct) =>
        await db.RecognitionSchedules.Include(x => x.Occurrences).SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw Missing();
    private static RecognitionOccurrence FindOccurrence(RecognitionSchedule schedule, Guid id) =>
        schedule.Occurrences.SingleOrDefault(x => x.Id == id) ?? throw new ApiException(404, "Recognition occurrence not found.", "recognition_missing");
    private static ApiException Missing() => new(404, "Recognition schedule not found.", "recognition_missing");
    private static ApiException Conflict(string detail) => new(409, detail, "recognition_conflict");
}
