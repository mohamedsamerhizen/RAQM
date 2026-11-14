using RAQM.Api.Entities.Organizations;
namespace RAQM.Api.Helpers.Organizations;
public sealed record TenantContext(Guid TenantId, Guid OrganizationId, Guid UserId, OrganizationRole Role);

