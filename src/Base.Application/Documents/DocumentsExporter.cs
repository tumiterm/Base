using Base.Application.Common.Persistence;
using Base.Application.DataExchange;
using Base.Application.Security;
using Base.Domain.Documents;

namespace Base.Application.Documents;

/// <summary>
/// The document list (metadata only, not the files). The access policy applies: people without
/// <c>Documents.View</c> export only their own. Filters: <c>search</c>, <c>type</c>, <c>tag</c>, <c>status</c>,
/// <c>linkType</c>/<c>linkId</c>, <c>includeArchived</c>.
/// </summary>
internal sealed class DocumentsExporter(IDocumentService documents) : Exporter<DocumentSummary>
{
    public override string Code => "documents";

    public override string Name => "Documents";

    public override string Permission => PermissionCodes.Documents.Create;

    protected override IReadOnlyList<ExportColumn<DocumentSummary>> Definition { get; } =
    [
        new("Title", d => d.Title),
        new("Type", d => DocumentTypeCatalog.Find(d.DocumentType)?.Name ?? d.DocumentType),
        new("Status", d => d.Status),
        new("Version", d => d.CurrentVersion, ColumnType.Integer),
        new("Tags", d => d.Tags),
        new("Linked to", d => d.LinkedEntityType is null ? null : $"{d.LinkedEntityType} {d.LinkedEntityId}"),
        new("Created", d => d.DateCreated, ColumnType.DateTime),
        new("Updated", d => d.DateModified, ColumnType.DateTime),
        new("Created by", d => d.UserCreated),
    ];

    protected override Task<PagedResult<DocumentSummary>> PageAsync(ExportFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var linkType = filter.Text("linkType");
        var linkId = filter.Text("linkId");
        return documents.SearchAsync(
            new DocumentQuery(
                filter.Text("search"), filter.Text("type"), filter.Text("tag"), filter.Enum<DocumentStatus>("status"),
                linkType is null || linkId is null ? null : new DocumentLink(linkType, linkId), filter.Flag("includeArchived"), page, pageSize),
            cancellationToken);
    }
}
