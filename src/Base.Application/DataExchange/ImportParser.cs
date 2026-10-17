using System.Globalization;
using System.Text.RegularExpressions;

namespace Base.Application.DataExchange;

/// <summary>Maps a file's header to an importer's columns and parses cells to typed values.</summary>
internal static partial class ImportParser
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd MMM yyyy", "d MMM yyyy", "d MMMM yyyy"];
    private static readonly string[] DateTimeFormats = ["yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss"];

    /// <summary>Column name to file column index. Errors for required columns that are missing or columns given twice.</summary>
    public static (Dictionary<string, int> Map, List<ImportRowError> Errors) MapHeader(IReadOnlyList<ImportColumn> columns, IReadOnlyList<string?> header)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var errors = new List<ImportRowError>();
        for (var i = 0; i < header.Count; i++)
        {
            var key = Normalize(header[i]);
            if (key.Length == 0)
            {
                continue;
            }

            var column = columns.FirstOrDefault(c => string.Equals(Normalize(c.Name), key, StringComparison.Ordinal) || (c.Aliases ?? []).Any(a => string.Equals(Normalize(a), key, StringComparison.Ordinal)));
            if (column is null)
            {
                continue; // extra columns are ignored, so users can keep notes next to the data
            }

            if (!map.TryAdd(column.Name, i))
            {
                errors.Add(new ImportRowError(1, column.Name, $"The column '{column.Name}' appears more than once."));
            }
        }

        errors.AddRange(columns.Where(c => c.Required && !map.ContainsKey(c.Name))
            .Select(c => new ImportRowError(1, c.Name, $"The required column '{c.Name}' is missing.")));
        return (map, errors);
    }

    /// <summary>Parses one row. Values hold typed results for the columns present; errors list the cells that didn't parse.</summary>
    public static (Dictionary<string, object?> Values, List<ImportRowError> Errors) ParseRow(
        IReadOnlyList<ImportColumn> columns, Dictionary<string, int> map, IReadOnlyList<string?> cells, int rowNumber)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors = new List<ImportRowError>();
        foreach (var column in columns)
        {
            var text = map.TryGetValue(column.Name, out var index) && index < cells.Count ? cells[index]?.Trim() : null;
            if (string.IsNullOrEmpty(text))
            {
                if (column.Required)
                {
                    errors.Add(new ImportRowError(rowNumber, column.Name, "A value is required."));
                }

                values[column.Name] = null;
                continue;
            }

            if (TryParse(column, text, out var value, out var problem))
            {
                values[column.Name] = value;
            }
            else
            {
                errors.Add(new ImportRowError(rowNumber, column.Name, problem));
            }
        }

        return (values, errors);
    }

    public static bool IsEmpty(IReadOnlyList<string?> cells) => cells.All(string.IsNullOrWhiteSpace);

    private static bool TryParse(ImportColumn column, string text, out object? value, out string problem)
    {
        problem = string.Empty;
        value = null;
        switch (column.Type)
        {
            case ColumnType.Integer when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number):
                value = number;
                return true;
            case ColumnType.Integer:
                problem = $"'{text}' is not a whole number.";
                return false;
            case ColumnType.Decimal when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number):
                value = number;
                return true;
            case ColumnType.Decimal:
                problem = $"'{text}' is not a number (use a dot for decimals).";
                return false;
            case ColumnType.Date when DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                value = date;
                return true;
            case ColumnType.Date when DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime) && dateTime.TimeOfDay == TimeSpan.Zero:
                value = DateOnly.FromDateTime(dateTime);
                return true;
            case ColumnType.Date:
                problem = $"'{text}' is not a date (use 2026-10-17).";
                return false;
            case ColumnType.DateTime when DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime):
                value = dateTime;
                return true;
            case ColumnType.DateTime when DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                value = date.ToDateTime(TimeOnly.MinValue);
                return true;
            case ColumnType.DateTime:
                problem = $"'{text}' is not a date and time (use 2026-10-17 14:30).";
                return false;
            case ColumnType.Boolean when ParseBoolean(text) is { } flag:
                value = flag;
                return true;
            case ColumnType.Boolean:
                problem = $"'{text}' is not yes or no.";
                return false;
            default:
                if (column.MaxLength is { } max && text.Length > max)
                {
                    problem = $"Too long: at most {max} characters.";
                    return false;
                }

                if (column.Choices is { Count: > 0 } choices)
                {
                    var match = choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        problem = $"'{text}' is not one of: {string.Join(", ", choices)}.";
                        return false;
                    }

                    text = match;
                }

                value = text;
                return true;
        }
    }

    private static bool? ParseBoolean(string text) => text.ToLowerInvariant() switch
    {
        "yes" or "y" or "true" or "1" => true,
        "no" or "n" or "false" or "0" => false,
        _ => null,
    };

    private static string Normalize(string? header) => Separators().Replace(header ?? string.Empty, string.Empty).ToLowerInvariant();

    [GeneratedRegex(@"[\s\-_]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Separators();
}
