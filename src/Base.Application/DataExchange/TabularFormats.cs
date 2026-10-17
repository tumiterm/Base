using Base.Domain.DataExchange;

namespace Base.Application.DataExchange;

public enum ColumnType
{
    Text = 0,
    Integer = 1,
    Decimal = 2,
    Date = 3,
    DateTime = 4,
    Boolean = 5,
}

/// <param name="Choices">For templates: the allowed values, offered as a drop-down in Excel.</param>
public sealed record TabularColumn(string Header, ColumnType Type = ColumnType.Text, IReadOnlyList<string>? Choices = null);

/// <summary>
/// Writes rows to one file format. Values are typed (<see cref="DateTime"/> for dates, already in the display time zone;
/// numbers; booleans; strings). Implementations stream: rows are written as they arrive.
/// </summary>
public interface ITabularWriter
{
    DataFormat Format { get; }

    string Extension { get; }

    Task WriteAsync(IReadOnlyList<TabularColumn> columns, IAsyncEnumerable<IReadOnlyList<object?>> rows, Stream output, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the first sheet of a file as rows of text: the first row is the header. Excel dates come back as ISO 8601
/// (<c>2026-10-17</c>, or <c>2026-10-17T14:30:00</c> with a time), numbers in invariant culture.
/// </summary>
public interface ITabularReader
{
    DataFormat Format { get; }

    IAsyncEnumerable<IReadOnlyList<string?>> ReadAsync(Stream input, CancellationToken cancellationToken = default);
}

public static class DataFormats
{
    public static string ExtensionOf(DataFormat format) => format == DataFormat.Xlsx ? ".xlsx" : ".csv";

    public static DataFormat? FromExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".csv" => DataFormat.Csv,
        ".xlsx" => DataFormat.Xlsx,
        _ => null,
    };

    public static bool TryParse(string? value, out DataFormat format) =>
        Enum.TryParse(value, ignoreCase: true, out format) && Enum.IsDefined(format);
}
