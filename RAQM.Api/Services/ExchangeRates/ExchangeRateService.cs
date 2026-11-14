using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.Entities.MultiCurrency;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.MultiCurrency;
using static RAQM.Api.Helpers.MultiCurrency.FxMath;
using static RAQM.Api.Services.MultiCurrency.FxSetup;

namespace RAQM.Api.Services.ExchangeRates;

public sealed class ExchangeRateService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions, FxQueries queries, TimeProvider clock)
{
    public Task<FxRate> Prepare(Guid actor, Guid org, Guid company, FxRateRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await queries.Currency(company, input.CurrencyId, token);
            if (!Enum.IsDefined(input.Type) || input.Date.Year < 1900 || input.Date > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
                throw Bad("Choose a supported rate type and a rate date no later than today.");
            Rate(input.ExchangeRate);
            var peers = db.FxRates.AsNoTracking().Where(x => x.CompanyId == company && x.CurrencyId == input.CurrencyId &&
                x.Type == input.Type && x.Date == input.Date);
            if (await peers.AnyAsync(x => x.State == FxRateState.Draft, token)) throw Conflict("This pair, type and date already has a draft rate.");
            var current = await peers.SingleOrDefaultAsync(x => x.State == FxRateState.Approved, token);
            if (current?.Id != input.ReplacesId) throw Conflict("An existing approved rate requires an explicit supersession reference.");
            var row = Scoped(scope, new FxRate { CurrencyId = input.CurrencyId, Type = input.Type, Date = input.Date,
                ExchangeRate = input.ExchangeRate, ReplacesId = input.ReplacesId, CreatedBy = actor,
                Revision = 1 + (await peers.MaxAsync(x => (int?)x.Revision, token) ?? 0),
                Source = AccountingRules.Text(input.Source, 160, "Rate source"),
                Evidence = AccountingRules.Text(input.Evidence, 1000, "Reliability evidence") });
            db.FxRates.Add(row);
            access.Audit(scope, actor, row.Id, "FxRatePrepared", row.Source);
            return row;
        }, ct);

    public Task<FxRate> Approve(Guid actor, Guid org, Guid company, Guid id, FxAction input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var row = await db.FxRates.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token) ?? throw Missing("Exchange rate not found.");
            if (row.Version != input.Version || row.State != FxRateState.Draft || row.CreatedBy == actor)
                throw Conflict("A current draft and an independent Owner are required.");
            var reason = AccountingRules.Text(input.Reason, 500, "Rate decision");
            var active = await db.FxRates.SingleOrDefaultAsync(x => x.CompanyId == company && x.CurrencyId == row.CurrencyId &&
                x.Type == row.Type && x.Date == row.Date && x.State == FxRateState.Approved, token);
            if (active?.Id != row.ReplacesId) throw Conflict("The predecessor rate is no longer current.");
            if (row.Type == FxRateType.Closing && await db.Periods.AnyAsync(x => x.CompanyId == company &&
                x.EndDate == row.Date && x.State != RAQM.Api.Entities.Accounting.PeriodState.Open, token))
                throw Conflict("A closed accounting period cannot receive a revised closing rate.");
            if (active is not null)
            {
                if (row.Type == FxRateType.Closing)
                {
                    var batches = await db.FxOperations.AsNoTracking().Where(x => x.CompanyId == company &&
                        x.Kind == FxKind.Revaluation && x.State == FxState.Posted && x.Date == row.Date).ToListAsync(token);
                    var corrected = await db.FxOperations.AsNoTracking().Where(x => x.CompanyId == company &&
                        x.Kind == FxKind.RevaluationCorrection && x.State == FxState.Posted && x.Date == row.Date)
                        .Select(x => x.OriginalId).ToListAsync(token);
                    if (batches.Any(x => !corrected.Contains(x.Id) &&
                        FxQueries.Plan(x).Valuations?.Any(v => v.RateId == active.Id) == true))
                        throw Conflict("Correct the dependent posted revaluation before superseding its closing rate.");
                }
                active.State = FxRateState.Superseded;
                active.Version = Guid.NewGuid();
                await db.SaveChangesAsync(token);
            }
            row.State = FxRateState.Approved;
            row.ApprovedBy = actor;
            row.Version = Guid.NewGuid();
            access.Audit(scope, actor, row.Id, "FxRateApproved", reason);
            return row;
        }, ct);

    internal async Task<FxRate> Select(Guid company, Guid currency, DateOnly date, FxRateType type, int fallback, CancellationToken ct)
    {
        var earliest = type == FxRateType.Closing ? date : date.AddDays(-fallback);
        return await db.FxRates.AsNoTracking().Where(x => x.CompanyId == company && x.CurrencyId == currency && x.Type == type &&
            x.State == FxRateState.Approved && x.Date >= earliest && x.Date <= date).OrderByDescending(x => x.Date).FirstOrDefaultAsync(ct)
            ?? throw Conflict($"No eligible approved {type} rate for currency {currency} at {date:yyyy-MM-dd}.");
    }

    public Task<FxRate> Cancel(Guid actor, Guid org, Guid company, Guid id, FxAction input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var row = await db.FxRates.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token)
                ?? throw Missing("Exchange rate not found.");
            if (row.Version != input.Version || row.State != FxRateState.Draft)
                throw Conflict("Only a current draft rate can be cancelled.");
            row.State = FxRateState.Cancelled;
            row.Version = Guid.NewGuid();
            access.Audit(scope, actor, row.Id, "FxRateCancelled", AccountingRules.Text(input.Reason, 500, "Cancellation reason"));
            return row;
        }, ct);

    public async Task<PageResult<FxRate>> List(Guid actor, Guid org, Guid company, Guid? currency, FxRateType? type,
        DateOnly? date, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        await access.Resolve(actor, org, company, false, false, ct);
        var query = db.FxRates.AsNoTracking().Where(x => x.CompanyId == company);
        if (currency.HasValue) query = query.Where(x => x.CurrencyId == currency);
        if (type.HasValue) query = query.Where(x => x.Type == type);
        if (date.HasValue) query = query.Where(x => x.Date == date);
        return new(await query.OrderByDescending(x => x.Date).ThenBy(x => x.CurrencyId).ThenByDescending(x => x.Revision)
            .ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct), await query.CountAsync(ct), page, size);
    }

    public async Task<FxRate> Get(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.FxRates.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw Missing("Exchange rate not found.");
    }
}
