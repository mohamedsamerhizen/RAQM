using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Helpers.Organizations;
using RAQM.Api.Interfaces.Organizations;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Services.AuditEvidence;
using RAQM.Api.Helpers.Accounting;
namespace RAQM.Api.Services.Organizations;
public sealed class OrganizationService(RaqmDbContext db, GovernanceAudit? governanceAudit = null) : IOrganizationService
{
    public static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 160)
            throw new ApiException(400, "Name must contain 1 to 160 characters.");
    }
    public static void ValidateSettings(string? currency, int month, int day)
    {
        CurrencyPolicy.Units(currency);
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2001, month))
            throw new ApiException(400, "Fiscal start must be a valid recurring date (February 29 is not supported).");
    }
    public async Task<Organization> Create(Guid userId, string name, CancellationToken ct)
    {
        ValidateName(name);
        var tenant = new Tenant();
        var organization = new Organization { TenantId = tenant.Id, Name = name.Trim(), CreatedBy = userId };
        db.Tenants.Add(tenant); db.Organizations.Add(organization);
        db.OrganizationMembers.Add(new OrganizationMember { TenantId = tenant.Id, OrganizationId = organization.Id, UserId = userId, Role = OrganizationRole.Owner });
        await db.SaveChangesAsync(ct);
        return organization;
    }
    public async Task<TenantContext> Resolve(Guid userId, Guid orgId, CancellationToken ct)
    {
        var member = await db.OrganizationMembers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == orgId && x.UserId == userId, ct)
            ?? throw new ApiException(404, "Organization not found.");
        return new TenantContext(member.TenantId, member.OrganizationId, userId, member.Role);
    }
    public async Task<Organization> GetOrganization(Guid userId, Guid orgId, CancellationToken ct)
    {
        var context = await Resolve(userId, orgId, ct);
        return await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == context.OrganizationId && x.TenantId == context.TenantId, ct);
    }
    private static void RequireOwner(TenantContext context)
    {
        if (context.Role != OrganizationRole.Owner) throw new ApiException(403, "Organization owner permission required.");
    }
    public async Task AddMember(Guid userId, Guid orgId, AddMemberRequest request, CancellationToken ct)
    {
        var context = await Resolve(userId, orgId, ct); RequireOwner(context);
        if (!await db.Users.AnyAsync(x => x.Id == request.UserId, ct)) throw new ApiException(400, "User does not exist.");
        if (await db.OrganizationMembers.AnyAsync(x => x.OrganizationId == orgId && x.UserId == request.UserId, ct))
            throw new ApiException(409, "User is already a member.");
        db.OrganizationMembers.Add(new OrganizationMember { TenantId = context.TenantId, OrganizationId = orgId, UserId = request.UserId, Role = OrganizationRole.Member });
        await db.SaveChangesAsync(ct);
    }
    private IQueryable<Company> Accessible(TenantContext context) =>
        db.Companies.Where(x => x.TenantId == context.TenantId && x.OrganizationId == context.OrganizationId &&
            (context.Role == OrganizationRole.Owner || db.CompanyMembers.Any(m =>
                m.TenantId == context.TenantId && m.CompanyId == x.Id && m.UserId == context.UserId)));
    public async Task<List<Company>> List(Guid userId, Guid orgId, CancellationToken ct) =>
        await Accessible(await Resolve(userId, orgId, ct)).AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
    public async Task<Company> Get(Guid userId, Guid orgId, Guid companyId, CancellationToken ct) =>
        await Accessible(await Resolve(userId, orgId, ct)).AsNoTracking().SingleOrDefaultAsync(x => x.Id == companyId, ct)
            ?? throw new ApiException(404, "Company not found.");
    public async Task<Company> CreateCompany(Guid userId, Guid orgId, CompanyRequest request, CancellationToken ct)
    {
        var context = await Resolve(userId, orgId, ct); RequireOwner(context);
        ValidateName(request.Name); ValidateSettings(request.BaseCurrency, request.FiscalYearStartMonth, request.FiscalYearStartDay);
        if (string.IsNullOrWhiteSpace(request.RegistrationNumber) || request.RegistrationNumber.Trim().Length > 80)
            throw new ApiException(400, "Registration number must contain 1 to 80 characters.");
        var company = new Company
        {
            TenantId = context.TenantId,
            OrganizationId = orgId,
            Name = request.Name.Trim(),
            RegistrationNumber = request.RegistrationNumber.Trim().ToUpperInvariant(),
            BaseCurrency = request.BaseCurrency,
            FiscalYearStartMonth = request.FiscalYearStartMonth,
            FiscalYearStartDay = request.FiscalYearStartDay,
            CreatedBy = userId,
            UpdatedBy = userId
        };
        db.Companies.Add(company); await db.SaveChangesAsync(ct); return company;
    }
    public async Task<Company> Update(Guid userId, Guid orgId, Guid companyId, UpdateCompanyRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var context = await Resolve(userId, orgId, ct);
        _ = await Accessible(context).AsNoTracking().SingleOrDefaultAsync(x => x.Id == companyId, ct)
            ?? throw new ApiException(404, "Company not found.");
        await CompanyWriteLock.Acquire(db, companyId, ct);
        var company = await Accessible(context).SingleOrDefaultAsync(x => x.Id == companyId, ct)
            ?? throw new ApiException(404, "Company not found.");
        if (context.Role != OrganizationRole.Owner && !await db.CompanyMembers.AnyAsync(x =>
            x.TenantId == context.TenantId && x.CompanyId == companyId && x.UserId == userId && x.Role == CompanyRole.Manager, ct))
            throw new ApiException(403, "Company settings permission required.");
        ValidateName(request.Name); ValidateSettings(request.BaseCurrency, request.FiscalYearStartMonth, request.FiscalYearStartDay);
        if (company.Version != request.Version) throw new ApiException(409, "Company changed; retrieve its current version.");
        var fiscalChanged = company.FiscalYearStartMonth != request.FiscalYearStartMonth || company.FiscalYearStartDay != request.FiscalYearStartDay;
        var currencyChanged = company.BaseCurrency != request.BaseCurrency;
        if(currencyChanged && (await db.TradingParties.AnyAsync(x=>x.CompanyId==companyId,ct) || await db.TradingAccounts.AnyAsync(x=>x.CompanyId==companyId,ct) ||
            await db.TreasuryAccounts.AnyAsync(x => x.CompanyId == companyId, ct) || await db.AssetCategories.AnyAsync(x => x.CompanyId == companyId, ct) ||
            await db.InventoryPolicies.AnyAsync(x => x.CompanyId == companyId, ct) ||
            await db.FinancialBudgets.AnyAsync(x => x.CompanyId == companyId, ct) ||
            await db.BudgetPolicies.AnyAsync(x => x.CompanyId == companyId, ct) ||
            await db.FxConfigurations.AnyAsync(x => x.CompanyId == companyId, ct) ||
            await db.ForecastVersions.AnyAsync(x => x.CompanyId == companyId, ct)))
            throw new ApiException(409,"Financial configuration freezes company currency.","company_trading_locked");
        if ((fiscalChanged && await db.FiscalYears.AnyAsync(x => x.CompanyId == companyId, ct)) ||
            ((currencyChanged || fiscalChanged) && await db.Journals.AnyAsync(x => x.CompanyId == companyId && x.State == JournalState.Posted, ct)))
            throw new ApiException(409, "Currency/fiscal settings are frozen by accounting configuration or posted activity.", "company_accounting_locked");
        company.Name = request.Name.Trim(); company.BaseCurrency = request.BaseCurrency;
        company.FiscalYearStartMonth = request.FiscalYearStartMonth; company.FiscalYearStartDay = request.FiscalYearStartDay;
        company.UpdatedBy = userId; company.UpdatedAt = DateTimeOffset.UtcNow; company.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return company;
    }
    public async Task Grant(Guid userId, Guid orgId, Guid companyId, GrantCompanyRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var context = await Resolve(userId, orgId, ct); RequireOwner(context);
        var company = await Get(userId, orgId, companyId, ct);
        await CompanyWriteLock.Acquire(db, companyId, ct);
        if (!Enum.IsDefined(request.Role)) throw new ApiException(400, "Invalid company role.");
        if (!await db.OrganizationMembers.AnyAsync(x => x.TenantId == context.TenantId && x.OrganizationId == orgId && x.UserId == request.UserId, ct))
            throw new ApiException(400, "Organization membership required before company access.");
        var member = await db.CompanyMembers.SingleOrDefaultAsync(x => x.CompanyId == companyId && x.UserId == request.UserId, ct);
        var priorRole = member?.Role;
        if (member is null) db.CompanyMembers.Add(new CompanyMember
        {
            TenantId = context.TenantId,
            OrganizationId = orgId,
            CompanyId = companyId,
            UserId = request.UserId,
            Role = request.Role
        });
        else member.Role = request.Role;
        if (governanceAudit is not null && priorRole != request.Role)
            await governanceAudit.Append(company, userId, new("access", "CompanyRoleAssigned", companyId, "Company membership role changed",
                Subject: request.UserId, Prior: priorRole?.ToString(), Next: request.Role.ToString()), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
    public async Task Revoke(Guid userId, Guid orgId, Guid companyId, Guid memberId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var context = await Resolve(userId, orgId, ct); RequireOwner(context);
        var company = await Get(userId, orgId, companyId, ct);
        await CompanyWriteLock.Acquire(db, companyId, ct);
        var removed = await db.CompanyMembers.Where(x => x.TenantId == context.TenantId && x.CompanyId == companyId && x.UserId == memberId).ExecuteDeleteAsync(ct);
        if (governanceAudit is not null && removed != 0)
        {
            await governanceAudit.Append(company, userId, new("access", "CompanyRoleRevoked", companyId, "Company membership role removed", Subject: memberId), ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}


