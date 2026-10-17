using Base.Domain.Auditing;
using Base.Domain.Common;

namespace Base.Domain.DataExchange;

public enum DataFormat
{
    Csv = 0,
    Xlsx = 1,
}

public enum ExportStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

public enum ImportStatus
{
    /// <summary>The dry run found errors; it can't be applied (unless the importer allows partial imports).</summary>
    Invalid = 0,

    /// <summary>The dry run passed; waiting for the user to confirm.</summary>
    Validated = 1,
    Queued = 2,
    Running = 3,
    Completed = 4,

    /// <summary>Finished, but some rows were skipped or failed (see the errors).</summary>
    CompletedWithErrors = 5,
    Failed = 6,
}

public sealed record ExportQueued(Guid JobId) : IDomainEvent;

public sealed record ImportQueued(Guid JobId) : IDomainEvent;

/// <summary>
/// One export: who exported which list, with which filter, how many rows. Every export is recorded (and so audited),
/// immediate ones included, because exports are how data leaves the system.
/// </summary>
public sealed class ExportJob : EntityBase, ITenantOwned
{
    public const int CodeMaxLength = 100;
    public const int FilterMaxLength = 2000;
    public const int ErrorMaxLength = 2000;

    private ExportJob()
    {
    }

    private ExportJob(string exporterCode, DataFormat format, string filterJson, Guid requestedByUserId)
    {
        Id = SequentialGuid.Next();
        ExporterCode = exporterCode;
        Format = format;
        FilterJson = filterJson;
        RequestedByUserId = requestedByUserId;
    }

    public Guid TenantId { get; set; }

    public string ExporterCode { get; private set; } = string.Empty;

    public DataFormat Format { get; private set; }

    /// <summary>The filter the list was exported with, as JSON.</summary>
    public string FilterJson { get; private set; } = "{}";

    public Guid RequestedByUserId { get; private set; }

    public ExportStatus Status { get; private set; }

    public int RowCount { get; private set; }

    /// <summary>The produced file (background exports only; immediate ones are streamed and not kept).</summary>
    public Guid? FileId { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>A large export, produced in the background.</summary>
    public static ExportJob Queue(string exporterCode, DataFormat format, string filterJson, Guid requestedByUserId)
    {
        var job = new ExportJob(exporterCode, format, filterJson, requestedByUserId);
        job.Raise(new ExportQueued(job.Id));
        return job;
    }

    /// <summary>A small export, streamed to the user straight away.</summary>
    public static ExportJob Immediate(string exporterCode, DataFormat format, string filterJson, Guid requestedByUserId, int rowCount, DateTimeOffset now) =>
        new(exporterCode, format, filterJson, requestedByUserId) { Status = ExportStatus.Completed, RowCount = rowCount, CompletedAt = now };

    public void MarkRunning() => Status = ExportStatus.Running;

    public void MarkCompleted(Guid fileId, int rowCount, DateTimeOffset now)
    {
        FileId = fileId;
        RowCount = rowCount;
        CompletedAt = now;
        Status = ExportStatus.Completed;
    }

    public void MarkFailed(string error, DateTimeOffset now)
    {
        Error = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        CompletedAt = now;
        Status = ExportStatus.Failed;
    }
}

/// <summary>
/// One import: the uploaded file, its dry-run result and, once confirmed and applied, what happened to each row.
/// </summary>
public sealed class ImportJob : EntityBase, ITenantOwned
{
    public const int CodeMaxLength = 100;
    public const int ErrorMaxLength = 2000;

    private ImportJob()
    {
    }

    public ImportJob(string importerCode, DataFormat format, Guid fileId, string fileName, Guid requestedByUserId, bool allowPartial)
    {
        Id = SequentialGuid.Next();
        ImporterCode = importerCode;
        Format = format;
        FileId = fileId;
        FileName = fileName;
        RequestedByUserId = requestedByUserId;
        AllowPartial = allowPartial;
    }

    public Guid TenantId { get; set; }

    public string ImporterCode { get; private set; } = string.Empty;

    public DataFormat Format { get; private set; }

    /// <summary>The uploaded file (kept until it expires, for the apply step and the error file).</summary>
    public Guid FileId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public Guid RequestedByUserId { get; private set; }

    /// <summary>Apply the valid rows even when others fail, instead of all-or-nothing.</summary>
    public bool AllowPartial { get; private set; }

    public ImportStatus Status { get; private set; }

    public int TotalRows { get; private set; }

    public int ValidRows { get; private set; }

    public int InvalidRows { get; private set; }

    public int AppliedRows { get; private set; }

    public int FailedRows { get; private set; }

    /// <summary>Row errors as JSON (the first <c>ImportLimits.MaxStoredErrors</c>). Not audited: it's a report, not data.</summary>
    [NotAudited]
    public string ErrorsJson { get; private set; } = "[]";

    public string? Error { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool CanConfirm => Status == ImportStatus.Validated || (Status == ImportStatus.Invalid && AllowPartial && ValidRows > 0);

    public void RecordDryRun(int total, int valid, string errorsJson)
    {
        TotalRows = total;
        ValidRows = valid;
        InvalidRows = total - valid;
        ErrorsJson = errorsJson;
        Status = InvalidRows == 0 && total > 0 ? ImportStatus.Validated : ImportStatus.Invalid;
    }

    public void Confirm()
    {
        if (!CanConfirm)
        {
            throw new InvalidOperationException("This import can't be applied: fix the errors and upload it again.");
        }

        Status = ImportStatus.Queued;
        Raise(new ImportQueued(Id));
    }

    public void MarkRunning() => Status = ImportStatus.Running;

    public void MarkCompleted(int applied, int failed, string errorsJson, DateTimeOffset now)
    {
        AppliedRows = applied;
        FailedRows = failed;
        ErrorsJson = errorsJson;
        CompletedAt = now;
        Status = failed == 0 && applied == TotalRows ? ImportStatus.Completed : ImportStatus.CompletedWithErrors;
    }

    /// <param name="errorsJson">Row errors explaining the failure, when there are any.</param>
    public void MarkFailed(string error, DateTimeOffset now, string? errorsJson = null)
    {
        Error = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        ErrorsJson = errorsJson ?? ErrorsJson;
        CompletedAt = now;
        Status = ImportStatus.Failed;
    }
}
