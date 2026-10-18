using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Settings;
using Base.Application.Users;
using Base.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Identity;

/// <summary>
/// Builds set-password links for the current tenant, never from the request host. Tokens are made on demand and never
/// stored; an invite link works once (setting the password changes the security stamp) and expires after the invite
/// token lifespan.
/// </summary>
internal sealed class PasswordLinks(
    UserManager<ApplicationUser> users,
    IApplicationSettingService settings,
    ITenantContext tenant,
    IPlatformTenant platform,
    AppDbContext db,
    IOptions<InviteTokenProviderOptions> inviteOptions,
    TimeProvider time)
{
    /// <summary>A fresh invite link for a user who hasn't set a password yet, to hand over by any channel.</summary>
    public async Task<ValidationResponse<InviteLink>> CreateInviteAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        if (await ReachableBaseUrlAsync(cancellationToken) is not { } baseUrl)
        {
            return ValidationResponse.Conflict("This organisation has no verified address yet, so nobody could open the link. Verify one of its domains first.").As<InviteLink>();
        }

        var token = await users.GenerateUserTokenAsync(user, InviteTokenProvider.ProviderName, InviteTokenProvider.Purpose);
        return new InviteLink(Build(baseUrl, user, token, PasswordTokenKind.Invite), time.GetUtcNow() + inviteOptions.Value.TokenLifespan, user.DisplayName);
    }

    /// <summary>The address for the current tenant's links, or the <c>General.PublicBaseUrl</c> setting when it has none.</summary>
    public async Task<string> BaseUrlAsync(CancellationToken cancellationToken) =>
        await ReachableBaseUrlAsync(cancellationToken) ?? await ConfiguredAsync(cancellationToken);

    public static string Build(string baseUrl, ApplicationUser user, string token, PasswordTokenKind kind) =>
        $"{baseUrl}/account/set-password?kind={kind.ToString().ToLowerInvariant()}&userId={user.Id}&token={Uri.EscapeDataString(token)}";

    /// <summary>
    /// An address that opens the current tenant, so the link finds its user. The platform tenant uses
    /// <c>General.PublicBaseUrl</c>; any other tenant is reached only through a verified domain: the setting when it
    /// names one of them, otherwise the first one (keeping the setting's scheme and port). Null when it has none.
    /// </summary>
    private async Task<string?> ReachableBaseUrlAsync(CancellationToken cancellationToken)
    {
        var configured = await ConfiguredAsync(cancellationToken);
        if (await platform.IsCurrentAsync(cancellationToken))
        {
            return configured;
        }

        List<string> hosts;
        using (tenant.BeginSystemScope())
        {
            hosts = await db.TenantDomains.AsNoTracking()
                .Where(d => d.TenantId == tenant.TenantId && d.IsVerified)
                .OrderBy(d => d.DateCreated)
                .Select(d => d.Host)
                .ToListAsync(cancellationToken);
        }

        if (hosts.Count == 0)
        {
            return null;
        }

        var uri = Uri.TryCreate(configured, UriKind.Absolute, out var parsed) ? parsed : null;
        if (uri is not null && hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return configured;
        }

        var scheme = uri?.Scheme ?? Uri.UriSchemeHttps;
        var port = uri is { IsDefaultPort: false } ? $":{uri.Port}" : string.Empty;
        return $"{scheme}://{hosts[0]}{port}";
    }

    private async Task<string> ConfiguredAsync(CancellationToken cancellationToken) =>
        (await settings.GetAsync(SettingCodes.General.PublicBaseUrl, cancellationToken))!.TrimEnd('/');
}
