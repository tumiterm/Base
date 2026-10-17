using Base.Domain.Common;

namespace Base.Domain.Notifications;

/// <summary>A tenant's replacement for one part of a file-based template (e.g. <c>email.subject</c>).</summary>
public sealed class NotificationTemplateOverride : EntityBase, ITenantOwned
{
    public const int PartMaxLength = 50;
    public const int ContentMaxLength = 20000;

    private NotificationTemplateOverride()
    {
    }

    public NotificationTemplateOverride(string templateCode, string part, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(part);
        TemplateCode = templateCode;
        Part = part;
        Content = content;
    }

    public Guid TenantId { get; set; }

    public string TemplateCode { get; private set; } = string.Empty;

    public string Part { get; private set; } = string.Empty;

    public string Content { get; private set; } = string.Empty;

    public void Change(string content) => Content = content;
}
