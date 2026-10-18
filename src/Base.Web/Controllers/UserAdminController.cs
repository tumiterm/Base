using Base.Application.Common.Persistence;
using Base.Application.Security;
using Base.Application.Users;
using Base.Web.Results;
using Base.Web.Security;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

[Route("admin/users")]
[HasPermission(PermissionCodes.Users.View)]
public sealed class UserAdminController(IUserAdministration users, IRoleAdministration roles) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? search, int page = 1, CancellationToken cancellationToken = default) =>
        View(new UserListViewModel(await users.SearchAsync(new UserQuery(search, page, PageRequest.DefaultPageSize), cancellationToken), search));

    [HttpGet("invite")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> Invite(CancellationToken cancellationToken) =>
        View(new InviteUserViewModel { AvailableRoles = await roles.ListAsync(cancellationToken), Roles = [RoleNames.User] });

    [HttpPost("invite")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> Invite(InviteUserViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (ModelState.IsValid)
        {
            var result = await users.InviteAsync(new InviteUserRequest(model.Email, model.DisplayName, [.. model.Roles]), cancellationToken);
            if (result.IsSuccess)
            {
                this.Flash(Application.Common.Results.ValidationResponse.Success($"Invitation sent to {model.Email}."));
                return RedirectToAction(nameof(Index));
            }

            result.AddToModelState(ModelState);
        }

        model.AvailableRoles = await roles.ListAsync(cancellationToken);
        return View(model);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var result = await users.GetAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return NotFound();
        }

        var user = result.Data!;
        return View(new EditUserViewModel
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            Roles = [.. user.Roles],
            IsLockedOut = user.IsLockedOut,
            HasPassword = user.HasPassword,
            LastSignInAt = user.LastSignInAt,
            AvailableRoles = await roles.ListAsync(cancellationToken),
        });
    }

    [HttpPost("{id:guid}")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> Edit(Guid id, EditUserViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (ModelState.IsValid)
        {
            var result = await users.UpdateAsync(id, new UpdateUserRequest(model.DisplayName, [.. model.Roles]), cancellationToken);
            if (result.IsSuccess)
            {
                this.Flash(result);
                return RedirectToAction(nameof(Edit), new { id });
            }

            result.AddToModelState(ModelState);
        }

        var current = await users.GetAsync(id, cancellationToken);
        if (!current.IsSuccess)
        {
            return NotFound();
        }

        model.Id = id;
        model.Email = current.Data!.Email;
        model.IsLockedOut = current.Data.IsLockedOut;
        model.HasPassword = current.Data.HasPassword;
        model.LastSignInAt = current.Data.LastSignInAt;
        model.AvailableRoles = await roles.ListAsync(cancellationToken);
        return View(model);
    }

    [HttpPost("{id:guid}/lock")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> Lock(Guid id, CancellationToken cancellationToken) =>
        FlashAndReturn(id, await users.LockAsync(id, cancellationToken));

    [HttpPost("{id:guid}/unlock")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken cancellationToken) =>
        FlashAndReturn(id, await users.UnlockAsync(id, cancellationToken));

    [HttpPost("{id:guid}/password-reset")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> SendPasswordReset(Guid id, CancellationToken cancellationToken) =>
        FlashAndReturn(id, await users.SendPasswordResetAsync(id, cancellationToken));

    /// <summary>A fresh invitation link: JSON for the copy button, otherwise a page showing it.</summary>
    [HttpPost("{id:guid}/invite-link")]
    [HasPermission(PermissionCodes.Users.Manage)]
    public async Task<IActionResult> InviteLink(Guid id, CancellationToken cancellationToken) =>
        this.InviteLinkResult(await users.CreateInviteLinkAsync(id, cancellationToken), Url.Action(nameof(Index))!);

    private RedirectToActionResult FlashAndReturn(Guid id, Application.Common.Results.ValidationResponse result)
    {
        this.Flash(result);
        return RedirectToAction(nameof(Edit), new { id });
    }
}
