using System.Text.Json;
using Base.Domain.Auditing;

namespace Base.Application.Auditing;

/// <summary>One property change inside an <see cref="AuditEntry"/>. Redacted changes carry no values.</summary>
public sealed record AuditChange(string Property, string? Old, string? New, bool Redacted = false)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    public static string Serialize(IReadOnlyList<AuditChange> changes) => JsonSerializer.Serialize(changes, JsonOptions);

    public static IReadOnlyList<AuditChange> Deserialize(string json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<AuditChange>>(json, JsonOptions) ?? [];
}

public sealed record AuditRecord(
    Guid Id,
    DateTimeOffset OccurredAt,
    string EntityType,
    string EntityId,
    AuditAction Action,
    string UserName,
    Guid? UserId,
    string? TraceId,
    IReadOnlyList<AuditChange> Changes);

/// <param name="EntityId">Matches the record itself and rows keyed under it (a user's role assignments and claims).</param>
/// <param name="To">Exclusive upper bound.</param>
public sealed record AuditQuery(
    string? EntityType = null,
    string? EntityId = null,
    Guid? UserId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50);
