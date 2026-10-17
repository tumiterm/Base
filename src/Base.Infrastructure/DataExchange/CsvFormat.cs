using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Base.Application.DataExchange;
using Base.Domain.DataExchange;

namespace Base.Infrastructure.DataExchange;

/// <summary>
/// RFC 4180 CSV, UTF-8 with a byte-order mark so Excel shows accents correctly. Cells that Excel would run as a
/// formula (starting with = + - @ or a tab/CR) are prefixed with an apostrophe (OWASP "CSV injection").
/// </summary>
internal sealed class CsvWriter : ITabularWriter
{
    public DataFormat Format => DataFormat.Csv;

    public string Extension => ".csv";

    public async Task WriteAsync(IReadOnlyList<TabularColumn> columns, IAsyncEnumerable<IReadOnlyList<object?>> rows, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 65536, leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteLineAsync(string.Join(',', columns.Select(c => Escape(c.Header))));
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            await writer.WriteLineAsync(string.Join(',', row.Select(v => Escape(Text(v)))));
        }
    }

    internal static string Text(object? value) => value switch
    {
        null => string.Empty,
        DateTime { TimeOfDay.Ticks: 0 } date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool flag => flag ? "Yes" : "No",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    internal static string Escape(string text)
    {
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !IsNumber(text))
        {
            text = "'" + text;
        }

        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    // A negative number isn't a formula; keep "-12.5" usable as a number.
    private static bool IsNumber(string text) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
}

/// <summary>Reads RFC 4180 CSV (quoted fields, embedded commas and line breaks). Detects UTF-8/UTF-16 by BOM.</summary>
internal sealed class CsvReader : ITabularReader
{
    public DataFormat Format => DataFormat.Csv;

    public async IAsyncEnumerable<IReadOnlyList<string?>> ReadAsync(Stream input, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 65536, leaveOpen: true);
        var row = new List<string?>();
        var cell = new StringBuilder();
        var quoted = false;
        var buffer = new char[8192];
        int read;
        var pendingQuote = false;
        var lastWasCr = false;
        var any = false;

        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (pendingQuote)
                {
                    pendingQuote = false;
                    if (c == '"')
                    {
                        cell.Append('"'); // "" inside quotes
                        continue;
                    }

                    quoted = false; // the quote closed the field
                }

                if (quoted)
                {
                    if (c == '"')
                    {
                        pendingQuote = true;
                    }
                    else
                    {
                        cell.Append(c);
                    }

                    continue;
                }

                if (lastWasCr && c == '\n')
                {
                    lastWasCr = false;
                    continue;
                }

                lastWasCr = false;
                switch (c)
                {
                    case '"' when cell.Length == 0:
                        quoted = true;
                        any = true;
                        break;
                    case ',':
                        row.Add(cell.ToString());
                        cell.Clear();
                        any = true;
                        break;
                    case '\r' or '\n':
                        lastWasCr = c == '\r';
                        row.Add(cell.ToString());
                        cell.Clear();
                        yield return row;
                        row = [];
                        any = false;
                        break;
                    default:
                        cell.Append(c);
                        any = true;
                        break;
                }
            }
        }

        if (any || cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            yield return row;
        }
    }
}
