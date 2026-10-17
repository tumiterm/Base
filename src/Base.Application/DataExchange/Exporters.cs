using System.Runtime.CompilerServices;
using Base.Application.Common.Persistence;

namespace Base.Application.DataExchange;

/// <summary>The filter an export runs with: the list's query-string values (e.g. <c>search</c>, <c>status</c>).</summary>
public sealed class ExportFilter(IReadOnlyDictionary<string, string?> values)
{
    public static ExportFilter Empty { get; } = new(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, string?> Values { get; } = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);

    public string? Text(string name) => Values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public Guid? Guid(string name) => System.Guid.TryParse(Text(name), out var id) ? id : null;

    public bool Flag(string name) => bool.TryParse(Text(name), out var flag) && flag;

    public TEnum? Enum<TEnum>(string name)
        where TEnum : struct, Enum =>
        System.Enum.TryParse<TEnum>(Text(name), ignoreCase: true, out var value) && System.Enum.IsDefined(value) ? value : null;
}

public sealed record ExportColumn<TRow>(string Header, Func<TRow, object?> Value, ColumnType Type = ColumnType.Text);

/// <summary>
/// A list that can be exported. Register implementations as <see cref="IExporter"/>; derive from
/// <see cref="Exporter{TRow}"/>. Rows should come from an existing use case (so its access rules apply), paged.
/// </summary>
public interface IExporter
{
    /// <summary>Stable id used in URLs, e.g. <c>users</c>.</summary>
    string Code { get; }

    string Name { get; }

    /// <summary>Needed to run the export (the rows' own access rules apply on top).</summary>
    string Permission { get; }

    IReadOnlyList<TabularColumn> Columns { get; }

    Task<int> CountAsync(ExportFilter filter, CancellationToken cancellationToken);

    IAsyncEnumerable<IReadOnlyList<object?>> RowsAsync(ExportFilter filter, CancellationToken cancellationToken);
}

public abstract class Exporter<TRow> : IExporter
{
    protected const int PageSize = PageRequest.MaxPageSize;

    public abstract string Code { get; }

    public abstract string Name { get; }

    public abstract string Permission { get; }

    public IReadOnlyList<TabularColumn> Columns => Definition.Select(c => new TabularColumn(c.Header, c.Type)).ToList();

    protected abstract IReadOnlyList<ExportColumn<TRow>> Definition { get; }

    public async Task<int> CountAsync(ExportFilter filter, CancellationToken cancellationToken) =>
        (await PageAsync(filter, 1, 1, cancellationToken)).Total;

    public async IAsyncEnumerable<IReadOnlyList<object?>> RowsAsync(ExportFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var columns = Definition;
        for (var page = 1; ; page++)
        {
            var result = await PageAsync(filter, page, PageSize, cancellationToken);
            foreach (var item in result.Items)
            {
                yield return columns.Select(c => c.Value(item)).ToArray();
            }

            if (!result.HasNext)
            {
                yield break;
            }
        }
    }

    /// <summary>One page of rows for the filter, newest first or in another stable order.</summary>
    protected abstract Task<PagedResult<TRow>> PageAsync(ExportFilter filter, int page, int pageSize, CancellationToken cancellationToken);
}
