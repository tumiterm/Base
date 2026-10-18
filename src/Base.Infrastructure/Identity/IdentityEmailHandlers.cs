using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Application.Subscriptions;
using Base.Application.Users;
using Base.Domain.Subscriptions;
using Base.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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
    PasswordLinks links,
    AppDbContext db,
    ITenantContext tenant,
    ISubscriptionService subscriptions) : IDomainEventHandler<UserInvited>, IDomainEventHandler<PasswordResetRequested>
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
        if (domainEvent.Welcome)
        {
            await SendWelcomeAsync(user, token, cancellationToken);
            return;
        }

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

    /// <summary>The first administrator of a new organisation: the invitation, where to sign in, and the first steps.</summary>
    private async Task SendWelcomeAsync(ApplicationUser user, string token, CancellationToken cancellationToken)
    {
        string organisation;
        using (tenant.BeginSystemScope())
        {
            organisation = await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.TenantId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken) ?? "Your organisation";
        }

        var subscription = await subscriptions.GetAsync(tenant.TenantId, cancellationToken);
        var planLine = subscription switch
        {
            { Status: SubscriptionStatus.Trialing, TrialEndsAt: { } ends } =>
                $"You're on a free trial of the {subscription.PlanName} plan until {ends.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}.",
            not null => $"You're on the {subscription.PlanName} plan.",
            _ => string.Empty,
        };

        var baseUrl = await links.BaseUrlAsync(cancellationToken);
        await notifications.SendAsync(
            new Notification(
                NotificationTemplates.Tenancy.Welcome,
                new NotificationRecipient(user.Id, user.Email, null, user.DisplayName),
                new { Link = PasswordLinks.Build(baseUrl, user, token, PasswordTokenKind.Invite), OrganisationName = organisation, SignInUrl = $"{baseUrl}/account/login", PlanLine = planLine }),
            cancellationToken);
    }

    private async Task SendAsync(string template, ApplicationUser user, string token, PasswordTokenKind kind, CancellationToken cancellationToken)
    {
        var link = PasswordLinks.Build(await links.BaseUrlAsync(cancellationToken), user, token, kind);

        // Explicit address: an invited user's email isn't confirmed yet, and that's the point of the link.
        await notifications.SendAsync(
            new Notification(template, new NotificationRecipient(user.Id, user.Email, null, user.DisplayName), new { Link = link }),
            cancellationToken);
    }
}
