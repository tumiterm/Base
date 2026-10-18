using Base.Application.Common.Persistence;
using Base.Application.Security;
using Base.Application.Users;
using Base.Web.Results;
using Base.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

[ApiController]
[Route("api/users")]
public sealed class UsersController(IUserAdministration users) : ControllerBase
{
    [HttpGet]
    [HasPermission(PermissionCodes.Users.View)]
    public Task<PagedResult<UserSummary>> Search([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = PageRequest.DefaultPageSize, CancellationToken cancellationToken = default) =>
        users.SearchAsync(new UserQuery(search, page, pageSize), cancellationToken);

    [HttpGet("{id:guid}")]
    [HasPermission(PermissionCodes.Users.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await users.GetAsync(id, cancellationToken)).ToActionResult(this);

    /// <summary>Invites a user; they receive an email to choose their password.</summary>
    [HttpPost]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> Invite(InviteUserRequest request, CancellationToken cancellationToken)
    {
        var result = await users.InviteAsync(request, cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Data }, new { id = result.Data }) : result.ToActionResult(this);
    }

    [HttpPut("{id:guid}")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest request, CancellationToken cancellationToken) =>
        (await users.UpdateAsync(id, request, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/lock")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> Lock(Guid id, CancellationToken cancellationToken) =>
        (await users.LockAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/unlock")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken cancellationToken) =>
        (await users.UnlockAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/password-reset")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> SendPasswordReset(Guid id, CancellationToken cancellationToken) =>
        (await users.SendPasswordResetAsync(id, cancellationToken)).ToActionResult(this);

    /// <summary>A fresh invitation link for someone who hasn't accepted yet. It works once; send it over a private channel.</summary>
    [HttpPost("{id:guid}/invite-link")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> CreateInviteLink(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return (await users.CreateInviteLinkAsync(id, cancellationToken)).ToActionResult(this);
    }
}
