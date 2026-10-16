using Base.Domain.Common;
using Microsoft.AspNetCore.Identity;

namespace Base.Infrastructure.Identity;

/// <summary>An account. Belongs to one tenant; user names and emails are unique per tenant.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>, ITenantOwned, IAuditable, IHasDomainEvents
{
    public const int DisplayNameMaxLength = 200;

    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Queues an event; it is written to the outbox by the next save that includes this user.</summary>
    public void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();

    public bool HasPassword => PasswordHash is not null;

    public bool IsLockedOut(DateTimeOffset now) => LockoutEnd is { } end && end > now;

    public Guid TenantId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset? LastSignInAt { get; set; }

    public DateTimeOffset DateCreated { get; set; }

    public DateTimeOffset DateModified { get; set; }

    public string? UserCreated { get; set; }

    public string? UserModified { get; set; }
}

/// <summary>A role. Roles are data; their permissions are role claims of type <c>permission</c>.</summary>
public sealed class ApplicationRole : IdentityRole<Guid>
{
    public const int DescriptionMaxLength = 500;

    public ApplicationRole()
    {
    }

    public ApplicationRole(string name)
        : base(name)
    {
    }

    public string? Description { get; set; }
}
