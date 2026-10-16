namespace Base.Domain.Common;

/// <summary>
/// Rows are retired, not removed. Retired rows are hidden by the <c>SoftDelete</c> named query filter.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }

    DateTimeOffset? DateDeleted { get; set; }

    string? UserDeleted { get; set; }
}
