using System.Collections.Frozen;
using Base.Application.Common.Records;
using Base.Domain.People;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Formatting;

/// <summary>
/// Where each linkable entity type's page is, and its icon. Add a row when a module registers an
/// <see cref="IRecordLabelProvider"/>; types without one are shown by name only.
/// </summary>
public static class RecordLinks
{
    private static readonly FrozenDictionary<string, (string Controller, string Action, string Icon)> Pages =
        new Dictionary<string, (string Controller, string Action, string Icon)>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(Person)] = ("People", "Details", "user"),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string Icon(string entityType) => Pages.TryGetValue(entityType, out var page) ? page.Icon : "link";

    /// <summary>The record's page, or null when it has none or can't be shown.</summary>
    public static string? Url(IUrlHelper url, ResolvedRecord record)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(record);
        return record.Label is not null && Pages.TryGetValue(record.Ref.EntityType, out var page)
            ? url.Action(page.Action, page.Controller, new { id = record.Ref.EntityId })
            : null;
    }
}
