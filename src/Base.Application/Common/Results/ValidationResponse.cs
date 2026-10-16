namespace Base.Application.Common.Results;

/// <summary>
/// The outcome of a use case. Immutable; created through factories only.
/// <see cref="IsSuccess"/> is the single source of truth (FO had overlapping IsValid/IsError flags).
/// </summary>
public class ValidationResponse
{
    private static readonly ValidationResponse SuccessInstance = new(true, ErrorType.None, null, null, []);

    protected ValidationResponse(bool isSuccess, ErrorType errorType, string? message, string? messageTitle, IReadOnlyList<ValidationError> errors)
    {
        IsSuccess = isSuccess;
        ErrorType = errorType;
        Message = message;
        MessageTitle = messageTitle;
        Errors = errors;
    }

    public bool IsSuccess { get; }

    public bool IsError => !IsSuccess;

    public ErrorType ErrorType { get; }

    /// <summary>User-facing summary (success confirmation or failure reason).</summary>
    public string? Message { get; }

    public string? MessageTitle { get; }

    public IReadOnlyList<ValidationError> Errors { get; }

    public static ValidationResponse Success(string? message = null, string? title = null) =>
        message is null && title is null ? SuccessInstance : new(true, ErrorType.None, message, title, []);

    public static ValidationResponse<T> Success<T>(T data, string? message = null) => ValidationResponse<T>.Success(data, message);

    public static ValidationResponse Failure(string message, string? code = null) =>
        new(false, ErrorType.Validation, message, null, [ValidationError.General(message, code)]);

    public static ValidationResponse Failure(ValidationError error) =>
        new(false, ErrorType.Validation, error.Message, null, [error]);

    public static ValidationResponse Failure(IEnumerable<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var list = errors.ToArray();
        if (list.Length == 0)
        {
            throw new ArgumentException("A failure needs at least one error.", nameof(errors));
        }

        return new(false, ErrorType.Validation, list[0].Message, null, list);
    }

    public static ValidationResponse NotFound(string message = "The requested item was not found.") => Error(ErrorType.NotFound, message);

    public static ValidationResponse Forbidden(string message = "You do not have permission to do this.") => Error(ErrorType.Forbidden, message);

    public static ValidationResponse Unauthorized(string message = "You need to sign in to do this.") => Error(ErrorType.Unauthorized, message);

    public static ValidationResponse Conflict(string message) => Error(ErrorType.Conflict, message);

    public static ValidationResponse Error(ErrorType type, string message, string? code = null)
    {
        if (type == ErrorType.None)
        {
            throw new ArgumentException("Use Success() for a successful outcome.", nameof(type));
        }

        return new(false, type, message, null, [ValidationError.General(message, code)]);
    }

    /// <summary>Carries this failure into a typed response (e.g. a guard clause inside a method returning data).</summary>
    public ValidationResponse<T> As<T>()
    {
        if (IsSuccess)
        {
            throw new InvalidOperationException("Only failures can be converted; a typed success needs data.");
        }

        return ValidationResponse<T>.FromFailure(this);
    }

    public override string ToString() => IsSuccess ? "Success" : $"{ErrorType}: {Message}";
}

/// <summary>An outcome that carries data on success.</summary>
public sealed class ValidationResponse<T> : ValidationResponse
{
    private ValidationResponse(bool isSuccess, ErrorType errorType, string? message, string? messageTitle, IReadOnlyList<ValidationError> errors, T? data)
        : base(isSuccess, errorType, message, messageTitle, errors)
    {
        Data = data;
    }

    /// <summary>The payload. Only meaningful when <see cref="ValidationResponse.IsSuccess"/> is true.</summary>
    public T? Data { get; }

    public static ValidationResponse<T> Success(T data, string? message = null) => new(true, ErrorType.None, message, null, [], data);

    internal static ValidationResponse<T> FromFailure(ValidationResponse failure) =>
        new(false, failure.ErrorType, failure.Message, failure.MessageTitle, failure.Errors, default);

    /// <summary>Unwraps the data, throwing if this is a failure. For tests and code paths that already checked success.</summary>
    public T GetDataOrThrow() =>
        IsSuccess ? Data! : throw new InvalidOperationException($"Cannot read data from a failed response ({this}).");

    public static implicit operator ValidationResponse<T>(T data) => Success(data);
}
