using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.FixedAssets;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.FixedAssets;
using RAQM.Api.Services.FinancialDimensions;

namespace RAQM.Api.Services.FixedAssets;

public sealed class AssetPostingPolicy(RaqmDbContext db)
{
    public async Task Validate(Guid actor, Guid company, PostingRequest request, CancellationToken ct)
    {
        if (request.SourceModule.StartsWith("FixedAssets.", StringComparison.Ordinal))
        {
            var operation = await db.AssetOperations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.Id == request.SourceDocumentId, ct);
            if (operation is null || operation.State != AssetOperationState.Approved || operation.PostedBy != actor ||
                operation.Date != request.AccountingDate || request.SourceModule != AssetMath.Source(operation.Kind) ||
                request.IdempotencyKey != AssetWorkflow.PostingKey(operation.Id) ||
                operation.PlanJson is null || !AssetSnapshot.Plan(operation).Lines.SequenceEqual(request.Lines))
                throw new ApiException(403, "Fixed asset postings require an approved and staged source.", "asset_source");
            return;
        }
        if (request.ReversesJournalId is { } original &&
            (await db.AssetOperations.AnyAsync(x => x.CompanyId == company && (x.JournalId == original || x.OpeningJournalId == original) &&
                x.State == AssetOperationState.Posted, ct) ||
             await Claimed(company, original, ct)))
            throw new ApiException(409, "The journal is linked to retained asset evidence. Use an eligible asset correction.", "asset_source");
        if (request.SourceModule == "AP.Credit")
        {
            var bill = await (from credit in db.TradeDocuments join source in db.TradeDocuments on credit.OriginalDocumentId equals (Guid?)source.Id
                where credit.CompanyId == company && source.CompanyId == company && credit.Id == request.SourceDocumentId
                select source).AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(ct);
            var originalIds = await db.TradeLines.Where(x => x.CompanyId == company && x.DocumentId == request.SourceDocumentId)
                .Select(x => x.OriginalLineId).ToListAsync(ct);
            var ordinals = bill is null ? [] : DimensionBindings.CommercialOrdinals(bill.Lines)
                .Where(x => bill.Lines.Any(l => l.Number == x.Key && originalIds.Contains(l.Id)))
                .Select(x => x.Value).ToArray();
            if (bill?.JournalId is { } billJournal && await
                (from claim in db.AssetSourceClaims join line in db.JournalLines on claim.JournalLineId equals line.Id
                 where claim.CompanyId == company && line.CompanyId == company && line.JournalId == billJournal &&
                     ordinals.Contains(line.Number)
                 select claim.Id).AnyAsync(ct))
                throw new ApiException(409, "A fixed asset operation already consumed bill evidence. Resolve the asset and supplier correction together.", "asset_bill_claimed");
        }
        var ids = request.Lines.Select(x => x.AccountId).Distinct().ToArray();
        var controls = await db.AssetControls.AsNoTracking().Where(x => x.CompanyId == company && ids.Contains(x.AccountId)).ToListAsync(ct);
        if (controls.Count == 0) return;
        if (request.SourceModule == "OpeningBalance") return;
        if (request.SourceModule is "AP.Bill" or "AP.Credit" &&
            controls.All(x => x.Role is AssetControlRole.Cost or AssetControlRole.Clearing)) return;
        throw new ApiException(403, "Asset control accounts require an approved asset, opening or eligible bill source.", "asset_control");
    }

    private Task<bool> Claimed(Guid company, Guid journal, CancellationToken ct) =>
        (from claim in db.AssetSourceClaims join line in db.JournalLines on claim.JournalLineId equals line.Id
         where claim.CompanyId == company && line.CompanyId == company && line.JournalId == journal select claim.Id).AnyAsync(ct);
}
