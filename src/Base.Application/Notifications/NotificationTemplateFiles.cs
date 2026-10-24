using System.Collections.Frozen;

namespace Base.Application.Notifications;

/// <summary>
/// The default wording, from embedded <c>Templates/{Code}.txt</c> files. A file is a list of sections:
/// <code>
/// --- email.subject
/// Welcome to {{AppName}}
/// --- email.heading
/// You're in
/// --- email.body
/// &lt;p&gt;Hello {{RecipientName}}&lt;/p&gt;
/// --- email.button
/// Sign in
/// --- email.action
/// {{PublicBaseUrl}}/account/login
/// </code>
/// The email layout turns the parts into the message (see <see cref="EmailLayout"/>).
/// Files are versioned and reviewed with the code; tenants can override single parts in the database.
/// </summary>
public static class NotificationTemplateFiles
{
    private const string SectionMarker = "--- ";

    private static readonly Lazy<FrozenDictionary<string, FrozenDictionary<string, string>>> Defaults = new(Load);

    public static IReadOnlyDictionary<string, string> PartsOf(string templateCode) =>
        Defaults.Value.TryGetValue(templateCode, out var parts)
            ? parts
            : throw new KeyNotFoundException($"No template file for '{templateCode}' (expected Notifications/Templates/{templateCode}.txt).");

    public static bool Exists(string templateCode) => Defaults.Value.ContainsKey(templateCode);

    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var buffer = new List<string>();

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith(SectionMarker, StringComparison.Ordinal))
            {
                Flush();
                current = line[SectionMarker.Length..].Trim();
                if (!TemplateParts.All.Contains(current, StringComparer.Ordinal))
                {
                    throw new FormatException($"Unknown template section '{current}'. Known: {string.Join(", ", TemplateParts.All)}.");
                }

                continue;
            }

            buffer.Add(line);
        }

        Flush();

        // Older files hold the whole email in email.html: it is the body the layout wraps.
        if (parts.Remove(TemplateParts.EmailHtml, out var html) && !parts.ContainsKey(TemplateParts.EmailBody))
        {
            parts[TemplateParts.EmailBody] = html;
        }

        return parts;

        void Flush()
        {
            if (current is not null && !parts.TryAdd(current, string.Join('\n', buffer).Trim()))
            {
                throw new FormatException($"Template section '{current}' appears twice.");
            }

            buffer.Clear();
        }
    }

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        const string Prefix = "Notifications.";
        var assembly = typeof(NotificationTemplateFiles).Assembly;
        var result = new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".txt", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var code = resource[Prefix.Length..^".txt".Length];
            result[code] = Parse(reader.ReadToEnd()).ToFrozenDictionary(StringComparer.Ordinal);
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
