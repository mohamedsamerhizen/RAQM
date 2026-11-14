using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.FinancialDimensions;

namespace RAQM.Api.Services.Accounting;
public sealed class JournalService(RaqmDbContext db, FinancialAccess access, IPostingEngine engine, PostingValidator validator,
    DimensionPosting? dimensions = null, DimensionBudgetService? scopedBudgets = null) : IJournalService
{
    internal async Task<JournalEntry> AddDraft(Company c, Guid actor, JournalRequest r, string source, Guid? sourceId, CancellationToken ct)
    {
        if (r is null)
            throw new ApiException(400, "Journal draft is required.", "journal_required");
        AccountingRules.Lines(r.Lines, c.BaseCurrency, false);
        await RequireAccounts(c.Id, r.Lines, ct);
        var j = new JournalEntry
        {
            TenantId = c.TenantId,
            OrganizationId = c.OrganizationId,
            CompanyId = c.Id,
            PostingDate = r.PostingDate,
            DocumentDate = r.DocumentDate,
            Description = AccountingRules.Text(r.Description, 1000, "Description"),
            Reference = r.Reference,
            Currency = c.BaseCurrency,
            MinorUnits = CurrencyPolicy.Units(c.BaseCurrency),
            SourceModule = source,
            CreatedBy = actor,
            UpdatedBy = actor
        };
        AccountingRules.OptionalText(r.Reference, 160);
        j.SourceDocumentId = sourceId ?? j.Id;
        j.Lines = Lines(j, r.Lines);
        db.Journals.Add(j);
        access.Audit(c, actor, j.Id, "JournalDraftCreated");
        return j;
    }

    private async Task RequireAccounts(Guid company, IReadOnlyList<JournalDraftLine> lines, CancellationToken ct)
    {
        var ids = lines.Select(x => x.AccountId).Distinct().ToArray();
        if (await db.Accounts.CountAsync(x => x.CompanyId == company && ids.Contains(x.Id), ct) != ids.Length)
            throw new ApiException(400, "Journal references an account outside the company.", "account_not_found");
    }

    private static List<JournalLine> Lines(JournalEntry j, IReadOnlyList<JournalDraftLine> lines) => lines.Select((l, i) => new JournalLine { TenantId = j.TenantId, OrganizationId = j.OrganizationId, CompanyId = j.CompanyId, JournalId = j.Id, Number = i + 1, AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit, Description = l.Description }).ToList();
    private async Task<JournalEntry> Find(Guid company, Guid id, CancellationToken ct) => await db.Journals.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw new ApiException(404, "Journal not found.", "journal_not_found");
    internal static PostingRequest Request(JournalEntry j, PostJournalRequest r) => new(new(j.TenantId, j.OrganizationId, j.CompanyId), j.SourceModule, j.SourceDocumentId, r.IdempotencyKey, j.PostingDate, j.Currency, j.Lines.OrderBy(x => x.Number).Select(x => new JournalDraftLine(x.AccountId, x.Debit, x.Credit, x.Description)).ToList(), j.ReversesJournalId, j.DocumentDate, j.Description, j.Reference, r.AllowBackdated, r.Reason, j.Id, r.Version);
    public async Task<JournalResponse> Create(Guid actor, Guid org, Guid company, JournalRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await access.Lock(actor, org, company, false, ct);
        var j = await AddDraft(c, actor, r, "ManualJournal", null, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return JournalResponse.From(j);
    }

    public async Task<JournalResponse> Update(Guid actor, Guid org, Guid company, Guid id, UpdateJournalRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await access.Lock(actor, org, company, false, ct);
        var j = await db.Journals.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct) ?? throw new ApiException(404, "Journal not found.", "journal_not_found");
        if (j.State != JournalState.Draft)
            throw new ApiException(409, "Posted journals are immutable; use a reversal.", "journal_immutable");
        if (j.Version != r.Version)
            throw new ApiException(409, "Journal version changed.", "version_conflict");
        if (r.Draft is null)
            throw new ApiException(400, "Draft is required.", "journal_required");
        if (j.SourceModule == "OpeningBalance" && r.Draft.PostingDate != j.PostingDate)
            throw new ApiException(409, "Opening batch date is fixed; correct balances in its draft.", "opening_date");
        AccountingRules.Lines(r.Draft.Lines, c.BaseCurrency, false);
        await RequireAccounts(company, r.Draft.Lines, ct);
        AccountingRules.OptionalText(r.Draft.Reference, 160);
        j.Description = AccountingRules.Text(r.Draft.Description, 1000, "Description");
        j.Reference = r.Draft.Reference;
        j.PostingDate = r.Draft.PostingDate;
        j.DocumentDate = r.Draft.DocumentDate;
        j.Currency = c.BaseCurrency;
        j.MinorUnits = CurrencyPolicy.Units(c.BaseCurrency);
        j.Version = Guid.NewGuid();
        j.UpdatedAt = DateTimeOffset.UtcNow;
        j.UpdatedBy = actor;
        await db.JournalLines.Where(x => x.CompanyId == company && x.JournalId == id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct); // Persist the draft's currency/scale before line guards inspect new amounts.
        j.Lines = Lines(j, r.Draft.Lines);
        db.JournalLines.AddRange(j.Lines);
        access.Audit(c, actor, id, "JournalDraftUpdated");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return JournalResponse.From(j);
    }

    public async Task Delete(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await access.Lock(actor, org, company, false, ct);
        var j = await Find(company, id, ct);
        if (j.State != JournalState.Draft)
            throw new ApiException(409, "Posted journals cannot be deleted.", "journal_immutable");
        if (j.SourceModule == "OpeningBalance")
            throw new ApiException(409, "Opening source evidence is retained; edit its draft instead.", "opening_source");
        await db.JournalLines.Where(x => x.CompanyId == company && x.JournalId == id).ExecuteDeleteAsync(ct);
        await db.Journals.Where(x => x.CompanyId == company && x.Id == id).ExecuteDeleteAsync(ct);
        access.Audit(c, actor, id, "JournalDraftDeleted");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<JournalResponse> Get(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return JournalResponse.From(await Find(company, id, ct));
    }

    public async Task<PageResult<JournalResponse>> Search(Guid actor, Guid org, Guid company, DateOnly? from, DateOnly? to, JournalState? state, string? search, int page, int pageSize, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        AccountingRules.Page(page, pageSize);
        AccountingRules.OptionalText(search, 100);
        if (from > to || state.HasValue && !Enum.IsDefined(state.Value))
            throw new ApiException(400, "Invalid journal filters.", "journal_filter");
        var q = db.Journals.AsNoTracking().Where(x => x.CompanyId == company);
        if (from.HasValue)
            q = q.Where(x => x.PostingDate >= from);
        if (to.HasValue)
            q = q.Where(x => x.PostingDate <= to);
        if (state.HasValue)
            q = q.Where(x => x.State == state);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(x => EF.Functions.ILike(x.Description, "%" + search + "%") || (x.Reference != null && EF.Functions.ILike(x.Reference, "%" + search + "%")) || (x.Number != null && x.Number == search));
        var count = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.PostingDate).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).Include(x => x.Lines).ToListAsync(ct);
        return new(rows.Select(JournalResponse.From).ToList(), count, page, pageSize);
    }

    public async Task<JournalValidationResponse> Validate(Guid actor, Guid org, Guid company, Guid id, PostJournalRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await access.Lock(actor, org, company, false, ct);
        var j = await Find(company, id, ct);
        if (j.State != JournalState.Draft || j.Version != r.Version)
            throw new ApiException(409, "Draft state or version changed.", "version_conflict");
        await validator.Validate(actor, c, Request(j, r), ct);
        if (dimensions is not null)
        {
            var tags = await dimensions.Prepare(actor, c, Request(j, r), ct);
            if (scopedBudgets is not null) await scopedBudgets.CheckPosting(company, Request(j, r), tags, ct);
        }
        var total = AccountingRules.Lines(Request(j, r).Lines, j.Currency, true);
        await tx.CommitAsync(ct);
        return new(id, total.Debit, total.Credit, true);
    }

    public async Task<PostingReceipt> Post(Guid actor, Guid org, Guid company, Guid id, PostJournalRequest r, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, true, false, ct);
        var j = await Find(company, id, ct);
        return await engine.PostAsync(actor, Request(j, r), ct);
    }

    public async Task<PostingReceipt> Reverse(Guid actor, Guid org, Guid company, Guid id, ReverseJournalRequest r, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, true, false, ct);
        var j = await Find(company, id, ct);
        if (j.State != JournalState.Posted)
            throw new ApiException(409, "Only posted journals can be reversed.", "reversal_original");
        return await engine.PostAsync(actor, ReversalRequest(j, r), ct);
    }

    internal static PostingRequest ReversalRequest(JournalEntry original, ReverseJournalRequest request)
    {
        var reason = AccountingRules.Text(request.Reason, 500, "Reversal reason");
        return new(new(original.TenantId, original.OrganizationId, original.CompanyId), "Reversal", original.Id,
            request.IdempotencyKey, request.PostingDate, original.Currency,
            original.Lines.OrderBy(x => x.Number).Select(x => new JournalDraftLine(x.AccountId, x.Credit, x.Debit, x.Description)).ToList(),
            original.Id, request.PostingDate, $"Reversal of {original.Number}: {reason}", original.Number, request.AllowBackdated, reason);
    }

    public async Task<IReadOnlyList<AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid entity, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.AccountingAudit.AsNoTracking().Where(x => x.CompanyId == company && x.EntityId == entity).OrderBy(x => x.At).ThenBy(x => x.Id).Select(x => new AuditResponse(x.Id, x.EntityId, x.Action, x.ActorId, x.At, x.Reason)).ToListAsync(ct);
    }
}
