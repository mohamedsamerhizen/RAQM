using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Accruals;
using RAQM.Api.Entities.FinancialClose;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accruals;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Services.Accruals;

namespace RAQM.Api.Services.FinancialClose;

public sealed class ClosePostingPolicy(RaqmDbContext db)
{
    public async Task Validate(Guid actor, Guid company, PostingRequest request, CancellationToken ct)
    {
        if (await (from close in db.YearEndRecords join year in db.FiscalYears on close.FiscalYearId equals year.Id
            where close.CompanyId == company && close.Finalized && year.EndDate >= request.AccountingDate select close.Id).AnyAsync(ct))
            throw new ApiException(409, "The accounting date is covered by a finalized fiscal year.", "year_closed");
        if (request.SourceModule == RecognitionRules.Source)
        {
            var source = await (from occurrence in db.RecognitionOccurrences.AsNoTracking()
                join schedule in db.RecognitionSchedules.AsNoTracking() on occurrence.ScheduleId equals schedule.Id
                where occurrence.CompanyId == company && occurrence.Id == request.SourceDocumentId && schedule.CompanyId == company
                select new { Occurrence = occurrence, Schedule = schedule }).SingleOrDefaultAsync(ct);
            if (source is null || source.Schedule.State != RecognitionState.Approved || source.Occurrence.JournalId.HasValue ||
                source.Occurrence.Date != request.AccountingDate || request.IdempotencyKey != $"recognition:{source.Occurrence.Id:N}" ||
                !RecognitionRules.Lines(source.Schedule, source.Occurrence).SequenceEqual(request.Lines))
                throw InvalidSource();
        }
        else if (request.SourceModule == FinancialMath.YearEndSource)
        {
            var source = await (from close in db.YearEndRecords.AsNoTracking()
                join year in db.FiscalYears.AsNoTracking() on close.FiscalYearId equals year.Id
                join session in db.PeriodCloseSessions.AsNoTracking() on close.SessionId equals session.Id
                where close.CompanyId == company && close.Id == request.SourceDocumentId && !close.Finalized
                select new { Record = close, year.EndDate, session.State }).SingleOrDefaultAsync(ct);
            if (source is null || source.Record.ClosedBy != actor || source.State != CloseWorkflowState.Approved ||
                source.EndDate != request.AccountingDate || request.IdempotencyKey != $"year-end:{source.Record.FiscalYearId:N}" ||
                !(JsonSerializer.Deserialize<JournalDraftLine[]>(source.Record.ClosingLinesJson) ?? []).SequenceEqual(request.Lines))
                throw InvalidSource();
        }
        else if (request.SourceModule.StartsWith("FinancialClose.", StringComparison.Ordinal)) throw InvalidSource();

        if (request.ReversesJournalId is not { } original) return;
        var sourceModule = await db.Journals.AsNoTracking().Where(x => x.CompanyId == company && x.Id == original)
            .Select(x => x.SourceModule).SingleOrDefaultAsync(ct);
        if (sourceModule == FinancialMath.YearEndSource && await db.YearEndRecords.AnyAsync(x => x.CompanyId == company && x.JournalId == original, ct))
            throw new ApiException(409, "Year-end closing journals are retained. Use an eligible later-period correction.", "year_closed");
        if (sourceModule == RecognitionRules.Source)
        {
            var occurrence = await db.RecognitionOccurrences.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.JournalId == original, ct);
            if (occurrence is not null && (occurrence.ReversalHash is null || occurrence.ReversalJournalId.HasValue ||
                request.IdempotencyKey != RecognitionService.ReversalKey(occurrence))) throw InvalidSource();
        }
        if (await (from schedule in db.RecognitionSchedules
            join basis in db.JournalLines on schedule.BasisLineId equals (Guid?)basis.Id
            where schedule.CompanyId == company && basis.JournalId == original &&
                (schedule.State != RecognitionState.Cancelled || schedule.Occurrences.Any(x => x.JournalId != null && x.ReversalJournalId == null))
            select schedule.Id).AnyAsync(ct))
            throw new ApiException(409, "Cancel the dependent deferral and reverse its recognized occurrences before reversing the basis.", "recognition_basis");
    }

    private static ApiException InvalidSource() => new(403, "This posting requires the coordinated financial close or recognition source.", "financial_close_source");
}
