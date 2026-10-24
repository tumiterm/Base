using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Domain.Notifications;

namespace Base.Application.Notifications;

public sealed record NotificationPreferenceItem(string Category, NotificationChannel Channel, bool IsEnabled, bool IsLocked);

/// <summary>The signed-in user's opt-ins and opt-outs, per category and channel.</summary>
public interface INotificationPreferences
{
    Task<IReadOnlyList<NotificationPreferenceItem>> GetMineAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse> SetMineAsync(string category, NotificationChannel channel, bool isEnabled, CancellationToken cancellationToken = default);
}

internal sealed class NotificationPreferences(IUnitOfWork unitOfWork, ICurrentUser currentUser) : INotificationPreferences
{
    public async Task<IReadOnlyList<NotificationPreferenceItem>> GetMineAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is not { } userId)
        {
            return [];
        }

        var saved = await unitOfWork.Repository<NotificationPreference>().GetAllAsync(
            p => new { p.Category, p.Channel, p.IsEnabled }, p => p.UserId == userId, cancellationToken: cancellationToken);

        return ChannelsByCategory()
            .SelectMany(c => c.Channels.Select(channel => (c.Category, Channel: channel)))
            .Select(x => new NotificationPreferenceItem(
                x.Category,
                x.Channel,
                saved.FirstOrDefault(s => string.Equals(s.Category, x.Category, StringComparison.Ordinal) && s.Channel == x.Channel)?.IsEnabled ?? true,
                NotificationCatalog.IsMandatoryCategory(x.Category)))
            .ToList();
    }

    public async Task<ValidationResponse> SetMineAsync(string category, NotificationChannel channel, bool isEnabled, CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is not { } userId)
        {
            return ValidationResponse.Unauthorized();
        }

        if (!ChannelsByCategory().Any(c => string.Equals(c.Category, category, StringComparison.Ordinal) && c.Channels.Contains(channel)))
        {
            return ValidationResponse.NotFound($"There are no {category} notifications on {channel}.");
        }

        if (NotificationCatalog.IsMandatoryCategory(category))
        {
            return ValidationResponse.Conflict($"{category} notifications can't be switched off.");
        }

        var repository = unitOfWork.Repository<NotificationPreference>();
        var row = await repository.GetAsync(p => p.UserId == userId && p.Category == category && p.Channel == channel, cancellationToken: cancellationToken);
        if (row is null)
        {
            await repository.AddAsync(new NotificationPreference(userId, category, channel, isEnabled), cancellationToken);
        }
        else
        {
            row.Set(isEnabled);
        }

        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success("Preference saved.");
    }

    private static IEnumerable<(string Category, List<NotificationChannel> Channels)> ChannelsByCategory() =>
        NotificationCatalog.All
            .GroupBy(t => t.Category, StringComparer.Ordinal)
            .Select(g => (g.Key, g.SelectMany(t => t.Channels.Concat(t.Fallback ?? [])).Distinct().Order().ToList()));
}
