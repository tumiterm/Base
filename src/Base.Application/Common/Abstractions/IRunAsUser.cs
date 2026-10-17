namespace Base.Application.Common.Abstractions;

/// <summary>
/// Lets background work act as the person who asked for it, with their permissions as they are now, so the same rules
/// apply as in a request (e.g. an import can't grant a role its initiator doesn't hold).
/// </summary>
public interface IRunAsUser
{
    /// <summary>
    /// Makes <see cref="ICurrentUser"/> in this DI scope that user until disposed. Null when the user can no longer act
    /// (deleted or locked out); the caller should then fail the work.
    /// </summary>
    Task<IDisposable?> BeginAsync(Guid userId, CancellationToken cancellationToken = default);
}
