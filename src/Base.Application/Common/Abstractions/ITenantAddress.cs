namespace Base.Application.Common.Abstractions;

/// <summary>
/// Where the current tenant's people open the application: its verified domain, or the <c>General.PublicBaseUrl</c>
/// setting for the platform tenant (and a tenant with no verified domain yet). Links in emails start here, never with
/// the request's host.
/// </summary>
public interface ITenantAddress
{
    /// <summary>The address without a trailing slash, e.g. <c>https://acme.example.com</c>.</summary>
    Task<string> BaseUrlAsync(CancellationToken cancellationToken = default);
}
