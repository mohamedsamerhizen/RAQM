using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Entities.Organizations;
using RAQM.Api.Helpers.Organizations;
namespace RAQM.Api.Interfaces.Organizations;

public interface IOrganizationService
{
    Task<Organization> Create(Guid userId, string name, CancellationToken ct);
    Task<TenantContext> Resolve(Guid userId, Guid orgId, CancellationToken ct);
    Task<Organization> GetOrganization(Guid userId, Guid orgId, CancellationToken ct);
    Task AddMember(Guid userId, Guid orgId, AddMemberRequest request, CancellationToken ct);
    Task<List<Company>> List(Guid userId, Guid orgId, CancellationToken ct);
    Task<Company> Get(Guid userId, Guid orgId, Guid companyId, CancellationToken ct);
    Task<Company> CreateCompany(Guid userId, Guid orgId, CompanyRequest request, CancellationToken ct);
    Task<Company> Update(Guid userId, Guid orgId, Guid companyId, UpdateCompanyRequest request, CancellationToken ct);
    Task Grant(Guid userId, Guid orgId, Guid companyId, GrantCompanyRequest request, CancellationToken ct);
    Task Revoke(Guid userId, Guid orgId, Guid companyId, Guid memberId, CancellationToken ct);
}
