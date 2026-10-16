namespace Base.Application.Common.Abstractions;

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string HtmlBody, string? TextBody = null);

/// <summary>Sends email. Implementations must not throw for delivery problems that callers can't act on; log instead.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
