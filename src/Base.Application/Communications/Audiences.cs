using System.Runtime.CompilerServices;
using System.Text;
using Base.Domain.Notifications;

namespace Base.Application.Communications;

public static class AudienceCodes
{
    public const string People = "people";
    public const string Users = "users";

    /// <summary>A pasted or uploaded list, kept with the message rather than read from a module.</summary>
    public const string List = "list";
}

/// <summary>A placeholder a message can use for this audience: <c>{{Name}}</c> shows as <paramref name="Label"/>.</summary>
public sealed record AudienceField(string Name, string Label);

public sealed record AudienceOption(string Value, string Label);

/// <summary>A filter the audience offers (Department, Role...). Empty value means "any".</summary>
public sealed record AudienceFilter(string Code, string Label, IReadOnlyList<AudienceOption> Options);

/// <summary>
/// One person a message can go to. <paramref name="Key"/> identifies them within the audience (picking them again,
/// removing duplicates); <paramref name="Values"/> fill the audience's extra placeholders.
/// </summary>
/// <param name="AlreadyReached">Channels someone earlier in the audience already gets with the same address or account.</param>
public sealed record AudienceMember(
    string Key,
    string Name,
    string? Email,
    string? Phone,
    Guid? UserId,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<NotificationChannel>? AlreadyReached = null)
{
    public string FirstName => Name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Name;

    /// <summary>Every placeholder value: the common ones and the audience's own.</summary>
    public IReadOnlyDictionary<string, string?> AllValues()
    {
        var values = new Dictionary<string, string?>(Values, StringComparer.Ordinal)
        {
            [AudienceFields.Name] = Name,
            [AudienceFields.FirstName] = FirstName,
            [AudienceFields.Email] = Email,
            [AudienceFields.Phone] = Phone,
        };
        return values;
    }
}

/// <summary>Placeholders every audience fills.</summary>
public static class AudienceFields
{
    public const string Name = "Name";
    public const string FirstName = "FirstName";
    public const string Email = "Email";
    public const string Phone = "Phone";

    public static IReadOnlyList<AudienceField> Common { get; } =
    [
        new(FirstName, "First name"),
        new(Name, "Full name"),
        new(Email, "Email address"),
        new(Phone, "Phone number"),
    ];
}

/// <summary>
/// A group of people a module can message: People, Users, and in a project Employees, Students... Each one reads its
/// own data through its own use cases, so tenant isolation and permissions apply; nothing outside it chooses tables or
/// columns. Register it in DI as <c>IAudienceSource</c>.
/// </summary>
public interface IAudienceSource
{
    /// <summary>Stable id stored on each message (<c>people</c>).</summary>
    string Code { get; }

    string Name { get; }

    string Description { get; }

    /// <summary>A design-system icon name.</summary>
    string Icon { get; }

    /// <summary>What the sender must hold to use it (usually the module's view permission).</summary>
    string Permission { get; }

    /// <summary>Placeholders beyond <see cref="AudienceFields.Common"/>.</summary>
    IReadOnlyList<AudienceField> Fields { get; }

    Task<IReadOnlyList<AudienceFilter>> FiltersAsync(CancellationToken cancellationToken = default);

    /// <summary>Everyone the filters match, in a stable order.</summary>
    IAsyncEnumerable<AudienceMember> MembersAsync(IReadOnlyDictionary<string, string> filters, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudienceMember>> FindAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);

    /// <summary>For picking people one by one: name or address contains <paramref name="term"/>.</summary>
    Task<IReadOnlyList<AudienceMember>> SearchAsync(string term, int take, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads a pasted or uploaded CSV list. The first row names the columns; Name, Email and Phone are recognised (with
/// common variants: Full name, Mobile, Cell...), and every other column becomes a placeholder named after it.
/// </summary>
public static class RecipientList
{
    public const int MaxRows = 5000;

    private static readonly string[] NameHeaders = ["name", "fullname", "displayname", "firstname"];
    private static readonly string[] EmailHeaders = ["email", "emailaddress", "mail"];
    private static readonly string[] PhoneHeaders = ["phone", "phonenumber", "mobile", "mobilenumber", "cell", "cellphone", "cellnumber", "telephone", "whatsapp"];

    public sealed record Parsed(IReadOnlyList<AudienceField> Fields, IReadOnlyList<AudienceMember> Members, IReadOnlyList<string> Problems);

    public static Parsed Parse(string? text)
    {
        var problems = new List<string>();
        var rows = ReadRows(text ?? string.Empty).Where(r => r.Exists(c => c.Length > 0)).ToList();
        if (rows.Count < 2)
        {
            return new([], [], ["Paste a header row (for example Name,Email,Phone) and at least one person."]);
        }

        var header = rows[0].Select(Key).ToList();
        int Find(string[] names) => header.FindIndex(h => names.Contains(h));
        var name = Find(NameHeaders);
        var email = Find(EmailHeaders);
        var phone = Find(PhoneHeaders);
        if (email < 0 && phone < 0)
        {
            return new([], [], ["The first row needs an Email or a Phone column."]);
        }

        var extra = new List<(int Index, AudienceField Field)>();
        for (var i = 0; i < header.Count; i++)
        {
            if (i != name && i != email && i != phone && Placeholder(rows[0][i]) is { Length: > 0 } placeholder
                && !AudienceFields.Common.Any(f => string.Equals(f.Name, placeholder, StringComparison.Ordinal)) && !extra.Exists(e => string.Equals(e.Field.Name, placeholder, StringComparison.Ordinal)))
            {
                extra.Add((i, new AudienceField(placeholder, rows[0][i].Trim())));
            }
        }

        if (rows.Count - 1 > MaxRows)
        {
            problems.Add($"Only the first {MaxRows:N0} people are used; split longer lists.");
        }

        var members = new List<AudienceMember>();
        foreach (var (row, number) in rows.Skip(1).Take(MaxRows).Select((r, i) => (r, i + 2)))
        {
            string? Cell(int index) => index >= 0 && index < row.Count && row[index].Length > 0 ? row[index].Trim() : null;
            var address = Cell(email);
            var mobile = Cell(phone);
            if (address is null && mobile is null)
            {
                problems.Add($"Row {number} has no email or phone; it was left out.");
                continue;
            }

            var values = extra.ToDictionary(e => e.Field.Name, e => Cell(e.Index), StringComparer.Ordinal);
            members.Add(new AudienceMember($"row-{number}", Cell(name) ?? address ?? mobile!, address, mobile, null, values));
        }

        return new(extra.Select(e => e.Field).ToList(), members, problems);
    }

    /// <summary>"Student number" becomes <c>StudentNumber</c>.</summary>
    public static string Placeholder(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var builder = new StringBuilder();
        var upper = true;
        foreach (var c in header.Trim().TrimStart("0123456789".ToCharArray()))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }

        return builder.ToString();
    }

    private static string Key(string header) => Placeholder(header).ToLowerInvariant();

    /// <summary>RFC 4180-ish: commas or semicolons, quotes with doubled quotes inside.</summary>
    private static IEnumerable<List<string>> ReadRows(string text)
    {
        var separator = text.Split('\n', 2)[0].Count(c => c == ';') > text.Split('\n', 2)[0].Count(c => c == ',') ? ';' : ',';
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator || c == '\t')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\n')
            {
                row.Add(cell.ToString().TrimEnd('\r'));
                cell.Clear();
                yield return row;
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        row.Add(cell.ToString().TrimEnd('\r'));
        yield return row;
    }
}

internal static class AsyncEnumerables
{
    /// <summary>Reads page after page (1, 2, ...) until one comes back short.</summary>
    public static async IAsyncEnumerable<T> Pages<T>(int pageSize, Func<int, Task<IReadOnlyList<T>>> page, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var number = 1; ; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await page(number);
            foreach (var item in items)
            {
                yield return item;
            }

            if (items.Count < pageSize)
            {
                yield break;
            }
        }
    }
}
