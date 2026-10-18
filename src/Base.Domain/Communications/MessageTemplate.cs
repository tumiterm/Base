using Base.Domain.Common;

namespace Base.Domain.Communications;

/// <summary>
/// Wording an organisation writes once and reuses when sending messages ("Payslips are ready", "Office closed").
/// Separate from the system's notification templates, which the application itself sends.
/// </summary>
public sealed class MessageTemplate : EntityBase, ITenantOwned
{
    public const int NameMaxLength = 200;

    private MessageTemplate()
    {
    }

    public MessageTemplate(string name)
    {
        Id = SequentialGuid.Next();
        Rename(name);
    }

    public Guid TenantId { get; set; }

    public string Name { get; private set; } = string.Empty;

    public string? Subject { get; private set; }

    public string? HtmlBody { get; private set; }

    public string? SmsBody { get; private set; }

    public string? WhatsAppTemplateName { get; private set; }

    public string? WhatsAppLanguage { get; private set; }

    public string? WhatsAppParameters { get; private set; }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        Name = trimmed.Length <= NameMaxLength ? trimmed : trimmed[..NameMaxLength];
    }

    public void SetContent(string? subject, string? htmlBody, string? smsBody, string? whatsAppTemplateName, string? whatsAppLanguage, string? whatsAppParameters)
    {
        Subject = Empty(subject) ? null : subject!.Trim();
        HtmlBody = Empty(htmlBody) ? null : htmlBody;
        SmsBody = Empty(smsBody) ? null : smsBody;
        WhatsAppTemplateName = Empty(whatsAppTemplateName) ? null : whatsAppTemplateName!.Trim();
        WhatsAppLanguage = Empty(whatsAppLanguage) ? null : whatsAppLanguage!.Trim();
        WhatsAppParameters = Empty(whatsAppParameters) ? null : whatsAppParameters;
    }

    private static bool Empty(string? value) => string.IsNullOrWhiteSpace(value);
}
