using RAQM.Api.DTOs.Identity;
namespace RAQM.Api.Interfaces.Identity;

public interface IIdentityService
{
    Task<TokenResponse> Onboard(OnboardRequest request, CancellationToken ct);
    Task<TokenResponse> Login(LoginRequest request, CancellationToken ct);
    Task<TokenResponse> Refresh(RefreshRequest request, CancellationToken ct);
    Task Logout(Guid userId, Guid sessionId, CancellationToken ct);
    Task RevokeAll(Guid userId, CancellationToken ct);
    Task<CurrentUserResponse> GetCurrentUser(Guid userId, CancellationToken ct);
}
