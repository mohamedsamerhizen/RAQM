using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.Inventory;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Entities.Trading;
using RAQM.Api.Helpers.Accruals;
using RAQM.Api.Helpers.FinancialReporting;
using static RAQM.Api.Helpers.FinancialDimensions.DimensionMath;
using static RAQM.Api.Services.FinancialDimensions.DimensionRegister;

namespace RAQM.Api.Services.FinancialDimensions;

public sealed partial class DimensionPosting(RaqmDbContext db, DimensionBindings bindings)
{
    public async Task<IReadOnlyDictionary<int, EffectiveDimension[]>> Prepare(Guid actor, Company company, PostingRequest request, CancellationToken ct)
    {
        if (request.SourceModule == FinancialMath.YearEndSource) return new Dictionary<int, EffectiveDimension[]>();
        if (request.ReversesJournalId is { } reverse)
        {
            if (await (from allocation in db.CostAllocationExecutions join line in db.JournalLines on allocation.SourceLineId equals line.Id
                where allocation.CompanyId == company.Id && line.CompanyId == company.Id && allocation.State == AllocationState.Executed &&
                    (line.JournalId == reverse || allocation.JournalId == reverse) select allocation.Id).AnyAsync(ct))
                throw Conflict("Correct the active allocation before reversing its original source.");
            return await Original(company.Id, reverse, DimensionOrigin.Reversal, ct);
        }
        var definitions = await db.DimensionDefinitions.AsNoTracking().Where(x => x.CompanyId == company.Id).ToListAsync(ct);
        if (definitions.Count == 0) return new Dictionary<int, EffectiveDimension[]>();
        var (assigned, source) = await Assignments(actor, company.Id, request, ct);
        if (request.SourceModule == "Dimensions.Correction") return source;
        var rules = (await db.DimensionRules.AsNoTracking().Where(x => x.CompanyId == company.Id &&
            x.EffectiveDate <= request.AccountingDate).ToListAsync(ct)).GroupBy(x => (x.AccountId, x.DefinitionId))
            .ToDictionary(x => x.Key, x => x.MaxBy(v => v.EffectiveDate)!);
        var values = await db.DimensionValues.AsNoTracking().Where(x => x.CompanyId == company.Id).ToDictionaryAsync(x => x.Id, ct);
        var projects = await ProjectStatuses(company.Id, request.AccountingDate, ct);
        var kinds = definitions.ToDictionary(x => x.Id, x => x.Kind);
        var party = await SalesParty(company.Id, request, ct);
        var result = new Dictionary<int, EffectiveDimension[]>();
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var ordinal = i + 1;
            var effective = new List<EffectiveDimension>();
            var explicitValues = assigned.GetValueOrDefault(ordinal) ?? [];
            var inherited = source.GetValueOrDefault(ordinal) ?? [];
            if (request.SourceModule == "Inventory.CustomerReturn" && source.ContainsKey(ordinal))
            {
                if (explicitValues.Any(tag => !inherited.Any(original =>
                    original.DefinitionId == tag.DefinitionId && original.ValueId == tag.ValueId)))
                    throw Conflict("A return must preserve its original issue attribution.");
                if (inherited.Length > 0) result.Add(ordinal, inherited);
                continue;
            }
            var account = request.Lines[i].AccountId;
            foreach (var definition in definitions)
            {
                var direct = explicitValues.SingleOrDefault(x => x.DefinitionId == definition.Id);
                var fallback = inherited.SingleOrDefault(x => x.DefinitionId == definition.Id);
                var selection = direct ?? fallback;
                var value = Resolve(definition.Id, selection is null ? null : new(definition.Id, selection.ValueId),
                    rules.GetValueOrDefault((account, definition.Id)), rules.GetValueOrDefault((null, definition.Id)),
                    selection?.BindingId, selection?.Origin ?? DimensionOrigin.Source);
                if (value is not null) effective.Add(value);
            }
            Validate(effective, values, projects, kinds, request.AccountingDate, party);
            if (request.SourceModule == "Dimensions.Allocation" &&
                !effective.Select(x => (x.DefinitionId, x.ValueId)).OrderBy(x => x.DefinitionId)
                    .SequenceEqual(explicitValues.Select(x => (x.DefinitionId, x.ValueId)).OrderBy(x => x.DefinitionId)))
                throw Conflict("Account defaults would change the approved allocation dimensions.");
            if (effective.Count > 0) result.Add(ordinal, effective.ToArray());
        }
        return result;
    }

    internal static void Validate(IEnumerable<EffectiveDimension> tags, IReadOnlyDictionary<Guid, DimensionValue> values,
        IReadOnlyDictionary<Guid, (ProjectState State, Guid? Customer)> projects, IReadOnlyDictionary<Guid, DimensionKind> kinds,
        DateOnly date, Guid? salesParty)
    {
        var assigned = tags.Where(x => x.ValueId.HasValue).ToArray();
        var category = assigned.Where(x => kinds[x.DefinitionId] != DimensionKind.Custom)
            .ToDictionary(x => kinds[x.DefinitionId], x => x.ValueId!.Value);
        foreach (var tag in assigned)
        {
            if (!values.TryGetValue(tag.ValueId!.Value, out var value) || value.DefinitionId != tag.DefinitionId || !Active(value, date))
                throw Conflict("The selected dimension value is outside its company, definition or effective dates.");
            if (kinds[tag.DefinitionId] == DimensionKind.Project && (!projects.TryGetValue(value.Id, out var project) ||
                project.State != ProjectState.Active || (salesParty.HasValue && project.Customer != salesParty)))
                throw Conflict("Project attribution requires an active project and matching sales customer where applicable.");
            if (Mismatch(DimensionKind.Department, value.DepartmentId) || Mismatch(DimensionKind.Branch, value.BranchId) ||
                Mismatch(DimensionKind.CostCenter, value.CostCenterId))
                throw Conflict("Cost center, department, branch and project selections conflict.");
        }
        bool Mismatch(DimensionKind kind, Guid? expected) => expected.HasValue && category.TryGetValue(kind, out var actual) && actual != expected;
    }

    internal async Task<Dictionary<Guid, (ProjectState State, Guid? Customer)>> ProjectStatuses(Guid company, DateOnly date, CancellationToken ct)
    {
        var events = await (from project in db.FinancialProjects join e in db.FinancialProjectEvents on project.Id equals e.ProjectId
            where project.CompanyId == company && e.CompanyId == company && e.Date <= date
            select new { project.ValueId, project.CustomerId, e.State, e.Number }).ToListAsync(ct);
        return events.GroupBy(x => x.ValueId).Select(x => x.MaxBy(y => y.Number)!)
            .ToDictionary(x => x.ValueId, x => (x.State, x.CustomerId));
    }

    public async Task CheckPurchase(Guid company, ProcurementDocument document, DateOnly date, CancellationToken ct)
    {
        var ids = document.Lines.Select(x => x.Id).ToArray();
        var rows = await db.DimensionBindings.AsNoTracking().Where(x => x.CompanyId == company && x.Source == DimensionSource.Procurement &&
            ids.Contains(x.SourceId) && x.SourceVersion == document.Version).ToListAsync(ct);
        if (rows.Count == 0) return;
        var values = await db.DimensionValues.AsNoTracking().Where(x => x.CompanyId == company).ToDictionaryAsync(x => x.Id, ct);
        var kinds = await db.DimensionDefinitions.AsNoTracking().Where(x => x.CompanyId == company).ToDictionaryAsync(x => x.Id, x => x.Kind, ct);
        var projects = await ProjectStatuses(company, date, ct);
        foreach (var group in rows.GroupBy(x => x.SourceId))
            Validate(Read(group.MaxBy(x => x.Revision)).SelectMany(x => ToTags(x.Values, null, DimensionOrigin.Source)),
                values, projects, kinds, date, null);
    }

    public void Persist(Company company, JournalEntry journal, IReadOnlyDictionary<int, EffectiveDimension[]> plan)
    {
        foreach (var line in journal.Lines)
        {
            if (!plan.TryGetValue(line.Number, out var tags)) continue;
            foreach (var tag in tags)
                db.PostedDimensions.Add(Scoped(company, new PostedDimension { JournalLineId = line.Id, DefinitionId = tag.DefinitionId,
                    ValueId = tag.ValueId, Origin = tag.Origin, RuleId = tag.RuleId, BindingId = tag.BindingId }));
        }
    }

    private async Task<Dictionary<int, EffectiveDimension[]>> Original(Guid company, Guid journal, DimensionOrigin origin, CancellationToken ct)
    {
        var rows = await (from dimension in db.PostedDimensions.AsNoTracking()
            join line in db.JournalLines.AsNoTracking() on dimension.JournalLineId equals line.Id
            where dimension.CompanyId == company && line.CompanyId == company && line.JournalId == journal
            select new { line.Number, dimension.DefinitionId, dimension.ValueId, dimension.RuleId, dimension.BindingId }).ToListAsync(ct);
        return rows.GroupBy(x => x.Number).ToDictionary(x => x.Key, x => x.Select(d => new EffectiveDimension(d.DefinitionId,
            d.ValueId, d.RuleId, d.BindingId, d.ValueId.HasValue ? origin : DimensionOrigin.Unassigned)).ToArray());
    }

    private async Task<Guid?> SalesParty(Guid company, PostingRequest request, CancellationToken ct)
    {
        if (request.SourceModule == "AR.Invoice")
            return await db.TradeDocuments.Where(x => x.CompanyId == company && x.Id == request.SourceDocumentId).Select(x => (Guid?)x.PartyId).SingleOrDefaultAsync(ct);
        if (request.SourceModule == "FX.Invoice")
            return await db.FxOperations.Where(x => x.CompanyId == company && x.Id == request.SourceDocumentId).Select(x => x.PartyId).SingleOrDefaultAsync(ct);
        return null;
    }
}
