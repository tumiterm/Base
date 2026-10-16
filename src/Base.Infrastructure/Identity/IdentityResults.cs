using Base.Application.Common.Results;
using Microsoft.AspNetCore.Identity;

namespace Base.Infrastructure.Identity;

internal static class IdentityResults
{
    /// <summary>Maps Identity errors to field errors; password-rule failures land on <paramref name="passwordField"/>.</summary>
    public static ValidationResponse ToValidationResponse(this IdentityResult result, string passwordField = "Password")
    {
        if (result.Succeeded)
        {
            return ValidationResponse.Success();
        }

        return ValidationResponse.Failure(result.Errors.Select(e => new ValidationError(FieldFor(e.Code, passwordField), e.Description, e.Code)));
    }

    private static string FieldFor(string code, string passwordField) => code switch
    {
        _ when code.StartsWith("Password", StringComparison.Ordinal) && !string.Equals(code, nameof(IdentityErrorDescriber.PasswordMismatch), StringComparison.Ordinal) => passwordField,
        nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.InvalidEmail) or nameof(IdentityErrorDescriber.DuplicateUserName) => "Email",
        nameof(IdentityErrorDescriber.DuplicateRoleName) or nameof(IdentityErrorDescriber.InvalidRoleName) => "Name",
        _ => string.Empty,
    };
}

/// <summary>Carries an Identity failure out of a transaction so it rolls back, then becomes a ValidationResponse.</summary>
internal sealed class IdentityOperationException(ValidationResponse response) : Exception(response.Message)
{
    public ValidationResponse Response { get; } = response;

    public static void ThrowIfFailed(IdentityResult result, string passwordField = "Password")
    {
        if (!result.Succeeded)
        {
            throw new IdentityOperationException(result.ToValidationResponse(passwordField));
        }
    }
}
