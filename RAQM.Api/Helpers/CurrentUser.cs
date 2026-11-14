using System.Security.Claims;
namespace RAQM.Api.Helpers;
public static class CurrentUser
{
    public static Guid Id(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id : throw new ApiException(401, "Authentication required.");
}


