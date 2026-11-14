using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Services.MultiCurrency;

namespace RAQM.Api.Services.ForeignExchange;

public sealed class FxPostingPolicy(RaqmDbContext db)
{
    public async Task Validate(Guid actor, Guid company, PostingRequest request, CancellationToken ct)
    {
        if (request.SourceModule.StartsWith("FX.", StringComparison.Ordinal))
        {
            var operation = await db.FxOperations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company &&
                x.Id == request.SourceDocumentId && x.State == FxState.Approved && x.PostedBy == actor, ct);
            if (operation is null || operation.Date != request.AccountingDate || operation.PlanJson is null ||
                request.SourceModule != FxWorkflow.Source(operation.Kind) || request.IdempotencyKey != FxWorkflow.Key(operation.Id) ||
                !FxQueries.Plan(operation).Lines.SequenceEqual(request.Lines))
                throw Denied();
            return;
        }
        if (request.ReversesJournalId is { } original &&
            await db.FxOperations.AnyAsync(x => x.CompanyId == company && x.JournalId == original, ct))
            throw Denied();
        var ids = request.Lines.Select(x => x.AccountId).Distinct().ToArray();
        if (await db.FxPositions.AnyAsync(x => x.CompanyId == company && x.Kind == FxPositionKind.Bank && ids.Contains(x.AccountId), ct) ||
            await db.FxAccounts.AnyAsync(x => x.CompanyId == company && ids.Contains(x.AccountId) &&
                (request.SourceModule != FinancialMath.YearEndSource || (int)x.Role < 4 || (int)x.Role > 7), ct))
            throw Denied();
    }

    private static ApiException Denied() => new(403, "FX controls require an approved coordinated FX source and corrections.", "fx_source");
}
