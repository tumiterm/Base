using Base.Application.Operations;

namespace Base.Application.Documents;

/// <summary>Applies each document type's archive and delete rules (<see cref="IDocumentRetention"/>).</summary>
internal sealed class DocumentRetentionJob(IDocumentRetention retention) : IScheduledJob
{
    public string Code => "document-retention";

    public string Name => "Document retention";

    public string Description => "Archives and deletes documents as their type's rules say.";

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var result = await retention.ApplyAsync(cancellationToken);
        return $"Archived {result.Archived}, deleted {result.Deleted}.";
    }
}
