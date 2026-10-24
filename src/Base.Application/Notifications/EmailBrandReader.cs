using Base.Application.Branding;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Settings;
using Base.Domain.Tenancy;

namespace Base.Application.Notifications;

/// <summary>
/// The current tenant as its emails show it: its application name, its logo and colour (when its plan includes
/// custom branding; otherwise the defaults), and who it is for the footer: the platform's seller details from
/// Settings → Invoices, or the organisation's billing name and address.
/// </summary>
internal sealed class EmailBrandReader(
    IApplicationSettingService settings,
    IBrandingService branding,
    ITenantAddress address,
    IPlatformTenant platform,
    ITenantContext tenant,
    IUnitOfWork unitOfWork)
{
    public async Task<EmailBrand> ReadAsync(CancellationToken cancellationToken)
    {
        var theme = await branding.GetAsync(cancellationToken);
        var baseUrl = await address.BaseUrlAsync(cancellationToken);
        string? company;
        if (await platform.IsCurrentAsync(cancellationToken))
        {
            var seller = await settings.GetAsync(SettingCodes.Invoices.SellerName, cancellationToken);
            company = Join(string.IsNullOrWhiteSpace(seller) ? theme.ApplicationName : seller, await settings.GetAsync(SettingCodes.Invoices.SellerAddress, cancellationToken));
        }
        else
        {
            var organisation = tenant.IsResolved ? await unitOfWork.Repository<Tenant>().GetByIdAsync(tenant.TenantId, cancellationToken) : null;
            company = organisation is null ? null : Join(organisation.LegalName ?? organisation.Name, organisation.Address);
        }

        return new EmailBrand(
            theme.ApplicationName,
            theme.LogoFileId is null ? null : $"{baseUrl}/branding/logo?v={theme.Version}",
            IsColour(theme.PrimaryColor) ? theme.PrimaryColor.ToLowerInvariant() : EmailBrand.DefaultAccent,
            company);
    }

    private static string Join(string name, string? postal) =>
        string.IsNullOrWhiteSpace(postal)
            ? name
            : name + ", " + string.Join(", ", postal.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool IsColour(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).ToString().All(Uri.IsHexDigit);
}
