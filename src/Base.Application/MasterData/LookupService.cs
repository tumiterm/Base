using System.ComponentModel;
using System.Text.RegularExpressions;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Messaging;
using Base.Domain.MasterData;
using Base.Domain.Tenancy;
using Microsoft.Extensions.Caching.Hybrid;

namespace Base.Application.MasterData;

[ImmutableObject(true)]
public sealed record LookupItem(Guid Id, string ListCode, string Code, string Name, int SortOrder, bool IsActive, bool IsSeeded);

public sealed record LookupListSummary(LookupListDefinition Definition, int Count, int ActiveCount);

/// <summary>
/// The current tenant's lookup lists: read (cached) by forms and validators, maintained by <c>MasterData.Manage</c>.
/// Records store codes; use <see cref="EnsureValidAsync"/> before saving one.
/// </summary>
public interface ILookupService
{
    /// <summary>The list's entries in order. Inactive ones only when asked for (to label existing records).</summary>
    Task<IReadOnlyList<LookupItem>> GetAsync(string listCode, bool includeInactive = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LookupListSummary>> ListsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fails, on <paramref name="field"/>, for any code that isn't an active entry. Codes in <paramref name="existing"/>
    /// (what the record already holds) stay valid after their entry is deactivated.
    /// </summary>
    Task<ValidationResponse> EnsureValidAsync(string listCode, IEnumerable<string?> codes, string field, IEnumerable<string?>? existing = null, CancellationToken cancellationToken = default);

    /// <summary>Finds an active entry by code or name, ignoring case (for imports and APIs that accept names).</summary>
    Task<LookupItem?> FindAsync(string listCode, string codeOrName, CancellationToken cancellationToken = default);

    Task<ValidationResponse<Guid>> AddAsync(string listCode, string name, CancellationToken cancellationToken = default);

    Task<ValidationResponse> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);

    Task<ValidationResponse> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default);

    /// <summary>Swaps the entry with its neighbour: -1 moves it up, +1 down.</summary>
    Task<ValidationResponse> MoveAsync(Guid id, int direction, CancellationToken cancellationToken = default);
}

/// <summary>Gives a tenant the catalogue's starting values it doesn't have yet. Safe to run again.</summary>
public interface ILookupDefaults
{
    Task<int> EnsureAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

internal sealed partial class LookupService(IUnitOfWork unitOfWork, ITenantContext tenant, HybridCache cache) : ILookupService, ILookupDefaults
{
    private const string CacheTag = "lookups";
    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromMinutes(30) };
    private static readonly string[] CacheTags = [CacheTag];

    public async Task<IReadOnlyList<LookupItem>> GetAsync(string listCode, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        LookupCatalog.Get(listCode);
        var all = await AllAsync(cancellationToken);
        return all.Where(v => string.Equals(v.ListCode, listCode, StringComparison.Ordinal) && (includeInactive || v.IsActive)).ToList();
    }

    public async Task<IReadOnlyList<LookupListSummary>> ListsAsync(CancellationToken cancellationToken = default)
    {
        var all = await AllAsync(cancellationToken);
        return LookupCatalog.All.Select(d =>
        {
            var values = all.Where(v => string.Equals(v.ListCode, d.Code, StringComparison.Ordinal)).ToList();
            return new LookupListSummary(d, values.Count, values.Count(v => v.IsActive));
        }).ToList();
    }

    public async Task<ValidationResponse> EnsureValidAsync(
        string listCode, IEnumerable<string?> codes, string field, IEnumerable<string?>? existing = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var keep = (existing ?? []).Where(c => c is not null).ToHashSet(StringComparer.Ordinal);
        var active = (await GetAsync(listCode, includeInactive: false, cancellationToken)).Select(v => v.Code).ToHashSet(StringComparer.Ordinal);
        var unknown = codes.Where(c => !string.IsNullOrWhiteSpace(c) && !active.Contains(c!) && !keep.Contains(c!)).ToList();
        return unknown.Count == 0
            ? ValidationResponse.Success()
            : ValidationResponse.Failure(new ValidationError(field, $"Choose from the {LookupCatalog.Get(listCode).Name.ToLowerInvariant()} list.", "lookup.unknown"));
    }

    public async Task<LookupItem?> FindAsync(string listCode, string codeOrName, CancellationToken cancellationToken = default)
    {
        var wanted = codeOrName?.Trim() ?? string.Empty;
        return (await GetAsync(listCode, includeInactive: false, cancellationToken))
            .FirstOrDefault(v => string.Equals(v.Code, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(v.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ValidationResponse<Guid>> AddAsync(string listCode, string name, CancellationToken cancellationToken = default)
    {
        var definition = LookupCatalog.Find(listCode);
        if (definition is null)
        {
            return ValidationResponse.NotFound("There is no such list.").As<Guid>();
        }

        if (!definition.AllowTenantValues)
        {
            return ValidationResponse.Failure($"{definition.Name} is a fixed list; rename or hide its entries instead.").As<Guid>();
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > LookupValue.NameMaxLength)
        {
            return ValidationResponse.Failure(new ValidationError("name", $"Enter a name of up to {LookupValue.NameMaxLength} characters.", "required")).As<Guid>();
        }

        var existing = await GetAsync(listCode, includeInactive: true, cancellationToken);
        if (existing.Any(v => string.Equals(v.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return ValidationResponse.Failure(new ValidationError("name", "This list already has an entry with that name.", "lookup.duplicate")).As<Guid>();
        }

        var code = UniqueCode(Slug(trimmed), existing.Select(v => v.Code).ToHashSet(StringComparer.Ordinal));
        var value = new LookupValue(listCode, code, trimmed, existing.Count == 0 ? 10 : existing.Max(v => v.SortOrder) + 10, isSeeded: false);
        await unitOfWork.Repository<LookupValue>().AddAsync(value, cancellationToken);
        await SaveAsync(cancellationToken);
        return ValidationResponse.Success(value.Id, $"{trimmed} added.");
    }

    public Task<ValidationResponse> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default) =>
        ChangeAsync(id, async value =>
        {
            var trimmed = name?.Trim() ?? string.Empty;
            if (trimmed.Length is 0 or > LookupValue.NameMaxLength)
            {
                return ValidationResponse.Failure(new ValidationError("name", $"Enter a name of up to {LookupValue.NameMaxLength} characters.", "required"));
            }

            var siblings = await GetAsync(value.ListCode, includeInactive: true, cancellationToken);
            if (siblings.Any(v => v.Id != id && string.Equals(v.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return ValidationResponse.Failure(new ValidationError("name", "This list already has an entry with that name.", "lookup.duplicate"));
            }

            value.Rename(trimmed);
            return ValidationResponse.Success("Renamed. Records using it show the new name.");
        }, cancellationToken);

    public Task<ValidationResponse> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default) =>
        ChangeAsync(id, value =>
        {
            if (active)
            {
                value.Activate();
            }
            else
            {
                value.Deactivate();
            }

            return Task.FromResult(ValidationResponse.Success(active ? $"{value.Name} can be chosen again." : $"{value.Name} is hidden from new choices; records using it keep it."));
        }, cancellationToken);

    public async Task<ValidationResponse> MoveAsync(Guid id, int direction, CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.Repository<LookupValue>();
        var value = await repository.GetByIdAsync(id, cancellationToken);
        if (value is null)
        {
            return ValidationResponse.NotFound("Entry not found.");
        }

        var ordered = (await repository.GetAllAsync(v => v.ListCode == value.ListCode, includeProperties: null, cancellationToken: cancellationToken))
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var index = ordered.FindIndex(v => v.Id == id);
        var target = index + Math.Sign(direction);
        if (direction == 0 || target < 0 || target >= ordered.Count)
        {
            return ValidationResponse.Success();
        }

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].MoveTo((i + 1) * 10);
        }

        await SaveAsync(cancellationToken);
        return ValidationResponse.Success();
    }

    public async Task<int> EnsureAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var system = tenant.BeginSystemScope();
        var repository = unitOfWork.Repository<LookupValue>();
        var present = (await repository.GetAllAsync(v => new { v.ListCode, v.Code, v.SortOrder }, v => v.TenantId == tenantId, cancellationToken: cancellationToken))
            .GroupBy(v => v.ListCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (Codes: g.Select(v => v.Code).ToHashSet(StringComparer.Ordinal), Max: g.Max(v => v.SortOrder)), StringComparer.Ordinal);

        var added = 0;
        foreach (var list in LookupCatalog.All)
        {
            var (codes, max) = present.GetValueOrDefault(list.Code, ([], 0));
            foreach (var value in list.Defaults.Where(d => !codes.Contains(d.Code)))
            {
                max += 10;
                await repository.AddAsync(new LookupValue(list.Code, value.Code, value.Name, max, isSeeded: true) { TenantId = tenantId }, cancellationToken);
                added++;
            }
        }

        if (added > 0)
        {
            await SaveAsync(cancellationToken);
        }

        return added;
    }

    private async Task<IReadOnlyList<LookupItem>> AllAsync(CancellationToken cancellationToken)
    {
        if (!tenant.IsResolved)
        {
            return [];
        }

        return await cache.GetOrCreateAsync(
            $"lookups:{tenant.TenantId}",
            unitOfWork,
            static async (uow, ct) => (await uow.Repository<LookupValue>().GetAllAsync(
                v => new LookupItem(v.Id, v.ListCode, v.Code, v.Name, v.SortOrder, v.IsActive, v.IsSeeded),
                orderBy: q => q.OrderBy(v => v.ListCode).ThenBy(v => v.SortOrder).ThenBy(v => v.Name),
                cancellationToken: ct)).ToList(),
            CacheOptions,
            CacheTags,
            cancellationToken);
    }

    private async Task<ValidationResponse> ChangeAsync(Guid id, Func<LookupValue, Task<ValidationResponse>> change, CancellationToken cancellationToken)
    {
        var value = await unitOfWork.Repository<LookupValue>().GetByIdAsync(id, cancellationToken);
        if (value is null)
        {
            return ValidationResponse.NotFound("Entry not found.");
        }

        var result = await change(value);
        if (result.IsSuccess)
        {
            await SaveAsync(cancellationToken);
        }

        return result;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await unitOfWork.SaveAsync(cancellationToken);
        await cache.RemoveByTagAsync(CacheTag, cancellationToken);
    }

    private static string Slug(string name)
    {
        var slug = NonCodeCharacters().Replace(name.ToLowerInvariant(), "-").Trim('-');
        slug = slug.Length > LookupValue.CodeMaxLength - 4 ? slug[..(LookupValue.CodeMaxLength - 4)].TrimEnd('-') : slug;
        return slug.Length == 0 ? "value" : slug;
    }

    private static string UniqueCode(string baseCode, HashSet<string> taken)
    {
        var code = baseCode;
        for (var n = 2; taken.Contains(code); n++)
        {
            code = $"{baseCode}-{n}";
        }

        return code;
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.None, 1000)]
    private static partial Regex NonCodeCharacters();
}

/// <summary>A new tenant starts with the catalogue's lookup values.</summary>
internal sealed class LookupDefaultsForNewTenants(ILookupDefaults defaults) : IDomainEventHandler<TenantCreated>
{
    public async Task HandleAsync(TenantCreated domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await defaults.EnsureAsync(domainEvent.TenantId, cancellationToken);
    }
}
