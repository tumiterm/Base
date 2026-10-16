using Base.Application.Security;
using Base.Application.Users;
using Base.Web.Results;
using Base.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

[ApiController]
[Route("api/roles")]
public sealed class RolesController(IRoleAdministration roles) : ControllerBase
{
    /// <summary>Readable by user administrators too, so they can pick roles.</summary>
    [HttpGet]
    [HasPermission(PermissionCodes.Users.View)]
    public Task<IReadOnlyList<RoleSummary>> List(CancellationToken cancellationToken) => roles.ListAsync(cancellationToken);

    [HttpGet("{id:guid}")]
    [HasPermission(PermissionCodes.Users.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await roles.GetAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost]
    [HasPermission(PermissionCodes.Roles.Manage)]
    public async Task<IActionResult> Create(SaveRoleRequest request, CancellationToken cancellationToken)
    {
        var result = await roles.CreateAsync(request, cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Data }, new { id = result.Data }) : result.ToActionResult(this);
    }

    [HttpPut("{id:guid}")]
    [HasPermission(PermissionCodes.Roles.Manage)]
    public async Task<IActionResult> Update(Guid id, SaveRoleRequest request, CancellationToken cancellationToken) =>
        (await roles.UpdateAsync(id, request, cancellationToken)).ToActionResult(this);

    [HttpDelete("{id:guid}")]
    [HasPermission(PermissionCodes.Roles.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await roles.DeleteAsync(id, cancellationToken)).ToActionResult(this);
}

[ApiController]
[Route("api/permissions")]
public sealed class PermissionsController : ControllerBase
{
    /// <summary>The permission catalogue, for building role editors.</summary>
    [HttpGet]
    [HasPermission(PermissionCodes.Roles.Manage)]
    public IReadOnlyList<PermissionDefinition> List() => PermissionCatalog.All;
}
