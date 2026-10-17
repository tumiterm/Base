using System.Text.Json;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Files;
using Base.Application.Settings;
using Base.Domain.DataExchange;
using Base.Domain.Files;

namespace Base.Application.DataExchange;

public sealed record ImporterInfo(string Code, string Name, string Description, IReadOnlyList<ImportColumn> Columns, bool AllowPartial);

public sealed record ImportJobView(
    Guid Id,
    string ImporterCode,
    string ImporterName,
    string FileName,
    ImportStatus Status,
    int TotalRows,
    int ValidRows,
    int InvalidRows,
    int AppliedRows,
    int FailedRows,
    bool CanConfirm,
    bool AllowPartial,
    string? Error,
    IReadOnlyList<ImportRowError> Errors,
    DateTimeOffset UploadedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Imports CSV or Excel files through a registered <see cref="IImporter"/>: upload (normal file checks apply), a dry run
/// that checks every row and saves nothing, then, once confirmed, the apply step in the background as the uploader.
/// </summary>
public interface IImportService
{
    /// <summary>The importers the current user may run.</summary>
    IReadOnlyList<ImporterInfo> Available { get; }

    /// <summary>A file with the importer's headers, an example row and drop-downs for choice columns.</summary>
    Task<ValidationResponse<ExportedFile>> TemplateAsync(string importerCode, DataFormat format, CancellationToken cancellationToken = default);

    /// <summary>Stores the file and runs the dry run. Nothing is applied until <see cref="ConfirmAsync"/>.</summary>
    Task<ValidationResponse<ImportJobView>> UploadAsync(string importerCode, Stream content, string fileName, CancellationToken cancellationToken = default);

    Task<ValidationResponse<ImportJobView>> ConfirmAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<ValidationResponse<ImportJobView>> GetAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>The current user's imports, newest first.</summary>
    Task<PagedResult<ImportJobView>> HistoryAsync(PageRequest page, CancellationToken cancellationToken = default);

    /// <summary>The rows that had errors, with an extra Errors column: fix them and upload this file again.</summary>
    Task<ValidationResponse<ExportedFile>> ErrorFileAsync(Guid jobId, CancellationToken cancellationToken = default);
}

internal sealed class ImportService(
    IEnumerable<IImporter> importers,
    IEnumerable<ITabularWriter> writers,
    IEnumerable<ITabularReader> readers,
    IUnitOfWork unitOfWork,
    FileService files,
    IApplicationSettingService settings,
    ICurrentUser currentUser,
    TimeProvider time) : IImportService
{
    /// <summary>Row errors kept per job; the counts stay exact beyond it.</summary>
    internal const int MaxStoredErrors = 1000;

    private static readonly string[] Extensions = [".csv", ".xlsx"];

    public IReadOnlyList<ImporterInfo> Available =>
        importers.Where(i => currentUser.HasPermission(i.Permission))
            .Select(i => new ImporterInfo(i.Code, i.Name, i.Description, i.Columns, i.AllowPartial)).ToList();

    public async Task<ValidationResponse<ExportedFile>> TemplateAsync(string importerCode, DataFormat format, CancellationToken cancellationToken = default)
    {
        var importer = Find(importerCode);
        if (importer is null || !currentUser.HasPermission(importer.Permission))
        {
            return ValidationResponse.NotFound("Unknown import.").As<ExportedFile>();
        }

        var columns = importer.Columns.Select(c => new TabularColumn(c.Name, ColumnType.Text, c.Choices)).ToList();
        IReadOnlyList<object?> example = importer.Columns.Select(c => (object?)c.Example).ToArray();
        var spool = TempFiles.Create();
        await writers.Single(w => w.Format == format).WriteAsync(columns, One(example), spool, cancellationToken);
        spool.Position = 0;
        return new ExportedFile($"{importer.Code}-template{DataFormats.ExtensionOf(format)}", ExportService.ContentType(format), spool);
    }

    public async Task<ValidationResponse<ImportJobView>> UploadAsync(string importerCode, Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        var importer = Find(importerCode);
        if (importer is null || currentUser.UserId is not { } userId || !currentUser.HasPermission(importer.Permission))
        {
            return ValidationResponse.NotFound("Unknown import.").As<ImportJobView>();
        }

        if (DataFormats.FromExtension(Path.GetExtension(fileName)) is not { } format)
        {
            return ValidationResponse.Failure("Upload a .csv or .xlsx file.", "import.format").As<ImportJobView>();
        }

        var retention = await settings.GetIntAsync(SettingCodes.Imports.RetentionDays, cancellationToken);
        var staged = await files.StageAsync(
            new FileUploadRequest(content, fileName, null, "imports", time.GetUtcNow().AddDays(retention)), userId, Extensions, replaceAllowList: true, cancellationToken);
        if (!staged.IsSuccess)
        {
            return staged.As<ImportJobView>();
        }

        var file = staged.Data!.File;
        var job = new ImportJob(importer.Code, format, file.Id, file.OriginalFileName, userId, importer.AllowPartial);
        await unitOfWork.Repository<ImportJob>().AddAsync(job, cancellationToken);
        await staged.Data.SaveOrDiscardAsync(unitOfWork, cancellationToken);

        var run = await RunAsync(importer, job, file, new ImportContext(isDryRun: true), cancellationToken);
        job.RecordDryRun(run.Total, run.Valid.Count, Serialize(run.Errors));
        await unitOfWork.SaveAsync(cancellationToken);
        return View(job, importer, run.Errors);
    }

    public async Task<ValidationResponse<ImportJobView>> ConfirmAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await MineAsync(jobId, tracked: true, cancellationToken);
        var importer = job is null ? null : Find(job.ImporterCode);
        if (job is null || importer is null || !currentUser.HasPermission(importer.Permission))
        {
            return ValidationResponse.NotFound("Import not found.").As<ImportJobView>();
        }

        if (!job.CanConfirm)
        {
            return ValidationResponse.Conflict(job.Status == ImportStatus.Invalid
                ? "Every row must be valid before this import can be applied. Download the error file, fix it and upload it again."
                : "This import has already been applied.").As<ImportJobView>();
        }

        job.Confirm();
        await unitOfWork.SaveAsync(cancellationToken);
        return View(job, importer, Deserialize(job.ErrorsJson));
    }

    public async Task<ValidationResponse<ImportJobView>> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await MineAsync(jobId, tracked: false, cancellationToken);
        return job is null ? ValidationResponse.NotFound("Import not found.").As<ImportJobView>() : View(job, Find(job.ImporterCode), Deserialize(job.ErrorsJson));
    }

    public async Task<PagedResult<ImportJobView>> HistoryAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        var result = await unitOfWork.Repository<ImportJob>().GetPagedAsync(
            page, j => j, q => q.OrderByDescending(j => j.DateCreated).ThenByDescending(j => j.Id), j => j.RequestedByUserId == userId, cancellationToken);
        return new PagedResult<ImportJobView>(result.Items.Select(j => View(j, Find(j.ImporterCode), [])).ToList(), result.Total, result.Page, result.PageSize);
    }

    public async Task<ValidationResponse<ExportedFile>> ErrorFileAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await MineAsync(jobId, tracked: false, cancellationToken);
        var file = job is null ? null : await unitOfWork.Repository<StoredFile>().GetAsync(f => f.Id == job.FileId, asNoTracking: true, cancellationToken: cancellationToken);
        if (job is null || file is null)
        {
            return ValidationResponse.NotFound("The uploaded file is no longer available.").As<ExportedFile>();
        }

        var byRow = Deserialize(job.ErrorsJson).GroupBy(e => e.Row).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(e => e.Column is null ? e.Message : $"{e.Column}: {e.Message}")));
        var header = Array.Empty<string?>();
        var rows = new List<IReadOnlyList<object?>>();
        await using (var content = await files.OpenReadAsync(file, cancellationToken))
        {
            var number = 0;
            await foreach (var cells in readers.Single(r => r.Format == job.Format).ReadAsync(content, cancellationToken))
            {
                number++;
                if (number == 1)
                {
                    header = [.. cells];
                }
                else if (byRow.TryGetValue(number, out var errors))
                {
                    rows.Add([.. cells.Cast<object?>().Concat(Enumerable.Repeat<object?>(null, Math.Max(0, header.Length - cells.Count))), errors]);
                }
            }
        }

        var columns = header.Select(h => new TabularColumn(h ?? string.Empty)).Append(new TabularColumn("Errors")).ToList();
        var spool = TempFiles.Create();
        await writers.Single(w => w.Format == job.Format).WriteAsync(columns, rows.ToAsyncEnumerable(), spool, cancellationToken);
        spool.Position = 0;
        return new ExportedFile($"{Path.GetFileNameWithoutExtension(job.FileName)}-errors{DataFormats.ExtensionOf(job.Format)}", ExportService.ContentType(job.Format), spool);
    }

    internal IImporter? Find(string code) => importers.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads and checks every row. Valid rows are returned for the apply step; errors are capped at <see cref="MaxStoredErrors"/>.</summary>
    internal async Task<ImportRun> RunAsync(IImporter importer, ImportJob job, StoredFile file, ImportContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAndCheckAsync(importer, job, file, context, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            // A file that passed the signature check but isn't a readable workbook: a user error, not a crash.
            return new ImportRun(0, [], [new ImportRowError(1, null, exception.Message)]);
        }
    }

    private async Task<ImportRun> ReadAndCheckAsync(IImporter importer, ImportJob job, StoredFile file, ImportContext context, CancellationToken cancellationToken)
    {
        var maxRows = await settings.GetIntAsync(SettingCodes.Imports.MaxRows, cancellationToken);
        var errors = new List<ImportRowError>();
        var valid = new List<ImportRow>();
        var total = 0;
        Dictionary<string, int>? map = null;
        var number = 0;

        await using var content = await files.OpenReadAsync(file, cancellationToken);
        await foreach (var cells in readers.Single(r => r.Format == job.Format).ReadAsync(content, cancellationToken))
        {
            number++;
            if (map is null)
            {
                var header = ImportParser.MapHeader(importer.Columns, cells);
                if (header.Errors.Count > 0)
                {
                    return new ImportRun(0, [], header.Errors);
                }

                map = header.Map;
                continue;
            }

            if (ImportParser.IsEmpty(cells))
            {
                continue;
            }

            if (++total > maxRows)
            {
                return new ImportRun(total, [], [new ImportRowError(number, null, $"The file has more than {maxRows:N0} rows. Split it into smaller files.")]);
            }

            var (values, rowErrors) = ImportParser.ParseRow(importer.Columns, map, cells, number);
            var row = new ImportRow(number, values, cells);
            if (rowErrors.Count == 0)
            {
                rowErrors.AddRange(await importer.ValidateAsync(row, context, cancellationToken));
            }

            if (rowErrors.Count == 0)
            {
                valid.Add(row);
                continue;
            }

            errors.AddRange(rowErrors.Take(Math.Max(0, MaxStoredErrors - errors.Count)));
        }

        if (map is null)
        {
            return new ImportRun(0, [], [new ImportRowError(1, null, "The file is empty: the first row must hold the column headers.")]);
        }

        if (total == 0)
        {
            errors.Add(new ImportRowError(2, null, "The file has headers but no data rows."));
        }

        return new ImportRun(total, valid, errors);
    }

    internal static string Serialize(IReadOnlyList<ImportRowError> errors) => JsonSerializer.Serialize(errors.Take(MaxStoredErrors));

    internal static IReadOnlyList<ImportRowError> Deserialize(string json) => JsonSerializer.Deserialize<List<ImportRowError>>(json) ?? [];

    private async Task<ImportJob?> MineAsync(Guid jobId, bool tracked, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        return await unitOfWork.Repository<ImportJob>().GetAsync(j => j.Id == jobId && j.RequestedByUserId == userId, asNoTracking: !tracked, cancellationToken: cancellationToken);
    }

    private static ImportJobView View(ImportJob job, IImporter? importer, IReadOnlyList<ImportRowError> errors) => new(
        job.Id, job.ImporterCode, importer?.Name ?? job.ImporterCode, job.FileName, job.Status, job.TotalRows, job.ValidRows, job.InvalidRows,
        job.AppliedRows, job.FailedRows, job.CanConfirm, job.AllowPartial, job.Error, errors, job.DateCreated, job.CompletedAt);

    private static async IAsyncEnumerable<IReadOnlyList<object?>> One(IReadOnlyList<object?> row)
    {
        await Task.CompletedTask;
        yield return row;
    }
}

/// <param name="Total">Data rows read (empty rows don't count).</param>
internal sealed record ImportRun(int Total, IReadOnlyList<ImportRow> Valid, IReadOnlyList<ImportRowError> Errors);
