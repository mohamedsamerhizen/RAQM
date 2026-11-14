using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.CorporateGroups;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Consolidation;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.CorporateGroups;
using static RAQM.Api.Services.CorporateGroups.CorporateGroupService;

namespace RAQM.Api.Services.CurrencyTranslation;

public sealed class GroupRateService(RaqmDbContext db, CorporateGroupService groups, FinancialAccess access)
{
    public async Task<IReadOnlyList<GroupTranslationRate>> List(Guid actor, Guid org, Guid group, Guid member,
        int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        await groups.Resolve(actor, org, group, ct);
        return await db.Set<GroupTranslationRate>().AsNoTracking().Where(x => x.GroupId == group && x.MemberId == member)
            .OrderByDescending(x => x.RateDate).ThenByDescending(x => x.EffectiveOn).ThenBy(x => x.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
    }

    public Task<GroupTranslationRate> Prepare(Guid actor, Guid org, Guid group, TranslationRateRequest input, CancellationToken ct) =>
        groups.Change(actor, org, group, [input.MemberId], async (scope, parent, token) =>
        {
            await groups.RequireMember(scope, input.MemberId, input.RateDate, token);
            var member = await access.Resolve(actor, org, input.MemberId, false, false, token);
            if (member.BaseCurrency == scope.Currency || input.Rate <= 0 || input.Rate >= 1_000_000_000_000m ||
                decimal.Round(input.Rate, 12) != input.Rate || input.EffectiveOn < input.RateDate)
                throw Invalid("Use a positive foreign translation rate effective on or after its observation date.");
            if (input.EquityAccountId is { } equity && !await db.Accounts.AnyAsync(x => x.CompanyId == member.Id && x.Id == equity &&
                    x.Classification == AccountClass.Equity, token)) throw Invalid("Historical rate requires a scoped equity account.");
            if (await db.Set<GroupTranslationRate>().AnyAsync(x => x.GroupId == group && x.MemberId == member.Id &&
                x.EquityAccountId == input.EquityAccountId && x.RateDate == input.RateDate && x.EffectiveOn == input.EffectiveOn, token))
                throw Conflict("A rate already exists for the observation and version date.");
            var rate = InGroup(scope, new GroupTranslationRate { MemberId = member.Id, EquityAccountId = input.EquityAccountId,
                RateDate = input.RateDate, EffectiveOn = input.EffectiveOn, Rate = input.Rate, PreparedBy = actor,
                Evidence = AccountingRules.Text(input.Evidence, 1000, "Translation evidence") });
            db.Add(rate);
            access.Audit(parent, actor, rate.Id, "GroupTranslationRatePrepared", rate.Evidence);
            return rate;
        }, ct);

    public Task<GroupTranslationRate> Approve(Guid actor, Guid org, Guid group, Guid id, CancellationToken ct) =>
        groups.Change(actor, org, group, [], async (_, parent, token) =>
        {
            var rate = await db.Set<GroupTranslationRate>().SingleOrDefaultAsync(x => x.GroupId == group && x.Id == id, token) ?? throw Missing();
            if (rate.ApprovedBy == actor) return rate;
            if (rate.ApprovedBy.HasValue || rate.PreparedBy == actor) throw Conflict("An independent owner must approve the translation rate.");
            rate.ApprovedBy = actor;
            access.Audit(parent, actor, id, "GroupTranslationRateApproved");
            return rate;
        }, ct);

    public static GroupTranslationRate Select(IEnumerable<GroupTranslationRate> rates, Guid company, Guid account,
        AccountClass classification, DateOnly postedOn, DateOnly cutoff)
    {
        var equity = classification == AccountClass.Equity;
        var date = classification is AccountClass.Asset or AccountClass.Liability ? cutoff : postedOn;
        return rates.Where(x => x.MemberId == company && x.RateDate == date && x.EffectiveOn <= cutoff && x.ApprovedBy.HasValue &&
                x.EquityAccountId == (equity ? account : (Guid?)null))
            .MaxBy(x => x.EffectiveOn) ?? throw Conflict($"Missing approved translation rate for {company}, account {account}, on {date:yyyy-MM-dd}.");
    }
}
