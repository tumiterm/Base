using Base.Application.Security;
using Base.Application.Settings;
using Base.Web.Results;
using Base.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

public sealed record SettingResponse(
    string Code,
    string Name,
    string Category,
    string Description,
    string DataType,
    string Scope,
    string? Value,
    string? DefaultValue,
    bool IsTenantOverride,
    IReadOnlyList<string>? AllowedValues);

public sealed record UpdateSettingRequest(string? Value);

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(IApplicationSettingService settings) : ControllerBase
{
    [HttpGet]
    [HasPermission(PermissionCodes.Settings.View)]
    public async Task<IReadOnlyList<SettingResponse>> GetAll(CancellationToken cancellationToken) =>
        (await settings.GetAllAsync(cancellationToken))
            .Select(s => new SettingResponse(
                s.Definition.Code,
                s.Definition.Name,
                s.Definition.Category,
                s.Definition.Description,
                s.Definition.DataType.ToString(),
                s.Definition.Scope.ToString(),
                s.Value,
                s.Definition.DefaultValue,
                s.IsTenantOverride,
                s.Definition.AllowedValues))
            .ToList();

    [HttpPut("{code}")]
    [HasPermission(PermissionCodes.Settings.Edit)]
    public async Task<IActionResult> Update(string code, UpdateSettingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await settings.SetAsync(code, request.Value, cancellationToken)).ToActionResult(this);
    }
}
