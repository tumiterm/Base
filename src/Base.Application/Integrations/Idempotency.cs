namespace Base.Application.Integrations;

public enum IdempotencyOutcome
{
    /// <summary>First time this key is seen: do the work, then complete or abandon the claim.</summary>
    Started = 0,

    /// <summary>Already done: send the stored response again.</summary>
    Replay = 1,

    /// <summary>The first request with this key is still running.</summary>
    InProgress = 2,

    /// <summary>The key was used before for a different request.</summary>
    Mismatch = 3,
}

public sealed record IdempotencyClaim(IdempotencyOutcome Outcome, Guid RecordId = default, int StatusCode = 0, string? ContentType = null, byte[]? Body = null);

/// <summary>
/// Remembers responses to requests sent with an <c>Idempotency-Key</c>, per tenant and caller, for the
/// <c>Api.IdempotencyHours</c> window, so a client that retries after a timeout gets the first answer instead of the work
/// being done twice (a payment recorded twice, two people created).
/// </summary>
public interface IIdempotencyStore
{
    Task<IdempotencyClaim> BeginAsync(string caller, string key, string requestHash, CancellationToken cancellationToken = default);

    Task CompleteAsync(Guid recordId, int statusCode, string? contentType, byte[] body, CancellationToken cancellationToken = default);

    /// <summary>Forgets the claim (the request failed with a server error), so a retry runs again.</summary>
    Task AbandonAsync(Guid recordId, CancellationToken cancellationToken = default);
}
