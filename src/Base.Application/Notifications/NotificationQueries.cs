using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Domain.Notifications;

namespace Base.Application.Notifications;

public sealed record InboxItem(Guid Id, string Title, string? Body, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

/// <summary>The signed-in user's in-app notifications.</summary>
public interface INotificationInbox
{
    Task<PagedResult<InboxItem>> GetAsync(PageRequest page, CancellationToken cancellationToken = default);

    Task<int> UnreadCountAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse> MarkReadAsync(Guid messageId, CancellationToken cancellationToken = default);

    Task<int> MarkAllReadAsync(CancellationToken cancellationToken = default);
}

public sealed record NotificationLogItem(
    Guid Id,
    DateTimeOffset CreatedAt,
    string TemplateCode,
    NotificationChannel Channel,
    string RecipientAddress,
    string? RecipientName,
    NotificationStatus Status,
    string? Provider,
    string? ProviderMessageId,
    int Attempts,
    string? LastError,
    DateTimeOffset? SentAt,
    bool IsSensitive,
    string? Subject,
    string? Body,
    Guid? FallbackOf);

public sealed record NotificationLogQuery(
    string? Search = null,
    NotificationChannel? Channel = null,
    NotificationStatus? Status = null,
    string? TemplateCode = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>Every message sent, skipped or failed in this tenant, for administrators.</summary>
public interface INotificationLog
{
    Task<PagedResult<NotificationLogItem>> SearchAsync(NotificationLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Queues an exact copy on the same channel. Sensitive messages kept no content and can't be resent.</summary>
    Task<ValidationResponse<Guid>> ResendAsync(Guid messageId, CancellationToken cancellationToken = default);
}

internal sealed class NotificationInbox(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider time) : INotificationInbox
{
    public async Task<PagedResult<InboxItem>> GetAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is not { } userId)
        {
            return PagedResult<InboxItem>.Empty(page);
        }

        return await unitOfWork.Repository<NotificationMessage>().GetPagedAsync(
            page,
            m => new InboxItem(m.Id, m.Subject ?? string.Empty, m.Body, m.DateCreated, m.ReadAt),
            q => q.OrderByDescending(m => m.DateCreated).ThenByDescending(m => m.Id),
            m => m.RecipientUserId == userId && m.Channel == NotificationChannel.InApp && m.Status == NotificationStatus.Sent,
            cancellationToken);
    }

    public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default) =>
        currentUser.UserId is { } userId
            ? unitOfWork.Repository<NotificationMessage>().CountAsync(
                m => m.RecipientUserId == userId && m.Channel == NotificationChannel.InApp && m.Status == NotificationStatus.Sent && m.ReadAt == null,
                cancellationToken)
            : Task.FromResult(0);

    public async Task<ValidationResponse> MarkReadAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId;
        var message = await unitOfWork.Repository<NotificationMessage>().GetAsync(
            m => m.Id == messageId && m.RecipientUserId == userId && m.Channel == NotificationChannel.InApp, cancellationToken: cancellationToken);
        if (message is null)
        {
            return ValidationResponse.NotFound("Notification not found.");
        }

        message.MarkRead(time.GetUtcNow());
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success();
    }

    public async Task<int> MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is not { } userId)
        {
            return 0;
        }

        var unread = await unitOfWork.Repository<NotificationMessage>().GetAllAsync(
            filter: m => m.RecipientUserId == userId && m.Channel == NotificationChannel.InApp && m.ReadAt == null,
            take: 500,
            cancellationToken: cancellationToken);
        var now = time.GetUtcNow();
        foreach (var message in unread)
        {
            message.MarkRead(now);
        }

        await unitOfWork.SaveAsync(cancellationToken);
        return unread.Count;
    }
}

internal sealed class NotificationLog(IUnitOfWork unitOfWork) : INotificationLog
{
    public Task<PagedResult<NotificationLogItem>> SearchAsync(NotificationLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var template = string.IsNullOrWhiteSpace(query.TemplateCode) ? null : query.TemplateCode;

        return unitOfWork.Repository<NotificationMessage>().GetPagedAsync(
            new PageRequest(query.Page, query.PageSize),
            m => new NotificationLogItem(m.Id, m.DateCreated, m.TemplateCode, m.Channel, m.RecipientAddress, m.RecipientName, m.Status,
                m.Provider, m.ProviderMessageId, m.Attempts, m.LastError, m.SentAt, m.IsSensitive, m.Subject, m.Body, m.FallbackOf),
            q => q.OrderByDescending(m => m.DateCreated).ThenByDescending(m => m.Id),
            m => (search == null || m.RecipientAddress.Contains(search) || (m.RecipientName != null && m.RecipientName.Contains(search)))
                && (query.Channel == null || m.Channel == query.Channel)
                && (query.Status == null || m.Status == query.Status)
                && (template == null || m.TemplateCode == template),
            cancellationToken);
    }

    public async Task<ValidationResponse<Guid>> ResendAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.Repository<NotificationMessage>();
        var message = await repository.GetAsync(m => m.Id == messageId, asNoTracking: true, cancellationToken: cancellationToken);
        if (message is null)
        {
            return ValidationResponse.NotFound("Message not found.").As<Guid>();
        }

        if (message.IsSensitive)
        {
            return ValidationResponse.Conflict("This message contained a sign-in link and kept no content. Send a new invite or reset instead.").As<Guid>();
        }

        if (message.Status == NotificationStatus.Skipped)
        {
            return ValidationResponse.Conflict("Skipped messages have no content to resend.").As<Guid>();
        }

        var copy = message.CopyForResend();
        await repository.AddAsync(copy, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
        return copy.Id;
    }
}
