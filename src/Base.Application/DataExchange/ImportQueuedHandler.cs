using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Domain.DataExchange;
using Base.Domain.Files;
using Microsoft.Extensions.Logging;

namespace Base.Application.DataExchange;

/// <summary>
/// Applies a confirmed import in the background, as the user who uploaded it. Every row is checked again first (the
/// data may have changed since the preview); without <see cref="IImporter.AllowPartial"/>, nothing is applied unless
/// all rows still pass. Rows are then applied one by one; an unexpected error stops the run, and the report says
/// which rows went in.
/// </summary>
internal sealed partial class ImportQueuedHandler(
    IUnitOfWork unitOfWork,
    ImportService imports,
    IRunAsUser runAs,
    ICurrentUser currentUser,
    INotificationService notifications,
    TimeProvider time,
    ILogger<ImportQueuedHandler> logger) : IDomainEventHandler<ImportQueued>
{
    public async Task HandleAsync(ImportQueued domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var job = await unitOfWork.Repository<ImportJob>().GetAsync(j => j.Id == domainEvent.JobId, cancellationToken: cancellationToken);
        if (job is null || job.Status != ImportStatus.Queued)
        {
            return; // already handled (the outbox delivers at least once)
        }

        var importer = imports.Find(job.ImporterCode);
        var file = await unitOfWork.Repository<StoredFile>().GetAsync(f => f.Id == job.FileId, asNoTracking: true, cancellationToken: cancellationToken);
        using var actingAs = await runAs.BeginAsync(job.RequestedByUserId, cancellationToken);
        if (actingAs is null || importer is null || file is null || !currentUser.HasPermission(importer.Permission))
        {
            await FinishAsync(job, importer, j => j.MarkFailed("The import can no longer run: the user, their permission or the file is gone.", time.GetUtcNow()), cancellationToken);
            return;
        }

        job.MarkRunning();
        await unitOfWork.SaveAsync(cancellationToken);

        var context = new ImportContext(isDryRun: false);
        var run = await imports.RunAsync(importer, job, file, context, cancellationToken);
        if (!job.AllowPartial && run.Errors.Count > 0)
        {
            await FinishAsync(job, importer, j => j.MarkFailed(
                $"Nothing was applied: {run.Total - run.Valid.Count} row(s) are no longer valid (the data changed since the preview).",
                time.GetUtcNow(), ImportService.Serialize(run.Errors)), cancellationToken);
            return;
        }

        var failures = new List<ImportRowError>();
        var applied = 0;
        foreach (var row in run.Valid)
        {
            try
            {
                var result = await importer.ApplyAsync(row, context, cancellationToken);
                if (result.IsSuccess)
                {
                    applied++;
                    continue;
                }

                failures.AddRange(result.Errors.Select(e => new ImportRowError(row.Number, string.IsNullOrEmpty(e.Field) ? null : e.Field, e.Message)));
                if (!job.AllowPartial)
                {
                    break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The unit of work may hold a half-applied change, so stop rather than risk compounding it.
                LogRowFailed(logger, exception, job.Id, row.Number);
                failures.Add(new ImportRowError(row.Number, null, "An unexpected error stopped the import at this row."));
                break;
            }
        }

        var failed = run.Total - applied;
        var errors = run.Errors.Concat(failures).ToList();
        await FinishAsync(job, importer, j => j.MarkCompleted(applied, failed, ImportService.Serialize(errors), time.GetUtcNow()), cancellationToken);
    }

    private async Task FinishAsync(ImportJob job, IImporter? importer, Action<ImportJob> finish, CancellationToken cancellationToken)
    {
        finish(job);
        await unitOfWork.SaveAsync(cancellationToken);

        var outcome = job.Status switch
        {
            ImportStatus.Completed => $"All {job.AppliedRows.ToString("N0", CultureInfo.InvariantCulture)} rows were applied.",
            ImportStatus.CompletedWithErrors => $"{job.AppliedRows.ToString("N0", CultureInfo.InvariantCulture)} of {job.TotalRows.ToString("N0", CultureInfo.InvariantCulture)} rows were applied; see Imports for the rest.",
            _ => job.Error ?? "The import failed.",
        };
        await notifications.SendAsync(
            new Notification(NotificationTemplates.DataExchange.ImportFinished, NotificationRecipient.ToUser(job.RequestedByUserId), new { Import = importer?.Name ?? job.ImporterCode, Outcome = outcome }),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Import {JobId} failed at row {Row}")]
    private static partial void LogRowFailed(ILogger logger, Exception exception, Guid jobId, int row);
}
