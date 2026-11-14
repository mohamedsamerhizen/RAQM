using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;

namespace RAQM.Api.Services.Accounting;
public sealed class OpeningBalanceService(RaqmDbContext db, FinancialAccess access, JournalService journals) : IOpeningBalanceService
{
    public async Task<OpeningBatchResponse> Create(Guid actor, Guid org, Guid company, OpeningBalanceRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await access.Lock(actor, org, company, false, ct);
        AccountingRules.Lines(r.Balances, c.BaseCurrency, true);
        if (await db.OpeningBatches.AnyAsync(x => x.CompanyId == company, ct))
            throw new ApiException(409, "This company already has its initial opening batch; edit or post its draft.", "opening_duplicate");
        var start = await db.FiscalYears.Where(x => x.CompanyId == company).MinAsync(x => (DateOnly? )x.StartDate, ct);
        if (start != r.PostingDate)
            throw new ApiException(400, "Opening balances must use the first configured fiscal start date.", "opening_date");
        var batch = new OpeningBalanceBatch
        {
            TenantId = c.TenantId,
            OrganizationId = org,
            CompanyId = company,
            BatchKey = AccountingRules.Text(r.BatchKey, 128, "Batch key"),
            Name = AccountingRules.Text(r.Name, 160, "Name"),
            CreatedBy = actor
        };
        var journal = await journals.AddDraft(c, actor, new(r.PostingDate, r.PostingDate, batch.Name, batch.BatchKey, r.Balances), "OpeningBalance", batch.Id, ct);
        batch.JournalId = journal.Id;
        db.OpeningBatches.Add(batch);
        access.Audit(c, actor, batch.Id, "OpeningBatchCreated");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(batch.Id, batch.BatchKey, batch.Name, batch.JournalId, journal.State);
    }

    public async Task<OpeningBatchResponse?> Get(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await (
            from b in db.OpeningBatches.AsNoTracking()join j in db.Journals on b.JournalId equals j.Id
            where b.CompanyId == company && j.CompanyId == company
            select new OpeningBatchResponse(b.Id, b.BatchKey, b.Name, b.JournalId, j.State)).SingleOrDefaultAsync(ct);
    }

    public async Task<PostingReceipt> Post(Guid actor, Guid org, Guid company, Guid id, PostJournalRequest r, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, true, false, ct);
        var batch = await db.OpeningBatches.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw new ApiException(404, "Opening batch not found.", "opening_not_found");
        return await journals.Post(actor, org, company, batch.JournalId, r, ct);
    }
}
