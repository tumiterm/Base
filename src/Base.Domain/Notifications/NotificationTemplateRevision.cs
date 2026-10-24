using Base.Domain.Common;

namespace Base.Domain.Notifications;

/// <summary>
/// One saved version of a tenant's wording of a template: every reworded part as it stood after the save (none means
/// the application's wording). Kept so an earlier version can be looked at and brought back.
/// </summary>
public sealed class NotificationTemplateRevision : EntityBase, ITenantOwned
{
    public const int NoteMaxLength = 200;

    private NotificationTemplateRevision()
    {
    }

    public NotificationTemplateRevision(string templateCode, int number, string partsJson, string? note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(partsJson);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        TemplateCode = templateCode;
        Number = number;
        PartsJson = partsJson;
        var trimmed = note?.Trim();
        Note = string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > NoteMaxLength ? trimmed[..NoteMaxLength] : trimmed;
    }

    public Guid TenantId { get; set; }

    public string TemplateCode { get; private set; } = string.Empty;

    /// <summary>1, 2, 3... per tenant and template.</summary>
    public int Number { get; private set; }

    /// <summary>The reworded parts, as a JSON object of part name to wording.</summary>
    public string PartsJson { get; private set; } = string.Empty;

    public string? Note { get; private set; }
}
