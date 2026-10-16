using Base.Application.Common.Abstractions;

namespace Base.Infrastructure.Identity;

/// <summary>The acting user outside a web request (jobs, seeders, the outbox): "System", with no permissions.</summary>
internal sealed class SystemCurrentUser : ICurrentUser
{
    public Guid? UserId => null;

    public string AuditName => ICurrentUser.SystemName;

    public bool IsAuthenticated => false;

    public bool HasPermission(string permission) => false;
}
