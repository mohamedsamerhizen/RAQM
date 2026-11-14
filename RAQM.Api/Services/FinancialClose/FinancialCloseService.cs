using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.FinancialClose;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialClose;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.YearEnd;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Interfaces.FinancialClose;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.FinancialReporting;

namespace RAQM.Api.Services.FinancialClose;

public sealed class FinancialCloseService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    IFiscalPeriodService periods, CloseReadinessService readiness, FinancialReportService reports, FinancialLedger ledger,
    IPostingEngine engine, TimeProvider clock) : IFinancialCloseService
{
    public Task<CloseView> Start(Guid actor, Guid org, Guid company, Guid periodId, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var period = await Period(company, periodId, token);
            if (period.State != PeriodState.Open) throw Conflict("Start preparation on an open accounting period.");
            var prior = await db.PeriodCloseSessions.Include(x => x.Tasks).SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.PeriodId == periodId && (x.State == CloseWorkflowState.Preparing || x.State == CloseWorkflowState.Approved), token);
            if (prior is not null) return new CloseView(prior, period.State, await Exceptions(company, prior.Id, token));
            var session = new PeriodCloseSession { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, PeriodId = periodId, CreatedBy = actor };
            session.Tasks.Add(new() { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, SessionId = session.Id,
                Code = "GL_REVIEW", Title = "Review posted ledger, adjustments and trial balance", ReviewerId = actor, Required = true });
            db.PeriodCloseSessions.Add(session);
            access.Audit(scope, actor, session.Id, "FinancialCloseStarted", periodId.ToString());
            return new CloseView(session, period.State, []);
        }, ct);

    public async Task<IReadOnlyList<CloseView>> History(Guid actor, Guid org, Guid company, Guid periodId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await access.Resolve(actor, org, company, false, false, ct);
        var period = await Period(company, periodId, ct);
        var sessions = await db.PeriodCloseSessions.AsNoTracking().Include(x => x.Tasks).Where(x => x.CompanyId == company && x.PeriodId == periodId)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        var result = new List<CloseView>();
        foreach (var session in sessions) result.Add(new(session, period.State, await Exceptions(company, session.Id, ct)));
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<CloseReadiness> Readiness(Guid actor, Guid org, Guid company, Guid sessionId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await access.Resolve(actor, org, company, false, false, ct);
        var session = await Session(company, sessionId, ct);
        var period = await Period(company, session.PeriodId, ct);
        var result = await readiness.Check(scope, actor, session, period, await Exceptions(company, sessionId, ct), ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public Task<CloseView> AddTask(Guid actor, Guid org, Guid company, Guid sessionId, CloseTaskRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var session = await Preparing(company, sessionId, request.Version, token);
            var code = AccountingRules.Text(request.Code, 64, "Task code").ToUpperInvariant();
            if (session.Tasks.Any(x => x.Code == code)) throw Conflict("This task code already exists in the session.");
            if (!await db.OrganizationMembers.AnyAsync(x => x.OrganizationId == org && x.TenantId == scope.TenantId && x.UserId == request.ReviewerId &&
                (x.Role == OrganizationRole.Owner || db.CompanyMembers.Any(m => m.CompanyId == company && m.UserId == x.UserId && m.Role == CompanyRole.Manager)), token))
                throw new ApiException(400, "Assign a company financial writer as reviewer.", "close_reviewer");
            db.PeriodCloseTasks.Add(new() { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, SessionId = session.Id, Code = code,
                Title = AccountingRules.Text(request.Title, 160, "Task title"), ReviewerId = request.ReviewerId, Required = request.Required });
            Touch(scope, actor, session, "FinancialCloseTaskAdded", code);
            return await View(session, token);
        }, ct);

    public Task<CloseView> CompleteTask(Guid actor, Guid org, Guid company, Guid sessionId, Guid taskId, CloseAction request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var session = await Preparing(company, sessionId, request.Version, token);
            var task = session.Tasks.SingleOrDefault(x => x.Id == taskId)
                ?? throw new ApiException(404, "Close task not found.", "close_task");
            if (task.CompletedAt.HasValue) throw Conflict("This task already has completed evidence.");
            if (actor != task.ReviewerId) await access.Resolve(actor, org, company, true, true, token);
            task.CompletedAt = clock.GetUtcNow();
            task.CompletedBy = actor;
            task.Evidence = AccountingRules.Text(request.Reason, 500, "Review evidence");
            Touch(scope, actor, session, "FinancialCloseTaskCompleted", task.Id.ToString());
            return await View(session, token);
        }, ct);

    public Task<CloseView> Raise(Guid actor, Guid org, Guid company, Guid sessionId, ExceptionRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            var session = await Preparing(company, sessionId, request.Version, token);
            var exception = new CloseException { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId,
                SessionId = session.Id, Detail = AccountingRules.Text(request.Detail, 1000, "Exception detail"), Blocking = request.Blocking, RaisedBy = actor };
            db.CloseExceptions.Add(exception);
            Touch(scope, actor, session, "FinancialCloseExceptionRaised", exception.Id.ToString());
            await db.SaveChangesAsync(token);
            return await View(session, token);
        }, ct);

    public Task<CloseView> Resolve(Guid actor, Guid org, Guid company, Guid sessionId, Guid exceptionId, CloseAction request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var session = await Preparing(company, sessionId, request.Version, token);
            var item = await db.CloseExceptions.SingleOrDefaultAsync(x => x.CompanyId == company && x.SessionId == sessionId && x.Id == exceptionId, token)
                ?? throw new ApiException(404, "Close exception not found.", "close_exception");
            if (item.ResolvedAt.HasValue) throw Conflict("This exception is already resolved.");
            item.ResolvedAt = clock.GetUtcNow();
            item.ResolvedBy = actor;
            item.Resolution = AccountingRules.Text(request.Reason, 500, "Resolution evidence");
            Touch(scope, actor, session, "FinancialCloseExceptionResolved", item.Id.ToString());
            await db.SaveChangesAsync(token);
            return await View(session, token);
        }, ct);

    public Task<CloseView> Transition(Guid actor, Guid org, Guid company, Guid sessionId, string action, CloseAction request, CancellationToken ct) =>
        RecordFailure(actor, org, company, sessionId, () => transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var session = await Session(company, sessionId, token);
            Version(session, request.Version);
            var period = await Period(company, session.PeriodId, token);
            var reason = AccountingRules.Text(request.Reason, 500, "Close reason");
            if (action == "approve" && session.State == CloseWorkflowState.Preparing)
            {
                var result = await readiness.Check(scope, actor, session, period, await Exceptions(company, session.Id, token), token);
                RequireReady(result);
                session.State = CloseWorkflowState.Approved;
                session.ApprovalHash = result.Fingerprint;
                session.ApprovalReason = reason;
                session.ApprovedBy = actor;
            }
            else if (action == "revalidate" && session.State == CloseWorkflowState.Approved)
            {
                access.Audit(scope, actor, session.Id, "FinancialCloseApprovalWithdrawn", session.ApprovalHash);
                session.State = CloseWorkflowState.Preparing;
                session.ApprovalHash = null;
                session.ApprovalReason = null;
                session.ApprovedBy = null;
            }
            else if (action == "supersede" && session.State is CloseWorkflowState.Preparing or CloseWorkflowState.Approved)
                session.State = CloseWorkflowState.Superseded;
            else if (action == "close" && session.State == CloseWorkflowState.Approved)
            {
                if (await db.FiscalYears.AnyAsync(x => x.CompanyId == company && x.Id == period.FiscalYearId && x.EndDate == period.EndDate, token))
                    throw Conflict("Use the fiscal year-end execution for the last period.");
                var result = await Approved(scope, actor, session, period, token);
                return await Finish(scope, actor, session, period, result, reason, null, token);
            }
            else throw Conflict("This close transition is unavailable.");
            Touch(scope, actor, session, $"FinancialClose:{action}", reason);
            return await View(session, token);
        }, ct), ct);

    public Task<YearEndRecord> YearEnd(Guid actor, Guid org, Guid company, Guid yearId, YearEndRequest request, CancellationToken ct) =>
        RecordFailure(actor, org, company, request.SessionId, () => transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var key = AccountingRules.Text(request.Key, 64, "Year-end key");
            var reason = AccountingRules.Text(request.Reason, 500, "Year-end reason");
            var hash = TradeRules.Hash(new { yearId, request.SessionId, key, reason, request.AllowBackdated });
            var prior = await db.YearEndRecords.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.FiscalYearId == yearId, token);
            if (prior is not null)
            {
                if (prior.RequestHash != hash) throw Conflict("This fiscal year was already finalized with different intent.");
                return prior;
            }
            if (await db.YearEndRecords.AnyAsync(x => x.CompanyId == company && x.RequestKey == key, token))
                throw Conflict("The year-end key is already in use.");
            var year = await db.FiscalYears.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == yearId, token)
                ?? throw new ApiException(404, "Fiscal year not found.", "fiscal_year");
            var session = await Session(company, request.SessionId, token);
            Version(session, request.Version);
            var period = await Period(company, session.PeriodId, token);
            if (period.FiscalYearId != yearId || period.EndDate != year.EndDate) throw Conflict("Approve a close session for the final fiscal period.");
            if (await (from completed in db.YearEndRecords join later in db.FiscalYears on completed.FiscalYearId equals later.Id
                where completed.CompanyId == company && completed.Finalized && later.EndDate >= year.EndDate select completed.Id).AnyAsync(token))
                throw Conflict("A later fiscal year is already finalized.");
            var result = await Approved(scope, actor, session, period, token);
            var definition = await reports.Definition(company, null, token) ?? throw Conflict("Publish the retained earnings definition.");
            var retained = definition.RetainedEarningsId ?? throw Conflict("Map a retained earnings account.");
            var nominal = (await ledger.Accounts(company, token)).Where(x => FinancialMath.Nominal(x.Classification)).Select(x => x.Id).ToArray();
            var entries = await ledger.Lines(company, year.EndDate, year.StartDate, nominal).ToListAsync(token);
            var amounts = FinancialMath.Balances(entries);
            var lines = FinancialMath.ClosingLines(amounts, retained);
            var record = new YearEndRecord { CompanyId = company, OrganizationId = org, TenantId = scope.TenantId, FiscalYearId = year.Id,
                SessionId = session.Id, DefinitionId = definition.Id, RetainedEarningsId = retained, RequestKey = key, RequestHash = hash,
                Profit = -amounts.Values.Sum(), ClosingLinesJson = JsonSerializer.Serialize(lines), ClosedBy = actor, Reason = reason };
            db.YearEndRecords.Add(record);
            await db.SaveChangesAsync(token);
            if (lines.Count > 0)
            {
                var receipt = await engine.PostAsync(actor, new(new(scope.TenantId, org, company), FinancialMath.YearEndSource, record.Id,
                    $"year-end:{year.Id:N}", year.EndDate, scope.BaseCurrency, lines, null, year.EndDate,
                    "Fiscal year closing", year.Id.ToString(), request.AllowBackdated, reason), token);
                record.JournalId = receipt.JournalId;
            }
            await Finish(scope, actor, session, period, result, reason, record.JournalId, token);
            record.Finalized = true;
            record.ClosedAt = clock.GetUtcNow();
            access.Audit(scope, actor, record.Id, "FinancialYearEndCompleted", record.JournalId?.ToString() ?? "No nonzero nominal balances");
            return record;
        }, ct), ct);

    public async Task<IReadOnlyList<YearEndRecord>> Years(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.YearEndRecords.AsNoTracking().Where(x => x.CompanyId == company && x.Finalized).OrderByDescending(x => x.ClosedAt).ToListAsync(ct);
    }

    private async Task<CloseReadiness> Approved(Company scope, Guid actor, PeriodCloseSession session, AccountingPeriod period, CancellationToken ct)
    {
        if (session.State != CloseWorkflowState.Approved) throw Conflict("A current Owner approval is required.");
        var result = await readiness.Check(scope, actor, session, period, await Exceptions(scope.Id, session.Id, ct), ct);
        if (result.Fingerprint != session.ApprovalHash)
            throw new ApiException(409, "Financial readiness changed after approval. Revalidate and approve again.", "close_stale");
        RequireReady(result);
        return result;
    }

    private async Task<CloseView> Finish(Company scope, Guid actor, PeriodCloseSession session, AccountingPeriod period,
        CloseReadiness result, string reason, Guid? journal, CancellationToken ct)
    {
        session.State = CloseWorkflowState.Completed;
        session.ClosedAt = clock.GetUtcNow();
        session.ClosedBy = actor;
        session.EvidenceJson = JsonSerializer.Serialize(new { Readiness = result, JournalId = journal, ApprovedBy = session.ApprovedBy,
            session.ApprovalReason, ClosedBy = actor, Reason = reason });
        Touch(scope, actor, session, "FinancialCloseCompleted", reason);
        await db.SaveChangesAsync(ct);
        var changed = await periods.ChangeState(actor, scope.OrganizationId, scope.Id, period.Id, new(PeriodState.Closed, reason, period.Version), ct);
        return new(session, changed.State, await Exceptions(scope.Id, session.Id, ct));
    }

    private async Task<T> RecordFailure<T>(Guid actor, Guid org, Guid company, Guid sessionId, Func<Task<T>> operation, CancellationToken ct)
    {
        try { return await operation(); }
        catch (ApiException e) when (e.Status == 409 && db.Database.CurrentTransaction is null)
        {
            await transactions.Execute(actor, org, company, async (scope, token) =>
            {
                await access.Resolve(actor, org, company, true, true, token);
                if (await db.PeriodCloseSessions.AnyAsync(x => x.CompanyId == company && x.Id == sessionId, token))
                    access.Audit(scope, actor, sessionId, "FinancialCloseAttemptRejected", e.Message[..Math.Min(500, e.Message.Length)]);
                return true;
            }, ct);
            throw;
        }
    }

    private async Task<CloseView> View(PeriodCloseSession session, CancellationToken ct) =>
        new(session, (await Period(session.CompanyId, session.PeriodId, ct)).State, await Exceptions(session.CompanyId, session.Id, ct));
    private async Task<AccountingPeriod> Period(Guid company, Guid id, CancellationToken ct) =>
        await db.Periods.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw new ApiException(404, "Accounting period not found.", "period_not_found");
    private async Task<PeriodCloseSession> Session(Guid company, Guid id, CancellationToken ct) =>
        await db.PeriodCloseSessions.Include(x => x.Tasks).SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw new ApiException(404, "Close session not found.", "close_missing");
    private Task<List<CloseException>> Exceptions(Guid company, Guid id, CancellationToken ct) =>
        db.CloseExceptions.AsNoTracking().Where(x => x.CompanyId == company && x.SessionId == id).OrderBy(x => x.RaisedAt).ToListAsync(ct);
    private async Task<PeriodCloseSession> Preparing(Guid company, Guid id, Guid version, CancellationToken ct)
    {
        var session = await Session(company, id, ct);
        Version(session, version);
        if (session.State != CloseWorkflowState.Preparing) throw Conflict("Return the close session to preparation before changing evidence.");
        return session;
    }
    private static void Version(PeriodCloseSession session, Guid version)
    {
        if (session.Version != version) throw Conflict("The close session version changed.");
    }
    private void Touch(Company scope, Guid actor, PeriodCloseSession session, string action, string reason)
    {
        session.Version = Guid.NewGuid();
        access.Audit(scope, actor, session.Id, action, reason);
    }
    private static void RequireReady(CloseReadiness result)
    {
        var blocker = result.Findings.FirstOrDefault(x => x.Severity == FindingSeverity.Blocking);
        if (blocker is not null) throw Conflict($"Close blocked by {blocker.Rule}. {blocker.Detail}");
    }
    private static ApiException Conflict(string message) => new(409, message, "financial_close");
}
