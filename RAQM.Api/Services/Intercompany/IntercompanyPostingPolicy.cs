using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.Intercompany;
using RAQM.Api.Entities.Organizations;
using static RAQM.Api.Services.CorporateGroups.CorporateGroupService;

namespace RAQM.Api.Services.Intercompany;

public sealed class IntercompanyPostingPolicy(RaqmDbContext db)
{
    public async Task Validate(Guid actor, Company company, PostingRequest request, CancellationToken ct)
    {
        var original = request.ReversesJournalId is { } reversal && await db.Journals.AnyAsync(x => x.Id == reversal &&
            x.CompanyId == company.Id && x.SourceModule == IntercompanyService.Source, ct);
        if (request.SourceModule == IntercompanyService.Source || original)
        {
            var matches = db.Set<IntercompanyPair>().AsNoTracking().Where(x => x.State == PairState.Posting &&
                (x.CreditorId == company.Id || x.DebtorId == company.Id) && x.Date == request.AccountingDate);
            if (original)
                matches = from correction in matches join basis in db.Set<IntercompanyPair>() on correction.BasisId equals (Guid?)basis.Id
                    where correction.Kind == PairKind.Correction &&
                        (basis.CreditorJournalId == request.ReversesJournalId || basis.DebtorJournalId == request.ReversesJournalId)
                    select correction;
            else matches = matches.Where(x => x.Id == request.SourceDocumentId && x.Kind != PairKind.Correction);
            var pair = await matches.SingleOrDefaultAsync(ct);
            if (pair?.PlanJson is null || request.IdempotencyKey != IntercompanyService.Key(pair.Id))
                throw Conflict("Use the approved reciprocal posting workflow.");
            var plan = JsonSerializer.Deserialize<PairPlan>(pair.PlanJson)!;
            var left = company.Id == pair.CreditorId;
            if (plan.Actor != actor || pair.Currency != request.Currency || pair.Reference != request.Reference ||
                (left ? plan.CreditorReversal : plan.DebtorReversal) != request.ReversesJournalId ||
                !(left ? plan.Creditor : plan.Debtor).SequenceEqual(request.Lines))
                throw Conflict("The journal differs from its staged reciprocal source.");
            return;
        }

        var accounts = request.Lines.Select(x => x.AccountId).ToArray();
        if (await db.Set<IntercompanyPolicy>().AnyAsync(x => x.ApprovedBy != null &&
                (x.CreditorId == company.Id && accounts.Contains(x.DueFromId) || x.DebtorId == company.Id && accounts.Contains(x.DueToId)), ct))
            throw Conflict("Dedicated reciprocal accounts require an approved intercompany source.");
    }
}
