using System.Collections.Concurrent;
using Base.Application.Common.Abstractions;
using Base.Application.Notifications;
using Base.Application.Security;
using Base.Domain.Notifications;

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

/// <summary>
/// A provider for every channel that records what it was asked to send. <see cref="SentMessages.FailNext"/> makes the next sends on
/// a channel fail, for retry and fallback tests.
/// </summary>
public sealed class CapturingNotificationProvider(NotificationChannel channel, ConcurrentQueue<OutgoingMessage> sent, ConcurrentDictionary<NotificationChannel, ProviderResult> failures) : INotificationProvider
{
    public const string ProviderName = "capture";

    public string Name => ProviderName;

    public NotificationChannel Channel { get; } = channel;

    public Task<ProviderResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        if (failures.TryGetValue(Channel, out var failure))
        {
            return Task.FromResult(failure);
        }

        sent.Enqueue(message);
        return Task.FromResult(ProviderResult.Sent($"captured-{message.MessageId:N}"));
    }
}

/// <summary>Everything the capturing providers sent, shared across channels.</summary>
public sealed class SentMessages
{
    public ConcurrentQueue<OutgoingMessage> All { get; } = new();

    public ConcurrentDictionary<NotificationChannel, ProviderResult> Failures { get; } = new();

    public IEnumerable<OutgoingMessage> On(NotificationChannel channel) => All.Where(m => m.Channel == channel);

    public void FailNext(NotificationChannel channel, string error, bool retryable) => Failures[channel] = ProviderResult.Failed(error, retryable);

    public void Clear()
    {
        All.Clear();
        Failures.Clear();
    }
}
