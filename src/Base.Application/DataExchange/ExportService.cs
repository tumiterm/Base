using System.Runtime.CompilerServices;
using System.Text.Json;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Settings;
using Base.Domain.DataExchange;

namespace Base.Application.DataExchange;

/// <summary>A finished export to send to the user. The caller disposes <see cref="Content"/>.</summary>
public sealed record ExportedFile(string FileName, string ContentType, Stream Content);

/// <summary>Either the file (small exports) or the id of the background job producing it.</summary>
public sealed record ExportOutcome(Guid JobId, ExportedFile? File);

public sealed record ExporterInfo(string Code, string Name);

public sealed record ExportJobView(Guid Id, string ExporterCode, string ExporterName, DataFormat Format, ExportStatus Status, int RowCount, bool CanDownload, string? Error, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);

/// <summary>
/// Exports any registered <see cref="IExporter"/> list as CSV or Excel. Every export is recorded as an
/// <see cref="ExportJob"/>, so who exported what (and with which filter) is in the history and the audit trail.
/// </summary>
public interface IExportService
{
    /// <summary>The exporters the current user may run.</summary>
    IReadOnlyList<ExporterInfo> Available { get; }

    Task<ValidationResponse<ExportOutcome>> ExportAsync(string exporterCode, DataFormat format, ExportFilter filter, CancellationToken cancellationToken = default);

    /// <summary>The current user's exports, newest first.</summary>
    Task<PagedResult<ExportJobView>> HistoryAsync(PageRequest page, CancellationToken cancellationToken = default);

    /// <summary>A background export's file (only its owner can download it, until it expires).</summary>
    Task<ValidationResponse<Files.FileDownload>> DownloadAsync(Guid jobId, CancellationToken cancellationToken = default);
}

internal sealed class ExportService(
    IEnumerable<IExporter> exporters,
    IEnumerable<ITabularWriter> writers,
    IUnitOfWork unitOfWork,
    IApplicationSettingService settings,
    ICurrentUser currentUser,
    Files.IFileService files,
    TimeProvider time) : IExportService
{
    public IReadOnlyList<ExporterInfo> Available =>
        exporters.Where(e => currentUser.HasPermission(e.Permission)).Select(e => new ExporterInfo(e.Code, e.Name)).ToList();

    public async Task<ValidationResponse<ExportOutcome>> ExportAsync(string exporterCode, DataFormat format, ExportFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var exporter = Find(exporterCode);
        if (exporter is null || currentUser.UserId is not { } userId)
        {
            return ValidationResponse.NotFound("Unknown export.").As<ExportOutcome>();
        }

        if (!currentUser.HasPermission(exporter.Permission))
        {
            return ValidationResponse.Forbidden().As<ExportOutcome>();
        }

        var count = await exporter.CountAsync(filter, cancellationToken);
        var max = await settings.GetIntAsync(SettingCodes.Exports.MaxRows, cancellationToken);
        if (count > max)
        {
            return ValidationResponse.Failure($"This export has {count:N0} rows; the most an export can hold is {max:N0}. Narrow the filter.", "export.too-large").As<ExportOutcome>();
        }

        var filterJson = JsonSerializer.Serialize(filter.Values);
        var jobs = unitOfWork.Repository<ExportJob>();
        if (count > await settings.GetIntAsync(SettingCodes.Exports.MaxImmediateRows, cancellationToken))
        {
            var queued = ExportJob.Queue(exporter.Code, format, filterJson, userId);
            await jobs.AddAsync(queued, cancellationToken);
            await unitOfWork.SaveAsync(cancellationToken);
            return new ExportOutcome(queued.Id, null);
        }

        var content = await WriteAsync(exporter, format, filter, cancellationToken);
        var job = ExportJob.Immediate(exporter.Code, format, filterJson, userId, content.Rows, time.GetUtcNow());
        await jobs.AddAsync(job, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
        return new ExportOutcome(job.Id, new ExportedFile(FileName(exporter, format), ContentType(format), content.Stream));
    }

    public async Task<PagedResult<ExportJobView>> HistoryAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        var now = time.GetUtcNow();
        var result = await unitOfWork.Repository<ExportJob>().GetPagedAsync(
            page,
            j => j,
            q => q.OrderByDescending(j => j.DateCreated).ThenByDescending(j => j.Id),
            j => j.RequestedByUserId == userId,
            cancellationToken);
        var retention = await settings.GetIntAsync(SettingCodes.Exports.RetentionDays, cancellationToken);
        return new PagedResult<ExportJobView>(
            result.Items.Select(j => new ExportJobView(
                j.Id, j.ExporterCode, Find(j.ExporterCode)?.Name ?? j.ExporterCode, j.Format, j.Status, j.RowCount,
                j.FileId is not null && j.CompletedAt?.AddDays(retention) > now, j.Error, j.DateCreated, j.CompletedAt)).ToList(),
            result.Total, result.Page, result.PageSize);
    }

    public async Task<ValidationResponse<Files.FileDownload>> DownloadAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        var job = await unitOfWork.Repository<ExportJob>().GetAsync(j => j.Id == jobId && j.RequestedByUserId == userId, asNoTracking: true, cancellationToken: cancellationToken);
        return job?.FileId is { } fileId
            ? await files.GetDownloadAsync(fileId, cancellationToken)
            : ValidationResponse.NotFound("Export not found.").As<Files.FileDownload>();
    }

    internal IExporter? Find(string code) => exporters.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes the export to a temporary file (deleted when the stream is disposed), positioned at the start.</summary>
    internal async Task<(Stream Stream, int Rows)> WriteAsync(IExporter exporter, DataFormat format, ExportFilter filter, CancellationToken cancellationToken)
    {
        var writer = writers.Single(w => w.Format == format);
        var zone = await DisplayZoneAsync(cancellationToken);
        var spool = TempFiles.Create();
        try
        {
            var counter = new RowCounter();
            await writer.WriteAsync(exporter.Columns, Values(exporter.RowsAsync(filter, cancellationToken), zone, counter, cancellationToken), spool, cancellationToken);
            spool.Position = 0;
            return (spool, counter.Count);
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }

    internal static string FileName(IExporter exporter, DataFormat format) => exporter.Code + DataFormats.ExtensionOf(format);

    internal static string ContentType(DataFormat format) => Files.FileSignatures.ContentTypeFor(DataFormats.ExtensionOf(format));

    private async Task<TimeZoneInfo> DisplayZoneAsync(CancellationToken cancellationToken)
    {
        var zoneId = await settings.GetAsync(SettingCodes.General.DisplayTimeZone, cancellationToken);
        return zoneId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    /// <summary>Timestamps become local wall-clock times in the display time zone; enums and ids become text.</summary>
    private static async IAsyncEnumerable<IReadOnlyList<object?>> Values(
        IAsyncEnumerable<IReadOnlyList<object?>> rows, TimeZoneInfo zone, RowCounter counter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            counter.Count++;
            yield return row.Select(value => value switch
            {
                DateTimeOffset moment => TimeZoneInfo.ConvertTime(moment, zone).DateTime,
                DateOnly date => date.ToDateTime(TimeOnly.MinValue),
                Enum or Guid => value.ToString(),
                IEnumerable<string> list => string.Join(", ", list),
                _ => value,
            }).ToArray();
        }
    }

    private sealed class RowCounter
    {
        public int Count { get; set; }
    }
}

internal static class TempFiles
{
    public static FileStream Create() => new(
        Path.Join(Path.GetTempPath(), Path.GetRandomFileName()),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
        FileOptions.Asynchronous | FileOptions.DeleteOnClose);
}
