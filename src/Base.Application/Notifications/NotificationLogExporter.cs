using Base.Application.Common.Persistence;
using Base.Application.DataExchange;
using Base.Application.Security;
using Base.Domain.Notifications;

namespace Base.Application.Notifications;

/// <summary>The notification log. Filters: <c>search</c>, <c>channel</c>, <c>status</c>, <c>template</c>.</summary>
internal sealed class NotificationLogExporter(INotificationLog log) : Exporter<NotificationLogItem>
{
    public override string Code => "notifications";

    public override string Name => "Notification log";

    public override string Permission => PermissionCodes.Notifications.View;

    protected override IReadOnlyList<ExportColumn<NotificationLogItem>> Definition { get; } =
    [
        new("When", m => m.CreatedAt, ColumnType.DateTime),
        new("Template", m => m.TemplateCode),
        new("Channel", m => m.Channel),
        new("To", m => m.RecipientAddress),
        new("Name", m => m.RecipientName),
        new("Status", m => m.Status),
        new("Provider", m => m.Provider),
        new("Attempts", m => m.Attempts, ColumnType.Integer),
        new("Error", m => m.LastError),
        new("Sent", m => m.SentAt, ColumnType.DateTime),
    ];

    protected override Task<PagedResult<NotificationLogItem>> PageAsync(ExportFilter filter, int page, int pageSize, CancellationToken cancellationToken) =>
        log.SearchAsync(
            new NotificationLogQuery(filter.Text("search"), filter.Enum<NotificationChannel>("channel"), filter.Enum<NotificationStatus>("status"), filter.Text("template"), page, pageSize),
            cancellationToken);
}
