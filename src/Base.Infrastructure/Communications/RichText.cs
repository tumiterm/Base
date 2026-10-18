using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Base.Application.Communications;
using Ganss.Xss;

namespace Base.Infrastructure.Communications;

internal sealed class RichText : IRichText
{
    private static readonly string[] Tags = ["p", "br", "strong", "b", "em", "i", "u", "s", "h2", "h3", "ul", "ol", "li", "a", "blockquote", "hr", "div", "span"];
    private static readonly HtmlParser Parser = new();
    private readonly HtmlSanitizer _sanitizer = Build();

    public string Sanitize(string? html) => string.IsNullOrWhiteSpace(html) ? string.Empty : _sanitizer.Sanitize(html).Trim();

    public string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var document = Parser.ParseDocument("<body>" + html + "</body>");
        var text = new StringBuilder();
        Write(document.Body!, text);
        var lines = text.ToString().Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd());
        var result = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            blank = line.Length == 0 ? blank + 1 : 0;
            if (blank < 2)
            {
                result.Append(line).Append('\n');
            }
        }

        return result.ToString().Trim();
    }

    private static void Write(INode node, StringBuilder text)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText t:
                    text.Append(System.Text.RegularExpressions.Regex.Replace(t.Data, @"\s+", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)));
                    break;
                case IElement e:
                    var name = e.LocalName;
                    if (name is "br")
                    {
                        text.Append('\n');
                        break;
                    }

                    if (name is "hr")
                    {
                        text.Append("\n---\n");
                        break;
                    }

                    var block = name is "p" or "div" or "h2" or "h3" or "ul" or "ol" or "blockquote";
                    if (block)
                    {
                        text.Append("\n\n");
                    }

                    if (name is "li")
                    {
                        text.Append('\n').Append(e.ParentElement?.LocalName is "ol" ? $"{e.Index() + 1}. " : "- ");
                    }

                    Write(e, text);
                    if (name is "a" && e.GetAttribute("href") is { Length: > 0 } href && !string.Equals(href, e.TextContent.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        text.Append(" (").Append(href).Append(')');
                    }

                    if (block)
                    {
                        text.Append("\n\n");
                    }

                    break;
            }
        }
    }

    private static HtmlSanitizer Build()
    {
        var options = new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(Tags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(["href", "title"], StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(["http", "https", "mailto", "tel"], StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(["href"], StringComparer.OrdinalIgnoreCase),
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
        var built = new HtmlSanitizer(options);
        built.PostProcessNode += (_, e) =>
        {
            // Links open in a new tab and never send the referrer.
            if (e.Node is IElement { LocalName: "a" } link)
            {
                link.SetAttribute("rel", "noopener noreferrer");
            }
        };
        built.AllowedAttributes.Add("rel");
        return built;
    }
}
