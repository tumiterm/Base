using Base.Application.Common.Abstractions;
using Base.Application.Security;
using Base.Domain.Files;

namespace Base.Application.Files;

/// <summary>
/// "May this user have this file for this purpose?" Asked on every download/delete; a raw file id is never enough.
/// Replace or decorate the default per project (e.g. per-category rules).
/// </summary>
public interface IStoredFileAccessPolicy
{
    Task<bool> CanAccessAsync(StoredFile file, FileAccessPurpose purpose, CancellationToken cancellationToken = default);
}

/// <summary>Default rule: owners may read and delete their own files; otherwise the matching "All" permission is needed.</summary>
internal sealed class DefaultStoredFileAccessPolicy(ICurrentUser currentUser) : IStoredFileAccessPolicy
{
    public Task<bool> CanAccessAsync(StoredFile file, FileAccessPurpose purpose, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!currentUser.IsAuthenticated)
        {
            return Task.FromResult(false);
        }

        if (file.OwnerUserId is { } owner && owner == currentUser.UserId)
        {
            return Task.FromResult(true);
        }

        var permission = purpose == FileAccessPurpose.Delete ? PermissionCodes.Files.DeleteAll : PermissionCodes.Files.ReadAll;
        return Task.FromResult(currentUser.HasPermission(permission));
    }
}
