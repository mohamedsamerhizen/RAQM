using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.FinancialReporting;
using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.Services.FinancialReporting;

public sealed class FinancialLedger(RaqmDbContext db)
{
    public IQueryable<FinancialLine> Lines(Guid company, DateOnly cutoff, DateOnly? earliest = null, Guid[]? accounts = null,
        bool performance = false, long? asOfSequence = null) =>
        from line in db.JournalLines.AsNoTracking()
        join journal in db.Journals.AsNoTracking() on line.JournalId equals journal.Id
        let original = db.Journals.Where(x => x.CompanyId == company && x.Id == journal.ReversesJournalId)
            .Select(x => x.SourceModule).FirstOrDefault()
        where line.CompanyId == company && journal.CompanyId == company && journal.State == JournalState.Posted &&
            journal.PostingDate <= cutoff && (!earliest.HasValue || journal.PostingDate >= earliest.Value) &&
            (!asOfSequence.HasValue || journal.Sequence <= asOfSequence.Value) &&
            (accounts == null || accounts.Contains(line.AccountId)) &&
            (!performance || (journal.SourceModule != Helpers.FinancialReporting.FinancialMath.YearEndSource &&
                journal.SourceModule != "OpeningBalance" && (original == null || original != "OpeningBalance")))
        orderby journal.PostingDate, journal.Sequence, line.Number
        select new FinancialLine
        {
            LineId = line.Id,
            JournalId = journal.Id,
            AccountId = line.AccountId,
            Date = journal.PostingDate,
            Sequence = journal.Sequence!.Value,
            Number = line.Number,
            Source = journal.SourceModule,
            Reference = journal.Reference,
            JournalNumber = journal.Number,
            Debit = line.Debit,
            Credit = line.Credit,
            ReversesJournalId = journal.ReversesJournalId,
            OriginalSource = original
        };

    public Task<List<LedgerAccount>> Accounts(Guid company, CancellationToken ct) =>
        db.Accounts.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct);
}
