namespace Base.Web.Observability;

/// <summary>
/// Where to look at a trace or an error's logs, for links on the error page. Templates take <c>{traceId}</c> and
/// <c>{ticket}</c>, e.g. the Aspire dashboard (<c>http://localhost:18888/traces/detail/{traceId}</c>) or Seq.
/// </summary>
public sealed class ObservabilityLinks
{
    public const string SectionName = "Observability";

    public string? TraceUrlTemplate { get; set; }

    public string? LogSearchUrlTemplate { get; set; }

    public Uri? TraceUrl(string? traceId) => Fill(TraceUrlTemplate, "{traceId}", traceId);

    public Uri? LogSearchUrl(string ticket) => Fill(LogSearchUrlTemplate, "{ticket}", ticket);

    private static Uri? Fill(string? template, string placeholder, string? value) =>
        string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(value)
            ? null
            : Uri.TryCreate(template.Replace(placeholder, Uri.EscapeDataString(value), StringComparison.Ordinal), UriKind.Absolute, out var uri) ? uri : null;
}
