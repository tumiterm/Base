using System.Net;
using Base.Application.Common.Abstractions;
using Base.Application.Messaging;
using Base.Application.Settings;
using Base.Application.Users;
using Microsoft.AspNetCore.Identity;

namespace Base.Infrastructure.Identity;

/// <summary>
/// Sends invite and reset emails from the outbox, after the change that requested them has committed. Tokens are
/// generated here, at send time, so they are never stored. Links use the <c>General.PublicBaseUrl</c> setting, never
/// the request's Host header. A retry may send a second email with a fresh link; both links work until one is used.
/// </summary>
internal sealed class IdentityEmailHandlers(
    UserManager<ApplicationUser> users,
    IEmailSender email,
    IApplicationSettingService settings) : IDomainEventHandler<UserInvited>, IDomainEventHandler<PasswordResetRequested>
{
    public async Task HandleAsync(UserInvited domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var user = await users.FindByIdAsync(domainEvent.UserId.ToString());
        if (user?.Email is null || user.HasPassword)
        {
            return; // Gone, or the invite was already accepted.
        }

        var token = await users.GenerateUserTokenAsync(user, InviteTokenProvider.ProviderName, InviteTokenProvider.Purpose);
        var appName = await AppNameAsync(cancellationToken);
        var link = await LinkAsync(user.Id, token, PasswordTokenKind.Invite, cancellationToken);

        await email.SendAsync(
            new EmailMessage(
                [user.Email],
                $"You've been invited to {appName}",
                Html($"Hello {user.DisplayName},", $"You've been invited to {appName}. Choose a password to activate your account. The link expires in 3 days.", "Set my password", link)),
            cancellationToken);
    }

    public async Task HandleAsync(PasswordResetRequested domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var user = await users.FindByIdAsync(domainEvent.UserId.ToString());
        if (user?.Email is null || !user.HasPassword)
        {
            return;
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var appName = await AppNameAsync(cancellationToken);
        var link = await LinkAsync(user.Id, token, PasswordTokenKind.Reset, cancellationToken);

        await email.SendAsync(
            new EmailMessage(
                [user.Email],
                $"Reset your {appName} password",
                Html($"Hello {user.DisplayName},", "Someone asked to reset your password. If it was you, use the link below within the next hour. If not, you can ignore this email.", "Reset my password", link)),
            cancellationToken);
    }

    private async Task<string> AppNameAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(SettingCodes.General.ApplicationName, cancellationToken) ?? "Base";

    private async Task<string> LinkAsync(Guid userId, string token, PasswordTokenKind kind, CancellationToken cancellationToken)
    {
        var baseUrl = (await settings.GetAsync(SettingCodes.General.PublicBaseUrl, cancellationToken))!.TrimEnd('/');
        return $"{baseUrl}/account/set-password?kind={kind.ToString().ToLowerInvariant()}&userId={userId}&token={Uri.EscapeDataString(token)}";
    }

    private static string Html(string greeting, string body, string action, string link) =>
        $"""
         <p>{WebUtility.HtmlEncode(greeting)}</p>
         <p>{WebUtility.HtmlEncode(body)}</p>
         <p><a href="{WebUtility.HtmlEncode(link)}">{WebUtility.HtmlEncode(action)}</a></p>
         <p style="color:#666;font-size:12px">If the button doesn't work, copy this address into your browser:<br>{WebUtility.HtmlEncode(link)}</p>
         """;
}
