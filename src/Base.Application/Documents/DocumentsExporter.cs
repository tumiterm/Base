using Base.Application.Common.Persistence;
using Base.Application.Common.Records;
using Base.Application.DataExchange;
using Base.Application.Security;
using Base.Domain.Documents;

namespace Base.Application.Documents;

/// <summary>
/// The document list (metadata only, not the files). The access policy applies: people without
/// <c>Documents.View</c> export only their own. Filters: <c>search</c>, <c>type</c>, <c>tag</c>, <c>status</c>,
/// <c>linkType</c>/<c>linkId</c>, <c>includeArchived</c>.
/// </summary>
internal sealed class DocumentsExporter(IDocumentService documents, IRecordLabels records) : Exporter<DocumentExportRow>
{
    public override string Code => "documents";

    public override string Name => "Documents";

    public override string Permission => PermissionCodes.Documents.Create;

    protected override IReadOnlyList<ExportColumn<DocumentExportRow>> Definition { get; } =
    [
        new("Title", r => r.Document.Title),
        new("Type", r => DocumentTypeCatalog.Find(r.Document.DocumentType)?.Name ?? r.Document.DocumentType),
        new("Status", r => r.Document.Status),
        new("Version", r => r.Document.CurrentVersion, ColumnType.Integer),
        new("Tags", r => r.Document.Tags),
        new("Linked to", r => r.LinkedTo),
        new("Created", r => r.Document.DateCreated, ColumnType.DateTime),
        new("Updated", r => r.Document.DateModified, ColumnType.DateTime),
        new("Created by", r => r.Document.UserCreated),
    ];

    protected override async Task<PagedResult<DocumentExportRow>> PageAsync(ExportFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var linkType = filter.Text("linkType");
        var linkId = filter.Text("linkId");
        var found = await documents.SearchAsync(
            new DocumentQuery(
                filter.Text("search"), filter.Text("type"), filter.Text("tag"), filter.Enum<DocumentStatus>("status"),
                linkType is null || linkId is null ? null : new DocumentLink(linkType, linkId), filter.Flag("includeArchived"), page, pageSize),
            cancellationToken);
        var linked = await records.ResolveAsync(found.Items.Select(d => d.LinkedTo).OfType<RecordRef>(), cancellationToken);
        var rows = found.Items.Select(d => new DocumentExportRow(d, d.LinkedTo is { } link ? linked[link].Text : null)).ToList();
        return new PagedResult<DocumentExportRow>(rows, found.Total, found.Page, found.PageSize);
    }
}

/// <summary>A document with its link already put into words ("Person: Thandi Nkosi").</summary>
internal sealed record DocumentExportRow(DocumentSummary Document, string? LinkedTo);
