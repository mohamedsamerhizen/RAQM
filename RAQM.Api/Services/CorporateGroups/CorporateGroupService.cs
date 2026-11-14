using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.CorporateGroups;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.CorporateGroups;
using RAQM.Api.Entities.Intercompany;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Organizations;
using RAQM.Api.Services.Accounting;

namespace RAQM.Api.Services.CorporateGroups;

public sealed class CorporateGroupService(RaqmDbContext db, FinancialAccess access, FinancialTransaction transactions,
    IOrganizationService organizations, TimeProvider clock)
{
    internal static ApiException Missing() => new(404, "Group evidence was not found.", "group_missing");
    internal static ApiException Conflict(string message) => new(409, message, "group_conflict");
    internal static ApiException Invalid(string message) => new(400, message, "group_input");

    public async Task<CorporateGroup> Resolve(Guid actor, Guid org, Guid id, CancellationToken ct)
    {
        var member = await organizations.Resolve(actor, org, ct);
        if (member.Role != OrganizationRole.Owner) throw Missing();
        return await db.Set<CorporateGroup>().AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == id &&
            x.TenantId == member.TenantId, ct) ?? throw Missing();
    }

    public async Task<bool> ReportAccess(Guid actor, Guid org, Guid id, CancellationToken ct)
    {
        var member = await organizations.Resolve(actor, org, ct);
        var group = await db.Set<CorporateGroup>().AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == id &&
            x.TenantId == member.TenantId, ct) ?? throw Missing();
        if (member.Role == OrganizationRole.Owner) return true;
        if (!group.ReportReaders.Contains(actor)) throw Missing();
        return false;
    }

    public Task<CorporateGroup> Reader(Guid actor, Guid org, Guid id, GroupReaderRequest input, CancellationToken ct) =>
        Change(actor, org, id, [], async (_, parent, token) =>
        {
            var row = await db.Set<CorporateGroup>().SingleAsync(x => x.Id == id, token);
            if (input.Granted && !await db.OrganizationMembers.AnyAsync(x => x.TenantId == row.TenantId && x.OrganizationId == org &&
                x.UserId == input.UserId, token)) throw Missing();
            row.ReportReaders = (input.Granted ? row.ReportReaders.Append(input.UserId) : row.ReportReaders.Where(x => x != input.UserId))
                .Distinct().Order().ToArray();
            access.Audit(parent, actor, id, input.Granted ? "GroupReportReaderGranted" : "GroupReportReaderRevoked",
                $"{input.UserId:D} {AccountingRules.Text(input.Evidence, 430, "Reader grant evidence")}");
            return row;
        }, ct);

    public Task<CorporateGroup> Retire(Guid actor, Guid org, Guid id, GroupRetirementRequest input, CancellationToken ct) =>
        Change(actor, org, id, [], async (_, parent, token) =>
        {
            var row = await db.Set<CorporateGroup>().SingleAsync(x => x.Id == id, token);
            if (row.RetiredOn.HasValue || input.EffectiveOn <= row.StartsOn ||
                input.EffectiveOn <= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
                throw Conflict("Group retirement is prospective and can only be recorded once.");
            row.RetiredOn = input.EffectiveOn;
            access.Audit(parent, actor, id, "CorporateGroupRetired", AccountingRules.Text(input.Evidence, 500, "Retirement evidence"));
            return row;
        }, ct);

    public Task<CorporateGroup> Create(Guid actor, Guid org, GroupRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, input.ParentId, async (parent, token) =>
        {
            await access.Resolve(actor, org, parent.Id, true, true, token);
            _ = CurrencyPolicy.Units(input.Currency);
            if (input.StartsOn.Year < 1900) throw Invalid("A reporting start date is required.");
            var code = Code(input.Code);
            if (await db.Set<CorporateGroup>().AnyAsync(x => x.OrganizationId == org && x.Code == code, token))
                throw Conflict("Group code is already registered.");
            var group = new CorporateGroup { TenantId = parent.TenantId, OrganizationId = org, CompanyId = parent.Id,
                OwnerId = actor, Code = code, Name = AccountingRules.Text(input.Name, 160, "Group name"),
                Description = AccountingRules.Text(input.Description, 1000, "Group description"),
                Currency = input.Currency, StartsOn = input.StartsOn };
            db.Add(group);
            access.Audit(parent, actor, group.Id, "CorporateGroupRegistered");
            return group;
        }, ct);

    public async Task<IReadOnlyList<CorporateGroup>> List(Guid actor, Guid org, Guid parent, string? search, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        AccountingRules.OptionalText(search, 100);
        await access.Resolve(actor, org, parent, false, true, ct);
        var query = db.Set<CorporateGroup>().AsNoTracking().Where(x => x.OrganizationId == org && x.CompanyId == parent);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => EF.Functions.ILike(x.Code, "%" + search + "%") || EF.Functions.ILike(x.Name, "%" + search + "%"));
        return await query.OrderBy(x => x.Code).Skip((page - 1) * size).Take(size).ToListAsync(ct);
    }

    internal async Task<T> Change<T>(Guid actor, Guid org, Guid id, IReadOnlyCollection<Guid> members,
        Func<CorporateGroup, Company, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        var group = await Resolve(actor, org, id, ct);
        return await transactions.ExecuteCompanies(actor, org, members.Append(group.CompanyId).ToArray(), async (companies, token) =>
        {
            var locked = await Resolve(actor, org, id, token);
            if (companies.Values.Any(x => x.TenantId != locked.TenantId)) throw Missing();
            return await operation(locked, companies[locked.CompanyId], token);
        }, ct);
    }

    public Task<GroupOwnership> Ownership(Guid actor, Guid org, Guid id, OwnershipRequest input, CancellationToken ct) =>
        Change(actor, org, id, [input.InvestorId, input.InvesteeId], async (group, parent, token) =>
        {
            if (!Enum.IsDefined(input.Kind) || input.InvestorId == input.InvesteeId || input.InvesteeId == parent.Id ||
                input.Ownership is < 0 or > 100 || input.Voting is < 0 or > 100 || decimal.Round(input.Ownership, 4) != input.Ownership ||
                decimal.Round(input.Voting, 4) != input.Voting || input.EffectiveOn < group.StartsOn || group.RetiredOn <= input.EffectiveOn)
                throw Invalid("Invalid ownership, control or effective date.");
            var history = await History(group.Id, token);
            if (history.Any(x => x.InvesteeId == input.InvesteeId && x.EffectiveOn >= input.EffectiveOn))
                throw Conflict("A new ownership version must follow existing versions for that company.");
            var row = InGroup(group, new GroupOwnership { InvestorId = input.InvestorId, InvesteeId = input.InvesteeId, Kind = input.Kind,
                Ownership = input.Ownership, Voting = input.Voting, EffectiveOn = input.EffectiveOn, ActorId = actor,
                Evidence = AccountingRules.Text(input.Evidence, 1000, "Control evidence") });
            history.Add(row);
            foreach (var date in history.Select(x => x.EffectiveOn).Where(x => x >= row.EffectiveOn).Distinct())
                ResolveOwnership(parent.Id, history, date);
            db.Add(row);
            access.Audit(parent, actor, row.Id, "GroupOwnershipRecorded", row.Evidence);
            return row;
        }, ct);

    public Task<GroupAccount> Account(Guid actor, Guid org, Guid id, GroupAccountRequest input, CancellationToken ct) =>
        Change(actor, org, id, [], async (group, parent, token) =>
        {
            if (!Enum.IsDefined(input.Classification) || input.Cash && input.Classification != AccountClass.Asset)
                throw Invalid("Group cash accounts must be classified as assets.");
            if (input.ParentId is { } ancestor)
            {
                var account = await db.Set<GroupAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.GroupId == id && x.Id == ancestor, token);
                if (account is null || account.Classification != input.Classification) throw Invalid("Invalid group account parent.");
            }
            var code = Code(input.Code);
            if (await db.Set<GroupAccount>().AnyAsync(x => x.GroupId == id && x.Code == code, token))
                throw Conflict("Group account code is already registered.");
            var row = InGroup(group, new GroupAccount { Code = code, Name = AccountingRules.Text(input.Name, 160, "Account name"),
                Classification = input.Classification, Cash = input.Cash, ParentId = input.ParentId });
            db.Add(row);
            access.Audit(parent, actor, row.Id, "GroupAccountRegistered");
            return row;
        }, ct);

    public Task<GroupMapping> Map(Guid actor, Guid org, Guid id, MappingRequest input, CancellationToken ct) =>
        Change(actor, org, id, [input.MemberId], async (group, parent, token) =>
        {
            await RequireMember(group, input.MemberId, input.EffectiveOn, token);
            var source = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == input.MemberId && x.Id == input.LedgerAccountId, token);
            var target = await db.Set<GroupAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.GroupId == id && x.Id == input.GroupAccountId, token);
            if (source is null || !source.IsPosting || target is null || source.Classification != target.Classification)
                throw Invalid("Map a posting account to a matching group classification.");
            var treasury = await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == input.MemberId && x.LedgerAccountId == source.Id, token);
            var cash = treasury || source.Code == "1100" && source.Name == "Cash";
            if (target.Cash != cash)
                throw Invalid("Map registered treasury accounts and the standard cash account to a group cash account.");
            if (await db.Set<GroupMapping>().AnyAsync(x => x.GroupId == id && x.MemberId == input.MemberId &&
                x.LedgerAccountId == source.Id && x.EffectiveOn == input.EffectiveOn, token))
                throw Conflict("An account mapping already exists for this date.");
            var row = InGroup(group, new GroupMapping { MemberId = input.MemberId, LedgerAccountId = source.Id, GroupAccountId = target.Id,
                EffectiveOn = input.EffectiveOn, PreparedBy = actor, Evidence = AccountingRules.Text(input.Evidence, 1000, "Mapping evidence") });
            db.Add(row);
            access.Audit(parent, actor, row.Id, "GroupMappingPrepared", row.Evidence);
            return row;
        }, ct);

    public Task<GroupMapping> ApproveMapping(Guid actor, Guid org, Guid id, Guid mapping, CancellationToken ct) =>
        Change(actor, org, id, [], async (_, parent, token) =>
        {
            var row = await db.Set<GroupMapping>().SingleOrDefaultAsync(x => x.GroupId == id && x.Id == mapping, token) ?? throw Missing();
            if (row.ApprovedBy == actor) return row;
            if (row.ApprovedBy.HasValue || row.PreparedBy == actor) throw Conflict("An independent owner must approve a draft mapping.");
            row.ApprovedBy = actor;
            access.Audit(parent, actor, row.Id, "GroupMappingApproved");
            return row;
        }, ct);

    public async Task<object> Settings(Guid actor, Guid org, Guid id, DateOnly at, CancellationToken ct)
    {
        var group = await Resolve(actor, org, id, ct);
        var history = await History(id, ct);
        return new { Group = group, Ownership = history, AtDate = ResolveOwnership(group.CompanyId, history, at),
            Accounts = await db.Set<GroupAccount>().AsNoTracking().Where(x => x.GroupId == id).OrderBy(x => x.Code).ToListAsync(ct),
            Mappings = await db.Set<GroupMapping>().AsNoTracking().Where(x => x.GroupId == id).OrderBy(x => x.EffectiveOn).ToListAsync(ct),
            Partners = await db.Set<IntercompanyPolicy>().AsNoTracking().Where(x => x.GroupId == id).OrderBy(x => x.EffectiveOn).ToListAsync(ct) };
    }

    internal Task<List<GroupOwnership>> History(Guid id, CancellationToken ct) =>
        db.Set<GroupOwnership>().AsNoTracking().Where(x => x.GroupId == id).OrderBy(x => x.EffectiveOn).ThenBy(x => x.Id).ToListAsync(ct);

    internal async Task RequireMember(CorporateGroup group, Guid company, DateOnly date, CancellationToken ct)
    {
        if (date < group.StartsOn || group.RetiredOn <= date) throw Conflict("The group is outside its effective dates.");
        if (company != group.CompanyId && !ResolveOwnership(group.CompanyId, await History(group.Id, ct), date)
                .Any(x => x.Edge.InvesteeId == company && x.Edge.Kind == InvestmentKind.Controlled))
            throw Conflict("The company is outside the controlled group at the accounting date.");
    }

    public static IReadOnlyList<EffectiveOwnership> ResolveOwnership(Guid parent, IEnumerable<GroupOwnership> rows, DateOnly date)
    {
        var selected = rows.Where(x => x.EffectiveOn <= date).GroupBy(x => x.InvesteeId)
            .Select(x => x.MaxBy(y => y.EffectiveOn)!).Where(x => x.Kind != InvestmentKind.Excluded)
            .ToDictionary(x => x.InvesteeId);
        var result = new List<EffectiveOwnership>();
        foreach (var edge in selected.Values.OrderBy(x => x.InvesteeId))
        {
            var current = edge;
            var effective = edge.Ownership;
            var seen = new HashSet<Guid> { edge.InvesteeId };
            while (current.InvestorId != parent)
            {
                if (!seen.Add(current.InvestorId) || !selected.TryGetValue(current.InvestorId, out var investor) ||
                    investor.Kind != InvestmentKind.Controlled) throw Conflict("Ownership must be acyclic and rooted in the group through controlled investors.");
                current = investor;
                effective = effective * current.Ownership / 100m;
            }
            result.Add(new(edge, effective));
        }
        return result;
    }

    internal static T InGroup<T>(CorporateGroup group, T row) where T : IGroupEvidence
    {
        row.GroupId = group.Id;
        row.TenantId = group.TenantId;
        row.OrganizationId = group.OrganizationId;
        row.CompanyId = group.CompanyId;
        return row;
    }

    private static string Code(string value) => AccountingRules.Text(value, 40, "Code").ToUpperInvariant();
}

public sealed record EffectiveOwnership(GroupOwnership Edge, decimal EconomicPercent);
