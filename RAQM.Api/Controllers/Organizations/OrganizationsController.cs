using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Organizations;
namespace RAQM.Api.Controllers.Organizations;

[ApiController, Authorize]
[Route("api/v1/organizations")]
[Tags("Organizations")]
[ProducesResponseType(StatusCodes.Status200OK)]
public sealed class OrganizationsController(IOrganizationService service) : ControllerBase
{
    [HttpGet("{orgId:guid}")]
    public async Task<IResult> Get(Guid orgId, CancellationToken ct) =>
        Results.Ok(OrganizationResponse.From(await service.GetOrganization(CurrentUser.Id(User), orgId, ct)));

    [HttpPost]
    public async Task<IResult> Create([FromBody] CreateOrganizationRequest request, CancellationToken ct)
    {
        var organization = await service.Create(CurrentUser.Id(User), request.Name, ct);
        return Results.Created($"/api/v1/organizations/{organization.Id}", OrganizationResponse.From(organization));
    }

    [HttpPost("{orgId:guid}/members")]
    public async Task<IResult> AddMember(Guid orgId, [FromBody] AddMemberRequest request, CancellationToken ct)
    {
        await service.AddMember(CurrentUser.Id(User), orgId, request, ct);
        return Results.NoContent();
    }
}
