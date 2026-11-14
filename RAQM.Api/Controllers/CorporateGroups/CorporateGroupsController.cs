using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RAQM.Api.DTOs.CorporateGroups;
using RAQM.Api.Entities.CorporateGroups;
using RAQM.Api.Helpers;
using RAQM.Api.Services.CorporateGroups;

namespace RAQM.Api.Controllers.CorporateGroups;

[ApiController, Authorize, Tags("Corporate groups")]
[Route("api/v1/organizations/{orgId:guid}/corporate-groups")]
public sealed class CorporateGroupsController(CorporateGroupService groups) : ControllerBase
{
    private Guid Actor => CurrentUser.Id(User);
    [HttpGet]
    public Task<IReadOnlyList<CorporateGroup>> List(Guid orgId, Guid parent, CancellationToken ct, string? search = null,
        int page = 1, int pageSize = 50) => groups.List(Actor, orgId, parent, search, page, pageSize, ct);
    [HttpPost]
    public Task<CorporateGroup> Create(Guid orgId, GroupRequest input, CancellationToken ct) => groups.Create(Actor, orgId, input, ct);
    [HttpGet("{groupId:guid}")]
    public Task<object> Settings(Guid orgId, Guid groupId, DateOnly at, CancellationToken ct) => groups.Settings(Actor, orgId, groupId, at, ct);
    [HttpPost("{groupId:guid}/ownership")]
    public Task<GroupOwnership> Ownership(Guid orgId, Guid groupId, OwnershipRequest input, CancellationToken ct) =>
        groups.Ownership(Actor, orgId, groupId, input, ct);
    [HttpPost("{groupId:guid}/accounts")]
    public Task<GroupAccount> Account(Guid orgId, Guid groupId, GroupAccountRequest input, CancellationToken ct) =>
        groups.Account(Actor, orgId, groupId, input, ct);
    [HttpPost("{groupId:guid}/mappings")]
    public Task<GroupMapping> Map(Guid orgId, Guid groupId, MappingRequest input, CancellationToken ct) =>
        groups.Map(Actor, orgId, groupId, input, ct);
    [HttpPost("{groupId:guid}/mappings/{id:guid}/approve")]
    public Task<GroupMapping> Approve(Guid orgId, Guid groupId, Guid id, CancellationToken ct) => groups.ApproveMapping(Actor, orgId, groupId, id, ct);
    [HttpPost("{groupId:guid}/readers")]
    public Task<CorporateGroup> Reader(Guid orgId, Guid groupId, GroupReaderRequest input, CancellationToken ct) =>
        groups.Reader(Actor, orgId, groupId, input, ct);
    [HttpPost("{groupId:guid}/retire")]
    public Task<CorporateGroup> Retire(Guid orgId, Guid groupId, GroupRetirementRequest input, CancellationToken ct) =>
        groups.Retire(Actor, orgId, groupId, input, ct);
}
