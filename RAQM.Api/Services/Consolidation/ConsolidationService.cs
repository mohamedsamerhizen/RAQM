using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Entities.Governance;
using RAQM.Api.Services.ApprovalGovernance;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Consolidation;
using RAQM.Api.DTOs.CorporateGroups;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Consolidation;
using RAQM.Api.Entities.CorporateGroups;
using RAQM.Api.Entities.Intercompany;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.CorporateGroups;
using RAQM.Api.Services.CurrencyTranslation;
using RAQM.Api.Services.FinancialReporting;
using RAQM.Api.Services.Intercompany;
using static RAQM.Api.Services.CorporateGroups.CorporateGroupService;

namespace RAQM.Api.Services.Consolidation;

public sealed class ConsolidationService(RaqmDbContext db, CorporateGroupService groups, FinancialLedger ledger,
    IntercompanyService intercompany, FinancialAccess access, TimeProvider clock, GovernanceGate? governance = null)
{
    private sealed record RunInputs(WorksheetAdjustment[] Adjustments, OwnershipBasis[] OwnershipBases, string AccountingPolicyEvidence);

    public async Task<ConsolidationRun> Prepare(Guid actor, Guid org, Guid id, GroupRunRequest input, CancellationToken ct)
    {
        var hint = await groups.Resolve(actor, org, id, ct);
        var members = await Members(hint, input.From, input.Cutoff, ct);
        return await groups.Change(actor, org, id, members, async (group, parent, token) =>
        {
            var source = await Capture(group, members, input, token);
            var previous = await db.Set<ConsolidationRun>().AsNoTracking().Where(x => x.GroupId == id && x.From == input.From &&
                x.Cutoff == input.Cutoff).OrderByDescending(x => x.Version).ToListAsync(token);
            var published = previous.FirstOrDefault(x => x.State == GroupRunState.Published);
            if (published?.Id != input.SupersedesId) throw Conflict("Identify the current published version when preparing a revision.");
            var row = InGroup(group, new ConsolidationRun { From = input.From, Cutoff = input.Cutoff, SupersedesId = input.SupersedesId,
                TranslationReserveId = input.TranslationReserveId, Version = (previous.FirstOrDefault()?.Version ?? 0) + 1,
                PreparedBy = actor, SourceHash = TradeRules.Hash(source), SnapshotJson = JsonSerializer.Serialize(source),
                AdjustmentsJson = JsonSerializer.Serialize(new RunInputs(input.Adjustments?.ToArray() ?? [], input.OwnershipBases?.ToArray() ?? [],
                    source.AccountingPolicyEvidence)) });
            db.Add(row);
            access.Audit(parent, actor, row.Id, "ConsolidationPrepared");
            return row;
        }, ct);
    }

    public async Task<ConsolidationRun> Change(Guid actor, Guid org, Guid id, Guid run, string action, CancellationToken ct)
    {
        var hint = await groups.Resolve(actor, org, id, ct);
        var original = await Find(id, run, ct);
        var members = await Members(hint, original.From, original.Cutoff, ct);
        return await groups.Change(actor, org, id, members, async (group, parent, token) =>
        {
            var row = await db.Set<ConsolidationRun>().SingleAsync(x => x.GroupId == id && x.Id == run, token);
            if (action == "approve" && row.ApprovedBy == actor || action == "publish" && row.State == GroupRunState.Published) return row;
            if (!(action == "approve" && row.State == GroupRunState.Prepared && row.PreparedBy != actor ||
                  action == "publish" && row.State == GroupRunState.Approved && row.ApprovedBy == actor))
                throw Conflict("An independent owner must approve and publish this exact consolidation version.");
            var published = await db.Set<ConsolidationRun>().AsNoTracking().Where(x => x.GroupId == id && x.From == row.From &&
                    x.Cutoff == row.Cutoff && x.State == GroupRunState.Published).OrderByDescending(x => x.Version)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
            if (published != row.SupersedesId) throw Conflict("Another consolidation version has been published.");
            var inputs = JsonSerializer.Deserialize<RunInputs>(row.AdjustmentsJson)!;
            var current = await Capture(group, members, new(row.From, row.Cutoff, row.SupersedesId, row.TranslationReserveId,
                inputs.Adjustments, inputs.OwnershipBases, inputs.AccountingPolicyEvidence), token);
            if (TradeRules.Hash(current) != row.SourceHash) throw Conflict("Source evidence changed. Prepare and review a fresh version.");
            if (action == "approve")
            {
                row.State = GroupRunState.Approved;
                row.ApprovedBy = actor;
            }
            else
            {
                if (governance is not null) await governance.Enforce(parent, actor, FinancialDuty.ConsolidationPublication, row.Id, token);
                row.State = GroupRunState.Published;
                row.PublishedBy = actor;
                row.PublishedAt = AccountingRules.StoredUtcInstant(clock.GetUtcNow());
            }
            access.Audit(parent, actor, row.Id, action == "approve" ? "ConsolidationApproved" : "ConsolidationPublished");
            return row;
        }, ct);
    }

    private async Task<Guid[]> Members(CorporateGroup group, DateOnly from, DateOnly cutoff, CancellationToken ct)
    {
        if (from < group.StartsOn || cutoff < from || cutoff > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) || group.RetiredOn <= cutoff)
            throw Invalid("Use an ordered historical group fiscal window within the group's effective dates.");
        return ResolveOwnership(group.CompanyId, await groups.History(group.Id, ct), cutoff)
            .Where(x => x.Edge.Kind == InvestmentKind.Controlled).Select(x => x.Edge.InvesteeId).Append(group.CompanyId).Order().ToArray();
    }

    private async Task<ConsolidationSnapshot> Capture(CorporateGroup group, Guid[] locked, GroupRunRequest input, CancellationToken ct)
    {
        var members = await Members(group, input.From, input.Cutoff, ct);
        var policyEvidence = AccountingRules.Text(input.AccountingPolicyEvidence, 1000, "Aligned accounting policy and internal-flow review evidence");
        if (!locked.SequenceEqual(members)) throw Conflict("Group membership changed while acquiring company locks. Retry.");
        var history = await groups.History(group.Id, ct);
        var ownership = ResolveOwnership(group.CompanyId, history, input.Cutoff);
        if (ownership.Any(x => x.Edge.Kind == InvestmentKind.Controlled &&
            (x.Edge.InvestorId != group.CompanyId || x.EconomicPercent != 100m || x.Edge.EffectiveOn > input.From ||
             history.Count(h => h.InvesteeId == x.Edge.InvesteeId && h.EffectiveOn <= input.Cutoff) != 1)) ||
            history.Any(x => x.EffectiveOn <= input.Cutoff && x.Kind == InvestmentKind.Controlled && !members.Contains(x.InvesteeId)))
            throw Conflict("Automatic consolidation requires stable, direct, wholly owned subsidiaries. Changes of control and NCI require additional accounting treatment.");
        if (ownership.Any(x => x.Edge.Kind is InvestmentKind.Associate or InvestmentKind.JointArrangement))
            throw Conflict("Associate and joint arrangement equity-method accounting is outside automatic publication.");

        var accounts = await db.Set<GroupAccount>().AsNoTracking().Where(x => x.GroupId == group.Id).OrderBy(x => x.Id).ToArrayAsync(ct);
        var chart = accounts.ToDictionary(x => x.Id);
        var mappings = (await db.Set<GroupMapping>().AsNoTracking().Where(x => x.GroupId == group.Id && x.ApprovedBy != null &&
            x.EffectiveOn <= input.Cutoff && members.Contains(x.MemberId)).ToListAsync(ct))
            .GroupBy(x => (x.MemberId, x.LedgerAccountId)).ToDictionary(x => x.Key, x => x.MaxBy(y => y.EffectiveOn)!);
        var rates = await db.Set<GroupTranslationRate>().AsNoTracking().Where(x => x.GroupId == group.Id &&
            x.ApprovedBy != null && x.EffectiveOn <= input.Cutoff).ToListAsync(ct);
        var policies = (await db.Set<IntercompanyPolicy>().AsNoTracking().Where(x => x.GroupId == group.Id &&
            x.ApprovedBy != null && x.EffectiveOn <= input.Cutoff).ToListAsync(ct)).Where(x =>
                members.Contains(x.CreditorId) && members.Contains(x.DebtorId)).ToDictionary(x => x.Id);
        if (await db.Set<IntercompanyPolicy>().AsNoTracking().AnyAsync(x => x.GroupId != group.Id && x.ApprovedBy != null &&
            x.EffectiveOn <= input.Cutoff && members.Contains(x.CreditorId) && members.Contains(x.DebtorId), ct))
            throw Conflict("Included companies have a reciprocal relationship outside this consolidation group. Resolve its elimination scope before publication.");
        var reconciliations = new List<GroupReciprocalEvidence>();
        foreach (var policy in policies.Values.GroupBy(x => (x.CreditorId, x.DebtorId)).Select(x => x.MaxBy(y => y.EffectiveOn)!).OrderBy(x => x.Id))
        {
            var balance = await intercompany.Balance(policy, input.Cutoff, ct);
            if (!balance.Agrees || balance.Pending.Length != 0)
                throw Conflict("Unreconciled or pending intercompany evidence blocks consolidation.");
            if (balance.Posted.Length == 0) continue;
            var reviews = await db.Set<ReciprocalReview>().AsNoTracking().Where(x => x.GroupId == group.Id &&
                x.PolicyId == policy.Id && x.Cutoff == input.Cutoff).OrderBy(x => x.Id).ToListAsync(ct);
            var fingerprint = TradeRules.Hash(balance);
            var review = reviews.FirstOrDefault(x => TradeRules.Hash(JsonSerializer.Deserialize<ReciprocalBalance>(x.SnapshotJson)!) == fingerprint)
                ?? throw Conflict("Finalize the current reciprocal reconciliation at the group cutoff before consolidation.");
            reconciliations.Add(new(policy.Id, review.Id, balance.DueFrom, balance.Posted));
        }
        var pairs = (await db.Set<IntercompanyPair>().AsNoTracking().Where(x => x.GroupId == group.Id && x.Date <= input.Cutoff &&
                x.State == PairState.Posted).OrderBy(x => x.Id).ToListAsync(ct)).Where(x => policies.ContainsKey(x.PolicyId)).ToArray();
        if (pairs.Any(x => x.Currency != group.Currency))
            throw Conflict("Automated reciprocal elimination requires the pair's functional currency to equal group presentation currency.");
        var pairByJournal = pairs.SelectMany(x => new[] { (Id: x.CreditorJournalId!.Value, Pair: x), (Id: x.DebtorJournalId!.Value, Pair: x) })
            .ToDictionary(x => x.Id, x => x.Pair);
        var pairIndex = pairs.ToDictionary(x => x.Id);
        var cutoffEvidence = new List<CompanyCutoff>();
        var contributions = new List<GroupContribution>();
        var adjustments = new List<GroupAdjustmentLine>();
        var protectedAccounts = new HashSet<Guid>();
        var units = CurrencyPolicy.Units(group.Currency);
        foreach (var company in members)
        {
            var currency = await db.Companies.AsNoTracking().Where(x => x.Id == company && x.OrganizationId == group.OrganizationId &&
                x.TenantId == group.TenantId).Select(x => x.BaseCurrency).SingleAsync(ct);
            var periods = await Periods(company, input.From, input.Cutoff, ct);
            var sourceChart = (await ledger.Accounts(company, ct)).ToDictionary(x => x.Id);
            var source = await ledger.Lines(company, input.Cutoff).ToListAsync(ct);
            var edge = ownership.FirstOrDefault(x => x.Edge.InvesteeId == company);
            if (edge is not null && source.Any(x => x.Date < edge.Edge.EffectiveOn))
                throw Conflict("Pre-acquisition journal activity requires acquisition-date accounting outside automatic consolidation.");
            var previousNominal = source.Where(x => x.Date < input.From && FinancialMath.Nominal(sourceChart[x.AccountId].Classification));
            if (previousNominal.GroupBy(x => x.AccountId).Any(x => x.Sum(FinancialMath.Signed) != 0))
                throw Conflict("Unclosed prior-year nominal accounts block group reporting.");
            cutoffEvidence.Add(new(company, currency, periods[0].FiscalYearId, periods.Select(x => x.Version).ToArray(),
                source.Select(x => x.Sequence).DefaultIfEmpty().Max()));
            var total = 0m;
            foreach (var line in source.Where(x => !(x.Date >= input.From && x.Source == FinancialMath.YearEndSource) &&
                !(x.Date < input.From && FinancialMath.Nominal(sourceChart[x.AccountId].Classification))))
            {
                var classification = sourceChart[line.AccountId].Classification;
                if (!mappings.TryGetValue((company, line.AccountId), out var mapping) ||
                    !chart.TryGetValue(mapping.GroupAccountId, out var target) || target.Classification != classification)
                    throw Conflict($"An approved compatible mapping is missing for company {company}, account {line.AccountId}.");
                var signed = FinancialMath.Signed(line);
                var rate = currency == group.Currency ? null : GroupRateService.Select(rates, company, line.AccountId, classification, line.Date, input.Cutoff);
                var amount = Translate(signed, rate?.Rate ?? 1m, units);
                total += amount;
                var internalPair = pairByJournal.TryGetValue(line.JournalId, out var pair);
                contributions.Add(new(company, line.AccountId, line.JournalId, line.LineId, line.Date, mapping.Id, target.Id, signed,
                    rate?.Rate ?? 1m, rate?.Id, amount, internalPair, line.Source, line.OriginalSource, line.Reference));
                if (internalPair)
                {
                    var sourcePair = pair!;
                    var policy = policies[sourcePair.PolicyId];
                    var basis = sourcePair.Kind == PairKind.Correction ? pairIndex[sourcePair.BasisId!.Value] : sourcePair;
                    if (basis.Kind is PairKind.Principal or PairKind.Settlement &&
                        line.AccountId != policy.DueFromId && line.AccountId != policy.DueToId)
                    {
                        if (!target.Cash) throw Conflict("Map each reciprocal bank line to a group cash account.");
                        continue;
                    }
                    if (target.Cash) throw Conflict("Reciprocal noncash controls must map outside group cash accounts.");
                    protectedAccounts.Add(target.Id);
                    adjustments.Add(new("Elimination", sourcePair.Reference, target.Id, -amount, policy.Evidence, company, sourcePair.Id));
                }
            }
            if (currency != group.Currency)
            {
                if (input.TranslationReserveId is not { } reserve || !chart.TryGetValue(reserve, out var reserveAccount) ||
                    reserveAccount.Classification != AccountClass.Equity || mappings.Values.Any(x => x.GroupAccountId == reserve))
                    throw Conflict("Select a dedicated group translation reserve in equity.");
                if (total != 0) adjustments.Add(new("Translation", company.ToString(), reserve, -total, "Closing and historical rate reconciliation", company));
            }
            else if (total != 0) throw Conflict("The source company's eligible trial balance does not balance.");
        }
        if (adjustments.Where(x => x.Layer == "Elimination").GroupBy(x => x.SourceId).Any(x => x.Sum(y => y.Signed) != 0) ||
            pairs.Any(x => !contributions.Any(c => c.JournalId == x.CreditorJournalId) || !contributions.Any(c => c.JournalId == x.DebtorJournalId)))
            throw Conflict("Reciprocal source evidence is incomplete or elimination does not balance.");
        AddOwnership(group.CompanyId, ownership, input.OwnershipBases ?? [], contributions, chart, adjustments, protectedAccounts);
        AddWorksheet(input.Adjustments ?? [], members, chart, units, input.TranslationReserveId, protectedAccounts, adjustments);
        if (contributions.Sum(x => x.Translated) + adjustments.Sum(x => x.Signed) != 0)
            throw Conflict("The consolidated worksheet is unbalanced.");
        return new(group.Currency, policyEvidence, cutoffEvidence.ToArray(), ownership.Select(x => x.Edge).ToArray(), accounts,
            contributions.ToArray(), adjustments.ToArray(), reconciliations.ToArray());
    }

    private async Task<AccountingPeriod[]> Periods(Guid company, DateOnly from, DateOnly cutoff, CancellationToken ct)
    {
        var year = await db.FiscalYears.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.StartDate == from &&
            x.EndDate >= cutoff, ct) ?? throw Conflict("Each company must share the requested fiscal-year start and cutoff window.");
        var periods = await db.Periods.AsNoTracking().Where(x => x.CompanyId == company && x.FiscalYearId == year.Id &&
            x.StartDate >= from && x.EndDate <= cutoff).OrderBy(x => x.StartDate).ToArrayAsync(ct);
        if (periods.Length == 0 || periods[0].StartDate != from || periods[^1].EndDate != cutoff ||
            periods.Any(x => x.State == PeriodState.Open) ||
            periods.Zip(periods.Skip(1)).Any(x => x.First.EndDate.AddDays(1) != x.Second.StartDate))
            throw Conflict("All aligned company periods through cutoff must be closed or locked.");
        return periods;
    }

    private static void AddOwnership(Guid parent, IReadOnlyList<EffectiveOwnership> ownership, IReadOnlyList<OwnershipBasis> inputs,
        List<GroupContribution> sources, Dictionary<Guid, GroupAccount> chart, List<GroupAdjustmentLine> output, HashSet<Guid> protectedAccounts)
    {
        var controlled = ownership.Where(x => x.Edge.Kind == InvestmentKind.Controlled).Select(x => x.Edge.InvesteeId).ToHashSet();
        if (inputs.Any(x => x is null) || !controlled.SetEquals(inputs.Select(x => x.MemberId)) ||
            inputs.Select(x => x.MemberId).Distinct().Count() != inputs.Count ||
            inputs.Select(x => x.InvestmentAccountId).Distinct().Count() != inputs.Count)
            throw Conflict("Provide one separately traceable investment and contributed equity basis per controlled company.");
        foreach (var input in inputs.OrderBy(x => x.MemberId))
        {
            var left = sources.Where(x => x.CompanyId == parent && x.LedgerAccountId == input.InvestmentAccountId).ToArray();
            var right = sources.Where(x => x.CompanyId == input.MemberId && x.LedgerAccountId == input.ContributedEquityAccountId).ToArray();
            var investment = left.Sum(x => x.Translated);
            var capital = right.Sum(x => x.Translated);
            if (left.Length == 0 || right.Length == 0 || investment <= 0 || investment + capital != 0 ||
                left.Any(x => chart[x.GroupAccountId].Classification != AccountClass.Asset || chart[x.GroupAccountId].Cash || x.InternalPair) ||
                right.Any(x => chart[x.GroupAccountId].Classification != AccountClass.Equity || x.InternalPair))
                throw Conflict("Parent investment and subsidiary contributed equity must be fully traceable and equal. Acquisition differences require unsupported accounting.");
            var reason = AccountingRules.Text(input.Evidence, 1000, "Ownership elimination evidence");
            foreach (var item in left.Concat(right).GroupBy(x => (x.CompanyId, x.GroupAccountId)).OrderBy(x => x.Key))
            {
                protectedAccounts.Add(item.Key.GroupAccountId);
                output.Add(new("Ownership", input.MemberId.ToString(), item.Key.GroupAccountId, -item.Sum(x => x.Translated),
                    reason, item.Key.CompanyId, input.MemberId));
            }
        }
    }

    private static void AddWorksheet(IReadOnlyList<WorksheetAdjustment> inputs, Guid[] members, Dictionary<Guid, GroupAccount> chart,
        int units, Guid? reserve, HashSet<Guid> protectedAccounts, List<GroupAdjustmentLine> output)
    {
        if (inputs.Count > 200 || inputs.Any(x => x is null)) throw Invalid("Use up to 200 supported worksheet adjustments.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            var reference = AccountingRules.Text(input.Reference, 100, "Adjustment reference");
            var reason = AccountingRules.Text(input.Evidence, 1000, "Adjustment evidence");
            if (!seen.Add(reference) || input.Companies is null || input.Companies.Count == 0 ||
                input.Companies.Any(x => !members.Contains(x)) || input.Lines is null || input.Lines.Count is < 2 or > 100)
                throw Invalid("Each adjustment requires a unique reference, included company evidence and bounded lines.");
            foreach (var line in input.Lines)
            {
                if (line is null || !chart.TryGetValue(line.AccountId, out var account) || account.Cash ||
                    line.AccountId == reserve || protectedAccounts.Contains(line.AccountId) ||
                    line.Debit < 0 || line.Credit < 0 || (line.Debit == 0) == (line.Credit == 0))
                    throw Invalid("Use a single debit or credit on each eligible worksheet account.");
                CurrencyPolicy.Validate(line.Debit, units);
                CurrencyPolicy.Validate(line.Credit, units);
            }
            if (input.Lines.Sum(x => x.Debit - x.Credit) != 0) throw Invalid("Every worksheet adjustment must balance independently.");
            output.AddRange(input.Lines.Select(x => new GroupAdjustmentLine("Worksheet", reference, x.AccountId, x.Debit - x.Credit,
                reason, Companies: input.Companies.Distinct().Order().ToArray())));
        }
    }

    public static decimal Translate(decimal amount, decimal rate, int units) => decimal.Round(amount * rate, units, MidpointRounding.ToEven);

    public async Task<ConsolidationRun> Review(Guid actor, Guid org, Guid group, Guid id, CancellationToken ct)
    {
        await groups.Resolve(actor, org, group, ct);
        return await Find(group, id, ct);
    }

    internal async Task<ConsolidationRun> Find(Guid group, Guid id, CancellationToken ct) =>
        await db.Set<ConsolidationRun>().AsNoTracking().SingleOrDefaultAsync(x => x.GroupId == group && x.Id == id, ct) ?? throw Missing();
}
