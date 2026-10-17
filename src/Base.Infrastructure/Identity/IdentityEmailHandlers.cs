using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Application.Settings;
using Base.Application.Users;
using Microsoft.AspNetCore.Identity;

namespace Base.Infrastructure.Identity;

/// <summary>
/// Sends invite and reset links from the outbox, after the change that requested them has committed. Tokens are
/// generated here, at send time, and the templates are sensitive, so neither the token nor the link is ever stored.
/// Links use the <c>General.PublicBaseUrl</c> setting, never the request's Host header. If delivery fails the
/// notification service throws and the outbox retries with a fresh token.
/// </summary>
internal sealed class IdentityEmailHandlers(
    UserManager<ApplicationUser> users,
    INotificationService notifications,
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
        await SendAsync(NotificationTemplates.Identity.UserInvited, user, token, PasswordTokenKind.Invite, cancellationToken);
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
        await SendAsync(NotificationTemplates.Identity.PasswordReset, user, token, PasswordTokenKind.Reset, cancellationToken);
    }

    private async Task SendAsync(string template, ApplicationUser user, string token, PasswordTokenKind kind, CancellationToken cancellationToken)
    {
        var baseUrl = (await settings.GetAsync(SettingCodes.General.PublicBaseUrl, cancellationToken))!.TrimEnd('/');
        var link = $"{baseUrl}/account/set-password?kind={kind.ToString().ToLowerInvariant()}&userId={user.Id}&token={Uri.EscapeDataString(token)}";

        // Explicit address: an invited user's email isn't confirmed yet, and that's the point of the link.
        await notifications.SendAsync(
            new Notification(template, new NotificationRecipient(user.Id, user.Email, null, user.DisplayName), new { Link = link }),
            cancellationToken);
    }
}
