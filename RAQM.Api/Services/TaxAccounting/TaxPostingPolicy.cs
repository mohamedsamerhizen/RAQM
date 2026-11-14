using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.TaxAccounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.TaxAccounting;
using RAQM.Api.Entities.Trading;
using static RAQM.Api.Services.TaxJurisdictions.TaxConfigurationService;

namespace RAQM.Api.Services.TaxAccounting;

public sealed class TaxPostingPolicy(RaqmDbContext db)
{
    public async Task<bool> Validate(Guid actor, Company company, PostingRequest request, CancellationToken ct)
    {
        var reversalId = request.ReversesJournalId;
        var reversalBasis = reversalId.HasValue && await db.Set<TaxOperation>().AnyAsync(x => x.CompanyId == company.Id &&
            x.JournalId == reversalId && x.State == TaxEvidenceState.Posted, ct);
        if (request.SourceModule == TaxOperationService.Source || reversalBasis)
        {
            var staged = db.Set<TaxOperation>().AsNoTracking().Where(x => x.CompanyId == company.Id && x.State == TaxEvidenceState.Posting &&
                x.Date == request.AccountingDate && x.PostingActor == actor);
            if (reversalBasis)
                staged = from correction in staged join original in db.Set<TaxOperation>() on correction.BasisId equals (Guid?)original.Id
                    where correction.Kind == TaxOperationKind.Correction && original.JournalId == reversalId select correction;
            else staged = staged.Where(x => x.Id == request.SourceDocumentId && x.Kind != TaxOperationKind.Correction);
            var row = await staged.SingleOrDefaultAsync(ct);
            var plan = row?.PlanJson is { } json ? JsonSerializer.Deserialize<TaxPostingPlan>(json) : null;
            if (row is null || plan is null || request.IdempotencyKey != TaxOperationService.Key(row.Id) ||
                request.Reference != row.Reference || reversalId != plan.ReversesJournalId || !plan.Lines.SequenceEqual(request.Lines))
                throw Conflict("Use the matching independently approved tax operation.");
            return true;
        }

        var ids = request.Lines.Select(x => x.AccountId).ToArray();
        var controls = await db.Set<TaxControl>().AsNoTracking().Where(x => x.CompanyId == company.Id && ids.Contains(x.AccountId)).ToListAsync(ct);
        if (controls.Count == 0) return false;
        if (request.SourceModule == RAQM.Api.Helpers.FinancialReporting.FinancialMath.YearEndSource &&
            controls.All(x => x.Role is TaxControlRole.Nonrecoverable or TaxControlRole.CurrentExpense or TaxControlRole.DeferredExpense))
            return false;
        if (controls.All(x => x.Role is TaxControlRole.Output or TaxControlRole.Input) &&
            request.SourceModule is "AR.Invoice" or "AP.Bill" or "AR.Credit" or "AP.Credit" or "FX.Invoice" or "FX.Bill" or "FX.Credit")
            return false;
        if (controls.Count == 1 && controls[0].Role == TaxControlRole.Clearing)
        {
            var control = controls[0];
            if (request.SourceModule == "Settlement.Payment")
            {
                var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company.Id &&
                    x.Id == request.SourceDocumentId && x.State == SettlementState.Prepared && x.PartyKind == PartyKind.Supplier &&
                    !x.IsRefund && x.CashAccountId == control.AccountId, ct);
                var stage = await db.Set<TaxOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company.Id &&
                    x.RegistrationId == control.RegistrationId && x.Kind == TaxOperationKind.Withhold &&
                    x.State == TaxEvidenceState.Posting && x.PostingActor == actor && x.Date == request.AccountingDate, ct);
                var plan = stage?.PlanJson is { } json ? JsonSerializer.Deserialize<TaxPostingPlan>(json) : null;
                if (stage is not null && plan is not null && payment is not null && payment.RequestKey == TaxOperationService.PaymentKey(stage.Id) &&
                    payment.ControlAmount == plan.TaxableBase && payment.AdvanceAmount == 0) return false;
            }
            if (request.SourceModule == "Reversal" && await db.Payments.Where(x => x.CompanyId == company.Id && x.JournalId == reversalId)
                    .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct) is { } id)
            {
                await GuardPayment(db, company.Id, id, actor, null, ct);
                return false;
            }
        }
        throw Conflict("Protected tax controls require their coordinated source workflow.");
    }

    internal static async Task GuardPayment(RaqmDbContext db, Guid company, Guid payment, Guid actor, string? key, CancellationToken ct)
    {
        var original = await db.Set<TaxOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.PaymentId == payment, ct);
        if (original is null) return;
        var stage = await db.Set<TaxOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.BasisId == original.Id &&
            x.Kind == TaxOperationKind.Correction && x.State == TaxEvidenceState.Posting && x.PostingActor == actor, ct);
        if (stage is null || key is not null && key != TaxOperationService.ReversePaymentKey(stage.Id))
            throw Conflict("Reverse withholding settlements through a coordinated tax correction.");
    }
}
