using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Identity;

/// <summary>
/// Invite tokens live longer than password-reset tokens (72 hours vs 1 hour). Like all Data Protection tokens they
/// embed the security stamp, so setting the password invalidates the link: it works once.
/// </summary>
internal sealed class InviteTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<InviteTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<ApplicationUser>> logger)
    : DataProtectorTokenProvider<ApplicationUser>(dataProtectionProvider, options, logger)
{
    public const string ProviderName = "Invite";
    public const string Purpose = "AcceptInvite";
}

internal sealed class InviteTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public InviteTokenProviderOptions()
    {
        Name = "InviteDataProtectorTokenProvider";
        TokenLifespan = TimeSpan.FromHours(72);
    }
}
