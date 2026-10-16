namespace Base.Application.Common.Abstractions;

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string HtmlBody, string? TextBody = null);

/// <summary>
/// Sends email. Throws when delivery fails, so outbox handlers retry. Send from a domain-event handler rather than
/// inside a request, so a slow or failing mail server never fails the user's action.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
