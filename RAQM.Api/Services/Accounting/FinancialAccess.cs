using Microsoft.EntityFrameworkCore;
using RAQM.Api.Data;
using RAQM.Api.Entities.Accounting;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Organizations;

namespace RAQM.Api.Services.Accounting;
public sealed class FinancialAccess(RaqmDbContext db, IOrganizationService organizations)
{
    public async Task<Company> Resolve(Guid actor, Guid org, Guid company, bool write, bool ownerOnly, CancellationToken ct)
    {
        var result = await organizations.Get(actor, org, company, ct);
        var member = await organizations.Resolve(actor, org, ct);
        if ((ownerOnly && member.Role != OrganizationRole.Owner) || (write && member.Role != OrganizationRole.Owner && !await db.CompanyMembers.AnyAsync(m => m.CompanyId == company && m.TenantId == result.TenantId && m.UserId == actor && m.Role == CompanyRole.Manager, ct)))
            throw new ApiException(403, "Financial operation permission required.", "financial_permission");
        return result;
    }

    public async Task<Company> Lock(Guid actor, Guid org, Guid company, bool ownerOnly, CancellationToken ct)
    {
        _ = await Resolve(actor, org, company, true, ownerOnly, ct);
        await CompanyWriteLock.Acquire(db, company, ct);
        // Recheck after waiting, so a concurrent serialized permission revocation takes effect.
        return await Resolve(actor, org, company, true, ownerOnly, ct);
    }

    public void Audit(Company company, Guid actor, Guid entity, string action, string? reason = null) => db.AccountingAudit.Add(new AccountingAuditEvent { TenantId = company.TenantId, OrganizationId = company.OrganizationId, CompanyId = company.Id, EntityId = entity, ActorId = actor, Action = action, Reason = reason });
}
