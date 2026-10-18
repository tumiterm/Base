using Base.Application.Settings;
using Base.Application.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Identity;

/// <summary>
/// Builds set-password links from the <c>General.PublicBaseUrl</c> setting, never the request host. Tokens are made on
/// demand and never stored; an invite link works once (setting the password changes the security stamp) and expires
/// after the invite token lifespan.
/// </summary>
internal sealed class PasswordLinks(
    UserManager<ApplicationUser> users,
    IApplicationSettingService settings,
    IOptions<InviteTokenProviderOptions> inviteOptions,
    TimeProvider time)
{
    /// <summary>A fresh invite link for a user who hasn't set a password yet, to hand over by any channel.</summary>
    public async Task<InviteLink> CreateInviteAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var token = await users.GenerateUserTokenAsync(user, InviteTokenProvider.ProviderName, InviteTokenProvider.Purpose);
        return new InviteLink(Build(await BaseUrlAsync(cancellationToken), user, token, PasswordTokenKind.Invite), time.GetUtcNow() + inviteOptions.Value.TokenLifespan, user.DisplayName);
    }

    public async Task<string> BaseUrlAsync(CancellationToken cancellationToken) =>
        (await settings.GetAsync(SettingCodes.General.PublicBaseUrl, cancellationToken))!.TrimEnd('/');

    public static string Build(string baseUrl, ApplicationUser user, string token, PasswordTokenKind kind) =>
        $"{baseUrl}/account/set-password?kind={kind.ToString().ToLowerInvariant()}&userId={user.Id}&token={Uri.EscapeDataString(token)}";
}
