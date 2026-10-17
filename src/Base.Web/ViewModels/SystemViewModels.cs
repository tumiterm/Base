using Base.Application.Common.Persistence;
using Base.Application.Operations;

namespace Base.Web.ViewModels;

public sealed record SystemViewModel(
    HealthReportView Health,
    IReadOnlyList<JobStatusView> Jobs,
    DatabaseStats Database,
    IReadOnlyList<StorageUsage> Storage,
    OutboxStats Outbox,
    PagedResult<ErrorRecord> RecentErrors);

public sealed record ErrorListViewModel(PagedResult<ErrorRecord> Errors, string? Search);

public sealed record ErrorDetailsViewModel(ErrorRecord Error, Uri? TraceUrl, Uri? LogSearchUrl);
