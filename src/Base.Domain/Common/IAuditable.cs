namespace Base.Domain.Common;

/// <summary>
/// Creation/modification stamps. Set by the persistence interceptor, never by hand.
/// All timestamps are UTC; convert to local time in the UI only.
/// </summary>
public interface IAuditable
{
    DateTimeOffset DateCreated { get; set; }

    DateTimeOffset DateModified { get; set; }

    string? UserCreated { get; set; }

    string? UserModified { get; set; }
}
