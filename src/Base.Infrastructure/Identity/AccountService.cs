using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Common.Validation;
using Base.Application.Notifications;
using Base.Application.Settings;
using Base.Application.Users;
using FluentValidation;
using Microsoft.AspNetCore.Identity;

namespace Base.Infrastructure.Identity;

internal sealed class AccountService(
    UserManager<ApplicationUser> users,
    INotificationService notifications,
    ICurrentUser currentUser,
    IApplicationSettingService settings,
    TimeProvider time,
    IValidator<SetPasswordRequest> setPasswordValidator,
    IValidator<ChangePasswordRequest> changePasswordValidator) : IAccountService
{
    private const string InvalidLink = "This link is invalid, has already been used, or has expired. Ask for a new one.";

    public async Task<ValidationResponse> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        const string Response = "If an account exists for that address, we've emailed a link to reset the password.";
        if (string.IsNullOrWhiteSpace(email))
        {
            return ValidationResponse.Failure(new ValidationError("Email", "Enter your email address."));
        }

        if (!await settings.GetBoolAsync(SettingCodes.Security.PasswordResetEnabled, cancellationToken))
        {
            return ValidationResponse.Forbidden("Password reset by email is switched off. Ask your administrator to reset it.");
        }

        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not null && !user.IsLockedOut(time.GetUtcNow()))
        {
            user.Raise(user.HasPassword ? new PasswordResetRequested(user.Id) : new UserInvited(user.Id));
            await users.UpdateAsync(user);
        }

        return ValidationResponse.Success(Response);
    }

    public async Task<ValidationResponse> SetPasswordAsync(SetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await setPasswordValidator.ValidateToResponseAsync(request, cancellationToken);
        if (validation.IsError)
        {
            return validation;
        }

        var user = await users.FindByIdAsync(request.UserId.ToString());
        if (user is null)
        {
            return ValidationResponse.Failure(InvalidLink, "token.invalid");
        }

        IdentityResult result;
        if (request.Kind == PasswordTokenKind.Invite)
        {
            if (user.HasPassword || !await users.VerifyUserTokenAsync(user, InviteTokenProvider.ProviderName, InviteTokenProvider.Purpose, request.Token))
            {
                return ValidationResponse.Failure(InvalidLink, "token.invalid");
            }

            // AddPassword rotates the security stamp, which invalidates the invite token: it works once.
            result = await users.AddPasswordAsync(user, request.NewPassword);
        }
        else
        {
            result = await users.ResetPasswordAsync(user, request.Token, request.NewPassword);
            if (!result.Succeeded && result.Errors.Any(e => string.Equals(e.Code, nameof(IdentityErrorDescriber.InvalidToken), StringComparison.Ordinal)))
            {
                return ValidationResponse.Failure(InvalidLink, "token.invalid");
            }
        }

        if (!result.Succeeded)
        {
            return result.ToValidationResponse(nameof(SetPasswordRequest.NewPassword));
        }

        // Following the emailed link proves the address.
        user.EmailConfirmed = true;
        await users.ResetAccessFailedCountAsync(user);
        await users.UpdateAsync(user);
        if (request.Kind == PasswordTokenKind.Reset)
        {
            await NotifyPasswordChangedAsync(user, cancellationToken);
        }

        return ValidationResponse.Success("Your password has been set. You can sign in now.");
    }

    public async Task<ValidationResponse> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await changePasswordValidator.ValidateToResponseAsync(request, cancellationToken);
        if (validation.IsError)
        {
            return validation;
        }

        var user = currentUser.UserId is { } id ? await users.FindByIdAsync(id.ToString()) : null;
        if (user is null)
        {
            return ValidationResponse.Unauthorized();
        }

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (result.Errors.Any(e => string.Equals(e.Code, nameof(IdentityErrorDescriber.PasswordMismatch), StringComparison.Ordinal)))
        {
            return ValidationResponse.Failure(new ValidationError(nameof(ChangePasswordRequest.CurrentPassword), "The current password is incorrect."));
        }

        if (!result.Succeeded)
        {
            return result.ToValidationResponse(nameof(ChangePasswordRequest.NewPassword));
        }

        await NotifyPasswordChangedAsync(user, cancellationToken);
        return ValidationResponse.Success("Your password has been changed.");
    }

    /// <summary>Tells the owner, so an unexpected change gets noticed. Queued: never fails the password change.</summary>
    private async Task NotifyPasswordChangedAsync(ApplicationUser user, CancellationToken cancellationToken) =>
        await notifications.SendAsync(new Notification(NotificationTemplates.Identity.PasswordChanged, NotificationRecipient.ToUser(user.Id)), cancellationToken);
}
