using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Treasury;
using RAQM.Api.Entities.Accounting;

namespace RAQM.Api.Services.Treasury;

public sealed class TreasuryLedger(RaqmDbContext db)
{
    public IQueryable<BookMovement> Entries(Guid company, Guid ledger, DateOnly cutoff, DateOnly? earliest = null, Guid[]? ids = null) =>
        from line in db.JournalLines.AsNoTracking()
        join journal in db.Journals.AsNoTracking() on line.JournalId equals journal.Id
        where line.CompanyId == company && journal.CompanyId == company && line.AccountId == ledger &&
            journal.State == JournalState.Posted && journal.PostingDate <= cutoff &&
            (!earliest.HasValue || journal.PostingDate >= earliest.Value) && (ids == null || ids.Contains(line.Id))
        orderby journal.PostingDate, journal.Sequence, line.Number
        select new BookMovement(line.Id, journal.Id, journal.PostingDate, line.Debit - line.Credit,
            journal.SourceModule, journal.Reference,
            db.Payments.Where(p => p.CompanyId == company && (p.JournalId == journal.Id || p.ReversalJournalId == journal.Id))
                .Select(p => (Guid?)p.Id).FirstOrDefault(),
            db.Payments.Where(p => p.CompanyId == company && (p.JournalId == journal.Id || p.ReversalJournalId == journal.Id))
                .Select(p => p.BankAccountId).FirstOrDefault(),
            db.Journals.Where(original => original.CompanyId == company && original.Id == journal.ReversesJournalId)
                .Select(original => original.SourceModule).FirstOrDefault());

    public Task<decimal> Balance(Guid company, Guid ledger, DateOnly cutoff, CancellationToken ct) =>
        (from line in db.JournalLines
         join journal in db.Journals on line.JournalId equals journal.Id
         where line.CompanyId == company && journal.CompanyId == company && line.AccountId == ledger &&
             journal.State == JournalState.Posted && journal.PostingDate <= cutoff
         select line.Debit - line.Credit).SumAsync(ct);
}
