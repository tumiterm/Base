namespace Base.Application.Communications;

/// <summary>
/// HTML that people write (message bodies, templates). <see cref="Sanitize"/> keeps formatting (paragraphs, headings,
/// bold, lists, links, quotes) and drops everything that can run or track (scripts, styles, event handlers, images,
/// forms); placeholders such as <c>{{FirstName}}</c> survive as text.
/// </summary>
public interface IRichText
{
    string Sanitize(string? html);

    /// <summary>Readable plain text of the HTML: for the email's text part, SMS and in-app messages.</summary>
    string ToPlainText(string? html);
}
