using Base.Domain.Subscriptions;

namespace Base.Application.Subscriptions;

public sealed record PlanSummary(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    string Currency,
    decimal MonthlyPrice,
    decimal AnnualPrice,
    int TrialDays,
    bool IsActive,
    int SortOrder,
    int SubscriberCount,
    IReadOnlyDictionary<string, long?> Entitlements)
{
    public long? ValueOf(string code) =>
        Entitlements.TryGetValue(code, out var value) ? value : EntitlementCatalog.Get(code).DefaultValue;
}

/// <param name="Entitlements">Every catalogued entitlement: features 1/0, limits a maximum or null for unlimited.</param>
public sealed record SavePlanRequest(
    string Code,
    string Name,
    string? Description,
    string Currency,
    decimal MonthlyPrice,
    decimal AnnualPrice,
    int TrialDays,
    int SortOrder,
    IReadOnlyDictionary<string, long?> Entitlements);

public sealed record SubscriptionView(
    Guid Id,
    Guid TenantId,
    Guid PlanId,
    string PlanName,
    SubscriptionStatus Status,
    BillingCycle BillingCycle,
    decimal Price,
    string Currency,
    DateTimeOffset StartedAt,
    DateTimeOffset? TrialEndsAt,
    DateTimeOffset CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    DateTimeOffset StatusChangedAt,
    string? StatusReason,
    string? ExternalReference)
{
    public bool HasAccess => Status is SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    public bool IsEnded => Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired;
}

/// <summary>One limit and how much of it is used, both in the limit's units (MB for storage).</summary>
public sealed record UsageView(string Code, string Name, long Used, long? Limit, string? Unit)
{
    /// <summary>0–100, or null for unlimited.</summary>
    public int? Percent => Limit is > 0 ? (int)Math.Min(100, Math.Round(Used * 100d / Limit.Value)) : Limit == 0 ? 100 : null;
}

/// <summary>What the Subscription page shows a tenant: its plan, status, usage, and the plans on offer.</summary>
public sealed record MySubscription(SubscriptionView? Subscription, IReadOnlyList<UsageView> Usage, IReadOnlyList<PlanSummary> Plans);

public sealed record StartSubscriptionRequest(Guid PlanId, BillingCycle BillingCycle, bool WithTrial = true);

public sealed record ChangePlanRequest(Guid PlanId, BillingCycle BillingCycle);

public static class SubscriptionStatusText
{
    public static string Describe(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Trialing => "Trial",
        SubscriptionStatus.PastDue => "Past due",
        _ => status.ToString(),
    };
}
