using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Application.Operations;
using Base.Domain.Integrations;

namespace Base.Application.Integrations;

/// <summary>Flags keys that expire within two weeks; <see cref="ApiKeyExpiryNotice"/> tells their creators, once per key.</summary>
internal sealed class ApiKeyExpiryJob(IUnitOfWork unitOfWork, TimeProvider time) : IScheduledJob
{
    private static readonly TimeSpan Notice = TimeSpan.FromDays(14);

    public string Code => "api-key-expiry";

    public string Name => "API key expiry reminders";

    public string Description => "Reminds the creator of an API client to rotate its key two weeks before it expires.";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var horizon = now + Notice;
        var due = await unitOfWork.Repository<ApiClient>().GetAllAsync(
            c => c.RevokedAt == null && c.ExpiryNoticeSentAt == null && c.ExpiresAt > now && c.ExpiresAt <= horizon,
            includeProperties: null,
            cancellationToken: cancellationToken);

        foreach (var client in due)
        {
            client.MarkExpiryNoticeSent(now);
        }

        await unitOfWork.SaveAsync(cancellationToken);
        return string.Create(CultureInfo.InvariantCulture, $"{due.Count} key(s) expire within two weeks");
    }
}

/// <summary>Sends the reminder as the client's tenant (the job that raises it runs across tenants).</summary>
internal sealed class ApiKeyExpiryNotice(ITenantContext tenant, INotificationService notifications) : IDomainEventHandler<ApiKeyExpiring>
{
    public async Task HandleAsync(ApiKeyExpiring domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        tenant.SetTenant(domainEvent.TenantId, null);
        await notifications.SendAsync(
            new Notification(NotificationTemplates.Integrations.ApiKeyExpiring, NotificationRecipient.ToUser(domainEvent.CreatedByUserId), new
            {
                ClientName = domainEvent.Name,
                ExpiresOn = domainEvent.ExpiresAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
            }),
            cancellationToken);
    }
}
