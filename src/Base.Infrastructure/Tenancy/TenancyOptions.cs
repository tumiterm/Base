using System.ComponentModel.DataAnnotations;

namespace Base.Infrastructure.Tenancy;

public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    /// <summary>False: every request runs as the default tenant. True: tenants are resolved from the request host.</summary>
    public bool MultiTenant { get; set; }

    [Required]
    public string DefaultTenantCode { get; set; } = Domain.Tenancy.Tenant.DefaultCode;

    [Required]
    public string DefaultTenantName { get; set; } = "Default";

    /// <summary>Multi-tenant only: serve unknown hosts as the default tenant (logged as a warning) instead of 404.</summary>
    public bool FallbackToDefaultTenant { get; set; }
}
