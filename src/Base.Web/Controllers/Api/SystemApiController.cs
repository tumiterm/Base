using Base.Application.Common.Persistence;
using Base.Application.Operations;
using Base.Application.Security;
using Base.Web.Results;
using Base.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

[ApiController]
[Route("api/system")]
[HasPermission(PermissionCodes.System.HealthView)]
public sealed class SystemApiController(ISystemStatus status, IJobScheduler jobs, IErrorLog errors) : ControllerBase
{
    /// <summary>The running build: product version, commit and the Base version it is built on.</summary>
    [HttpGet("version")]
    public VersionView Version() => new(AppVersion.Version, AppVersion.Commit, AppVersion.BaseVersion, AppVersion.Informational);

    /// <summary>Every health check with its data (the anonymous <c>/health/*</c> probes only say up or down).</summary>
    [HttpGet("health")]
    public Task<HealthReportView> Health(CancellationToken cancellationToken) => status.HealthAsync(cancellationToken);

    [HttpGet("jobs")]
    public Task<IReadOnlyList<JobStatusView>> Jobs(CancellationToken cancellationToken) => jobs.StatusAsync(cancellationToken);

    [HttpPost("jobs/{code}/run")]
    [HasPermission(PermissionCodes.System.JobsManage)]
    public async Task<IActionResult> RunJob(string code, CancellationToken cancellationToken) => (await jobs.RunNowAsync(code, cancellationToken)).ToActionResult(this);

    [HttpGet("errors")]
    public Task<PagedResult<ErrorRecord>> Errors(string? search, int page = 1, CancellationToken cancellationToken = default) =>
        errors.SearchAsync(search, new PageRequest(page, 50), cancellationToken);

    [HttpGet("errors/{ticket}")]
    public async Task<IActionResult> Error(string ticket, CancellationToken cancellationToken) =>
        await errors.GetAsync(ticket, cancellationToken) is { } error ? Ok(error) : NotFound();
}

public sealed record VersionView(string Version, string? Commit, string BaseVersion, string Informational);
