using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Security;
using Base.Domain.Flags;
using Base.Domain.Tenancy;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Base.Application.Flags;

/// <summary>Configuration (<c>FeatureFlags:Overrides:{Code}</c>) that forces a flag on or off, for development and tests.</summary>
public sealed class FeatureFlagOptions
{
    public const string SectionName = "FeatureFlags";

    public IDictionary<string, bool> Overrides { get; set; } = new Dictionary<string, bool>(StringComparer.Ordinal);
}

/// <summary>Who a flag is on for, as saved (or its default).</summary>
public sealed record FeatureFlagSetting(FeatureFlagMode Mode, int Percentage, IReadOnlyList<Guid> TenantIds, bool IncludePlatform)
{
    public static FeatureFlagSetting Default(FeatureFlagDefinition flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        return new(flag.DefaultOn ? FeatureFlagMode.Everyone : FeatureFlagMode.Off, 0, [], false);
    }
}

public enum FeatureFlagSource
{
    /// <summary>Nobody has changed it: the catalogue default.</summary>
    Default = 0,

    Saved = 1,

    /// <summary>Forced by configuration; the screen can't change it here.</summary>
    Configuration = 2,
}

/// <param name="TenantsOn">How many organisations it is on for right now.</param>
/// <param name="IsStale">A release flag past its remove-by date.</param>
public sealed record FeatureFlagView(
    FeatureFlagDefinition Flag,
    FeatureFlagSetting Setting,
    FeatureFlagSource Source,
    bool? Override,
    int TenantsOn,
    int TenantsTotal,
    bool IsStale,
    string? ChangedBy,
    DateTimeOffset? ChangedAt);

/// <summary>
/// The rules, as pure functions. A configuration override wins; then the saved setting or the catalogue default. Off is
/// off for everyone; Everyone is on everywhere; Targeted is on for the platform (if chosen), the chosen organisations,
/// and the share of organisations whose stable bucket for this flag falls under the percentage.
/// </summary>
public static class FeatureFlagRules
{
    public static bool IsOn(string code, FeatureFlagSetting setting, Guid? tenantId, bool isPlatform, bool? overridden = null)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (overridden is { } forced)
        {
            return forced;
        }

        return setting.Mode switch
        {
            FeatureFlagMode.Everyone => true,
            FeatureFlagMode.Targeted when tenantId is { } id => (setting.IncludePlatform && isPlatform) || setting.TenantIds.Contains(id) || Bucket(code, id) < setting.Percentage,
            _ => false,
        };
    }

    /// <summary>
    /// 0–99 for this organisation and flag: the same every time, different per flag, so 10% then 50% keeps the first 10%.
    /// </summary>
    public static int Bucket(string code, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(code);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code + ":" + tenantId.ToString("N")));
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % 100);
    }
}

/// <summary>Feature flags: whether code runs for the current organisation, and the platform screen that sets them.</summary>
public interface IFeatureFlags
{
    /// <summary>Whether the flag is on for the current organisation (off outside one unless on for everyone).</summary>
    Task<bool> IsOnAsync(string code, CancellationToken cancellationToken = default);

    Task<bool> IsOnForAsync(string code, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Every flag with who it is on for. Needs <c>Platform.Flags.Manage</c>.</summary>
    Task<ValidationResponse<IReadOnlyList<FeatureFlagView>>> ListAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse> SetAsync(string code, FeatureFlagSetting setting, CancellationToken cancellationToken = default);
}

internal sealed class FeatureFlags(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    IPlatformTenant platform,
    ICurrentUser currentUser,
    IFeatureFlagCatalog catalog,
    IOptions<FeatureFlagOptions> options,
    HybridCache cache,
    TimeProvider time) : IFeatureFlags
{
    private const string CacheKey = "flags:all";
    private const string CacheTag = "flags";
    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromSeconds(30), LocalCacheExpiration = TimeSpan.FromSeconds(10) };
    private static readonly string[] CacheTags = [CacheTag];

    public async Task<bool> IsOnAsync(string code, CancellationToken cancellationToken = default)
    {
        var flag = Get(code);
        var tenantId = tenant.IsResolved && !tenant.IsSystemScope ? tenant.TenantId : (Guid?)null;
        var isPlatform = tenantId is not null && await platform.IsCurrentAsync(cancellationToken);
        return FeatureFlagRules.IsOn(flag.Code, await SettingAsync(flag, cancellationToken), tenantId, isPlatform, Override(flag.Code));
    }

    public async Task<bool> IsOnForAsync(string code, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var flag = Get(code);
        return FeatureFlagRules.IsOn(flag.Code, await SettingAsync(flag, cancellationToken), tenantId, tenantId == await platform.GetIdAsync(cancellationToken), Override(flag.Code));
    }

    public async Task<ValidationResponse<IReadOnlyList<FeatureFlagView>>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Platform.FlagsManage))
        {
            return ValidationResponse.Forbidden().As<IReadOnlyList<FeatureFlagView>>();
        }

        var saved = (await unitOfWork.Repository<FeatureFlagState>().GetAllAsync(null, Array.Empty<System.Linq.Expressions.Expression<Func<FeatureFlagState, object?>>>(), asNoTracking: true, cancellationToken: cancellationToken))
            .ToDictionary(s => s.Code, StringComparer.Ordinal);
        IReadOnlyList<Guid> tenants;
        using (tenant.BeginSystemScope())
        {
            tenants = await unitOfWork.Repository<Tenant>().GetAllAsync(t => t.Id, cancellationToken: cancellationToken);
        }

        var platformId = await platform.GetIdAsync(cancellationToken);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var views = catalog.Flags.Select(flag =>
        {
            var row = saved.GetValueOrDefault(flag.Code);
            var setting = row is null ? FeatureFlagSetting.Default(flag) : ToSetting(row);
            var forced = Override(flag.Code);
            var on = tenants.Count(id => FeatureFlagRules.IsOn(flag.Code, setting, id, id == platformId, forced));
            return new FeatureFlagView(
                flag,
                setting,
                forced is not null ? FeatureFlagSource.Configuration : row is null ? FeatureFlagSource.Default : FeatureFlagSource.Saved,
                forced,
                on,
                tenants.Count,
                flag.Kind == FeatureFlagKind.Release && flag.RemoveBy is { } removeBy && removeBy < today,
                row?.UserModified,
                row?.DateModified);
        }).ToList();
        return views;
    }

    public async Task<ValidationResponse> SetAsync(string code, FeatureFlagSetting setting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (!currentUser.HasPermission(PermissionCodes.Platform.FlagsManage))
        {
            return ValidationResponse.Forbidden();
        }

        if (catalog.Find(code) is not { } flag)
        {
            return ValidationResponse.NotFound("That flag doesn't exist in this build.");
        }

        if (setting.Percentage is < 0 or > 100)
        {
            return ValidationResponse.Failure(new ValidationError(nameof(setting.Percentage), "Choose a share from 0 to 100%."));
        }

        var chosen = setting.TenantIds.Distinct().ToList();
        if (chosen.Count > 0)
        {
            int known;
            using (tenant.BeginSystemScope())
            {
                known = await unitOfWork.Repository<Tenant>().CountAsync(t => chosen.Contains(t.Id), cancellationToken);
            }

            if (known != chosen.Count)
            {
                return ValidationResponse.Failure(new ValidationError(nameof(setting.TenantIds), "One of the organisations doesn't exist any more."));
            }
        }

        var repository = unitOfWork.Repository<FeatureFlagState>();
        var row = await repository.GetAsync(s => s.Code == flag.Code, cancellationToken: cancellationToken);
        if (row is null)
        {
            row = new FeatureFlagState(flag.Code);
            await repository.AddAsync(row, cancellationToken);
        }

        row.Set(setting.Mode, setting.Percentage, chosen, setting.IncludePlatform);
        await unitOfWork.SaveAsync(cancellationToken);
        await cache.RemoveByTagAsync(CacheTag, cancellationToken);
        var saved = ToSetting(row);
        var summary = saved.Mode switch
        {
            FeatureFlagMode.Off => "off for everyone",
            FeatureFlagMode.Everyone => "on for everyone",
            _ => string.Join(", ", new[]
            {
                saved.IncludePlatform ? "the platform's staff" : null,
                saved.TenantIds.Count > 0 ? $"{saved.TenantIds.Count} chosen organisation{(saved.TenantIds.Count == 1 ? string.Empty : "s")}" : null,
                saved.Percentage > 0 ? $"{saved.Percentage}% of organisations" : null,
            }.Where(p => p is not null)) is { Length: > 0 } who ? "on for " + who : "on for nobody yet",
        };
        var note = Override(flag.Code) is { } forced ? $" Configuration forces it {(forced ? "on" : "off")} here, so this takes effect where that isn't set." : string.Empty;
        return ValidationResponse.Success($"{flag.Name} is {summary}.{note}");
    }

    private FeatureFlagDefinition Get(string code) =>
        catalog.Find(code) ?? throw new InvalidOperationException($"Feature flag '{code}' is not in the FeatureFlagCatalog.");

    private bool? Override(string code) => options.Value.Overrides.TryGetValue(code, out var forced) ? forced : null;

    private async Task<FeatureFlagSetting> SettingAsync(FeatureFlagDefinition flag, CancellationToken cancellationToken)
    {
        var all = await cache.GetOrCreateAsync(
            CacheKey,
            unitOfWork,
            static async (uow, ct) => (await uow.Repository<FeatureFlagState>().GetAllAsync(
                    s => new CachedFlag(s.Code, s.Mode, s.Percentage, s.TenantIds, s.IncludePlatform), cancellationToken: ct))
                .ToList(),
            CacheOptions,
            CacheTags,
            cancellationToken);
        return all.Find(f => string.Equals(f.Code, flag.Code, StringComparison.Ordinal)) is { } saved
            ? new FeatureFlagSetting(saved.Mode, saved.Percentage, saved.TenantIds, saved.IncludePlatform)
            : FeatureFlagSetting.Default(flag);
    }

    private static FeatureFlagSetting ToSetting(FeatureFlagState row) => new(row.Mode, row.Percentage, row.TenantIds, row.IncludePlatform);

    internal sealed record CachedFlag(string Code, FeatureFlagMode Mode, int Percentage, IReadOnlyList<Guid> TenantIds, bool IncludePlatform);
}
