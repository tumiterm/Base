using Base.Application.Security;
using Base.Application.Users;
using Base.Web.Results;
using Base.Web.Security;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

[Route("admin/roles")]
[HasPermission(PermissionCodes.Roles.Manage)]
public sealed class RoleAdminController(IRoleAdministration roles) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(await roles.ListAsync(cancellationToken));

    [HttpGet("new")]
    public IActionResult Create() => View("Edit", new EditRoleViewModel());

    [HttpPost("new")]
    public async Task<IActionResult> Create(EditRoleViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (ModelState.IsValid)
        {
            var result = await roles.CreateAsync(new SaveRoleRequest(model.Name, model.Description, [.. model.Permissions]), cancellationToken);
            if (result.IsSuccess)
            {
                this.Flash(Application.Common.Results.ValidationResponse.Success($"Role '{model.Name}' created."));
                return RedirectToAction(nameof(Index));
            }

            result.AddToModelState(ModelState);
        }

        model.Id = null;
        return View("Edit", model);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var result = await roles.GetAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return NotFound();
        }

        var role = result.Data!;
        return View(new EditRoleViewModel { Id = role.Id, Name = role.Name, Description = role.Description, Permissions = [.. role.Permissions], IsSystem = role.IsSystem });
    }

    [HttpPost("{id:guid}")]
    public async Task<IActionResult> Edit(Guid id, EditRoleViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (ModelState.IsValid)
        {
            var result = await roles.UpdateAsync(id, new SaveRoleRequest(model.Name, model.Description, [.. model.Permissions]), cancellationToken);
            if (result.IsSuccess)
            {
                this.Flash(result);
                return RedirectToAction(nameof(Index));
            }

            result.AddToModelState(ModelState);
        }

        var current = await roles.GetAsync(id, cancellationToken);
        model.Id = id;
        model.IsSystem = current.Data?.IsSystem ?? false;
        return View(model);
    }

    [HttpPost("{id:guid}/delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        this.Flash(await roles.DeleteAsync(id, cancellationToken));
        return RedirectToAction(nameof(Index));
    }
}
