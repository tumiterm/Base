using Base.Application.DataExchange;

namespace Base.UnitTests.DataExchange;

public sealed class ImportParserTests
{
    private static readonly ImportColumn[] Columns =
    [
        new("Email address", Required: true, Aliases: ["E-mail"]),
        new("Count", ColumnType.Integer),
        new("Price", ColumnType.Decimal),
        new("Start date", ColumnType.Date),
        new("Active", ColumnType.Boolean),
        new("Size", Choices: ["Small", "Large"], MaxLength: 5),
    ];

    [Fact]
    public void Headers_match_names_and_aliases_ignoring_case_spaces_and_dashes()
    {
        var (map, errors) = ImportParser.MapHeader(Columns, ["notes", "e_mail", "START-DATE", " count "]);

        errors.ShouldBeEmpty();
        map.ShouldBe(new Dictionary<string, int>(StringComparer.Ordinal) { ["Email address"] = 1, ["Start date"] = 2, ["Count"] = 3 });
    }

    [Fact]
    public void Missing_required_and_repeated_columns_are_reported_on_row_1()
    {
        var (_, errors) = ImportParser.MapHeader(Columns, ["Count", "count"]);

        errors.ShouldBe([new ImportRowError(1, "Count", "The column 'Count' appears more than once."), new ImportRowError(1, "Email address", "The required column 'Email address' is missing.")]);
    }

    [Fact]
    public void Cells_parse_to_their_column_types()
    {
        var (map, _) = ImportParser.MapHeader(Columns, ["Email address", "Count", "Price", "Start date", "Active", "Size"]);

        var (values, errors) = ImportParser.ParseRow(Columns, map, ["a@b.c", "12", "9.99", "17/10/2026", "Yes", "large"], 2);

        errors.ShouldBeEmpty();
        var row = new ImportRow(2, values, []);
        row.Integer("Count").ShouldBe(12);
        row.Decimal("Price").ShouldBe(9.99m);
        row.Date("Start date").ShouldBe(new DateOnly(2026, 10, 17));
        row.Boolean("Active").ShouldBe(true);
        row.Text("Size").ShouldBe("Large", "choices are stored as declared");
    }

    [Fact]
    public void Bad_cells_are_reported_by_column_with_the_row_number()
    {
        var (map, _) = ImportParser.MapHeader(Columns, ["Email address", "Count", "Price", "Start date", "Active", "Size"]);

        var (_, errors) = ImportParser.ParseRow(Columns, map, ["", "1.5", "abc", "2026-13-40", "maybe", "Medium"], 7);

        errors.Select(e => (e.Row, e.Column)).ShouldBe([(7, "Email address"), (7, "Count"), (7, "Price"), (7, "Start date"), (7, "Active"), (7, "Size")]);
        errors[0].Message.ShouldBe("A value is required.");
        errors[^1].Message.ShouldBe("Too long: at most 5 characters.");
    }

    [Fact]
    public void Lists_split_on_commas_and_semicolons()
    {
        var row = new ImportRow(2, new Dictionary<string, object?>(StringComparer.Ordinal) { ["Roles"] = "User; Manager,, Admin " }, []);

        row.List("Roles").ShouldBe(["User", "Manager", "Admin"]);
    }
}
