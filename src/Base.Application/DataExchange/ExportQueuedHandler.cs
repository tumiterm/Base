using System.Globalization;
using System.Text.Json;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Files;
using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Application.Settings;
using Base.Domain.DataExchange;
using Microsoft.Extensions.Logging;

namespace Base.Application.DataExchange;

/// <summary>
/// Produces a large export in the background, as the user who asked for it (their permissions and the rows' access
/// rules apply), stores it as a file only they can download, and tells them in-app.
/// </summary>
internal sealed partial class ExportQueuedHandler(
    IUnitOfWork unitOfWork,
    ExportService exports,
    FileService files,
    IRunAsUser runAs,
    ICurrentUser currentUser,
    INotificationService notifications,
    IApplicationSettingService settings,
    TimeProvider time,
    ILogger<ExportQueuedHandler> logger) : IDomainEventHandler<ExportQueued>
{
    public async Task HandleAsync(ExportQueued domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var job = await unitOfWork.Repository<ExportJob>().GetAsync(j => j.Id == domainEvent.JobId, cancellationToken: cancellationToken);
        if (job is null || job.Status != ExportStatus.Queued)
        {
            return; // already handled (the outbox delivers at least once)
        }

        var exporter = exports.Find(job.ExporterCode);
        using var actingAs = await runAs.BeginAsync(job.RequestedByUserId, cancellationToken);
        if (actingAs is null || exporter is null || !currentUser.HasPermission(exporter.Permission))
        {
            job.MarkFailed("The export can no longer run: the user or their permission is gone.", time.GetUtcNow());
            await unitOfWork.SaveAsync(cancellationToken);
            return;
        }

        job.MarkRunning();
        await unitOfWork.SaveAsync(cancellationToken);

        try
        {
            var filter = new ExportFilter(JsonSerializer.Deserialize<Dictionary<string, string?>>(job.FilterJson) ?? []);
            var (content, rows) = await exports.WriteAsync(exporter, job.Format, filter, cancellationToken);
            await using (content)
            {
                var retention = await settings.GetIntAsync(SettingCodes.Exports.RetentionDays, cancellationToken);
                var extension = DataFormats.ExtensionOf(job.Format);
                var staged = await files.StageAsync(
                    new FileUploadRequest(content, ExportService.FileName(exporter, job.Format), null, "exports", time.GetUtcNow().AddDays(retention)),
                    job.RequestedByUserId, [extension], replaceAllowList: true, cancellationToken, maxBytes: long.MaxValue); // bounded by Exports.MaxRows
                if (!staged.IsSuccess)
                {
                    throw new InvalidOperationException(staged.Message);
                }

                job.MarkCompleted(staged.Data!.File.Id, rows, time.GetUtcNow());
                await staged.Data.SaveOrDiscardAsync(unitOfWork, cancellationToken);
            }

            await NotifyAsync(job, exporter.Name, $"{rows.ToString("N0", CultureInfo.InvariantCulture)} rows are ready. Open Exports to download the file.", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(logger, exception, job.Id, job.ExporterCode);
            job.MarkFailed("The export failed. Try again, or contact support with the time it happened.", time.GetUtcNow());
            await unitOfWork.SaveAsync(cancellationToken);
            await NotifyAsync(job, exporter.Name, "The export failed. Try again, or contact support.", cancellationToken);
        }
    }

    private async Task NotifyAsync(ExportJob job, string name, string outcome, CancellationToken cancellationToken) =>
        await notifications.SendAsync(
            new Notification(NotificationTemplates.DataExchange.ExportReady, NotificationRecipient.ToUser(job.RequestedByUserId), new { Export = name, Outcome = outcome }),
            cancellationToken);

    [LoggerMessage(Level = LogLevel.Error, Message = "Export {JobId} ({Exporter}) failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid jobId, string exporter);
}
