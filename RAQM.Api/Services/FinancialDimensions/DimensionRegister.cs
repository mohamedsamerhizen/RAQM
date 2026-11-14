using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Accounting;
using RAQM.Api.DTOs.FinancialDimensions;
using RAQM.Api.Entities.FinancialDimensions;
using RAQM.Api.Entities.FinancialReporting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Accounting;
using RAQM.Api.Interfaces.Accounting;
using RAQM.Api.Services.Accounting;
using static RAQM.Api.Helpers.FinancialDimensions.DimensionMath;

namespace RAQM.Api.Services.FinancialDimensions;

public sealed class DimensionRegister(RaqmDbContext db, FinancialAccess access, IFinancialTransaction transactions, TimeProvider clock)
{
    internal static T Scoped<T>(Company company, T row) where T : FinancialEvidence
    {
        row.CompanyId = company.Id;
        row.TenantId = company.TenantId;
        row.OrganizationId = company.OrganizationId;
        return row;
    }
    internal static string Code(string input) => AccountingRules.Text(input, 40, "Code").ToUpperInvariant();
    internal static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public Task<DimensionDefinition> Definition(Guid actor, Guid org, Guid company, DefinitionRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var code = Code(request.Code);
            if (!Enum.IsDefined(request.Kind)) throw Bad("Select a valid dimension category.");
            if (await db.DimensionDefinitions.CountAsync(x => x.CompanyId == company, token) >= 20)
                throw Conflict("At most 20 company dimension definitions are supported.");
            if (await db.DimensionDefinitions.AnyAsync(x => x.CompanyId == company && (x.Code == code ||
                (request.Kind != DimensionKind.Custom && x.Kind == request.Kind)), token))
                throw Conflict("Dimension code or built-in category already exists.");
            var row = Scoped(scope, new DimensionDefinition { Kind = request.Kind, Code = code,
                Name = AccountingRules.Text(request.Name, 160, "Dimension name"), Description = AccountingRules.Text(request.Description, 1000, "Description") });
            db.DimensionDefinitions.Add(row);
            access.Audit(scope, actor, row.Id, "DimensionDefinitionCreated", code);
            return row;
        }, ct);

    public Task<DimensionValue> Value(Guid actor, Guid org, Guid company, ValueRequest request, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            return await AddValue(scope, actor, request, false, token);
        }, ct);

    internal async Task<DimensionValue> AddValue(Company scope, Guid actor, ValueRequest input, bool project, CancellationToken ct)
    {
        var definition = await db.DimensionDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == scope.Id && x.Id == input.DefinitionId, ct)
            ?? throw Missing("Dimension definition was not found.");
        if ((definition.Kind == DimensionKind.Project) != project || input.EffectiveDate.Year < 1900)
            throw Bad("Use the matching project or dimension workflow and a valid effective date.");
        var code = Code(input.Code);
        if (await db.DimensionValues.AnyAsync(x => x.CompanyId == scope.Id && x.DefinitionId == definition.Id && x.Code == code, ct))
            throw Conflict("Dimension value code already exists.");
        if (input.ParentId is { } parent)
        {
            var row = await Find(scope.Id, parent, ct);
            if (row.DefinitionId != definition.Id || !Active(row, input.EffectiveDate))
                throw Bad("Parent must be effective and from the same dimension.");
        }
        if ((input.DepartmentId.HasValue || input.BranchId.HasValue) && definition.Kind is not (DimensionKind.CostCenter or DimensionKind.Project) ||
            input.CostCenterId.HasValue && !project)
            throw Bad("Organizational links apply to cost centers or projects.");
        foreach (var (id, kind) in new[] { (input.DepartmentId, DimensionKind.Department), (input.BranchId, DimensionKind.Branch),
                     (input.CostCenterId, DimensionKind.CostCenter) })
            if (id.HasValue)
            {
                var row = await Find(scope.Id, id.Value, ct);
                if (!Active(row, input.EffectiveDate) || !await db.DimensionDefinitions.AnyAsync(x =>
                    x.CompanyId == scope.Id && x.Id == row.DefinitionId && x.Kind == kind, ct))
                    throw Bad("Organizational link must have the expected dimension category and effective date.");
            }
        if (input.ManagerId is { } manager) await access.Resolve(manager, scope.OrganizationId, scope.Id, false, false, ct);
        var result = Scoped(scope, new DimensionValue { DefinitionId = definition.Id, Code = code, ParentId = input.ParentId,
            Name = AccountingRules.Text(input.Name, 160, "Value name"), Classification = AccountingRules.Text(input.Classification, 160, "Classification"),
            EffectiveDate = input.EffectiveDate, BranchId = input.BranchId, DepartmentId = input.DepartmentId, CostCenterId = input.CostCenterId,
            ManagerId = input.ManagerId });
        db.DimensionValues.Add(result);
        access.Audit(scope, actor, result.Id, "DimensionValueCreated", code);
        return result;
    }

    public Task<DimensionValue> Deactivate(Guid actor, Guid org, Guid company, Guid id, DimensionDeactivate input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            var row = await db.DimensionValues.SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, token)
                ?? throw Missing("Dimension value was not found.");
            if (input.InactiveOn < Today(clock) || input.InactiveOn <= row.EffectiveDate || row.InactiveOn.HasValue ||
                await db.FinancialProjects.AnyAsync(x => x.CompanyId == company && x.ValueId == id, token))
                throw Conflict("Deactivation is prospective and projects use their lifecycle.");
            row.InactiveOn = input.InactiveOn;
            access.Audit(scope, actor, row.Id, "DimensionValueDeactivated", AccountingRules.Text(input.Reason, 500, "Deactivation reason"));
            return row;
        }, ct);

    public Task<DimensionRule> Rule(Guid actor, Guid org, Guid company, DimensionRuleRequest input, CancellationToken ct) =>
        transactions.Execute(actor, org, company, async (scope, token) =>
        {
            await access.Resolve(actor, org, company, true, true, token);
            if (!Enum.IsDefined(input.Mode) || input.EffectiveDate.Year < 1900 ||
                (input.Mode == DimensionMode.Fixed && !input.DefaultValueId.HasValue) ||
                (input.Mode == DimensionMode.Prohibited && (input.DefaultValueId.HasValue || input.AllowedValues?.Count > 0)) ||
                !await db.DimensionDefinitions.AnyAsync(x => x.CompanyId == company && x.Id == input.DefinitionId, token))
                throw Bad("Invalid dimension policy.");
            if (input.AccountId.HasValue && !await db.Accounts.AnyAsync(x => x.CompanyId == company && x.Id == input.AccountId && x.IsPosting, token))
                throw Bad("Rule account must be a company posting account.");
            var allowed = (input.AllowedValues ?? []).Distinct().Order().ToArray();
            if (allowed.Length > 200 || (allowed.Length > 0 && input.DefaultValueId is { } candidate && !allowed.Contains(candidate)))
                throw Bad("Default must be within the permitted values.");
            foreach (var id in allowed.Concat(input.DefaultValueId is { } d ? [d] : Array.Empty<Guid>()).Distinct())
            {
                var value = await Find(company, id, token);
                if (value.DefinitionId != input.DefinitionId || !Active(value, input.EffectiveDate)) throw Bad("Policy contains an ineligible dimension value.");
            }
            if (await db.DimensionRules.AnyAsync(x => x.CompanyId == company && x.AccountId == input.AccountId &&
                x.DefinitionId == input.DefinitionId && x.EffectiveDate >= input.EffectiveDate, token))
                throw Conflict("Policy revisions must advance the effective date.");
            var row = Scoped(scope, new DimensionRule { DefinitionId = input.DefinitionId, AccountId = input.AccountId, EffectiveDate = input.EffectiveDate,
                DefaultValueId = input.DefaultValueId, Mode = input.Mode, AllowedJson = JsonSerializer.Serialize(allowed),
                Reason = AccountingRules.Text(input.Reason, 500, "Policy reason") });
            db.DimensionRules.Add(row);
            access.Audit(scope, actor, row.Id, "DimensionRulePublished", row.Reason);
            return row;
        }, ct);

    internal async Task<DimensionValue> Find(Guid company, Guid id, CancellationToken ct) =>
        await db.DimensionValues.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == company && x.Id == id, ct)
            ?? throw Missing("Dimension value was not found in this company.");

    public async Task<PageResult<DimensionValue>> Values(Guid actor, Guid org, Guid company, Guid? definition, string? search, int page, int size, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        AccountingRules.Page(page, size);
        AccountingRules.OptionalText(search, 100);
        var query = db.DimensionValues.AsNoTracking().Where(x => x.CompanyId == company);
        if (definition.HasValue) query = query.Where(x => x.DefinitionId == definition);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => EF.Functions.ILike(x.Code, "%" + search + "%") ||
            EF.Functions.ILike(x.Name, "%" + search + "%"));
        return new(await query.OrderBy(x => x.Code).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct),
            await query.CountAsync(ct), page, size);
    }

    public async Task<object> Settings(Guid actor, Guid org, Guid company, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return new
        {
            Definitions = await db.DimensionDefinitions.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.Code).ToListAsync(ct),
            Rules = await db.DimensionRules.AsNoTracking().Where(x => x.CompanyId == company).OrderBy(x => x.EffectiveDate).ThenBy(x => x.Id).ToListAsync(ct)
        };
    }

    public async Task<IReadOnlyList<AuditResponse>> Audit(Guid actor, Guid org, Guid company, Guid id, CancellationToken ct)
    {
        await access.Resolve(actor, org, company, false, false, ct);
        return await db.AccountingAudit.AsNoTracking().Where(x => x.CompanyId == company && x.EntityId == id && x.Action.StartsWith("Dimension"))
            .OrderBy(x => x.At).ThenBy(x => x.Id).Select(x => new AuditResponse(x.Id, x.EntityId, x.Action, x.ActorId, x.At, x.Reason)).ToListAsync(ct);
    }
}
