using Base.Application.Notifications;
using Base.Application.Settings;
using Base.Domain.Notifications;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Notifications;

/// <summary>Completes a recipient from their account (tenant-filtered) and normalises the phone number to E.164.</summary>
internal sealed class NotificationRecipientResolver(AppDbContext db, IApplicationSettingService settings) : INotificationRecipientResolver
{
    public async Task<NotificationRecipientSnapshot?> ResolveAsync(NotificationRecipient recipient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        string? email = recipient.Email, phone = recipient.Phone, name = recipient.Name;

        if (recipient.UserId is { } userId)
        {
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Email, u.PhoneNumber, u.DisplayName })
                .FirstOrDefaultAsync(cancellationToken);
            if (user is null)
            {
                return null;
            }

            email ??= user.Email;
            phone ??= user.PhoneNumber;
            name ??= user.DisplayName;
        }

        var countryCode = await settings.GetAsync(SettingCodes.Notifications.DefaultCountryCode, cancellationToken) ?? "27";
        return new NotificationRecipientSnapshot(
            recipient.UserId,
            name,
            string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal) ? null : email.Trim(),
            PhoneNumbers.ToE164(phone, countryCode));
    }
}
