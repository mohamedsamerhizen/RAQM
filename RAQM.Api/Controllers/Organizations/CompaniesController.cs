using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.Organizations;
using RAQM.Api.Helpers;
using RAQM.Api.Interfaces.Organizations;
namespace RAQM.Api.Controllers.Organizations;

[ApiController, Authorize]
[Route("api/v1/organizations/{orgId:guid}/companies")]
[Tags("Organizations")]
[ProducesResponseType(StatusCodes.Status200OK)]
public sealed class CompaniesController(IOrganizationService service) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> List(Guid orgId, CancellationToken ct) =>
        Results.Ok((await service.List(CurrentUser.Id(User), orgId, ct)).Select(CompanyResponse.From).ToList());

    [HttpPost]
    public async Task<IResult> Create(Guid orgId, [FromBody] CompanyRequest request, CancellationToken ct)
    {
        var company = await service.CreateCompany(CurrentUser.Id(User), orgId, request, ct);
        return Results.Created($"/api/v1/organizations/{orgId}/companies/{company.Id}", CompanyResponse.From(company));
    }

    [HttpGet("{companyId:guid}")]
    public async Task<IResult> Get(Guid orgId, Guid companyId, CancellationToken ct) =>
        Results.Ok(CompanyResponse.From(await service.Get(CurrentUser.Id(User), orgId, companyId, ct)));

    [HttpPut("{companyId:guid}")]
    public async Task<IResult> Update(Guid orgId, Guid companyId, [FromBody] UpdateCompanyRequest request, CancellationToken ct) =>
        Results.Ok(CompanyResponse.From(await service.Update(CurrentUser.Id(User), orgId, companyId, request, ct)));

    [HttpPut("{companyId:guid}/members")]
    public async Task<IResult> Grant(Guid orgId, Guid companyId, [FromBody] GrantCompanyRequest request, CancellationToken ct)
    {
        await service.Grant(CurrentUser.Id(User), orgId, companyId, request, ct);
        return Results.NoContent();
    }

    [HttpDelete("{companyId:guid}/members/{memberId:guid}")]
    public async Task<IResult> Revoke(Guid orgId, Guid companyId, Guid memberId, CancellationToken ct)
    {
        await service.Revoke(CurrentUser.Id(User), orgId, companyId, memberId, ct);
        return Results.NoContent();
    }
}
