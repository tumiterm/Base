using Base.Application.MasterData;

namespace Base.Web.Formatting;

/// <summary>Names for lookup codes in a page, loaded once per request (hidden entries included, so old records still read well).</summary>
public sealed class LookupLabels
{
    private readonly Dictionary<string, Dictionary<string, string>> _names;

    private LookupLabels(Dictionary<string, Dictionary<string, string>> names) => _names = names;

    public static async Task<LookupLabels> LoadAsync(ILookupService lookups, CancellationToken cancellationToken, params string[] lists)
    {
        ArgumentNullException.ThrowIfNull(lookups);
        ArgumentNullException.ThrowIfNull(lists);
        var names = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            names[list] = (await lookups.GetAsync(list, includeInactive: true, cancellationToken)).ToDictionary(v => v.Code, v => v.Name, StringComparer.Ordinal);
        }

        return new LookupLabels(names);
    }

    /// <summary>The entry's name, the code itself if the entry is gone, or null for no value.</summary>
    public string? Name(string list, string? code) =>
        code is null ? null : _names.TryGetValue(list, out var names) && names.TryGetValue(code, out var name) ? name : code;
}
