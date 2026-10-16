using Base.Application.Common.Results;
using FluentValidation;
using FluentValidation.Results;

namespace Base.Application.Common.Validation;

public static class ValidationExtensions
{
    public static ValidationResponse ToValidationResponse(this ValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsValid
            ? ValidationResponse.Success()
            : ValidationResponse.Failure(result.Errors.Select(e => new ValidationError(e.PropertyName, e.ErrorMessage, e.ErrorCode)));
    }

    public static async Task<ValidationResponse> ValidateToResponseAsync<T>(this IValidator<T> validator, T instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        var result = await validator.ValidateAsync(instance, cancellationToken);
        return result.ToValidationResponse();
    }
}
