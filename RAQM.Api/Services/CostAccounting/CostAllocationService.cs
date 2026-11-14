using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Helpers.FinancialReporting;
using RAQM.Api.Helpers.Trading;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using RAQM.Api.Services.FinancialDimensions;
using static RAQM.Api.Helpers.FinancialDimensions.DimensionMath;
using static RAQM.Api.Services.FinancialDimensions.DimensionRegister;

namespace RAQM.Api.Services.CostAccounting;

public sealed class CostAllocationService(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions,
    DimensionPosting dimensions, IPostingEngine engine, TimeProvider clock)
{
    public Task<DimensionAllocation> Prepare(Guid actor, Guid org, Guid company, CostAllocationRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            request = request with { Key = AccountingRules.Text(request.Key, 64, "Allocation key"),
                Reason = AccountingRules.Text(request.Reason, 500, "Allocation evidence") };
            if (request.Targets is null || request.Targets.Count is < 1 or > 100 || request.Targets.Any(x => x is null ||
                decimal.Round(x.Weight, 6) != x.Weight) || request.Targets.Select(x => (x.CostCenterId, x.ProjectValueId)).Distinct().Count() != request.Targets.Count)
                throw Bad("Provide distinct eligible destinations and valid weights.");
            var hash = TradeRules.Hash(request);
            var prior = await db.CostAllocationExecutions.SingleOrDefaultAsync(x => x.CompanyId == company && x.Key == request.Key, token);
            if (prior is not null) return prior.Hash == hash ? prior : throw Conflict("Allocation key identifies a different request.");
            var plan = await Build(scope, request, token);
            var row = Scoped(scope, new DimensionAllocation { SourceLineId = request.SourceLineId, Date = request.Date,
                Mode = request.Mode, CreatedBy = actor, Key = request.Key, Hash = hash, InputJson = JsonSerializer.Serialize(request),
                PlanJson = JsonSerializer.Serialize(plan) });
            db.CostAllocationExecutions.Add(row);
            access.Audit(scope, actor, row.Id, "DimensionAllocationPrepared", request.Reason);
            return row;
        }, ct);

    public Task<DimensionAllocation> Change(Guid actor, Guid org, Guid company, Guid id, string action, AllocationAction input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var row = await Find(company, id, token);
            if (action == "execute" && row.State == AllocationState.Executed) return row;
            if (action == "correct" && row.State == AllocationState.Corrected && row.CorrectedOn == input.CorrectionDate) return row;
            if (row.Version != input.Version) throw Conflict("The allocation changed.");
            var reason = AccountingRules.Text(input.Reason, 500, "Action reason");
            if (action == "cancel" && row.State is AllocationState.Draft or AllocationState.Approved) row.State = AllocationState.Cancelled;
            else if (action == "approve" && row.State == AllocationState.Draft)
            {
                if (actor == row.CreatedBy) throw Conflict("An independent Owner must approve allocations.");
                await Recheck(scope, row, token);
                row.ApprovedBy = actor;
                row.State = AllocationState.Approved;
            }
            else if (action == "execute" && row.State == AllocationState.Approved)
            {
                var plan = await Recheck(scope, row, token);
                if (await db.CostAllocationExecutions.AnyAsync(x => x.CompanyId == company && x.SourceLineId == row.SourceLineId &&
                    x.State == AllocationState.Executed, token)) throw Conflict("The source already has an active allocation.");
                row.PostedBy = actor;
                await db.SaveChangesAsync(token);
                if (row.Mode == AllocationMode.Financial)
                    row.JournalId = await Post(scope, actor, row, "Dimensions.Allocation", row.Date, plan.Lines, reason, input.AllowBackdated, token);
                row.State = AllocationState.Executed;
            }
            else if (action == "correct" && row.State == AllocationState.Executed && input.CorrectionDate is { } date)
            {
                if (date < row.Date || date > Today(clock)) throw Bad("Use a correction date from execution through today.");
                if (row.Mode == AllocationMode.Financial)
                {
                    var plan = JsonSerializer.Deserialize<AllocationPlan>(row.PlanJson)!;
                    var reversed = plan.Lines.Select(x => x with { Debit = x.Credit, Credit = x.Debit }).ToArray();
                    row.CorrectionJournalId = await Post(scope, actor, row, "Dimensions.Correction", date, reversed, reason, input.AllowBackdated, token);
                }
                row.CorrectedOn = date;
                row.State = AllocationState.Corrected;
            }
            else throw Conflict("Invalid allocation transition.");
            row.Version = Guid.NewGuid();
            access.Audit(scope, actor, id, $"DimensionAllocation:{action}", reason);
            return row;
        }, ct);

    private async Task<Guid> Post(Company scope, Guid actor, DimensionAllocation row, string source, DateOnly date,
        IReadOnlyList<JournalDraftLine> lines, string reason, bool backdated, CancellationToken ct) =>
        (await engine.PostAsync(actor, new(new(scope.TenantId, scope.OrganizationId, scope.Id), source, row.Id,
            $"{source}:{row.Id:N}", date, scope.BaseCurrency, lines, null, date, source, row.Key, backdated, reason), ct)).JournalId;

    private async Task<AllocationPlan> Recheck(Company company, DimensionAllocation row, CancellationToken ct)
    {
        var plan = await Build(company, JsonSerializer.Deserialize<CostAllocationRequest>(row.InputJson)!, ct);
        if (TradeRules.Hash(plan) != TradeRules.Hash(JsonSerializer.Deserialize<AllocationPlan>(row.PlanJson)))
            throw Conflict("Source or destination allocation terms changed.");
        return plan;
    }

    private async Task<AllocationPlan> Build(Company scope, CostAllocationRequest request, CancellationToken ct)
    {
        var company = scope.Id;
        if (!Enum.IsDefined(request.Mode) || request.Date.Year < 1900 || request.Date > Today(clock))
            throw Bad("Use a supported mode and accounting cutoff.");
        if (await db.CostAllocationExecutions.AnyAsync(x => x.CompanyId == company && x.SourceLineId == request.SourceLineId &&
            x.State == AllocationState.Corrected && x.CorrectedOn > request.Date, ct))
            throw Conflict("A replacement allocation cannot precede the correction of the previous allocation.");
        if (request.Basis == AllocationBasis.Statistical)
        {
            AccountingRules.Text(request.MeasurementUnit, 100, "Measurement unit");
            if (request.MeasurementDate is null || request.MeasurementDate > request.Date || request.MeasurementDate.Value.Year < 1900)
                throw Bad("Statistical evidence requires a measurement date through the cutoff.");
            foreach (var target in request.Targets) AccountingRules.Text(target.MeasurementEvidence, 250, "Measurement evidence");
        }
        var source = await (from line in db.JournalLines.AsNoTracking() join journal in db.Journals on line.JournalId equals journal.Id
            join account in db.Accounts on line.AccountId equals account.Id
            where line.CompanyId == company && journal.CompanyId == company && account.CompanyId == company && line.Id == request.SourceLineId &&
                journal.State == JournalState.Posted && line.Debit > 0 && account.Classification == AccountClass.Expense &&
                journal.PostingDate <= request.Date && journal.ReversesJournalId == null && journal.SourceModule != FinancialMath.YearEndSource &&
                journal.SourceModule != "OpeningBalance" && !journal.SourceModule.StartsWith("Dimensions.") &&
                !db.Journals.Any(x => x.CompanyId == company && x.ReversesJournalId == journal.Id && x.State == JournalState.Posted)
            select new { Line = line, journal.PostingDate }).SingleOrDefaultAsync(ct) ?? throw Bad("Select an eligible posted direct expense debit.");
        var period = await db.Periods.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.StartDate <= request.Date &&
            x.EndDate >= request.Date && x.StartDate <= source.PostingDate, ct);
        if (period is null || (request.Mode == AllocationMode.Financial && period.State != PeriodState.Open))
            throw Conflict("Allocation requires the source fiscal period. A financial allocation also requires it to be open.");
        var definitions = await db.DimensionDefinitions.AsNoTracking().Where(x => x.CompanyId == company).ToListAsync(ct);
        var kinds = definitions.ToDictionary(x => x.Id, x => x.Kind);
        var centers = definitions.SingleOrDefault(x => x.Kind == DimensionKind.CostCenter) ?? throw Bad("Configure a cost center dimension.");
        var rows = await db.PostedDimensions.AsNoTracking().Where(x => x.CompanyId == company && x.JournalLineId == source.Line.Id).ToListAsync(ct);
        var tags = rows.Select(x => new DimensionPick(x.DefinitionId, x.ValueId)).OrderBy(x => x.DefinitionId).ToArray();
        var sourceCenter = tags.SingleOrDefault(x => x.DefinitionId == centers.Id)?.ValueId
            ?? throw Bad("The source expense has no trusted cost center.");
        var values = await db.DimensionValues.AsNoTracking().Where(x => x.CompanyId == company).ToDictionaryAsync(x => x.Id, ct);
        var projects = await dimensions.ProjectStatuses(company, request.Date, ct);
        var amounts = Distribute(source.Line.Debit, request.Basis, request.Targets.Select(x => x.Weight).ToArray(),
            CurrencyPolicy.Units(scope.BaseCurrency));
        var lines = new List<JournalDraftLine> { new(source.Line.AccountId, 0, source.Line.Debit, "Source allocation offset") };
        var split = new List<DimensionLine> { new(1, tags) };
        for (var i = 0; i < amounts.Length; i++)
        {
            var target = request.Targets[i];
            if (!values.TryGetValue(target.CostCenterId, out var center) || center.DefinitionId != centers.Id || !Active(center, request.Date) ||
                target.CostCenterId == sourceCenter && target.ProjectValueId is null)
                throw Bad("Destination must be a different effective financial assignment.");
            var destination = tags.ToDictionary(x => x.DefinitionId);
            destination[centers.Id] = new(centers.Id, center.Id);
            Apply(DimensionKind.Department, center.DepartmentId);
            Apply(DimensionKind.Branch, center.BranchId);
            Apply(DimensionKind.Project, target.ProjectValueId);
            var selections = destination.Values.OrderBy(x => x.DefinitionId).ToArray();
            DimensionPosting.Validate(selections.Select(x => new EffectiveDimension(x.DefinitionId, x.ValueId, null, null,
                DimensionOrigin.Allocation)), values, projects, kinds, request.Date, null);
            if (selections.SequenceEqual(tags)) throw Bad("Destination must differ from the source assignment.");
            if (amounts[i] > 0)
            {
                lines.Add(new(source.Line.AccountId, amounts[i], 0, "Destination allocation expense"));
                split.Add(new(lines.Count, selections));
            }

            void Apply(DimensionKind kind, Guid? value)
            {
                if (!value.HasValue) return;
                var definition = definitions.SingleOrDefault(x => x.Kind == kind) ?? throw Bad("Destination dimension is missing.");
                destination[definition.Id] = new(definition.Id, value);
            }
        }
        return new(lines, split, source.Line.Debit);
    }

    public async Task<AllocationPlan> Preview(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return JsonSerializer.Deserialize<AllocationPlan>((await Find(company, id, ct)).PlanJson)!;
    }

    public async Task<PageResult<DimensionAllocation>> List(Guid actor, Guid org, Guid company, int page, int size, CancellationToken ct)
    {
        AccountingRules.Page(page, size);
        await access.Resolve(actor, org, company, false, false, ct);
        var query = db.CostAllocationExecutions.AsNoTracking().Where(x => x.CompanyId == company);
        return new(await query.OrderByDescending(x => x.Date).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct),
            await query.CountAsync(ct), page, size);
    }

    private async Task<DimensionAllocation> Find(Guid company, Guid id, CancellationToken ct) =>
        await db.CostAllocationExecutions.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw Missing("Allocation was not found.");
}
