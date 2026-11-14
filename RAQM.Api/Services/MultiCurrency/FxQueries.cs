using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.MultiCurrency;
using RAQM.Api.Entities.MultiCurrency;
using static RAQM.Api.Helpers.MultiCurrency.FxMath;

namespace RAQM.Api.Services.MultiCurrency;

public sealed class FxQueries(RaqmDbContext db)
{
    public async Task<FxConfiguration> Configuration(Guid company, CancellationToken ct) =>
        await db.FxConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company, ct)
            ?? throw Conflict("Configure company FX policy first.");
    public async Task<FxCurrency> Currency(Guid company, Guid id, CancellationToken ct) =>
        await db.FxCurrencies.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw Missing("Enabled company currency was not found.");
    public async Task<FxPosition> Position(Guid company, Guid id, CancellationToken ct) =>
        await db.FxPositions.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw Missing("Foreign monetary position or bank was not found.");
    public async Task<FxOperation> Posted(Guid company, Guid id, CancellationToken ct) =>
        await db.FxOperations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id && x.State == FxState.Posted, ct)
            ?? throw Missing("Posted FX source was not found.");

    public Task<Dictionary<FxAccountRole, Guid>> Accounts(Guid company, CancellationToken ct) =>
        db.FxAccounts.AsNoTracking().Where(x => x.CompanyId == company).ToDictionaryAsync(x => x.Role, x => x.AccountId, ct);

    public Task<List<FxBalance>> Balances(Guid company, DateOnly at, CancellationToken ct) =>
        db.FxEffects.AsNoTracking().Where(x => x.CompanyId == company && x.Date <= at).GroupBy(x => x.PositionId)
            .Select(x => new FxBalance(x.Key, x.Sum(y => y.ForeignDelta), x.Sum(y => y.BaseDelta))).ToListAsync(ct);

    public async Task<FxBalance> Balance(Guid company, Guid position, DateOnly at, CancellationToken ct) =>
        await db.FxEffects.AsNoTracking().Where(x => x.CompanyId == company && x.PositionId == position && x.Date <= at)
            .GroupBy(x => x.PositionId).Select(x => new FxBalance(x.Key, x.Sum(y => y.ForeignDelta), x.Sum(y => y.BaseDelta)))
            .SingleOrDefaultAsync(ct) ?? new(position, 0, 0);

    public Task<decimal> CustomerExposure(Guid company, Guid party, CancellationToken ct) =>
        (from movement in db.FxEffects.AsNoTracking() join position in db.FxPositions.AsNoTracking() on movement.PositionId equals position.Id
            where movement.CompanyId == company && position.CompanyId == company && position.PartyId == party &&
                position.Kind == FxPositionKind.Receivable select movement.BaseDelta).SumAsync(ct);

    internal static FxRequest Input(FxOperation operation) => JsonSerializer.Deserialize<FxRequest>(operation.InputJson)!;
    internal static FxPlan Plan(FxOperation operation) => JsonSerializer.Deserialize<FxPlan>(operation.PlanJson!)!;
}
