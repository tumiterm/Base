using System.ComponentModel;
using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Domain.Settings;
using Microsoft.Extensions.Caching.Hybrid;

namespace Base.Application.Settings;

internal sealed class ApplicationSettingService(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    IPlatformTenant platform,
    ISecretProtector secrets,
    HybridCache cache) : IApplicationSettingService
{
    private const string CacheTag = "settings";
    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromMinutes(10) };
    private static readonly string[] CacheTags = [CacheTag];

    public async Task<string?> GetAsync(string code, CancellationToken cancellationToken = default)
    {
        var definition = SettingCatalog.Get(code);
        var values = await LoadAsync(cancellationToken);
        var stored = Find(definition, values);
        if (definition.IsSecret)
        {
            return stored?.Protected is { } cipher ? secrets.Unprotect(cipher) : null;
        }

        return stored is null ? definition.DefaultValue : stored.Value;
    }

    public async Task<int> GetIntAsync(string code, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(code, cancellationToken);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : int.Parse(SettingCatalog.Get(code).DefaultValue ?? "0", CultureInfo.InvariantCulture);
    }

    public async Task<bool> GetBoolAsync(string code, CancellationToken cancellationToken = default) =>
        bool.TryParse(await GetAsync(code, cancellationToken), out var flag) && flag;

    public async Task<IReadOnlyList<string>> GetListAsync(string code, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(code, cancellationToken);
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<IReadOnlyList<SettingValue>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var values = await LoadAsync(cancellationToken);
        return SettingCatalog.All.Select(d =>
        {
            var stored = Find(d, values);
            var isTenant = d.Scope == SettingScope.Tenant && values.Tenant.TryGetValue(d.Code, out var own) && own.IsOverridden;
            if (d.IsSecret)
            {
                return new SettingValue(d, null, isTenant, ChangedHere(d, values), stored?.Protected is not null);
            }

            return new SettingValue(d, stored is null ? d.DefaultValue : stored.Value, isTenant, ChangedHere(d, values));
        }).ToList();
    }

    public Task<ValidationResponse> SetAsync(string code, string? value, CancellationToken cancellationToken = default) =>
        WriteAsync(new Dictionary<string, string?>(StringComparer.Ordinal) { [code] = value }, forceWrite: true, cancellationToken);

    public Task<ValidationResponse> SetManyAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default) =>
        WriteAsync(values, forceWrite: false, cancellationToken);

    public async Task<ValidationResponse> RevertAsync(string code, CancellationToken cancellationToken = default)
    {
        var definition = SettingCatalog.Find(code);
        if (definition is null)
        {
            return ValidationResponse.NotFound($"Setting '{code}' does not exist.");
        }

        if (definition.Scope == SettingScope.Platform && !await CanEditPlatformAsync(cancellationToken))
        {
            return PlatformOnly(definition);
        }

        var row = await RowAsync(definition, cancellationToken);
        if (row is null || !row.IsOverridden)
        {
            return ValidationResponse.Success($"'{definition.Name}' already uses its default.");
        }

        row.Revert(definition.Scope == SettingScope.Platform ? definition.DefaultValue : null);
        await unitOfWork.SaveAsync(cancellationToken);
        await InvalidateAsync([definition], cancellationToken);
        return ValidationResponse.Success(definition.Scope == SettingScope.Tenant && tenant.IsResolved && !await platform.IsCurrentAsync(cancellationToken)
            ? $"'{definition.Name}' follows the platform value again."
            : $"'{definition.Name}' is back to its default.");
    }

    public async Task<bool> CanEditPlatformAsync(CancellationToken cancellationToken = default) =>
        tenant.IsResolved && await platform.IsCurrentAsync(cancellationToken);

    private async Task<ValidationResponse> WriteAsync(IReadOnlyDictionary<string, string?> values, bool forceWrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        var changes = new List<(SettingDefinition Definition, string? Value)>();
        var errors = new List<ValidationError>();
        var canEditPlatform = (bool?)null;
        var current = await LoadAsync(cancellationToken);

        foreach (var (code, raw) in values)
        {
            var definition = SettingCatalog.Find(code);
            if (definition is null)
            {
                return ValidationResponse.NotFound($"Setting '{code}' does not exist.");
            }

            if (definition.Scope == SettingScope.Platform && !(canEditPlatform ??= await CanEditPlatformAsync(cancellationToken)))
            {
                return PlatformOnly(definition);
            }

            var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            if (definition.DataType == SettingDataType.Boolean && value is not null && bool.TryParse(value, out var flag))
            {
                value = flag ? "true" : "false";
            }

            var validation = definition.Validate(value);
            if (validation.IsError)
            {
                errors.AddRange(validation.Errors);
                continue;
            }

            var stored = Find(definition, current);
            var effective = definition.IsSecret ? null : stored is null ? definition.DefaultValue : stored.Value;
            if (forceWrite || definition.IsSecret || !string.Equals(effective, value, StringComparison.Ordinal))
            {
                changes.Add((definition, value));
            }
        }

        if (errors.Count > 0)
        {
            return ValidationResponse.Failure(errors);
        }

        foreach (var (definition, value) in changes)
        {
            var row = await RowAsync(definition, cancellationToken);
            if (row is null)
            {
                row = new ApplicationSetting(definition.Code, null, definition.Scope == SettingScope.Tenant ? tenant.TenantId : null);
                await unitOfWork.Repository<ApplicationSetting>().AddAsync(row, cancellationToken);
            }

            if (definition.IsSecret)
            {
                row.ChangeProtectedValue(value is null ? null : secrets.Protect(value));
            }
            else
            {
                row.ChangeValue(value);
            }
        }

        if (changes.Count > 0)
        {
            await unitOfWork.SaveAsync(cancellationToken);
            await InvalidateAsync(changes.Select(c => c.Definition), cancellationToken);
        }

        return ValidationResponse.Success(values.Count == 1 && SettingCatalog.Find(values.Keys.First()) is { } single
            ? $"'{single.Name}' saved."
            : changes.Count == 0 ? "Nothing changed." : $"{changes.Count} setting{(changes.Count == 1 ? "" : "s")} saved.");
    }

    private Task<ApplicationSetting?> RowAsync(SettingDefinition definition, CancellationToken cancellationToken)
    {
        Guid? tenantId = definition.Scope == SettingScope.Tenant ? tenant.TenantId : null;
        return unitOfWork.Repository<ApplicationSetting>().GetAsync(s => s.Code == definition.Code && s.TenantId == tenantId, cancellationToken: cancellationToken);
    }

    private async Task InvalidateAsync(IEnumerable<SettingDefinition> changed, CancellationToken cancellationToken)
    {
        // A platform value is inherited by every tenant, so every tenant's cached copy is stale.
        if (changed.Any(d => d.Scope == SettingScope.Platform))
        {
            await cache.RemoveByTagAsync(CacheTag, cancellationToken);
        }
        else
        {
            await cache.RemoveAsync(CacheKey(), cancellationToken);
        }
    }

    private static ValidationResponse PlatformOnly(SettingDefinition definition) =>
        ValidationResponse.Forbidden($"'{definition.Name}' applies to every tenant, so only the platform's administrators can change it.");

    /// <summary>The row that decides the value: the tenant's override, else the platform row; null means the default.</summary>
    private static StoredValue? Find(SettingDefinition definition, StoredValues values)
    {
        if (definition.Scope == SettingScope.Tenant && values.Tenant.TryGetValue(definition.Code, out var tenantValue) && tenantValue.IsOverridden)
        {
            return tenantValue;
        }

        return values.Platform.GetValueOrDefault(definition.Code);
    }

    /// <summary>Changed at the level the current tenant edits: its own override, or (platform settings) the platform row.</summary>
    private static bool ChangedHere(SettingDefinition definition, StoredValues values) =>
        definition.Scope == SettingScope.Tenant
            ? values.Tenant.TryGetValue(definition.Code, out var own) && own.IsOverridden
            : values.Platform.TryGetValue(definition.Code, out var platformRow) && platformRow.IsOverridden;

    private string CacheKey() => $"settings:{tenant.TenantId:N}";

    private ValueTask<StoredValues> LoadAsync(CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            CacheKey(),
            (unitOfWork, tenantId: tenant.TenantId),
            static async (state, ct) =>
            {
                var rows = await state.unitOfWork.Repository<ApplicationSetting>().GetAllAsync(
                    s => new { s.Code, s.Value, s.ProtectedValue, s.IsOverridden, s.TenantId },
                    s => s.TenantId == null || s.TenantId == state.tenantId,
                    cancellationToken: ct);

                return new StoredValues(
                    rows.Where(r => r.TenantId is null).ToDictionary(r => r.Code, r => new StoredValue(r.Value, r.ProtectedValue, r.IsOverridden), StringComparer.Ordinal),
                    rows.Where(r => r.TenantId is not null).ToDictionary(r => r.Code, r => new StoredValue(r.Value, r.ProtectedValue, r.IsOverridden), StringComparer.Ordinal));
            },
            CacheOptions,
            CacheTags,
            cancellationToken);

    /// <summary>A stored row. <see cref="Protected"/> holds a secret's ciphertext, never its plaintext.</summary>
    [ImmutableObject(true)]
    internal sealed record StoredValue(string? Value, string? Protected, bool IsOverridden);

    // Immutable by convention: lets HybridCache hand out the cached instance instead of a serialised copy.
    [ImmutableObject(true)]
    internal sealed record StoredValues(Dictionary<string, StoredValue> Platform, Dictionary<string, StoredValue> Tenant);
}
