using Base.Application.Common.Results;

namespace Base.Application.DataExchange;

/// <param name="Name">The header users see (and the template writes).</param>
/// <param name="Aliases">Other headers accepted for this column (e.g. "E-mail" for "Email"). Matching ignores case, spaces, dashes and underscores.</param>
/// <param name="Choices">Allowed values (matched ignoring case, stored as written here); a drop-down in the template.</param>
/// <param name="Example">Shown in the template's example row.</param>
public sealed record ImportColumn(
    string Name,
    ColumnType Type = ColumnType.Text,
    bool Required = false,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Choices = null,
    int? MaxLength = null,
    string? Example = null);

/// <param name="Row">The spreadsheet row number (the header is row 1), so users can find it.</param>
public sealed record ImportRowError(int Row, string? Column, string Message);

/// <summary>One data row, parsed to the column types. Read values by column name.</summary>
public sealed class ImportRow(int number, IReadOnlyDictionary<string, object?> values, IReadOnlyList<string?> raw)
{
    public int Number { get; } = number;

    /// <summary>The cells as they were in the file, in file column order (for the error report).</summary>
    public IReadOnlyList<string?> Raw { get; } = raw;

    public string? Text(string column) => Get<string>(column);

    public long? Integer(string column) => Get<long?>(column);

    public decimal? Decimal(string column) => Get<decimal?>(column);

    public DateOnly? Date(string column) => Get<DateOnly?>(column);

    public DateTime? DateTime(string column) => Get<DateTime?>(column);

    public bool? Boolean(string column) => Get<bool?>(column);

    /// <summary>A text cell split on commas or semicolons, trimmed, empty parts dropped.</summary>
    public IReadOnlyList<string> List(string column) =>
        (Text(column) ?? string.Empty).Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private T? Get<T>(string column) => values.TryGetValue(column, out var value) && value is T typed ? typed : default;
}

/// <summary>State shared by one run over a file: the dry run, or the apply. Importers keep things like keys already seen.</summary>
public sealed class ImportContext(bool isDryRun)
{
    public bool IsDryRun { get; } = isDryRun;

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>A set kept for the run, e.g. to catch the same email twice in one file.</summary>
    public ISet<string> Seen(string name)
    {
        if (!Items.TryGetValue(name, out var set) || set is not HashSet<string> seen)
        {
            seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Items[name] = seen;
        }

        return seen;
    }
}

/// <summary>
/// A kind of file users can import. Register implementations as <see cref="IImporter"/>; derive from
/// <see cref="Importer"/>. Apply rows through existing use cases, so their rules and permission checks hold:
/// imports run as the user who started them.
/// </summary>
public interface IImporter
{
    string Code { get; }

    string Name { get; }

    string Description { get; }

    string Permission { get; }

    IReadOnlyList<ImportColumn> Columns { get; }

    /// <summary>False (the default): nothing is applied unless every row is valid. True: valid rows go in, the rest are reported.</summary>
    bool AllowPartial { get; }

    /// <summary>Business checks on a row whose cells parsed (duplicates, references that must exist...).</summary>
    Task<IReadOnlyList<ImportRowError>> ValidateAsync(ImportRow row, ImportContext context, CancellationToken cancellationToken);

    /// <summary>Applies one valid row. A failure is recorded against the row.</summary>
    Task<ValidationResponse> ApplyAsync(ImportRow row, ImportContext context, CancellationToken cancellationToken);
}

public abstract class Importer : IImporter
{
    public abstract string Code { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string Permission { get; }

    public abstract IReadOnlyList<ImportColumn> Columns { get; }

    public virtual bool AllowPartial => false;

    public virtual Task<IReadOnlyList<ImportRowError>> ValidateAsync(ImportRow row, ImportContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ImportRowError>>([]);

    public abstract Task<ValidationResponse> ApplyAsync(ImportRow row, ImportContext context, CancellationToken cancellationToken);

    protected static IReadOnlyList<ImportRowError> Errors(params ImportRowError[] errors) => errors;
}
