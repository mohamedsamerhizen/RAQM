using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.FinancialDimensions;

namespace RAQM.Api.Services.Accounting;
public sealed class PostingEngine(RaqmDbContext db, FinancialAccess access, PostingValidator validator, TimeProvider clock,
    FinancialTransaction? coordinator = null, DimensionPosting? dimensions = null, DimensionBudgetService? scopedBudgets = null, GovernanceGate? governance = null) : IPostingEngine
{
    private readonly FinancialTransaction transactions = coordinator ?? new FinancialTransaction(db, access);
    public Task<PostingReceipt> PostAsync(Guid actor, PostingRequest request, CancellationToken ct)
    {
        if(request is null || request.Scope is null)throw new ApiException(400,"A posting scope and request are required.","posting_scope");
        return transactions.Execute(actor,request.Scope.OrganizationId,request.Scope.CompanyId,
            (_,token)=>PostCore(actor,request,token),ct);
    }
    private async Task<PostingReceipt> PostCore(Guid actor, PostingRequest request, CancellationToken ct)
    {
        if (request is null || request.Scope is null)
            throw new ApiException(400, "A posting scope and request are required.", "posting_scope");
        var r = request with
        {
            SourceModule = AccountingRules.Text(request.SourceModule, 50, "Source module"),
            IdempotencyKey = AccountingRules.Text(request.IdempotencyKey, 128, "Idempotency key"),
            Description = AccountingRules.Text(request.Description, 1000, "Description")
        };
        AccountingRules.OptionalText(r.Reference, 160);
        AccountingRules.OptionalText(r.Reason, 500);
        if (r.SourceDocumentId == Guid.Empty)
            throw new ApiException(400, "Source document ID is required.", "source_required");
        AccountingRules.Lines(r.Lines, r.Currency, true);

        var c = await access.Lock(actor, r.Scope.OrganizationId, r.Scope.CompanyId, false, ct);
        if (c.TenantId != r.Scope.TenantId)
            throw new ApiException(404, "Company not found.", "company_not_found");
        var hash = AccountingRules.Fingerprint(r);
        var prior = await db.Journals.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == c.Id && x.State == JournalState.Posted && x.IdempotencyKey == r.IdempotencyKey, ct);
        if (prior is not null)
        {
            if (prior.PayloadHash != hash)
                throw new ApiException(409, "Idempotency key was used for a different financial payload.", "idempotency_conflict");

            return Receipt(prior);
        }

        if (await db.Journals.AnyAsync(x => x.CompanyId == c.Id && x.State == JournalState.Posted && x.SourceModule == r.SourceModule && x.SourceDocumentId == r.SourceDocumentId, ct))
            throw new ApiException(409, "The source document already has a posted journal.", "source_already_posted");
        var period = await validator.Validate(actor, c, r, ct, commitBudgetEvidence: true);
        var attribution = dimensions is null ? null : await dimensions.Prepare(actor, c, r, ct);
        if (governance is not null && r.SourceModule == "ManualJournal" && r.DraftId is { } manual)
            await governance.Enforce(c, actor, FinancialDuty.ManualJournal, manual, ct, attribution);
        if (scopedBudgets is not null && attribution is not null) await scopedBudgets.CheckPosting(c.Id, r, attribution, ct);
        JournalEntry journal;
        if (r.DraftId is { } draft)
        {
            journal = await db.Journals.Include(x => x.Lines).SingleOrDefaultAsync(x => x.CompanyId == c.Id && x.Id == draft, ct) ?? throw new ApiException(404, "Journal draft not found.", "journal_not_found");
            if (journal.State != JournalState.Draft || journal.Version != r.ExpectedVersion)
                throw new ApiException(409, "Draft state or version changed.", "version_conflict");
            if (journal.SourceModule != r.SourceModule || journal.SourceDocumentId != r.SourceDocumentId || journal.PostingDate != r.AccountingDate || journal.DocumentDate != r.DocumentDate || journal.Currency != r.Currency || journal.Description != r.Description || journal.Reference != r.Reference || journal.ReversesJournalId != r.ReversesJournalId || !journal.Lines.OrderBy(x => x.Number).Select(x => new JournalDraftLine(x.AccountId, x.Debit, x.Credit, x.Description)).SequenceEqual(r.Lines))
                throw new ApiException(409, "Posting request differs from the current draft.", "draft_changed");
        }
        else
        {
            if (r.SourceModule is "ManualJournal" or "OpeningBalance")
                throw new ApiException(400, "This source must post its persisted draft.", "draft_required");
            journal = new JournalEntry
            {
                TenantId = c.TenantId,
                OrganizationId = c.OrganizationId,
                CompanyId = c.Id,
                PostingDate = r.AccountingDate,
                DocumentDate = r.DocumentDate,
                Currency = r.Currency,
                MinorUnits = CurrencyPolicy.Units(r.Currency),
                Description = r.Description,
                Reference = r.Reference,
                SourceModule = r.SourceModule,
                SourceDocumentId = r.SourceDocumentId,
                ReversesJournalId = r.ReversesJournalId,
                CreatedBy = actor,
                UpdatedBy = actor
            };
            journal.Lines = r.Lines.Select((l, i) => new JournalLine { TenantId = c.TenantId, OrganizationId = c.OrganizationId, CompanyId = c.Id, JournalId = journal.Id, Number = i + 1, AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit, Description = l.Description }).ToList();
            db.Journals.Add(journal);
            await db.SaveChangesAsync(ct); // Draft/lines exist before the database validates the posted transition.
        }

        if (governance is not null && r.ReversesJournalId.HasValue)
            await governance.EnforceReversal(c, actor, r, journal.Id, ct);
        if (attribution is { Count: > 0 })
        {
            dimensions!.Persist(c, journal, attribution);
            await db.SaveChangesAsync(ct);
        }
        var sequence = (await db.Journals.Where(x => x.CompanyId == c.Id && x.State == JournalState.Posted).MaxAsync(x => x.Sequence, ct) ?? 0) + 1;
        journal.State = JournalState.Posted;
        journal.PeriodId = period.Id;
        journal.Sequence = sequence;
        journal.Number = $"GL-{sequence:D8}";
        journal.IdempotencyKey = r.IdempotencyKey;
        journal.PayloadHash = hash;
        journal.PostedBy = actor;
        journal.PostedAt = AccountingRules.StoredUtcInstant(clock.GetUtcNow());
        journal.PostingReason = r.Reason;
        journal.UpdatedAt = journal.PostedAt.Value;
        journal.UpdatedBy = actor;
        journal.Version = Guid.NewGuid();
        access.Audit(c, actor, journal.Id, r.ReversesJournalId.HasValue ? "JournalReversalPosted" : "JournalPosted", r.Reason);
        if (r.ReversesJournalId is { } original)
            access.Audit(c, actor, original, "JournalReversed", r.Reason);
        await db.SaveChangesAsync(ct);

        return Receipt(journal);
    }

    private static PostingReceipt Receipt(JournalEntry j) => new(j.Id, j.Number!, j.PostedAt!.Value);
}

