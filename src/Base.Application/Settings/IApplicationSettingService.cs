using Base.Application.Common.Results;

namespace Base.Application.Settings;

/// <summary>
/// The effective value of a setting and where it came from. <see cref="Value"/> is always null for secrets;
/// <see cref="HasSecret"/> says whether one is stored.
/// </summary>
/// <param name="IsChanged">An administrator has set it (at this level), so it can be reverted.</param>
public sealed record SettingValue(SettingDefinition Definition, string? Value, bool IsTenantOverride, bool IsChanged = false, bool HasSecret = false);

/// <summary>
/// Reads and writes settings. Resolution order: tenant value → platform value → catalogue default.
/// Reads are cached; writes invalidate the cache. Platform-scope settings can only be changed from the platform tenant.
/// </summary>
public interface IApplicationSettingService
{
    /// <summary>The effective value; for a secret, the decrypted plaintext (null when none is stored).</summary>
    Task<string?> GetAsync(string code, CancellationToken cancellationToken = default);

    Task<int> GetIntAsync(string code, CancellationToken cancellationToken = default);

    Task<bool> GetBoolAsync(string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetListAsync(string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SettingValue>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse> SetAsync(string code, string? value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves several settings at once, all or nothing: every value is validated first and errors come back keyed by
    /// setting code. Values equal to the current effective value are left alone, so saving a form doesn't turn
    /// inherited values into tenant overrides.
    /// </summary>
    Task<ValidationResponse> SetManyAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default);

    /// <summary>Undoes a change: platform values go back to the default, tenant values inherit the platform's again.</summary>
    Task<ValidationResponse> RevertAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Whether the current tenant may change platform-scope settings (it is the platform tenant).</summary>
    Task<bool> CanEditPlatformAsync(CancellationToken cancellationToken = default);
}
