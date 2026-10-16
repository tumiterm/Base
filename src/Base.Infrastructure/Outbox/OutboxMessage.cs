using System.Text.Json;

namespace Base.Infrastructure.Outbox;

/// <summary>A domain event waiting to be dispatched. Written in the same transaction as the change that raised it.</summary>
public sealed class OutboxMessage
{
    public const int TypeMaxLength = 500;
    public const int ErrorMaxLength = 4000;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Guid Id { get; set; }

    /// <summary>"Namespace.Type, Assembly" of the event.</summary>
    public string Type { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    /// <summary>The tenant the event was raised in; handlers run in that tenant.</summary>
    public Guid? TenantId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Set after the final failed attempt. Dead-lettered messages need a human (System Health page / SQL).</summary>
    public DateTimeOffset? DeadLetteredAt { get; set; }

    public string? LastError { get; set; }

    internal static string TypeNameOf(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";
}
