using System.ComponentModel;
using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Domain.Settings;
using Microsoft.Extensions.Caching.Hybrid;

namespace Base.Application.Settings;

internal sealed class ApplicationSettingService(IUnitOfWork unitOfWork, ITenantContext tenant, HybridCache cache) : IApplicationSettingService
{
    private const string CacheTag = "settings";
    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromMinutes(10) };
    private static readonly string[] CacheTags = [CacheTag];

    public async Task<string?> GetAsync(string code, CancellationToken cancellationToken = default)
    {
        var definition = SettingCatalog.Get(code);
        var values = await LoadAsync(cancellationToken);
        return Resolve(definition, values).Value;
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
        return SettingCatalog.All.Select(d => Resolve(d, values)).ToList();
    }

    public async Task<ValidationResponse> SetAsync(string code, string? value, CancellationToken cancellationToken = default)
    {
        var definition = SettingCatalog.Find(code);
        if (definition is null)
        {
            return ValidationResponse.NotFound($"Setting '{code}' does not exist.");
        }

        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var validation = definition.Validate(value);
        if (validation.IsError)
        {
            return validation;
        }

        Guid? tenantId = definition.Scope == SettingScope.Tenant ? tenant.TenantId : null;
        var repository = unitOfWork.Repository<ApplicationSetting>();
        var row = await repository.GetAsync(s => s.Code == code && s.TenantId == tenantId, cancellationToken: cancellationToken);

        if (row is null)
        {
            row = new ApplicationSetting(code, value, tenantId);
            row.ChangeValue(value);
            await repository.AddAsync(row, cancellationToken);
        }
        else
        {
            row.ChangeValue(value);
        }

        await unitOfWork.SaveAsync(cancellationToken);
        // A platform value is inherited by every tenant, so every tenant's cached copy is stale.
        if (definition.Scope == SettingScope.Platform)
        {
            await cache.RemoveByTagAsync(CacheTag, cancellationToken);
        }
        else
        {
            await cache.RemoveAsync(CacheKey(), cancellationToken);
        }

        return ValidationResponse.Success($"'{definition.Name}' saved.");
    }

    private static SettingValue Resolve(SettingDefinition definition, StoredValues values)
    {
        if (definition.Scope == SettingScope.Tenant && values.Tenant.TryGetValue(definition.Code, out var tenantValue))
        {
            return new(definition, tenantValue, true);
        }

        return values.Platform.TryGetValue(definition.Code, out var platformValue)
            ? new(definition, platformValue, false)
            : new(definition, definition.DefaultValue, false);
    }

    private string CacheKey() => $"settings:{tenant.TenantId:N}";

    private ValueTask<StoredValues> LoadAsync(CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            CacheKey(),
            (unitOfWork, tenantId: tenant.TenantId),
            static async (state, ct) =>
            {
                var rows = await state.unitOfWork.Repository<ApplicationSetting>().GetAllAsync(
                    s => new { s.Code, s.Value, s.TenantId },
                    s => s.TenantId == null || s.TenantId == state.tenantId,
                    cancellationToken: ct);

                return new StoredValues(
                    rows.Where(r => r.TenantId is null).ToDictionary(r => r.Code, r => r.Value, StringComparer.Ordinal),
                    rows.Where(r => r.TenantId is not null).ToDictionary(r => r.Code, r => r.Value, StringComparer.Ordinal));
            },
            CacheOptions,
            CacheTags,
            cancellationToken);

    // Immutable by convention: lets HybridCache hand out the cached instance instead of a serialised copy.
    [ImmutableObject(true)]
    internal sealed record StoredValues(Dictionary<string, string?> Platform, Dictionary<string, string?> Tenant);
}
