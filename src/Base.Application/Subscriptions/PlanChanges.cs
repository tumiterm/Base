using Base.Domain.Subscriptions;

namespace Base.Application.Subscriptions;

/// <summary>What choosing a plan and billing cycle does to the subscription, from the organisation's side.</summary>
public enum PlanChangeKind
{
    /// <summary>A fresh period on the plan from today, at its full price (no paid period is running, or it can't be pro-rated).</summary>
    Start = 0,

    /// <summary>The same plan and cycle while a paid period runs: another period after it, at the full price.</summary>
    Extend = 1,

    /// <summary>A plan that costs at least as much, now, for the difference over the rest of the period.</summary>
    Upgrade = 2,

    /// <summary>A cheaper plan, or another billing cycle: when the paid period ends, with nothing to pay now.</summary>
    AtPeriodEnd = 3,
}

/// <summary>When the operator's change takes effect.</summary>
public enum PlanChangeTiming
{
    Now = 0,
    AtPeriodEnd = 1,
}

/// <summary>A limit the organisation uses more of than another plan allows, in the limit's units.</summary>
public sealed record LimitExcess(string Code, string Name, long Used, long Limit, string? Unit)
{
    public long Over => Used - Limit;

    public string Describe() =>
        string.Equals(Code, LimitCodes.Users, StringComparison.Ordinal)
            ? $"{Used} users; the plan allows {Limit}. Lock or remove {Over} first (locked users don't count)."
            : $"{Name}: {Used}{UnitText} used; the plan allows {Limit}{UnitText}. Free up {Over}{UnitText} first.";

    private string UnitText => Unit is null ? string.Empty : " " + Unit;
}

public sealed record ScheduledChangeView(Guid PlanId, string PlanName, BillingCycle BillingCycle, decimal Price, string Currency, DateTimeOffset At);

/// <summary>
/// What moving a subscription to <see cref="To"/> on <see cref="ToCycle"/> would do: when, what is due, which limits it
/// is over and which features come and go. <see cref="Kind"/> is what the organisation itself gets; the operator picks
/// the timing.
/// </summary>
public sealed record PlanChangePreview(
    Guid TenantId,
    PlanSummary From,
    BillingCycle FromCycle,
    PlanSummary To,
    BillingCycle ToCycle,
    PlanChangeKind Kind,
    DateTimeOffset EffectiveAt,
    decimal AmountDueNow,
    DateTimeOffset PeriodEnd,
    bool CanWaitForPeriodEnd,
    IReadOnlyList<LimitExcess> OverLimits,
    IReadOnlyList<string> FeaturesLost,
    IReadOnlyList<string> FeaturesGained,
    bool RenewsAutomatically,
    ScheduledChangeView? Scheduled)
{
    public bool IsCurrent => From.Id == To.Id && FromCycle == ToCycle;

    /// <summary>Over a limit of the new plan: the organisation has to get under it first.</summary>
    public bool IsBlocked => OverLimits.Count > 0;

    public decimal FromPrice => From.PriceFor(FromCycle);

    public decimal ToPrice => To.PriceFor(ToCycle);

    public string Currency => To.Currency;
}

/// <summary>The rules for changing plan part-way through a period. Pure, so they can be tested on their own.</summary>
public static class PlanChangeRules
{
    /// <summary>
    /// How the organisation moves to <paramref name="to"/> on <paramref name="cycle"/>: an upgrade applies now for the
    /// pro-rated difference, a downgrade or change of cycle waits for the paid period to end, and without a running paid
    /// period (trial, past due, suspended) any plan starts afresh today.
    /// </summary>
    public static (PlanChangeKind Kind, DateTimeOffset EffectiveAt, decimal AmountDueNow) Classify(
        TenantSubscription subscription, PlanSummary from, PlanSummary to, BillingCycle cycle, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var full = to.PriceFor(cycle);
        if (!subscription.IsInPaidPeriod(now))
        {
            return (PlanChangeKind.Start, now, full);
        }

        if (to.Id == subscription.PlanId && cycle == subscription.BillingCycle)
        {
            return (PlanChangeKind.Extend, subscription.CurrentPeriodEnd, full);
        }

        if (!string.Equals(from.Currency, to.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return (PlanChangeKind.Start, now, full);
        }

        if (cycle != subscription.BillingCycle)
        {
            return (PlanChangeKind.AtPeriodEnd, subscription.CurrentPeriodEnd, 0m);
        }

        var difference = to.PriceFor(cycle) - from.PriceFor(cycle);
        return difference >= 0
            ? (PlanChangeKind.Upgrade, now, ProRata(difference, subscription, now))
            : (PlanChangeKind.AtPeriodEnd, subscription.CurrentPeriodEnd, 0m);
    }

    /// <summary>
    /// <paramref name="amount"/> (a price per billing cycle) for the time left until the paid-up date, to the cent: half
    /// a month left is half of it, and three months paid ahead are three times it.
    /// </summary>
    public static decimal ProRata(decimal amount, TenantSubscription subscription, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var cycle = (subscription.CurrentPeriodEnd - subscription.CurrentPeriodStart).TotalSeconds;
        var left = (subscription.CurrentPeriodEnd - now).TotalSeconds;
        if (cycle <= 0 || left <= 0)
        {
            return 0m;
        }

        return decimal.Round(amount * (decimal)(left / cycle), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The limits of <paramref name="to"/> that current usage (in measured units) is over. Limits on what is used up
    /// each month don't count: they start again.
    /// </summary>
    public static IReadOnlyList<LimitExcess> OverLimits(PlanSummary to, IReadOnlyDictionary<string, long> measured)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(measured);
        var over = new List<LimitExcess>();
        foreach (var definition in EntitlementCatalog.Limits.Where(l => !l.ResetsMonthly))
        {
            if (to.ValueOf(definition.Code) is not { } limit)
            {
                continue;
            }

            var used = (measured.GetValueOrDefault(definition.Code) + definition.UnitSize - 1) / definition.UnitSize;
            if (used > limit)
            {
                over.Add(new LimitExcess(definition.Code, definition.Name, used, limit, definition.Unit));
            }
        }

        return over;
    }

    public static (IReadOnlyList<string> Lost, IReadOnlyList<string> Gained) Features(PlanSummary from, PlanSummary to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var features = EntitlementCatalog.Features.ToList();
        return (
            features.Where(f => from.ValueOf(f.Code) == 1 && to.ValueOf(f.Code) != 1).Select(f => f.Name).ToList(),
            features.Where(f => from.ValueOf(f.Code) != 1 && to.ValueOf(f.Code) == 1).Select(f => f.Name).ToList());
    }
}
