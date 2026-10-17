using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Base.Application.DataExchange;
using Base.Domain.DataExchange;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Base.Infrastructure.DataExchange;

/// <summary>
/// Excel (.xlsx) through the OpenXML SDK's streaming writer, so memory stays flat however many rows: bold, frozen
/// header; real dates, numbers and booleans; inline strings (never formulas); drop-downs for choice columns.
/// </summary>
internal sealed class XlsxWriter : ITabularWriter
{
    private const uint HeaderStyle = 1;
    private const uint DateStyle = 2;
    private const uint DateTimeStyle = 3;
    private const uint DateTimeFormatId = 164;

    public DataFormat Format => DataFormat.Xlsx;

    public string Extension => ".xlsx";

    // The SDK's async writer (3.5.1) silently drops child elements (cell values), so the sync writer is used; output
    // goes to a local temp file, never a network stream.
    [SuppressMessage("Usage", "CA1849:Call async methods when in an async method", Justification = "OpenXmlWriter async methods drop child elements in DocumentFormat.OpenXml 3.5.1.")]
    public async Task WriteAsync(IReadOnlyList<TabularColumn> columns, IAsyncEnumerable<IReadOnlyList<object?>> rows, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        using var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.AddNewPart<WorkbookStylesPart>().Stylesheet = Styles();
        var sheetPart = workbookPart.AddNewPart<WorksheetPart>();

        using (var writer = OpenXmlWriter.Create(sheetPart))
        {
            writer.WriteStartElement(new Worksheet());
            writer.WriteElement(new SheetViews(new SheetView(new Pane
            {
                VerticalSplit = 1,
                TopLeftCell = "A2",
                ActivePane = PaneValues.BottomLeft,
                State = PaneStateValues.Frozen,
            })
            { WorkbookViewId = 0 }));
            writer.WriteElement(new Columns(columns.Select((c, i) => new Column
            {
                Min = (uint)(i + 1),
                Max = (uint)(i + 1),
                CustomWidth = true,
                Width = Width(c),
            })));

            writer.WriteStartElement(new SheetData());
            WriteRow(writer, 1, columns.Select(c => (object?)c.Header).ToArray(), header: true);
            uint number = 1;
            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                WriteRow(writer, ++number, row, header: false);
            }

            writer.WriteEndElement(); // SheetData

            var choices = columns.Select((c, i) => (c, i)).Where(x => x.c.Choices is { Count: > 0 }).ToList();
            if (choices.Count > 0)
            {
                writer.WriteElement(new DataValidations(choices.Select(x => new DataValidation(new Formula1($"\"{string.Join(',', x.c.Choices!)}\""))
                {
                    Type = DataValidationValues.List,
                    AllowBlank = true,
                    ShowErrorMessage = true,
                    SequenceOfReferences = new ListValue<StringValue>([new StringValue($"{ColumnName(x.i)}2:{ColumnName(x.i)}1048576")]),
                }))
                { Count = (uint)choices.Count });
            }

            writer.WriteEndElement(); // Worksheet
        }

        workbookPart.Workbook = new Workbook(new Sheets(new Sheet { Id = workbookPart.GetIdOfPart(sheetPart), SheetId = 1, Name = "Data" }));
        workbookPart.Workbook.Save();
    }

    internal static string ColumnName(int index)
    {
        var name = new StringBuilder();
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name.Insert(0, (char)('A' + ((n - 1) % 26)));
        }

        return name.ToString();
    }

    private static void WriteRow(OpenXmlWriter writer, uint number, IReadOnlyList<object?> values, bool header)
    {
        writer.WriteStartElement(new Row { RowIndex = number });
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is { } value)
            {
                writer.WriteElement(Cell($"{ColumnName(i)}{number}", value, header));
            }
        }

        writer.WriteEndElement();
    }

    private static Cell Cell(string reference, object value, bool header)
    {
        var cell = new Cell { CellReference = reference };
        switch (value)
        {
            case DateTime date:
                cell.CellValue = new CellValue(date.ToOADate().ToString(CultureInfo.InvariantCulture));
                cell.StyleIndex = date.TimeOfDay == TimeSpan.Zero ? DateStyle : DateTimeStyle;
                break;
            case bool flag:
                cell.DataType = CellValues.Boolean;
                cell.CellValue = new CellValue(flag ? "1" : "0");
                break;
            case byte or short or int or long or float or double or decimal:
                cell.CellValue = new CellValue(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(Clean(value.ToString())) { Space = SpaceProcessingModeValues.Preserve });
                if (header)
                {
                    cell.StyleIndex = HeaderStyle;
                }

                break;
        }

        return cell;
    }

    /// <summary>XML can't carry most control characters; drop them rather than produce a corrupt file.</summary>
    private static string Clean(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : new string(text.Where(c => c >= ' ' || c is '\t' or '\n' or '\r').ToArray());

    private static double Width(TabularColumn column) => column.Type switch
    {
        ColumnType.Date => 12,
        ColumnType.DateTime => 18,
        ColumnType.Boolean or ColumnType.Integer => Math.Max(10, column.Header.Length + 3),
        _ => Math.Clamp(column.Header.Length + 6, 16, 50),
    };

    private static Stylesheet Styles() => new(
        new NumberingFormats(new NumberingFormat { NumberFormatId = DateTimeFormatId, FormatCode = "yyyy-mm-dd hh:mm" }) { Count = 1 },
        new Fonts(new Font(), new Font(new Bold())) { Count = 2 },
        new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
        new Borders(new Border()) { Count = 1 },
        new CellFormats(
            new CellFormat(),
            new CellFormat { FontId = 1, ApplyFont = true },
            new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true },
            new CellFormat { NumberFormatId = DateTimeFormatId, ApplyNumberFormat = true })
        { Count = 4 });
}

/// <summary>
/// Reads the first worksheet with the OpenXML SDK's streaming reader. Shared strings, inline strings, booleans and
/// numbers are returned as text; cells formatted as dates come back as ISO 8601. Gaps (missing rows or cells) are kept
/// so row numbers match what the user sees in Excel.
/// </summary>
internal sealed class XlsxReader : ITabularReader
{
    private static readonly HashSet<uint> BuiltInDateFormats = [14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47];

    public DataFormat Format => DataFormat.Xlsx;

    public async IAsyncEnumerable<IReadOnlyList<string?>> ReadAsync(Stream input, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The package format needs random access; copy remote (non-seekable) streams to a temp file first.
        Stream seekable = input;
        FileStream? spool = null;
        if (!input.CanSeek)
        {
            spool = new FileStream(Path.Join(Path.GetTempPath(), Path.GetRandomFileName()), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await input.CopyToAsync(spool, cancellationToken);
            spool.Position = 0;
            seekable = spool;
        }

        try
        {
            SpreadsheetDocument document;
            try
            {
                document = SpreadsheetDocument.Open(seekable, isEditable: false);
            }
            catch (Exception exception) when (exception is OpenXmlPackageException or InvalidDataException or FileFormatException)
            {
                throw new InvalidDataException("The file is not a valid .xlsx workbook.", exception);
            }

            using (document)
            {
                var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("The workbook has no sheets.");
                var sheet = workbookPart.Workbook?.Sheets?.Elements<Sheet>().FirstOrDefault() ?? throw new InvalidDataException("The workbook has no sheets.");
                var sheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
                var shared = workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToList() ?? [];
                var dateStyles = DateStyles(workbookPart);

                using var reader = OpenXmlReader.Create(sheetPart);
                uint expected = 1;
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                    {
                        continue;
                    }

                    var row = (Row)reader.LoadCurrentElement()!;
                    var index = row.RowIndex?.Value ?? expected;
                    for (; expected < index; expected++)
                    {
                        yield return [];
                    }

                    expected = index + 1;
                    yield return Cells(row, shared, dateStyles);
                }
            }
        }
        finally
        {
            if (spool is not null)
            {
                await spool.DisposeAsync();
            }
        }
    }

    private static List<string?> Cells(Row row, List<string> shared, HashSet<uint> dateStyles)
    {
        var cells = new List<string?>();
        foreach (var cell in row.Elements<Cell>())
        {
            var column = cell.CellReference?.Value is { } reference ? ColumnIndex(reference) : cells.Count;
            while (cells.Count < column)
            {
                cells.Add(null);
            }

            cells.Add(Text(cell, shared, dateStyles));
        }

        return cells;
    }

    private static string? Text(Cell cell, List<string> shared, HashSet<uint> dateStyles)
    {
        var raw = cell.CellValue?.Text;
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText;
        }

        if (raw is null)
        {
            return null;
        }

        if (cell.DataType?.Value == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i < shared.Count ? shared[i] : null;
        }

        if (cell.DataType?.Value == CellValues.Boolean)
        {
            return string.Equals(raw, "1", StringComparison.Ordinal) ? "true" : "false";
        }

        if ((cell.DataType is null || cell.DataType.Value == CellValues.Number)
            && cell.StyleIndex?.Value is { } style && dateStyles.Contains(style)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            var moment = DateTime.FromOADate(serial);
            return moment.TimeOfDay == TimeSpan.Zero
                ? moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : moment.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }

        return raw;
    }

    /// <summary>Style indexes whose number format is a date (built-in ids, or a custom code with day/month/year parts).</summary>
    private static HashSet<uint> DateStyles(WorkbookPart workbookPart)
    {
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        var customDates = stylesheet?.NumberingFormats?.Elements<NumberingFormat>()
            .Where(f => f.NumberFormatId?.Value is not null && IsDateCode(f.FormatCode?.Value))
            .Select(f => f.NumberFormatId!.Value)
            .ToHashSet() ?? [];
        var result = new HashSet<uint>();
        var index = 0u;
        foreach (var format in stylesheet?.CellFormats?.Elements<CellFormat>() ?? [])
        {
            var id = format.NumberFormatId?.Value ?? 0;
            if (BuiltInDateFormats.Contains(id) || customDates.Contains(id))
            {
                result.Add(index);
            }

            index++;
        }

        return result;
    }

    private static bool IsDateCode(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        // Ignore quoted literals and [colour]/[locale] sections, then look for date parts.
        var plain = new StringBuilder();
        var quoted = false;
        var bracket = false;
        foreach (var c in code)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '[')
            {
                bracket = true;
            }
            else if (!quoted && c == ']')
            {
                bracket = false;
            }
            else if (!quoted && !bracket)
            {
                plain.Append(char.ToLowerInvariant(c));
            }
        }

        var text = plain.ToString();
        return text.Contains('y', StringComparison.Ordinal) || text.Contains('d', StringComparison.Ordinal);
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference)
        {
            if (!char.IsAsciiLetter(c))
            {
                break;
            }

            index = (index * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return index - 1;
    }
}
