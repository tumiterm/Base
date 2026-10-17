namespace Base.Application.Common.Records;

/// <summary>A pointer to any record by type and id, e.g. <c>("Person", "8f1c…")</c>, as documents store their links.</summary>
public sealed record RecordRef(string EntityType, string EntityId);

/// <summary>What a person sees instead of an id: the record's name and a short description.</summary>
public sealed record RecordLabel(string Title, string? Subtitle = null);

/// <summary>
/// Names one entity type's records for screens and exports (documents "linked to", audit entries...). A module
/// registers one per entity it lets other records point at. Return only the records the current user may see:
/// anything left out is shown as its type alone, without a name or a link.
/// </summary>
public interface IRecordLabelProvider
{
    /// <summary>The stored type name, e.g. <c>nameof(Person)</c>.</summary>
    string EntityType { get; }

    /// <summary>The type as people say it: "Person", "Invoice".</summary>
    string TypeName { get; }

    Task<IReadOnlyDictionary<string, RecordLabel>> GetLabelsAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken = default);
}

/// <summary>A record pointer resolved for display. <see cref="Label"/> is null when the record is gone or hidden.</summary>
public sealed record ResolvedRecord(RecordRef Ref, string TypeName, RecordLabel? Label)
{
    /// <summary>"Person: Thandi Nkosi", or just the type when the record can't be shown.</summary>
    public string Text => Label is null ? TypeName : $"{TypeName}: {Label.Title}";
}

public interface IRecordLabels
{
    /// <summary>Resolves many pointers with one query per entity type.</summary>
    Task<IReadOnlyDictionary<RecordRef, ResolvedRecord>> ResolveAsync(IEnumerable<RecordRef> records, CancellationToken cancellationToken = default);
}

internal sealed class RecordLabels(IEnumerable<IRecordLabelProvider> providers) : IRecordLabels
{
    private readonly Dictionary<string, IRecordLabelProvider> _providers =
        providers.ToDictionary(p => p.EntityType, StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<RecordRef, ResolvedRecord>> ResolveAsync(IEnumerable<RecordRef> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var result = new Dictionary<RecordRef, ResolvedRecord>();
        foreach (var group in records.Distinct().GroupBy(r => r.EntityType, StringComparer.OrdinalIgnoreCase))
        {
            if (!_providers.TryGetValue(group.Key, out var provider))
            {
                foreach (var record in group)
                {
                    result[record] = new ResolvedRecord(record, Text.Humanize(record.EntityType), null);
                }

                continue;
            }

            var labels = await provider.GetLabelsAsync(group.Select(r => r.EntityId).ToList(), cancellationToken);
            foreach (var record in group)
            {
                result[record] = new ResolvedRecord(record, provider.TypeName, labels.GetValueOrDefault(record.EntityId));
            }
        }

        return result;
    }

    private static class Text
    {
        /// <summary>"PurchaseOrder" → "Purchase order", for types no module has named.</summary>
        public static string Humanize(string type)
        {
            var builder = new System.Text.StringBuilder(type.Length + 4);
            for (var i = 0; i < type.Length; i++)
            {
                var c = type[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(type[i - 1]))
                {
                    builder.Append(' ').Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }
}

/// <summary>Helpers for <see cref="IRecordLabelProvider"/>s keyed by <see cref="Guid"/>.</summary>
public static class RecordIds
{
    public static IReadOnlyList<Guid> Guids(IEnumerable<string> entityIds) =>
        entityIds.Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();

    /// <summary>Keys labels by the id text in its canonical "D" form and as stored, so lookups by either succeed.</summary>
    public static IReadOnlyDictionary<string, RecordLabel> ByStoredId(IEnumerable<string> entityIds, IReadOnlyDictionary<Guid, RecordLabel> labels)
    {
        var result = new Dictionary<string, RecordLabel>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in entityIds)
        {
            if (Guid.TryParse(id, out var guid) && labels.TryGetValue(guid, out var label))
            {
                result[id] = label;
            }
        }

        return result;
    }
}
