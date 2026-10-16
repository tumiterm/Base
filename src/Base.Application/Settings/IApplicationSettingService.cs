using Base.Application.Common.Results;

namespace Base.Application.Settings;

/// <summary>The effective value of a setting and where it came from.</summary>
public sealed record SettingValue(SettingDefinition Definition, string? Value, bool IsTenantOverride);

/// <summary>
/// Reads and writes settings. Resolution order: tenant value → platform value → catalogue default.
/// Reads are cached; writes invalidate the cache.
/// </summary>
public interface IApplicationSettingService
{
    Task<string?> GetAsync(string code, CancellationToken cancellationToken = default);

    Task<int> GetIntAsync(string code, CancellationToken cancellationToken = default);

    Task<bool> GetBoolAsync(string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetListAsync(string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SettingValue>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse> SetAsync(string code, string? value, CancellationToken cancellationToken = default);
}
