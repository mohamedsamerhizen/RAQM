using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Identity;
namespace RAQM.Api.Controllers.Identity;

[ApiController, Authorize]
[Route("api/v1/me")]
[Tags("Identity")]
public sealed class CurrentUserController(IIdentityService service) : ControllerBase
{
    [HttpGet, ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IResult> Get(CancellationToken ct) =>
        Results.Ok(await service.GetCurrentUser(CurrentUser.Id(User), ct));
}
