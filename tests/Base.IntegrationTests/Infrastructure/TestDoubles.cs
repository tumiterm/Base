using System.Collections.Concurrent;
using Base.Application.Common.Abstractions;
using Base.Application.Security;

namespace Base.IntegrationTests.Infrastructure;

/// <summary>An acting user whose identity and permissions a test controls.</summary>
public sealed class TestCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; } = Guid.NewGuid();

    public string AuditName { get; set; } = "Test Actor";

    public bool IsAuthenticated => UserId is not null;

    public HashSet<string> Permissions { get; } = new(StringComparer.Ordinal);

    public bool HasPermission(string permission) => Permissions.Contains(permission);

    public TestCurrentUser WithAllPermissions() => With(PermissionCatalog.All.Select(p => p.Key).ToArray());

    public TestCurrentUser With(params string[] permissions)
    {
        Permissions.Clear();
        Permissions.UnionWith(PermissionCatalog.Expand(permissions));
        return this;
    }
}

/// <summary>Records emails instead of sending them.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
