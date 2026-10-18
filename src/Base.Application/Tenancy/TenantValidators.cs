using Base.Domain.Common;
using Base.Domain.Tenancy;
using FluentValidation;

namespace Base.Application.Tenancy;

public static class TenantRules
{
    /// <summary>Lower-case letters, digits and dashes; it appears in URLs and object keys.</summary>
    public const string CodePattern = "^[a-z0-9](?:[a-z0-9-]{0,48}[a-z0-9])?$";

    /// <summary>A DNS host name (no scheme, port or path).</summary>
    public const string HostPattern = @"^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)*[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$";

    public const string HostMessage = "Enter a host name such as mtn.base.co.za (or mtn.dev.localhost on your own machine), without https:// or a port.";

    /// <summary>True when <paramref name="host"/>, once normalised (scheme, port and path dropped), is a DNS host name.</summary>
    public static bool IsValidHost(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && System.Text.RegularExpressions.Regex.IsMatch(TenantDomain.Normalize(host), HostPattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
}

/// <summary>Where a tenant's domain is opened: the platform address's scheme and port with the tenant's host.</summary>
public static class TenantAddresses
{
    /// <summary>
    /// <c>mtn.base.co.za</c> with <c>https://base.co.za</c> gives <c>https://mtn.base.co.za</c>; with
    /// <c>https://localhost:7262</c> (development) it gives <c>https://mtn.base.co.za:7262</c>.
    /// </summary>
    public static string Url(string host, string? platformBaseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var uri = Uri.TryCreate(platformBaseUrl, UriKind.Absolute, out var parsed) ? parsed : null;
        var scheme = uri?.Scheme ?? Uri.UriSchemeHttps;
        var port = uri is { IsDefaultPort: false } ? $":{uri.Port}" : string.Empty;
        return $"{scheme}://{host}{port}";
    }
}

internal sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(INamedEntity.NameMaxLength);
        RuleFor(r => r.Code).NotEmpty().Matches(TenantRules.CodePattern).WithMessage("Use 1–50 lower-case letters, digits or dashes, starting and ending with a letter or digit.");
        RuleFor(r => r.Host).Must(TenantRules.IsValidHost).WithMessage(TenantRules.HostMessage).When(r => !string.IsNullOrWhiteSpace(r.Host));
        RuleFor(r => r.AdminEmail).NotEmpty().MaximumLength(Tenant.ContactEmailMaxLength).EmailAddress();
        RuleFor(r => r.AdminName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.BillingCycle).IsInEnum();
    }
}

internal sealed class UpdateTenantRequestValidator : AbstractValidator<UpdateTenantRequest>
{
    public UpdateTenantRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(INamedEntity.NameMaxLength);
        RuleFor(r => r.ContactEmail).MaximumLength(Tenant.ContactEmailMaxLength).EmailAddress().When(r => !string.IsNullOrWhiteSpace(r.ContactEmail));
    }
}
