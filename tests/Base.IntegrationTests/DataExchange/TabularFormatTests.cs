using System.Text;
using Base.Application.DataExchange;
using Base.Infrastructure.DataExchange;

namespace Base.IntegrationTests.DataExchange;

public sealed class TabularFormatTests
{
    private static readonly TabularColumn[] Columns =
    [
        new("Name"), new("Count", ColumnType.Integer), new("Price", ColumnType.Decimal), new("Born", ColumnType.Date), new("Seen", ColumnType.DateTime), new("Active", ColumnType.Boolean),
    ];

    private static readonly object?[] Values = ["Zoë \"Z\", Jr\nline two", 42L, 12.5m, new DateTime(2026, 10, 17), new DateTime(2026, 10, 17, 14, 30, 0), true];

    [Fact]
    public async Task Csv_round_trips_quotes_commas_line_breaks_and_types()
    {
        var bytes = await WriteAsync(new CsvWriter(), [Values, [null, null, null, null, null, false]]);

        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF }, "UTF-8 BOM so Excel reads accents");
        var rows = await ReadAsync(new CsvReader(), bytes);
        rows[0].ShouldBe(["Name", "Count", "Price", "Born", "Seen", "Active"]);
        rows[1].ShouldBe(["Zoë \"Z\", Jr\nline two", "42", "12.5", "2026-10-17", "2026-10-17 14:30:00", "Yes"]);
        rows[2].ShouldBe(["", "", "", "", "", "No"]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-12.5", "-12.5")]
    [InlineData("plain", "plain")]
    public void Csv_cells_that_excel_would_run_as_formulas_are_neutralised(string value, string expected) =>
        CsvWriter.Escape(value).ShouldBe(expected);

    [Fact]
    public async Task Xlsx_round_trips_text_numbers_dates_and_booleans()
    {
        var bytes = await WriteAsync(new XlsxWriter(), [Values, [null, 7, null, null, null, null]]);

        var rows = await ReadAsync(new XlsxReader(), bytes);
        rows[0].ShouldBe(["Name", "Count", "Price", "Born", "Seen", "Active"]);
        rows[1].ShouldBe(["Zoë \"Z\", Jr\nline two", "42", "12.5", "2026-10-17", "2026-10-17T14:30:00", "true"]);
        rows[2].ShouldBe([null, "7"], "empty cells aren't written; the row stops at the last value");
    }

    [Fact]
    public async Task Xlsx_reads_from_streams_that_cannot_seek()
    {
        var bytes = await WriteAsync(new XlsxWriter(), [Values]);

        var rows = await ReadAsync(new XlsxReader(), bytes, seekable: false);

        rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Garbage_is_reported_as_an_invalid_workbook()
    {
        var error = await Should.ThrowAsync<InvalidDataException>(() => ReadAsync(new XlsxReader(), "not a zip"u8.ToArray()));

        error.Message.ShouldBe("The file is not a valid .xlsx workbook.");
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Column_names_follow_excel(int index, string expected) => XlsxWriter.ColumnName(index).ShouldBe(expected);

    private static async Task<byte[]> WriteAsync(ITabularWriter writer, IEnumerable<object?[]> rows)
    {
        using var output = new MemoryStream();
        await writer.WriteAsync(Columns, rows.Select(r => (IReadOnlyList<object?>)r).ToAsyncEnumerable(), output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static async Task<List<IReadOnlyList<string?>>> ReadAsync(ITabularReader reader, byte[] bytes, bool seekable = true)
    {
        Stream input = seekable ? new MemoryStream(bytes) : new ForwardOnlyStream(bytes);
        var rows = new List<IReadOnlyList<string?>>();
        await foreach (var row in reader.ReadAsync(input, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }

    private sealed class ForwardOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
