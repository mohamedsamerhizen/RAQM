using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.FinancialClose;
using RAQM.Api.Entities.FinancialClose;
using RAQM.Api.DTOs.FinancialClose;

namespace RAQM.Api.Services.Accounting;
public sealed class FiscalPeriodService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    CloseReadinessService readiness, GovernanceGate? governance = null) : IFiscalPeriodService
{
    public async Task<FiscalYearResponse> Create(Guid actor, Guid org, Guid company, FiscalYearRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await access.Lock(actor, org, company, true, ct);
        CurrencyPolicy.Units(c.BaseCurrency);
        if (r.StartYear is < 1900 or > 9998)
            throw new ApiException(400, "Fiscal start year must be 1900..9998.", "fiscal_year");
        var start = new DateOnly(r.StartYear, c.FiscalYearStartMonth, c.FiscalYearStartDay);
        var end = start.AddYears(1).AddDays(-1);
        if (await (from batch in db.OpeningBatches join journal in db.Journals on batch.JournalId equals journal.Id
                   where batch.CompanyId == company && journal.CompanyId == company && journal.PostingDate > start
                   select batch.Id).AnyAsync(ct))
            throw new ApiException(409, "An earlier fiscal year would invalidate the retained opening-balance anchor.", "opening_calendar");
        if (await db.FiscalYears.AnyAsync(x => x.CompanyId == company && x.StartDate <= end && x.EndDate >= start, ct))
            throw new ApiException(409, "Fiscal years cannot overlap.", "period_overlap");
        var year = new FiscalYear
        {
            TenantId = c.TenantId,
            OrganizationId = org,
            CompanyId = company,
            Name = AccountingRules.Text(r.Name, 160, "Name"),
            StartDate = start,
            EndDate = end,
            CreatedBy = actor
        };
        db.FiscalYears.Add(year);
        var periods = FiscalCalendar.Generate(start).Select(range => new AccountingPeriod { TenantId = c.TenantId, OrganizationId = org, CompanyId = company, FiscalYearId = year.Id, Number = range.Number, StartDate = range.Start, EndDate = range.End, UpdatedBy = actor }).ToList();
        db.Periods.AddRange(periods);
        access.Audit(c, actor, year.Id, "FiscalYearCreated");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(year.Id, year.Name, start, end, periods.Select(PeriodResponse.From).ToList());
    }

    public async Task<IReadOnlyList<FiscalYearResponse>> List(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        var years = await db.FiscalYears.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.StartDate).ToListAsync(ct);
        var periods = await db.Periods.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Number).ToListAsync(ct);
        return years.Select(y => new FiscalYearResponse(y.Id, y.Name, y.StartDate, y.EndDate, periods.Where(p => p.FiscalYearId == y.Id).Select(PeriodResponse.From).ToList())).ToList();
    }

    public Task<PeriodResponse> ChangeState(Guid actor, Guid org, Guid company, Guid id, PeriodStateRequest r, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (c, ct) =>
    {
        await access.Resolve(actor, org, company, true, true, ct);
        var period = await db.Periods.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw new ApiException(404, "Period not found.", "period_not_found");
        if (period.Version != r.Version)
            throw new ApiException(409, "Period version changed.", "version_conflict");
        if (!Enum.IsDefined(r.State))
            throw new ApiException(400, "Invalid period state.", "period_state");
        if (period.State == PeriodState.Locked)
            throw new ApiException(409, "Locked periods cannot be reopened or changed.", "period_locked");
        if (period.State == r.State)
            throw new ApiException(409, "Period already has this state.", "period_state");
        if (await db.PeriodCloseSessions.AnyAsync(x => x.CompanyId == company && x.PeriodId == id &&
            (x.State == Entities.FinancialClose.CloseWorkflowState.Preparing || x.State == Entities.FinancialClose.CloseWorkflowState.Approved), ct))
            throw new ApiException(409, "Complete or supersede the active financial close session before changing the period state.", "active_close_session");
        if (r.State == PeriodState.Open && await (from close in db.YearEndRecords
            join year in db.FiscalYears on close.FiscalYearId equals year.Id
            where close.CompanyId == company && close.Finalized && year.EndDate >= period.EndDate
            select close.Id).AnyAsync(ct))
            throw new ApiException(409, "The period is covered by a finalized fiscal year. Post a correction in an eligible later period.", "year_closed");
        if (period.State == PeriodState.Open && r.State is PeriodState.Closed or PeriodState.Locked)
        {
            var year = await db.FiscalYears.AsNoTracking().SingleAsync(x => x.CompanyId == company && x.Id == period.FiscalYearId, ct);
            if (period.EndDate == year.EndDate && !await db.YearEndRecords.AnyAsync(x => x.CompanyId == company &&
                x.FiscalYearId == year.Id && !x.Finalized, ct))
                throw new ApiException(409, "The final period requires coordinated fiscal year-end execution.", "year_end_required");
            if (governance is not null) await governance.Enforce(c, actor, FinancialDuty.PeriodClose, id, ct);
            var session = new PeriodCloseSession { CompanyId = company, OrganizationId = org, TenantId = c.TenantId,
                PeriodId = period.Id, CreatedBy = actor };
            var evidence = await readiness.Check(c, actor, session, period, [], ct);
            var blocker = evidence.Findings.FirstOrDefault(x => x.Severity == FindingSeverity.Blocking);
            if (blocker is not null) throw new ApiException(409, blocker.Detail, blocker.Rule);
        }
        var reason = AccountingRules.Text(r.Reason, 500, "Reason");
        var prior = period.State;
        period.State = r.State;
        period.Version = Guid.NewGuid();
        period.UpdatedBy = actor;
        period.UpdatedAt = DateTimeOffset.UtcNow;
        access.Audit(c, actor, id, $"Period{prior}To{r.State}", reason);
        await db.SaveChangesAsync(ct);
        return PeriodResponse.From(period);
    }, ct);
}
