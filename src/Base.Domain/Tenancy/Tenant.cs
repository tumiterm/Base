using Base.Domain.Common;

namespace Base.Domain.Tenancy;

/// <summary>Raised when a tenant is created. Handlers run after commit (e.g. provision storage, send a welcome email).</summary>
public sealed record TenantCreated(Guid TenantId, string Code) : IDomainEvent;

/// <summary>An organisation using the system. In single-tenant deployments there is exactly one: <see cref="DefaultCode"/>.</summary>
public sealed class Tenant : EntityBase, INamedEntity
{
    public const string DefaultCode = "default";
    public const int ContactEmailMaxLength = 256;
    public const int ReleaseVersionMaxLength = 32;

    /// <summary>What a tenant that never adopted a release is on: every gated feature is still ahead of it.</summary>
    public const string NoRelease = "0.0.0";

    private readonly List<TenantDomain> _domains = [];

    private Tenant()
    {
    }

    public Tenant(string code, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = SequentialGuid.Next();
        Code = code.Trim().ToLowerInvariant();
        Name = name.Trim();
        IsActive = true;
        Raise(new TenantCreated(Id, Code));
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    /// <summary>Who receives subscription and billing notices.</summary>
    public string? ContactEmail { get; private set; }

    public IReadOnlyCollection<TenantDomain> Domains => _domains.AsReadOnly();

    /// <summary>
    /// The release (SemVer) whose features this tenant has taken: everything introduced up to it is on. New tenants
    /// start on the running version; later releases wait until the tenant adopts them (or the support window does).
    /// </summary>
    public string ReleaseVersion { get; private set; } = NoRelease;

    public DateTimeOffset? ReleaseAdoptedAt { get; private set; }

    public void AdoptRelease(string version, DateTimeOffset adoptedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ReleaseVersion = version.Trim();
        ReleaseAdoptedAt = adoptedAt;
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public void ChangeContact(string? email) => ContactEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    public TenantDomain? FindDomain(Guid domainId) => _domains.FirstOrDefault(d => d.Id == domainId);

    public TenantDomain AddDomain(string host)
    {
        var domain = new TenantDomain(Id, host);
        _domains.Add(domain);
        return domain;
    }
}
