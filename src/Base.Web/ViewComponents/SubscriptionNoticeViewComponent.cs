using Base.Application.Common.Abstractions;
using Base.Application.Security;
using Base.Application.Subscriptions;
using Base.Domain.Subscriptions;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.ViewComponents;

/// <summary>A slim banner above the page when the subscription needs attention: past due, trial ending, or set to cancel.</summary>
public sealed class SubscriptionNoticeViewComponent(ITenantEntitlements entitlements, ICurrentUser user, TimeProvider time) : ViewComponent
{
    private static readonly TimeSpan TrialWarning = TimeSpan.FromDays(7);

    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!user.IsAuthenticated)
        {
            return Content(string.Empty);
        }

        var access = await entitlements.GetAsync(HttpContext.RequestAborted);
        var now = time.GetUtcNow();
        var notice = access switch
        {
            { IsRestricted: false } => null,
            { Status: SubscriptionStatus.PastDue } => new SubscriptionNotice("warning", "Payment is overdue. Access will be suspended at the end of the grace period.", access.PeriodEnd),
            { CancelAtPeriodEnd: true } => new SubscriptionNotice("info", "The subscription ends at the end of the current period.", access.PeriodEnd),
            { Status: SubscriptionStatus.Trialing, PeriodEnd: { } end } when end - now <= TrialWarning => new SubscriptionNotice("info", "The free trial ends soon.", end),
            _ => null,
        };

        return notice is null ? Content(string.Empty) : View(notice with { CanView = user.HasPermission(PermissionCodes.Subscription.View) });
    }
}

public sealed record SubscriptionNotice(string Tone, string Message, DateTimeOffset? Date, bool CanView = false);
