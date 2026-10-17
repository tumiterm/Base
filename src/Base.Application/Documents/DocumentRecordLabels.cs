using Base.Application.Common.Persistence;
using Base.Application.Common.Records;
using Base.Domain.Documents;

namespace Base.Application.Documents;

/// <summary>Names documents by title, for the documents the access policy lets the current user read.</summary>
internal sealed class DocumentRecordLabels(IUnitOfWork unitOfWork, IDocumentAccessPolicy policy) : IRecordLabelProvider
{
    public string EntityType => nameof(Document);

    public string TypeName => "Document";

    public async Task<IReadOnlyDictionary<string, RecordLabel>> GetLabelsAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken = default)
    {
        var ids = RecordIds.Guids(entityIds);
        if (ids.Count == 0)
        {
            return new Dictionary<string, RecordLabel>(StringComparer.Ordinal);
        }

        var documents = await unitOfWork.Repository<Document>().GetAllAsync(d => ids.Contains(d.Id), asNoTracking: true, cancellationToken: cancellationToken);
        return RecordIds.ByStoredId(entityIds, documents
            .Where(d => policy.Can(d, DocumentAction.Read))
            .ToDictionary(d => d.Id, d => new RecordLabel(d.Title, DocumentTypeCatalog.Find(d.DocumentType)?.Name)));
    }
}
