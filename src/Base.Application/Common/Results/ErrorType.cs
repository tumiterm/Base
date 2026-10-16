namespace Base.Application.Common.Results;

/// <summary>Why an operation failed. Drives the HTTP status / UI treatment; never branch on message text.</summary>
public enum ErrorType
{
    None = 0,
    Validation = 1,
    NotFound = 2,
    Forbidden = 3,
    Unauthorized = 4,
    Conflict = 5,
    Unexpected = 6,
}
