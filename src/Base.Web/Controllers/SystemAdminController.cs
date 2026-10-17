using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Operations;
using Base.Application.Security;
using Base.Web.Observability;
using Base.Web.Security;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Base.Web.Controllers;

/// <summary>The operations page: health of every dependency, jobs, database, storage, outbox and errors.</summary>
[Route("admin/system")]
[HasPermission(PermissionCodes.System.HealthView)]
public sealed class SystemAdminController(ISystemStatus status, IJobScheduler jobs, IErrorLog errors, IOptions<ObservabilityLinks> links) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(new SystemViewModel(
        await status.HealthAsync(cancellationToken),
        await jobs.StatusAsync(cancellationToken),
        await status.DatabaseAsync(cancellationToken),
        await status.StorageAsync(cancellationToken),
        await status.OutboxAsync(cancellationToken),
        await errors.SearchAsync(null, new PageRequest(1, 10), cancellationToken)));

    [HttpPost("jobs/{code}/run")]
    [HasPermission(PermissionCodes.System.JobsManage)]
    public async Task<IActionResult> RunJob(string code, CancellationToken cancellationToken)
    {
        this.Flash(await jobs.RunNowAsync(code, cancellationToken));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("outbox/{id:guid}/retry")]
    [HasPermission(PermissionCodes.System.JobsManage)]
    public async Task<IActionResult> RetryOutbox(Guid id, CancellationToken cancellationToken)
    {
        this.Flash(await status.RetryOutboxMessageAsync(id, cancellationToken)
            ? ValidationResponse.Success("Queued for another round of attempts.")
            : ValidationResponse.NotFound("That message is no longer dead-lettered."));
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("errors")]
    public async Task<IActionResult> Errors(string? search, int page = 1, CancellationToken cancellationToken = default) =>
        View(new ErrorListViewModel(await errors.SearchAsync(search, new PageRequest(page, 50), cancellationToken), search));

    [HttpGet("errors/{ticket}")]
    public async Task<IActionResult> Error(string ticket, CancellationToken cancellationToken)
    {
        var error = await errors.GetAsync(ticket, cancellationToken);
        return error is null ? NotFound() : View(new ErrorDetailsViewModel(error, links.Value.TraceUrl(error.TraceId), links.Value.LogSearchUrl(error.Ticket)));
    }
}
