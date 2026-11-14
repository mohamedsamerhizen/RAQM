using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RAQM.Api.DTOs.Identity;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Identity;
namespace RAQM.Api.Controllers.Identity;

[ApiController]
[Route("api/v1/auth")]
[Tags("Identity")]
[ProducesResponseType(StatusCodes.Status200OK)]
public sealed class AuthController(IIdentityService service) : ControllerBase
{
    [HttpPost("onboard"), EnableRateLimiting("auth")]
    public async Task<IResult> Onboard([FromBody] OnboardRequest request, CancellationToken ct) =>
        Results.Ok(await service.Onboard(request, ct));

    [HttpPost("login"), EnableRateLimiting("auth")]
    public async Task<IResult> Login([FromBody] LoginRequest request, CancellationToken ct) =>
        Results.Ok(await service.Login(request, ct));

    [HttpPost("refresh"), EnableRateLimiting("auth")]
    public async Task<IResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct) =>
        Results.Ok(await service.Refresh(request, ct));

    [HttpPost("logout"), Authorize]
    public async Task<IResult> Logout(CancellationToken ct)
    {
        await service.Logout(CurrentUser.Id(User), Guid.Parse(User.FindFirstValue("sid")!), ct);
        return Results.NoContent();
    }

    [HttpPost("revoke-all"), Authorize]
    public async Task<IResult> RevokeAll(CancellationToken ct)
    {
        await service.RevokeAll(CurrentUser.Id(User), ct);
        return Results.NoContent();
    }
}
