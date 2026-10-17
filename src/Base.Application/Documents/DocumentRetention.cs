using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Domain.Documents;

namespace Base.Application.Documents;

public sealed record DocumentRetentionResult(int Archived, int Deleted);

/// <summary>Applies each document type's <c>ArchiveAfterDays</c>/<c>DeleteAfterDays</c> across all tenants. Run daily.</summary>
public interface IDocumentRetention
{
    Task<DocumentRetentionResult> ApplyAsync(CancellationToken cancellationToken = default);
}

internal sealed class DocumentRetention(IUnitOfWork unitOfWork, ITenantContext tenant, TimeProvider time) : IDocumentRetention
{
    private const int BatchSize = 200;

    public async Task<DocumentRetentionResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        using var system = tenant.BeginSystemScope();
        var now = time.GetUtcNow();
        var repository = unitOfWork.Repository<Document>();
        int archived = 0, deleted = 0;

        foreach (var type in DocumentTypeCatalog.All)
        {
            if (type.ArchiveAfterDays is { } archiveDays)
            {
                var cutoff = now.AddDays(-archiveDays);
                IReadOnlyList<Document> batch;
                do
                {
                    batch = await repository.GetAllAsync(
                        filter: d => d.DocumentType == type.Code && d.Status == DocumentStatus.Final && d.FinalizedAt < cutoff,
                        orderBy: q => q.OrderBy(d => d.Id),
                        take: BatchSize,
                        cancellationToken: cancellationToken);
                    foreach (var document in batch)
                    {
                        document.Archive(now);
                    }

                    await unitOfWork.SaveAsync(cancellationToken);
                    archived += batch.Count;
                }
                while (batch.Count == BatchSize);
            }

            if (type.DeleteAfterDays is { } deleteDays)
            {
                var cutoff = now.AddDays(-deleteDays);
                IReadOnlyList<Document> batch;
                do
                {
                    batch = await repository.GetAllAsync(
                        filter: d => d.DocumentType == type.Code && d.Status == DocumentStatus.Archived && d.ArchivedAt < cutoff,
                        includeProperties: [nameof(Document.Versions)],
                        orderBy: q => q.OrderBy(d => d.Id),
                        take: BatchSize,
                        cancellationToken: cancellationToken);
                    foreach (var document in batch)
                    {
                        await DocumentService.RemoveAsync(unitOfWork, document, cancellationToken);
                    }

                    await unitOfWork.SaveAsync(cancellationToken);
                    deleted += batch.Count;
                }
                while (batch.Count == BatchSize);
            }
        }

        return new DocumentRetentionResult(archived, deleted);
    }
}
