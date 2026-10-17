using Base.Application.Common.Persistence;
using Base.Application.MasterData;
using Base.Application.People;
using Base.Application.Security;
using Base.Domain.People;
using Base.Web.Results;
using Base.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

[ApiController]
[Route("api/people")]
[HasPermission(PermissionCodes.People.View)]
public sealed class PeopleApiController(IPersonService people) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<PersonSummary>> Search([FromQuery] PersonQuery query, CancellationToken cancellationToken) =>
        people.SearchAsync(query ?? new PersonQuery(), cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) => (await people.GetAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost]
    [HasPermission(PermissionCodes.People.Manage)]
    public async Task<IActionResult> Create(SavePersonRequest request, CancellationToken cancellationToken)
    {
        var result = await people.CreateAsync(request, cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Data }, new { id = result.Data }) : result.ToActionResult(this);
    }

    [HttpPut("{id:guid}")]
    [HasPermission(PermissionCodes.People.Manage)]
    public async Task<IActionResult> Update(Guid id, SavePersonRequest request, CancellationToken cancellationToken) =>
        (await people.UpdateAsync(id, request, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/status")]
    [HasPermission(PermissionCodes.People.Manage)]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromQuery] PersonStatus status, CancellationToken cancellationToken) =>
        (await people.ChangeStatusAsync(id, status, cancellationToken)).ToActionResult(this);

    [HttpDelete("{id:guid}")]
    [HasPermission(PermissionCodes.People.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await people.DeleteAsync(id, cancellationToken)).ToActionResult(this);
}

/// <summary>Lookup entries for pickers. Any signed-in user may read them; maintaining them needs <c>MasterData.Manage</c>.</summary>
[ApiController]
[Route("api/lookups")]
public sealed class LookupsApiController(ILookupService lookups) : ControllerBase
{
    [HttpGet("{list}")]
    public async Task<IActionResult> Get(string list, [FromQuery] bool includeInactive = false, CancellationToken cancellationToken = default) =>
        LookupCatalog.Find(list) is null ? NotFound() : Ok(await lookups.GetAsync(list, includeInactive, cancellationToken));

    [HttpGet]
    [HasPermission(PermissionCodes.MasterData.Manage)]
    public Task<IReadOnlyList<LookupListSummary>> Lists(CancellationToken cancellationToken) => lookups.ListsAsync(cancellationToken);

    [HttpPost("{list}")]
    [HasPermission(PermissionCodes.MasterData.Manage)]
    public async Task<IActionResult> Add(string list, AddLookupRequest request, CancellationToken cancellationToken)
    {
        var result = await lookups.AddAsync(list, request?.Name ?? string.Empty, cancellationToken);
        return result.IsSuccess ? Ok(new { id = result.Data }) : result.ToActionResult(this);
    }

    public sealed record AddLookupRequest(string Name);
}
