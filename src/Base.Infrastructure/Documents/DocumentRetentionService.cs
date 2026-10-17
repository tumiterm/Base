using Base.Application.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Base.Infrastructure.Documents;

/// <summary>Runs <see cref="IDocumentRetention"/> once a day (archive and delete per document type).</summary>
internal sealed partial class DocumentRetentionService(IServiceScopeFactory scopes, TimeProvider time, ILogger<DocumentRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(4), time, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IDocumentRetention>().ApplyAsync(stoppingToken);
                if (result.Archived + result.Deleted > 0)
                {
                    LogApplied(logger, result.Archived, result.Deleted);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document retention archived {Archived} and deleted {Deleted} documents")]
    private static partial void LogApplied(ILogger logger, int archived, int deleted);

    [LoggerMessage(Level = LogLevel.Error, Message = "Document retention failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
