using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.CorporateGroups;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Banking;
using RAQM.Api.Entities.CorporateGroups;
using RAQM.Api.Entities.Intercompany;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.CorporateGroups;
using RAQM.Api.Services.FinancialReporting;
using static RAQM.Api.Services.CorporateGroups.CorporateGroupService;

namespace RAQM.Api.Services.Intercompany;

public sealed record PairPlan(Guid Actor, JournalDraftLine[] Creditor, JournalDraftLine[] Debtor, Guid? CreditorReversal, Guid? DebtorReversal);
public sealed record ReciprocalBalance(Guid Creditor, Guid Debtor, string Currency, decimal DueFrom, decimal DueTo, decimal Expected,
    Guid[] Posted, Guid[] Pending)
{
    public decimal Difference => DueFrom - DueTo;
    public bool Agrees => DueFrom == DueTo && DueFrom == Expected;
}

public sealed class IntercompanyService(RaqmDbContext db, CorporateGroupService groups, FinancialAccess access,
    IPostingEngine posting, FinancialLedger ledger, TimeProvider clock)
{
    internal const string Source = "Intercompany.Pair";

    public Task<IntercompanyPolicy> Register(Guid actor, Guid org, Guid id, PartnerRequest input, CancellationToken ct) =>
        groups.Change(actor, org, id, [input.CreditorId, input.DebtorId], async (group, parent, token) =>
        {
            if (input.CreditorId == input.DebtorId || input.CreditorBankId.HasValue != input.DebtorBankId.HasValue)
                throw Invalid("Select two companies and either both bank accounts or neither.");
            await groups.RequireMember(group, input.CreditorId, input.EffectiveOn, token);
            await groups.RequireMember(group, input.DebtorId, input.EffectiveOn, token);
            await Account(input.CreditorId, input.DueFromId, AccountClass.Asset, token);
            await Account(input.DebtorId, input.DueToId, AccountClass.Liability, token);
            await Account(input.CreditorId, input.RevenueId, AccountClass.Revenue, token);
            await Account(input.DebtorId, input.ExpenseId, AccountClass.Expense, token);
            if (input.CreditorBankId is { } left)
            {
                await Bank(input.CreditorId, left, token);
                await Bank(input.DebtorId, input.DebtorBankId!.Value, token);
                if (left == input.DueFromId) throw Invalid("The due-from account must be distinct from bank.");
            }
            var history = await db.Set<IntercompanyPolicy>().Where(x =>
                x.DueFromId == input.DueFromId || x.DueToId == input.DueToId ||
                x.GroupId == id && x.CreditorId == input.CreditorId && x.DebtorId == input.DebtorId).ToListAsync(token);
            if (history.Any(x => x.GroupId != id || x.CreditorId != input.CreditorId || x.DebtorId != input.DebtorId ||
                x.DueFromId != input.DueFromId || x.DueToId != input.DueToId || x.EffectiveOn >= input.EffectiveOn))
                throw Conflict("Use dedicated reciprocal accounts and keep their identity across prospective policy versions.");
            if (history.Count == 0 && await db.JournalLines.AnyAsync(x => x.AccountId == input.DueFromId || x.AccountId == input.DueToId, token))
                throw Conflict("New reciprocal control accounts must have no journal activity.");
            if (await db.TreasuryAccounts.AnyAsync(x => x.LedgerAccountId == input.DueFromId, token) ||
                await db.TradingAccounts.AnyAsync(x => x.AccountId == input.DueFromId || x.AccountId == input.DueToId, token) ||
                await db.InventoryAccounts.AnyAsync(x => x.AccountId == input.DueFromId || x.AccountId == input.DueToId, token) ||
                await db.FxAccounts.AnyAsync(x => x.AccountId == input.DueFromId || x.AccountId == input.DueToId, token) ||
                await db.AssetControls.AnyAsync(x => x.AccountId == input.DueFromId || x.AccountId == input.DueToId, token))
                throw Conflict("A reciprocal control account cannot overlap another source control.");
            var policy = InGroup(group, new IntercompanyPolicy { CreditorId = input.CreditorId, DebtorId = input.DebtorId,
                DueFromId = input.DueFromId, DueToId = input.DueToId, RevenueId = input.RevenueId, ExpenseId = input.ExpenseId,
                CreditorBankId = input.CreditorBankId, DebtorBankId = input.DebtorBankId, EffectiveOn = input.EffectiveOn,
                PreparedBy = actor, Evidence = AccountingRules.Text(input.Evidence, 1000, "Relationship evidence") });
            db.Add(policy);
            access.Audit(parent, actor, policy.Id, "IntercompanyPolicyPrepared", policy.Evidence);
            return policy;
        }, ct);

    public async Task<IntercompanyPolicy> ApprovePolicy(Guid actor, Guid org, Guid group, Guid policy, CancellationToken ct)
    {
        await groups.Resolve(actor, org, group, ct);
        var hint = await FindPolicy(group, policy, ct);
        return await groups.Change(actor, org, group, [hint.CreditorId, hint.DebtorId], async (_, parent, token) =>
        {
            var row = await db.Set<IntercompanyPolicy>().SingleAsync(x => x.Id == policy && x.GroupId == group, token);
            if (row.ApprovedBy == actor) return row;
            if (row.ApprovedBy.HasValue || row.PreparedBy == actor) throw Conflict("An independent owner must approve the partner policy.");
            if (!await db.Set<IntercompanyPolicy>().AnyAsync(x => x.GroupId == group && x.CreditorId == row.CreditorId &&
                    x.DebtorId == row.DebtorId && x.ApprovedBy != null, token) &&
                await db.JournalLines.AnyAsync(x => x.AccountId == row.DueFromId || x.AccountId == row.DueToId, token))
                throw Conflict("The reciprocal accounts gained activity before approval.");
            row.ApprovedBy = actor;
            access.Audit(parent, actor, row.Id, "IntercompanyPolicyApproved");
            return row;
        }, ct);
    }

    public async Task<IntercompanyPair> Prepare(Guid actor, Guid org, Guid group, PairRequest input, CancellationToken ct)
    {
        await groups.Resolve(actor, org, group, ct);
        var hint = await FindPolicy(group, input.PolicyId, ct);
        return await groups.Change(actor, org, group, [hint.CreditorId, hint.DebtorId], async (scope, parent, token) =>
        {
            var policy = await FindPolicy(group, input.PolicyId, token);
            var creditor = await access.Resolve(actor, org, policy.CreditorId, true, false, token);
            var debtor = await access.Resolve(actor, org, policy.DebtorId, true, false, token);
            if (creditor.BaseCurrency != debtor.BaseCurrency)
                throw Conflict("Intercompany posting requires a shared functional currency.");
            CurrencyPolicy.Validate(input.Amount, CurrencyPolicy.Units(creditor.BaseCurrency));
            if (input.Amount <= 0 || input.Date.Year < 1900 || input.Date > Today() || !Enum.IsDefined(input.Kind) ||
                input.Kind == PairKind.Service && !input.NoTaxApplicable ||
                input.Kind != PairKind.Interest && (input.InterestFrom.HasValue || input.InterestAnnualPercent.HasValue))
                throw Invalid("Use a positive amount and eligible date. A service requires documented no-tax applicability in both companies.");
            var pair = InGroup(scope, new IntercompanyPair { PolicyId = policy.Id, CreditorId = creditor.Id, DebtorId = debtor.Id,
                BasisId = input.BasisId, CreatedBy = actor, Kind = input.Kind, Date = input.Date, Amount = input.Amount, Currency = creditor.BaseCurrency,
                Reference = AccountingRules.Text(input.Reference, 100, "Unique reciprocal reference").ToUpperInvariant(),
                Evidence = AccountingRules.Text(input.Evidence, 500, "Source evidence"), InterestFrom = input.InterestFrom,
                InterestAnnualPercent = input.InterestAnnualPercent });
            var existing = await db.Set<IntercompanyPair>().AsNoTracking().SingleOrDefaultAsync(x => x.GroupId == group && x.Reference == pair.Reference, token);
            if (existing is not null)
            {
                if (existing.PolicyId == pair.PolicyId && existing.BasisId == pair.BasisId && existing.Kind == pair.Kind &&
                    existing.Date == pair.Date && existing.Amount == pair.Amount && existing.Evidence == pair.Evidence &&
                    existing.InterestFrom == pair.InterestFrom && existing.InterestAnnualPercent == pair.InterestAnnualPercent) return existing;
                throw Conflict("The reciprocal reference belongs to a different financial request.");
            }
            await Validate(scope, policy, pair, token);
            db.Add(pair);
            access.Audit(parent, actor, pair.Id, "IntercompanyPairPrepared", pair.Evidence);
            return pair;
        }, ct);
    }

    public async Task<IntercompanyPair> Change(Guid actor, Guid org, Guid group, Guid id, string action, CancellationToken ct)
    {
        await groups.Resolve(actor, org, group, ct);
        var hint = await db.Set<IntercompanyPair>().AsNoTracking().SingleOrDefaultAsync(x => x.GroupId == group && x.Id == id, ct) ?? throw Missing();
        return await groups.Change(actor, org, group, [hint.CreditorId, hint.DebtorId], async (scope, parent, token) =>
        {
            var pair = await db.Set<IntercompanyPair>().SingleAsync(x => x.GroupId == group && x.Id == id, token);
            if (action == "approve" && pair.ApprovedBy == actor || action == "post" && pair.State == PairState.Posted ||
                action == "cancel" && pair.State == PairState.Cancelled) return pair;
            if (action == "cancel" && pair.State is PairState.Draft or PairState.Approved)
            {
                pair.State = PairState.Cancelled;
                access.Audit(parent, actor, id, "IntercompanyPairCancelled");
                return pair;
            }
            var policy = await FindPolicy(group, pair.PolicyId, token);
            var basis = await Validate(scope, policy, pair, token);
            if (action == "approve" && pair.State == PairState.Draft && pair.CreatedBy != actor)
            {
                pair.State = PairState.Approved;
                pair.ApprovedBy = actor;
                access.Audit(parent, actor, id, "IntercompanyPairApproved");
                return pair;
            }
            if (action != "post" || pair.State != PairState.Approved)
                throw Conflict("An independently approved pair is required for posting.");
            var plan = pair.Kind == PairKind.Correction
                ? new PairPlan(actor, await Reverse(pair.CreditorId, basis!.CreditorJournalId!.Value, token),
                    await Reverse(pair.DebtorId, basis.DebtorJournalId!.Value, token), basis.CreditorJournalId, basis.DebtorJournalId)
                : BuildPlan(actor, policy, pair.Kind, pair.Amount);
            pair.PlanJson = JsonSerializer.Serialize(plan);
            pair.State = PairState.Posting;
            await db.SaveChangesAsync(token);
            var journals = new Dictionary<Guid, Guid>();
            foreach (var company in new[] { pair.CreditorId, pair.DebtorId }.Order())
            {
                var left = company == pair.CreditorId;
                var reversal = left ? plan.CreditorReversal : plan.DebtorReversal;
                var request = new PostingRequest(new(pair.TenantId, org, company), reversal.HasValue ? "Reversal" : Source, reversal ?? pair.Id,
                    Key(pair.Id), pair.Date, pair.Currency, left ? plan.Creditor : plan.Debtor, reversal, pair.Date,
                    $"Intercompany {pair.Kind}", pair.Reference, true, pair.Evidence);
                var receipt = await posting.PostAsync(actor, request, token);
                journals.Add(company, receipt.JournalId);
            }
            pair.CreditorJournalId = journals[pair.CreditorId];
            pair.DebtorJournalId = journals[pair.DebtorId];
            pair.State = PairState.Posted;
            access.Audit(parent, actor, id, "IntercompanyPairPosted", pair.Evidence);
            return pair;
        }, ct);
    }

    private async Task<IntercompanyPair?> Validate(CorporateGroup scope, IntercompanyPolicy policy, IntercompanyPair pair, CancellationToken ct)
    {
        await groups.RequireMember(scope, pair.CreditorId, pair.Date, ct);
        await groups.RequireMember(scope, pair.DebtorId, pair.Date, ct);
        var effective = await db.Set<IntercompanyPolicy>().AsNoTracking().Where(x => x.GroupId == scope.Id && x.CreditorId == pair.CreditorId &&
            x.DebtorId == pair.DebtorId && x.ApprovedBy != null && x.EffectiveOn <= pair.Date)
            .OrderByDescending(x => x.EffectiveOn).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (effective != policy.Id) throw Conflict("Use the approved policy version effective on the posting date.");
        if (pair.Kind is PairKind.Settlement or PairKind.Principal)
        {
            if (policy.CreditorBankId is not { } bank || policy.DebtorBankId is not { } other)
                throw Conflict("This movement requires two configured bank accounts and bank movement evidence.");
            await Bank(pair.CreditorId, bank, ct);
            await Bank(pair.DebtorId, other, ct);
        }
        if (pair.Kind is PairKind.Service or PairKind.Principal)
        {
            if (pair.BasisId.HasValue) throw Invalid("The initial obligation cannot reference an earlier pair.");
            return null;
        }

        var basis = await db.Set<IntercompanyPair>().AsNoTracking().SingleOrDefaultAsync(x => x.GroupId == pair.GroupId &&
            x.Id == pair.BasisId && x.State == PairState.Posted && x.CreditorId == pair.CreditorId && x.DebtorId == pair.DebtorId &&
            x.Date <= pair.Date, ct) ?? throw Conflict("A posted reciprocal basis is required.");
        var children = await db.Set<IntercompanyPair>().AsNoTracking().Where(x => x.GroupId == pair.GroupId && x.BasisId == basis.Id &&
            x.State == PairState.Posted).ToListAsync(ct);
        if (children.Any(x => x.Kind == PairKind.Correction) || pair.Kind == PairKind.Correction && (basis.Kind == PairKind.Correction || pair.Amount != basis.Amount))
            throw Conflict("Use one exact paired correction for the original posting.");
        if (pair.Kind == PairKind.Interest && basis.Kind != PairKind.Principal)
            throw Conflict("Simple accrued interest must reference a posted principal advance.");
        if (pair.Kind == PairKind.Settlement && basis.Kind is not (PairKind.Service or PairKind.Principal or PairKind.Interest))
            throw Conflict("Settle only an original service, loan or interest obligation.");
        var childIds = children.Select(x => x.Id).ToArray();
        var corrections = await db.Set<IntercompanyPair>().AsNoTracking().Where(x => x.GroupId == pair.GroupId && x.BasisId.HasValue &&
            childIds.Contains(x.BasisId.Value) && x.Kind == PairKind.Correction && x.State == PairState.Posted).ToListAsync(ct);
        var live = children.Where(x => !corrections.Any(r => r.BasisId == x.Id)).ToArray();
        if (pair.Kind == PairKind.Interest)
        {
            if (pair.InterestFrom is not { } from || pair.InterestAnnualPercent is not { } annual ||
                from <= basis.Date || from > pair.Date || live.Any(x => x.Kind == PairKind.Interest &&
                    x.InterestFrom <= pair.Date && x.Date >= from) ||
                children.Any(x => x.Kind == PairKind.Settlement && x.Date >= from && x.Date <= pair.Date) ||
                corrections.Any(x => x.Date >= from && x.Date <= pair.Date && children.Any(y => y.Id == x.BasisId && y.Kind == PairKind.Settlement)))
                throw Conflict("Interest requires a nonoverlapping actual/365 window with constant outstanding principal.");
            var priorSettlements = children.Where(x => x.Kind == PairKind.Settlement && x.Date < from).ToArray();
            var outstanding = basis.Amount - priorSettlements.Sum(x => x.Amount) +
                corrections.Where(x => x.Date < from && priorSettlements.Any(s => s.Id == x.BasisId)).Sum(x => x.Amount);
            if (pair.Amount != SimpleInterest(outstanding, annual, from, pair.Date, CurrencyPolicy.Units(pair.Currency)))
                throw Conflict("The requested accrual differs from the outstanding principal calculation.");
        }
        if (pair.Kind == PairKind.Correction && (live.Length != 0 || corrections.Any(x => x.Date > pair.Date)))
            throw Conflict("Correct dependent postings first and use a date at or after their corrections.");
        if (pair.Kind == PairKind.Settlement)
        {
            if (basis.Kind == PairKind.Principal && live.Any(x => x.Kind == PairKind.Interest && x.Date >= pair.Date))
                throw Conflict("A backdated repayment would change retained interest evidence.");
            var settlements = children.Where(x => x.Kind == PairKind.Settlement).ToArray();
            corrections = corrections.Where(x => settlements.Any(s => s.Id == x.BasisId)).ToList();
            var settledNow = children.Where(x => x.Kind == PairKind.Settlement).Sum(x => x.Amount) - corrections.Sum(x => x.Amount);
            var dated = children.Where(x => x.Kind == PairKind.Settlement && x.Date <= pair.Date).Sum(x => x.Amount)
                - corrections.Where(x => x.Date <= pair.Date).Sum(x => x.Amount);
            var futureDates = children.Select(x => x.Date).Concat(corrections.Select(x => x.Date)).Where(x => x > pair.Date).Distinct();
            var peak = futureDates.Select(date => children.Where(x => x.Kind == PairKind.Settlement && x.Date <= date).Sum(x => x.Amount)
                - corrections.Where(x => x.Date <= date).Sum(x => x.Amount)).Append(dated).Append(settledNow).Max();
            if (pair.Amount > basis.Amount - peak) throw Conflict("Settlement exceeds available reciprocal obligation.");
        }
        if (pair.Kind == PairKind.Correction && basis.Kind == PairKind.Settlement)
        {
            // Reversing a settlement restores exposure. It cannot be done against an obligation already corrected.
            if (await db.Set<IntercompanyPair>().AnyAsync(x => x.GroupId == pair.GroupId && x.BasisId == basis.BasisId &&
                x.Kind == PairKind.Correction && x.State == PairState.Posted, ct))
                throw Conflict("The underlying obligation was corrected.");
            var accruals = await db.Set<IntercompanyPair>().AsNoTracking().Where(x => x.GroupId == pair.GroupId &&
                x.BasisId == basis.BasisId && x.Kind == PairKind.Interest && x.State == PairState.Posted && x.Date >= pair.Date).ToListAsync(ct);
            foreach (var accrual in accruals)
                if (!await db.Set<IntercompanyPair>().AnyAsync(x => x.GroupId == pair.GroupId && x.BasisId == accrual.Id &&
                    x.Kind == PairKind.Correction && x.State == PairState.Posted, ct))
                    throw Conflict("A backdated correction would change retained interest evidence.");
        }
        return basis;
    }

    public async Task<object> Reconcile(Guid actor, Guid org, Guid group, Guid id, DateOnly cutoff, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await groups.Resolve(actor, org, group, ct);
        var policy = await FindPolicy(group, id, ct);
        if (!policy.ApprovedBy.HasValue || cutoff > Today() || cutoff < policy.EffectiveOn)
            throw Invalid("An approved policy and eligible cutoff are required.");
        var balance = await Balance(policy, cutoff, ct);
        var reviews = await db.Set<ReciprocalReview>().AsNoTracking().Where(x => x.GroupId == group && x.PolicyId == id && x.Cutoff == cutoff)
            .OrderBy(x => x.Id).ToListAsync(ct);
        var later = await db.Set<IntercompanyPair>().AsNoTracking().Where(x => x.GroupId == group &&
            x.CreditorId == policy.CreditorId && x.DebtorId == policy.DebtorId && x.Date > cutoff && x.State == PairState.Posted)
            .OrderBy(x => x.Date).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.Reference, x.Date, x.Kind, x.Amount, x.Currency }).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return new { Balance = balance, Reviews = reviews, PostCutoff = later };
    }

    public async Task<IReadOnlyList<IntercompanyPair>> List(Guid actor, Guid org, Guid group, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        await groups.Resolve(actor, org, group, ct);
        return await db.Set<IntercompanyPair>().AsNoTracking().Where(x => x.GroupId == group)
            .OrderByDescending(x => x.Date).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
    }

    public async Task<ReciprocalReview> Finalize(Guid actor, Guid org, Guid group, Guid id, DateOnly cutoff, GroupEvidenceRequest input, CancellationToken ct)
    {
        await groups.Resolve(actor, org, group, ct);
        var hint = await FindPolicy(group, id, ct);
        return await groups.Change(actor, org, group, [hint.CreditorId, hint.DebtorId], async (scope, parent, token) =>
        {
            var policy = await FindPolicy(group, id, token);
            if (!policy.ApprovedBy.HasValue || cutoff > Today() || cutoff < policy.EffectiveOn) throw Invalid("An approved policy and eligible cutoff are required.");
            var balance = await Balance(policy, cutoff, token);
            if (!balance.Agrees || balance.Pending.Length != 0) throw Conflict("Investigate reciprocal differences and pending pairs before finalizing.");
            var evidence = AccountingRules.Text(input.Evidence, 500, "Reconciliation evidence");
            var reviews = await db.Set<ReciprocalReview>().AsNoTracking().Where(x => x.GroupId == group && x.PolicyId == id && x.Cutoff == cutoff)
                .OrderBy(x => x.Id).ToListAsync(token);
            var fingerprint = RAQM.Api.Helpers.Trading.TradeRules.Hash(balance);
            var existing = reviews.FirstOrDefault(x => RAQM.Api.Helpers.Trading.TradeRules.Hash(JsonSerializer.Deserialize<ReciprocalBalance>(x.SnapshotJson)!) == fingerprint);
            if (existing is not null) return existing;
            var review = InGroup(scope, new ReciprocalReview { PolicyId = id, Cutoff = cutoff, ActorId = actor,
                SnapshotJson = JsonSerializer.Serialize(balance), Evidence = evidence });
            db.Add(review);
            access.Audit(parent, actor, review.Id, "ReciprocalReviewFinalized", review.Evidence);
            return review;
        }, ct);
    }

    internal async Task<ReciprocalBalance> Balance(IntercompanyPolicy policy, DateOnly cutoff, CancellationToken ct)
    {
        var rows = await db.Set<IntercompanyPair>().AsNoTracking().Where(x => x.GroupId == policy.GroupId &&
            x.CreditorId == policy.CreditorId && x.DebtorId == policy.DebtorId && x.Date <= cutoff).OrderBy(x => x.Id).ToListAsync(ct);
        var posted = rows.Where(x => x.State == PairState.Posted).ToDictionary(x => x.Id);
        var expected = posted.Values.Sum(x => x.Kind switch
        {
            PairKind.Settlement => -x.Amount,
            PairKind.Correction => posted[x.BasisId!.Value].Kind == PairKind.Settlement ? x.Amount : -x.Amount,
            _ => x.Amount
        });
        var left = await ledger.Lines(policy.CreditorId, cutoff, accounts: [policy.DueFromId]).SumAsync(x => x.Debit - x.Credit, ct);
        var right = await ledger.Lines(policy.DebtorId, cutoff, accounts: [policy.DueToId]).SumAsync(x => x.Credit - x.Debit, ct);
        var currency = await db.Companies.Where(x => x.Id == policy.CreditorId).Select(x => x.BaseCurrency).SingleAsync(ct);
        return new(policy.CreditorId, policy.DebtorId, currency, left, right, expected, posted.Keys.ToArray(),
            rows.Where(x => x.State is PairState.Draft or PairState.Approved or PairState.Posting).Select(x => x.Id).ToArray());
    }

    public static PairPlan BuildPlan(Guid actor, IntercompanyPolicy p, PairKind kind, decimal amount)
    {
        JournalDraftLine Debit(Guid account) => new(account, amount, 0, null);
        JournalDraftLine Credit(Guid account) => new(account, 0, amount, null);
        return kind switch
        {
            PairKind.Service or PairKind.Interest => new(actor, [Debit(p.DueFromId), Credit(p.RevenueId)], [Debit(p.ExpenseId), Credit(p.DueToId)], null, null),
            PairKind.Principal when p.CreditorBankId is { } left && p.DebtorBankId is { } right =>
                new(actor, [Debit(p.DueFromId), Credit(left)], [Debit(right), Credit(p.DueToId)], null, null),
            PairKind.Settlement when p.CreditorBankId is { } left && p.DebtorBankId is { } right =>
                new(actor, [Debit(left), Credit(p.DueFromId)], [Debit(p.DueToId), Credit(right)], null, null),
            _ => throw Invalid("This pair requires bank accounts or a retained correction basis.")
        };
    }

    internal static string Key(Guid id) => $"Intercompany:{id:N}";
    public static decimal SimpleInterest(decimal principal, decimal annualPercent, DateOnly from, DateOnly to, int units)
    {
        var days = to.DayNumber - from.DayNumber + 1;
        if (principal <= 0 || annualPercent is <= 0 or > 100 || decimal.Round(annualPercent, 4) != annualPercent ||
            days is < 1 or > 366)
            throw Invalid("Use positive principal, an annual percentage up to 100 and at most 366 actual days.");
        return decimal.Round(principal * annualPercent * days / 36_500m, units, MidpointRounding.ToEven);
    }
    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    private async Task<IntercompanyPolicy> FindPolicy(Guid group, Guid id, CancellationToken ct) =>
        await db.Set<IntercompanyPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.GroupId == group && x.Id == id, ct) ?? throw Missing();
    private async Task<JournalDraftLine[]> Reverse(Guid company, Guid journal, CancellationToken ct) =>
        await db.JournalLines.AsNoTracking().Where(x => x.CompanyId == company && x.JournalId == journal).OrderBy(x => x.Number)
            .Select(x => new JournalDraftLine(x.AccountId, x.Credit, x.Debit, x.Description)).ToArrayAsync(ct);
    private async Task Account(Guid company, Guid id, AccountClass classification, CancellationToken ct)
    {
        if (!await db.Accounts.AnyAsync(x => x.Id == id && x.CompanyId == company && x.Classification == classification && x.IsPosting && x.IsActive, ct))
            throw Invalid("An eligible posting account of the required classification is missing.");
    }
    private async Task Bank(Guid company, Guid id, CancellationToken ct)
    {
        if (!await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == company && x.LedgerAccountId == id && x.Active && x.Kind == TreasuryAccountKind.Bank, ct))
            throw Invalid("An active bank ledger account is required for each company.");
    }
}
