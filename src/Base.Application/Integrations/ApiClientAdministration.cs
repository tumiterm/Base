using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Domain.Integrations;
using Microsoft.Extensions.Caching.Hybrid;

namespace Base.Application.Integrations;

/// <summary>
/// The tenant's API clients (<c>Api.Clients.Manage</c>). A client can only be given permissions its creator holds, and
/// never platform permissions outside the platform tenant. Keys expire; rotate them before they do.
/// </summary>
public interface IApiClientAdministration
{
    Task<IReadOnlyList<ApiClientSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse<ApiClientSummary>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ValidationResponse<IssuedApiKey>> CreateAsync(SaveApiClientRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes the name, permissions and rate limit. The key and its expiry stay; rotate to change them.</summary>
    Task<ValidationResponse> UpdateAsync(Guid id, SaveApiClientRequest request, CancellationToken cancellationToken = default);

    /// <summary>Issues a new key valid for <paramref name="expiresInDays"/>; the old key stops working at once.</summary>
    Task<ValidationResponse<IssuedApiKey>> RotateAsync(Guid id, int expiresInDays, CancellationToken cancellationToken = default);

    Task<ValidationResponse> RevokeAsync(Guid id, CancellationToken cancellationToken = default);
}

internal sealed class ApiClientAdministration(
    IUnitOfWork unitOfWork,
    ICurrentUser actor,
    IPlatformTenant platform,
    IApplicationSettingService settings,
    HybridCache cache,
    TimeProvider time) : IApiClientAdministration
{
    internal const string CacheTag = "api-clients";
    private static readonly TimeSpan ExpiringSoon = TimeSpan.FromDays(14);

    public async Task<IReadOnlyList<ApiClientSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var clients = await unitOfWork.Repository<ApiClient>().GetAllAsync(
            filter: null, includeProperties: null, orderBy: q => q.OrderBy(c => c.RevokedAt != null).ThenBy(c => c.Name), asNoTracking: true, cancellationToken: cancellationToken);
        var now = time.GetUtcNow();
        return clients.Select(c => ToSummary(c, now)).ToList();
    }

    public async Task<ValidationResponse<ApiClientSummary>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var client = await unitOfWork.Repository<ApiClient>().GetAsync(c => c.Id == id, asNoTracking: true, cancellationToken: cancellationToken);
        return client is null ? ValidationResponse.NotFound("API client not found.").As<ApiClientSummary>() : ToSummary(client, time.GetUtcNow());
    }

    public async Task<ValidationResponse<IssuedApiKey>> CreateAsync(SaveApiClientRequest request, CancellationToken cancellationToken = default)
    {
        var valid = await ValidateAsync(request, cancellationToken);
        if (valid.IsError)
        {
            return valid.As<IssuedApiKey>();
        }

        if (actor.UserId is not { } creator)
        {
            return ValidationResponse.Forbidden("Only a signed-in person can create API clients.").As<IssuedApiKey>();
        }

        var (key, prefix, hash) = ApiKeys.Generate();
        var expiresAt = time.GetUtcNow().AddDays(request.ExpiresInDays);
        var client = new ApiClient(request.Name, prefix, hash, request.Permissions, expiresAt, creator);
        client.Change(request.Name, request.Description, request.Permissions, request.RequestsPerMinute);
        await unitOfWork.Repository<ApiClient>().AddAsync(client, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success(new IssuedApiKey(client.Id, key, expiresAt), $"{client.Name} created. Copy the key now; it won't be shown again.");
    }

    public async Task<ValidationResponse> UpdateAsync(Guid id, SaveApiClientRequest request, CancellationToken cancellationToken = default)
    {
        var client = await unitOfWork.Repository<ApiClient>().GetByIdAsync(id, cancellationToken);
        if (client is null)
        {
            return ValidationResponse.NotFound("API client not found.");
        }

        // You can only manage a client whose access you hold, and only give it access you hold.
        var current = Guard(client.Permissions, "change this client");
        if (current.IsError)
        {
            return current;
        }

        var valid = await ValidateAsync(request with { ExpiresInDays = 1 }, cancellationToken);
        if (valid.IsError)
        {
            return valid;
        }

        client.Change(request.Name, request.Description, request.Permissions, request.RequestsPerMinute);
        await SaveAsync(cancellationToken);
        return ValidationResponse.Success($"{client.Name} saved. Calls already in flight finish with the old permissions.");
    }

    public async Task<ValidationResponse<IssuedApiKey>> RotateAsync(Guid id, int expiresInDays, CancellationToken cancellationToken = default)
    {
        var client = await unitOfWork.Repository<ApiClient>().GetByIdAsync(id, cancellationToken);
        if (client is null)
        {
            return ValidationResponse.NotFound("API client not found.").As<IssuedApiKey>();
        }

        if (client.IsRevoked)
        {
            return ValidationResponse.Failure("A revoked client can't be given a new key; create a new client.").As<IssuedApiKey>();
        }

        var guard = Guard(client.Permissions, "rotate this client's key");
        var lifetime = await LifetimeAsync(expiresInDays, cancellationToken);
        if (guard.IsError || lifetime.IsError)
        {
            return (guard.IsError ? guard : lifetime).As<IssuedApiKey>();
        }

        var (key, prefix, hash) = ApiKeys.Generate();
        var now = time.GetUtcNow();
        client.Rotate(prefix, hash, now.AddDays(expiresInDays), now);
        await SaveAsync(cancellationToken);
        return ValidationResponse.Success(new IssuedApiKey(client.Id, key, client.ExpiresAt), "New key issued; the old one no longer works. Copy it now.");
    }

    public async Task<ValidationResponse> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var client = await unitOfWork.Repository<ApiClient>().GetByIdAsync(id, cancellationToken);
        if (client is null)
        {
            return ValidationResponse.NotFound("API client not found.");
        }

        var guard = Guard(client.Permissions, "revoke this client");
        if (guard.IsError)
        {
            return guard;
        }

        client.Revoke(time.GetUtcNow());
        await SaveAsync(cancellationToken);
        return ValidationResponse.Success($"{client.Name} is revoked; its key no longer works.");
    }

    internal static ApiClientSummary ToSummary(ApiClient c, DateTimeOffset now) => new(
        c.Id, c.Name, c.Description, ApiKeys.Mask(c.KeyPrefix), c.Permissions, c.RequestsPerMinute, c.ExpiresAt, c.LastUsedAt, c.RevokedAt,
        c.DateCreated, c.UserCreated,
        c.IsRevoked ? ApiClientState.Revoked : c.ExpiresAt <= now ? ApiClientState.Expired : c.ExpiresAt - ExpiringSoon <= now ? ApiClientState.ExpiringSoon : ApiClientState.Active);

    private async Task<ValidationResponse> ValidateAsync(SaveApiClientRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > ApiClient.NameMaxLength)
        {
            errors.Add(new ValidationError(nameof(request.Name), $"Enter a name of up to {ApiClient.NameMaxLength} characters.", "required"));
        }

        if (request.Description?.Length > ApiClient.DescriptionMaxLength)
        {
            errors.Add(new ValidationError(nameof(request.Description), $"At most {ApiClient.DescriptionMaxLength} characters.", "length"));
        }

        if (request.Permissions is null || request.Permissions.Count == 0)
        {
            errors.Add(new ValidationError(nameof(request.Permissions), "Give the client at least one permission.", "required"));
        }
        else if (request.Permissions.FirstOrDefault(p => !PermissionCatalog.Exists(p)) is { } unknown)
        {
            errors.Add(new ValidationError(nameof(request.Permissions), $"There is no permission '{unknown}'.", "permission.unknown"));
        }
        else if (!await platform.IsCurrentAsync(cancellationToken) && request.Permissions.Any(PermissionCatalog.IsPlatform))
        {
            errors.Add(new ValidationError(nameof(request.Permissions), "Platform permissions can only be given to the platform's own clients.", "permission.platform"));
        }

        if (request.RequestsPerMinute is < 1 or > 100_000)
        {
            errors.Add(new ValidationError(nameof(request.RequestsPerMinute), "Between 1 and 100,000 requests a minute.", "range"));
        }

        var lifetime = await LifetimeAsync(request.ExpiresInDays, cancellationToken);
        errors.AddRange(lifetime.Errors);
        if (errors.Count > 0)
        {
            return ValidationResponse.Failure(errors);
        }

        return Guard(request.Permissions!, "give a client these permissions");
    }

    private async Task<ValidationResponse> LifetimeAsync(int days, CancellationToken cancellationToken)
    {
        var max = await settings.GetIntAsync(SettingCodes.Api.KeyLifetimeDays, cancellationToken);
        return days >= 1 && days <= max
            ? ValidationResponse.Success()
            : ValidationResponse.Failure(new ValidationError(nameof(SaveApiClientRequest.ExpiresInDays), $"Keys last between 1 and {max} days.", "range"));
    }

    private ValidationResponse Guard(IEnumerable<string> permissions, string action) => PermissionGuard.EnsureHoldsAll(actor, permissions, action);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await unitOfWork.SaveAsync(cancellationToken);
        await cache.RemoveByTagAsync(CacheTag, cancellationToken);
    }
}
