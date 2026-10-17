using Base.Application.Auditing;
using Base.Application.Common.Results;
using Base.Application.MasterData;
using Base.Application.People;
using Base.Application.Security;
using Base.Domain.People;
using Base.Web.Formatting;
using Base.Web.Results;
using Base.Web.Security;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>
/// The sample module's screens. Copy this shape for a new module: a thin controller that maps a use-case service's
/// <see cref="ValidationResponse"/> onto views and flash messages, with permissions on every action.
/// </summary>
[Route("people")]
[HasPermission(PermissionCodes.People.View)]
public sealed class PeopleController(IPersonService people, ILookupService lookups, IAuditTrail audit) : Controller
{
    private static readonly string[] Lists = [LookupLists.People.Title, LookupLists.People.Department, LookupLists.People.Skill];

    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? search, PersonStatus? status, string? department, string? skill, PersonSort sort = PersonSort.Name, bool desc = false, int page = 1,
        CancellationToken cancellationToken = default)
    {
        var query = new PersonQuery(search, status, department, skill, sort, desc, page);
        return View(new PeopleListViewModel(
            await people.SearchAsync(query, cancellationToken),
            await people.CountAsync(query, cancellationToken),
            query,
            await LookupLabels.LoadAsync(lookups, cancellationToken, Lists),
            new PersonForm()));
    }

    [HttpGet("new")]
    [HasPermission(PermissionCodes.People.Manage)]
    public IActionResult New() => View(new PersonForm());

    [HttpPost("new")]
    [HasPermission(PermissionCodes.People.Manage)]
    public async Task<IActionResult> New(PersonForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        var result = await people.CreateAsync(form.ToRequest(), cancellationToken);
        if (result.IsSuccess)
        {
            this.Flash(result);
            return RedirectToAction(nameof(Details), new { id = result.Data });
        }

        result.AddToModelState(ModelState);
        return View(form);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, PersonTab tab = PersonTab.Profile, CancellationToken cancellationToken = default)
    {
        var person = await people.GetAsync(id, cancellationToken);
        return person.IsSuccess ? View(await PageAsync(person.Data!, PersonForm.From(person.Data!), tab, editing: false, cancellationToken)) : NotFound();
    }

    [HttpPost("{id:guid}")]
    [HasPermission(PermissionCodes.People.Manage)]
    public async Task<IActionResult> Details(Guid id, PersonForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        var result = await people.UpdateAsync(id, form.ToRequest(), cancellationToken);
        if (result.IsSuccess)
        {
            this.Flash(result);
            return RedirectToAction(nameof(Details), new { id });
        }

        var person = await people.GetAsync(id, cancellationToken);
        if (!person.IsSuccess)
        {
            return NotFound();
        }

        result.AddToModelState(ModelState);
        if (result.Errors.Count == 0)
        {
            ModelState.AddModelError(string.Empty, result.Message ?? "The changes couldn't be saved.");
        }

        return View(await PageAsync(person.Data!, form, PersonTab.Profile, editing: true, cancellationToken));
    }

    [HttpPost("{id:guid}/status")]
    [HasPermission(PermissionCodes.People.Manage)]
    public async Task<IActionResult> Status(Guid id, PersonStatus status, CancellationToken cancellationToken)
    {
        this.Flash(await people.ChangeStatusAsync(id, status, cancellationToken));
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/delete")]
    [HasPermission(PermissionCodes.People.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await people.DeleteAsync(id, cancellationToken);
        this.Flash(result);
        return result.IsSuccess ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/photo")]
    [HasPermission(PermissionCodes.People.Manage)]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2 * 1024 * 1024)]
    public async Task<IActionResult> Photo(Guid id, IFormFile? photo, CancellationToken cancellationToken)
    {
        if (photo is null)
        {
            this.Flash(ValidationResponse.Failure("Choose a photo."));
        }
        else
        {
            await using var stream = photo.OpenReadStream();
            this.Flash(await people.SetAvatarAsync(id, stream, photo.FileName, cancellationToken));
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/photo/remove")]
    [HasPermission(PermissionCodes.People.Manage)]
    public async Task<IActionResult> RemovePhoto(Guid id, CancellationToken cancellationToken)
    {
        this.Flash(await people.RemoveAvatarAsync(id, cancellationToken));
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet("{id:guid}/photo")]
    public async Task<IActionResult> PhotoFile(Guid id, CancellationToken cancellationToken)
    {
        var photo = await people.OpenAvatarAsync(id, cancellationToken);
        if (!photo.IsSuccess)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=300";
        var file = photo.Data!;
        return file.RedirectUrl is { } url ? Redirect(url.ToString()) : File(file.Content!, file.ContentType);
    }

    private async Task<PersonPageViewModel> PageAsync(PersonDetails person, PersonForm form, PersonTab tab, bool editing, CancellationToken cancellationToken) => new(
        person,
        form,
        tab,
        await LookupLabels.LoadAsync(lookups, cancellationToken, Lists),
        tab == PersonTab.History ? await audit.SearchAsync(new AuditQuery(nameof(Person), person.Id.ToString(), PageSize: 100), cancellationToken) : null,
        editing);
}

/// <summary>The tenant's lookup lists: pick a list on the left, maintain its entries on the right.</summary>
[Route("admin/master-data")]
[HasPermission(PermissionCodes.MasterData.Manage)]
public sealed class MasterDataController(ILookupService lookups) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? list, CancellationToken cancellationToken)
    {
        var lists = await lookups.ListsAsync(cancellationToken);
        var selected = lists.FirstOrDefault(l => string.Equals(l.Definition.Code, list, StringComparison.Ordinal)) ?? lists[0];
        return View(new MasterDataViewModel(lists, selected, await lookups.GetAsync(selected.Definition.Code, includeInactive: true, cancellationToken)));
    }

    [HttpPost("add")]
    public async Task<IActionResult> Add(string list, string name, CancellationToken cancellationToken)
    {
        this.Flash(await lookups.AddAsync(list, name, cancellationToken));
        return RedirectToAction(nameof(Index), new { list });
    }

    [HttpPost("{id:guid}/rename")]
    public async Task<IActionResult> Rename(Guid id, string list, string name, CancellationToken cancellationToken)
    {
        this.Flash(await lookups.RenameAsync(id, name, cancellationToken));
        return RedirectToAction(nameof(Index), new { list });
    }

    [HttpPost("{id:guid}/active")]
    public async Task<IActionResult> SetActive(Guid id, string list, bool active, CancellationToken cancellationToken)
    {
        this.Flash(await lookups.SetActiveAsync(id, active, cancellationToken));
        return RedirectToAction(nameof(Index), new { list });
    }

    [HttpPost("{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, string list, int direction, CancellationToken cancellationToken)
    {
        var result = await lookups.MoveAsync(id, direction, cancellationToken);
        if (result.IsError)
        {
            this.Flash(result);
        }

        return RedirectToAction(nameof(Index), new { list });
    }
}
