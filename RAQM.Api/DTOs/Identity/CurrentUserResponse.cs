namespace RAQM.Api.DTOs.Identity;

public sealed record OrganizationMembershipResponse(Guid OrganizationId, string Role);
public sealed record CurrentUserResponse(Guid UserId, IReadOnlyList<OrganizationMembershipResponse> Organizations);
