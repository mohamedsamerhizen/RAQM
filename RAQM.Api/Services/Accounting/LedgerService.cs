using System.Data;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;

namespace RAQM.Api.Services.Accounting;
public sealed class LedgerService(RaqmDbContext db, FinancialAccess access) : ILedgerService
{
    private static void Dates(DateOnly from, DateOnly to)
    {
        if (from.Year < 1900 || to < from)
            throw new ApiException(400, "Specify a valid report date range beginning in or after 1900.", "report_dates");
    }

    public async Task<AccountLedgerResponse> AccountLedger(Guid actor, Guid org, Guid company, Guid account, DateOnly from, DateOnly to, int page, int pageSize, CancellationToken ct)
    {
        Dates(from, to);
        AccountingRules.Page(page, pageSize);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var c = await access.Resolve(actor, org, company, false, false, ct);
        var accounts = await db.Accounts.AsNoTracking().Where(x => x.CompanyId == company).ToListAsync(ct);
        if (!accounts.Any(x => x.Id == account))
            throw new ApiException(404, "Account not found.", "account_not_found");
        var included = AccountingRules.Descendants(account, accounts.ToDictionary(x => x.Id, x => x.ParentId)).ToArray();
        var q =
            from l in db.JournalLines.AsNoTracking()join j in db.Journals on l.JournalId equals j.Id
            where l.CompanyId == company && j.CompanyId == company && j.State == JournalState.Posted && included.Contains(l.AccountId) && j.PostingDate <= to
            select new
            {
                l.Id,
                l.AccountId,
                l.Number,
                l.Debit,
                l.Credit,
                LineDescription = l.Description,
                Journal = j
            };
        var opening = await q.Where(x => x.Journal.PostingDate < from).Select(x => (decimal? )(x.Debit - x.Credit)).SumAsync(ct) ?? 0m;
        var period = q.Where(x => x.Journal.PostingDate >= from);
        var debit = await period.Select(x => (decimal? )x.Debit).SumAsync(ct) ?? 0m;
        var credit = await period.Select(x => (decimal? )x.Credit).SumAsync(ct) ?? 0m;
        var ordered = period.OrderBy(x => x.Journal.PostingDate).ThenBy(x => x.Journal.Sequence).ThenBy(x => x.Number);
        var offset = (page - 1) * pageSize;
        var carry = await ordered.Take(offset).Select(x => (decimal? )(x.Debit - x.Credit)).SumAsync(ct) ?? 0m;
        var count = await period.CountAsync(ct);
        var rows = await ordered.Skip(offset).Take(pageSize).ToListAsync(ct);
        decimal running = opening + carry;
        var lines = rows.Select(x =>
        {
            running += x.Debit - x.Credit;
            return new LedgerTransaction(x.Journal.Id, x.Journal.Number!, x.Id, x.AccountId, x.Number, x.Journal.PostingDate, x.Journal.DocumentDate, x.LineDescription ?? x.Journal.Description, x.Journal.Reference, x.Journal.SourceModule, x.Journal.SourceDocumentId, x.Journal.ReversesJournalId, x.Debit, x.Credit, running);
        }).ToList();
        await tx.CommitAsync(ct);
        return new(account, c.BaseCurrency, from, to, opening, debit, credit, opening + debit - credit, new(lines, count, page, pageSize));
    }

    public async Task<TrialBalanceResponse> TrialBalance(Guid actor, Guid org, Guid company, DateOnly from, DateOnly to, bool includeHierarchy, CancellationToken ct)
    {
        Dates(from, to);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var c = await access.Resolve(actor, org, company, false, false, ct);
        var accounts = await db.Accounts.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct);
        var totals = await (
            from l in db.JournalLines.AsNoTracking()join j in db.Journals on l.JournalId equals j.Id
            where l.CompanyId == company && j.CompanyId == company && j.State == JournalState.Posted && j.PostingDate <= to
            group new
            {
                l,
                j
            }

            by l.AccountId into g
                select new
                {
                    Id = g.Key,
                    Opening = g.Sum(x => (x.j.PostingDate < @from) ? x.l.Debit - x.l.Credit : 0m),
                    Debit = g.Sum(x => (x.j.PostingDate >= @from) ? x.l.Debit : 0m),
                    Credit = g.Sum(x => (x.j.PostingDate >= @from) ? x.l.Credit : 0m)
                }

        ).ToListAsync(ct);
        var amounts = accounts.ToDictionary(x => x.Id, x => (Opening: 0m, Debit: 0m, Credit: 0m));
        foreach (var t in totals)
            amounts[t.Id] = (t.Opening, t.Debit, t.Credit);
        if (includeHierarchy)
        {
            var parents = accounts.ToDictionary(x => x.Id, x => x.ParentId);
            foreach (var t in totals)
            {
                var seen = new HashSet<Guid>
                {
                    t.Id
                };
                var parent = parents[t.Id];
                while (parent is { } id)
                {
                    if (!seen.Add(id))
                        throw new ApiException(500, "Stored account hierarchy is invalid.", "account_cycle");
                    var value = amounts[id];
                    amounts[id] = (value.Opening + t.Opening, value.Debit + t.Debit, value.Credit + t.Credit);
                    parent = parents[id];
                }
            }
        }

        var rows = accounts.Where(x => includeHierarchy || x.IsPosting).Select(a =>
        {
            var t = amounts[a.Id];
            var close = t.Opening + t.Debit - t.Credit;
            return new TrialBalanceLine(a.Id, a.Code, a.Name, a.ParentId, a.IsPosting, t.Opening, t.Debit, t.Credit, close, Math.Max(close, 0), Math.Max(-close, 0));
        }).ToList();
        var posting = rows.Where(x => x.IsPosting).ToList(); // Never double count hierarchical rollups.
        await tx.CommitAsync(ct);
        return new(c.BaseCurrency, from, to, rows, posting.Sum(x => x.DebitMovement), posting.Sum(x => x.CreditMovement), posting.Sum(x => x.ClosingDebit), posting.Sum(x => x.ClosingCredit));
    }
}
