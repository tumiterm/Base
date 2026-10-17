namespace Base.Infrastructure.Operations;

/// <summary>A recorded unhandled error. Operational data like the outbox: not an entity, not audited.</summary>
public sealed class ErrorLogEntry
{
    public const int TicketMaxLength = 32;
    public const int TypeMaxLength = 300;
    public const int MessageMaxLength = 2000;
    public const int PathMaxLength = 1000;
    public const int DetailsMaxLength = 16000;

    public Guid Id { get; set; }

    public string Ticket { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public string ExceptionType { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string? User { get; set; }

    public Guid? TenantId { get; set; }

    public string? TraceId { get; set; }

    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>The exception with its stack trace.</summary>
    public string Details { get; set; } = string.Empty;
}
