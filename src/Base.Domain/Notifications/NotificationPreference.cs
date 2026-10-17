using Base.Domain.Common;

namespace Base.Domain.Notifications;

/// <summary>
/// A user's choice for one category on one channel. Only opt-outs need a row: without one, a channel is on.
/// Mandatory templates (security messages) ignore preferences.
/// </summary>
public sealed class NotificationPreference : EntityBase, ITenantOwned
{
    private NotificationPreference()
    {
    }

    public NotificationPreference(Guid userId, string category, NotificationChannel channel, bool isEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        UserId = userId;
        Category = category;
        Channel = channel;
        IsEnabled = isEnabled;
    }

    public Guid TenantId { get; set; }

    public Guid UserId { get; private set; }

    public string Category { get; private set; } = string.Empty;

    public NotificationChannel Channel { get; private set; }

    public bool IsEnabled { get; private set; }

    public void Set(bool isEnabled) => IsEnabled = isEnabled;
}
